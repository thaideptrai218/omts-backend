#!/usr/bin/env bash
# Staging deploy on the EC2 host, run by the "CD staging" workflow (.github/workflows/cd-staging.yml):
#   bash /opt/omts/deploy.sh <image reference>     e.g. ghcr.io/thaideptrai218/omts-backend:sha-1a2b3c4
# The workflow first uploads deploy/staging/compose.yml as /opt/omts/compose.next.yml (optional: without it the
# current compose.yml stays). Steps: switch compose + OMTS_IMAGE in .env, pull, apply migrations with a plain
# "seed --bootstrap" (retried while the Azure SQL serverless database wakes up), restart the API, wait for /health/db.
# A failure, or the run being killed (SIGHUP/SIGTERM), restores the last good compose file and the image that ran
# before, then checks the API again. Migrations are forward-only: a rollback never undoes one the new image applied.
# The Actions log of this public repository shows stdout: seed and API output go to deploy.log on the host instead,
# and the secrets of .env are never printed. Only the OMTS_IMAGE line of .env is ever changed.
#
# Everything runs inside main(), so bash has read the whole file before the first step: the workflow may upload a
# new copy of this file while an older run is still going.
set -euo pipefail

main() {
  local image="${1:?usage: deploy.sh <image reference>}"
  local dir="${OMTS_DIR:-/opt/omts}"
  HEALTH_URL="http://127.0.0.1:8080/health/db"
  LOG="$dir/deploy.log"
  local seed_attempts=4      # x 20 s: a paused free-tier database takes 30-60 s to resume (SQL error 40613)
  local seed_timeout=300     # seconds for one migration run; a hung run is killed, not waited for until the job dies

  cd "$dir"

  if ! valid_ref "$image"; then
    log "refused image reference: $image"
    rm -f compose.next.yml
    exit 2
  fi

  # One deploy at a time, whatever started it (workflow or a person on the host).
  exec 9>"$dir/.deploy.lock"
  flock -n 9 || { log "another deploy is running"; exit 1; }

  # Rollback targets come from what really ran, not from .env and compose.yml: a killed run may have left those
  # pointing at an untested version. compose.good.yml is written only after a deploy passed its health check.
  PREVIOUS_IMAGE="$(running_image)"
  if ! valid_ref "$PREVIOUS_IMAGE"; then
    PREVIOUS_IMAGE=""
  fi
  if [ ! -f compose.good.yml ]; then
    cp compose.yml compose.good.yml
  fi
  ROLLING_BACK=""
  trap 'rollback "interrupted"' HUP INT TERM

  log "deploying $image (previous: ${PREVIOUS_IMAGE:-none}); details in $LOG on the host"
  echo "===== $(date -u +%FT%TZ) deploy $image" >> "$LOG"

  if [ -f compose.next.yml ]; then
    mv compose.next.yml compose.yml
  fi
  set_image "$image"
  compose config -q >> "$LOG" 2>&1 || rollback "compose.yml does not resolve with this .env"

  compose pull -q api >> "$LOG" 2>&1 || rollback "pull $image (is the GHCR package public?)"

  # Best effort: wake the database through the API that still runs, so the first migration attempt rarely waits.
  curl -s -o /dev/null -m 60 "$HEALTH_URL" || true

  local migrated="" attempt
  for attempt in $(seq 1 "$seed_attempts"); do
    if timeout "$seed_timeout" docker compose -f compose.yml run --rm -T --name omts-staging-migrate api seed --bootstrap >> "$LOG" 2>&1; then
      migrated=1
      break
    fi
    docker rm -f omts-staging-migrate > /dev/null 2>&1 || true
    log "seed --bootstrap attempt $attempt/$seed_attempts failed; waiting for the database"
    sleep 20
  done
  [ -n "$migrated" ] || rollback "seed --bootstrap (migrations); the old API kept running"

  compose up -d --remove-orphans >> "$LOG" 2>&1 || rollback "up -d"

  if ! wait_healthy; then
    compose logs --tail 80 api >> "$LOG" 2>&1 || true
    rollback "health check $HEALTH_URL"
  fi

  local now
  now="$(running_image)"
  [ "$now" = "$image" ] || rollback "the API runs '${now:-nothing}' instead of $image"

  trap - HUP INT TERM
  cp compose.yml compose.good.yml
  remove_old_images "$image" "$PREVIOUS_IMAGE"
  log "OK: $image is healthy"
}

log() { echo "[deploy] $*"; }
compose() { docker compose -f compose.yml "$@"; }

# Registry, path, tag and digest characters only: the reference goes into sed and .env.
valid_ref() { [[ "$1" =~ ^[a-z0-9][a-z0-9./_:@-]*$ ]]; }

running_image() {
  local id
  id="$(compose ps -q api 2> /dev/null || true)"
  if [ -n "$id" ]; then
    docker inspect --format '{{.Config.Image}}' "$id" 2> /dev/null || true
  fi
}

set_image() {
  if grep -qE '^OMTS_IMAGE=' .env; then
    sed -i "s|^OMTS_IMAGE=.*|OMTS_IMAGE=$1|" .env
  else
    printf 'OMTS_IMAGE=%s\n' "$1" >> .env
  fi
}

wait_healthy() {
  local _
  for _ in $(seq 1 24); do   # x 5 s
    # Silent: connection resets are expected while the new container starts.
    if curl -fs -o /dev/null -m 5 "$HEALTH_URL"; then
      return 0
    fi
    sleep 5
  done
  return 1
}

rollback() {
  # A second signal during the rollback must not start another one.
  [ -z "$ROLLING_BACK" ] || exit 1
  ROLLING_BACK=1
  # The SSH session may be gone: a write to it must neither kill nor stop the rollback.
  trap '' HUP INT TERM PIPE
  set +e
  rlog "FAILED: $1"
  cp compose.good.yml compose.yml
  if [ -n "$PREVIOUS_IMAGE" ]; then
    rlog "rolling back to $PREVIOUS_IMAGE"
    set_image "$PREVIOUS_IMAGE"
    if compose up -d api >> "$LOG" 2>&1 && wait_healthy; then
      rlog "rolled back: $PREVIOUS_IMAGE is healthy"
    else
      rlog "ROLLBACK FAILED: staging is down, check the host"
    fi
  else
    rlog "nothing ran before this deploy: no image to roll back to"
  fi
  exit 1
}

# Rollback logging: always to the host file, and to the (maybe closed) SSH session when it is still there.
rlog() {
  echo "[deploy] $*" >> "$LOG"
  echo "[deploy] $*" 2> /dev/null
}

# Keeps the deployed and the previous image; other tags of the same repository only fill the small disk.
remove_old_images() {
  local repo="${1%:*}" ref
  docker images "$repo" --format '{{.Repository}}:{{.Tag}}' | while read -r ref; do
    if [ "$ref" != "$1" ] && [ "$ref" != "$2" ]; then
      docker rmi "$ref" > /dev/null 2>&1 || true
    fi
  done
  docker image prune -f > /dev/null
}

main "$@"
