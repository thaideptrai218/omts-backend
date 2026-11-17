#!/usr/bin/env bash
# Nightly full backup of the staging database (the "mssql" service, profile localdb) to S3. The "CD staging" workflow
# copies this file to /opt/omts; the host's cron runs it, for example (/etc/cron.d/omts-db-backup):
#   30 2 * * * omts-deploy bash /opt/omts/backup-db.sh >> /opt/omts/backup.log 2>&1
# Steps: BACKUP DATABASE inside the container (WITH CHECKSUM; Express has no backup compression), stream it out through
# gzip, upload with the aws-cli image, keep the last 3 copies on the host. Settings come from /opt/omts/.env (unquoted):
#   MSSQL_SA_PASSWORD (already there for the service), BACKUP_S3_URI (s3://<bucket>/<prefix>),
#   BACKUP_AWS_ACCESS_KEY_ID, BACKUP_AWS_SECRET_ACCESS_KEY, BACKUP_AWS_REGION.
# The key should only be allowed s3:PutObject under that prefix; bucket versioning keeps an overwritten backup, and
# lifecycle rules expire old ones. A failure leaves /opt/omts/backup.failed (deploy.sh warns about it), a success
# removes it. Secrets are never printed.
set -Eeuo pipefail   # -E: the ERR trap below also fires inside functions

main() {
  local dir="${OMTS_DIR:-/opt/omts}" db="${BACKUP_DATABASE:-OMTS}"
  local aws_image="amazon/aws-cli:2.39.3" inner="/var/opt/mssql/backup"
  cd "$dir"

  # One backup at a time (cron and a person); separate from deploy.sh's lock so a backup never fails a deploy.
  exec 8>"$dir/.backup.lock"
  flock -n 8 || { log "another backup is running"; exit 0; }
  trap 'touch "$dir/backup.failed"; log "FAILED at line $LINENO"' ERR

  local s3_uri key_id secret region
  s3_uri="$(env_value BACKUP_S3_URI)"
  key_id="$(env_value BACKUP_AWS_ACCESS_KEY_ID)"
  secret="$(env_value BACKUP_AWS_SECRET_ACCESS_KEY)"
  region="$(env_value BACKUP_AWS_REGION)"
  [[ "$s3_uri" =~ ^s3://[a-z0-9.-]+(/[A-Za-z0-9._/-]*)?$ ]] || { log "BACKUP_S3_URI missing or not s3://bucket/prefix"; false; }
  [ -n "$key_id" ] && [ -n "$secret" ] && [ -n "$region" ] || { log "BACKUP_AWS_* settings missing in .env"; false; }

  # Leftovers of a failed run, and room for tonight's copy: the disk is small and S3 may be failing for days.
  mkdir -p backups
  compose exec -T mssql sh -c "mkdir -p $inner && rm -f $inner/*.bak"
  rm -f backups/*.bak backups/*.tmp
  find backups -maxdepth 1 -name '*.bak.gz' -printf '%T@ %p\n' | sort -rn | tail -n +3 | cut -d' ' -f2- | xargs -r rm -f

  local name
  name="${db,,}-$(date -u +%Y%m%dT%H%M%SZ).bak"
  log "backing up $db to $name"
  # Inside the container with its own MSSQL_SA_PASSWORD: the password never appears in a command line here.
  # SIMPLE recovery first (repeatable): only full backups are taken, so a FULL-recovery log would grow until the disk
  # is full. sqlcmd output (page counts or the error) goes to backup.log.
  compose exec -T mssql bash -c \
    "SQLCMDPASSWORD=\"\$MSSQL_SA_PASSWORD\" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -b -Q \
     \"ALTER DATABASE [$db] SET RECOVERY SIMPLE; BACKUP DATABASE [$db] TO DISK = N'$inner/$name' WITH CHECKSUM, INIT, COPY_ONLY\""
  compose exec -T mssql cat "$inner/$name" | gzip > "backups/$name.gz.tmp"
  mv "backups/$name.gz.tmp" "backups/$name.gz"
  compose exec -T mssql rm -f "$inner/$name"

  log "uploading $name.gz ($(du -h "backups/$name.gz" | cut -f1)) to $s3_uri"
  AWS_ACCESS_KEY_ID="$key_id" AWS_SECRET_ACCESS_KEY="$secret" AWS_DEFAULT_REGION="$region" \
    docker run --rm -e AWS_ACCESS_KEY_ID -e AWS_SECRET_ACCESS_KEY -e AWS_DEFAULT_REGION \
      -v "$dir/backups:/backups:ro" "$aws_image" \
      s3 cp "/backups/$name.gz" "${s3_uri%/}/$name.gz" --only-show-errors

  rm -f backup.failed
  log "OK: ${s3_uri%/}/$name.gz"
}

log() { echo "[backup $(date -u +%FT%TZ)] $*"; }
compose() { docker compose -f compose.yml "$@"; }

# One value from .env without sourcing it (values may hold characters bash would interpret).
env_value() { grep -E "^$1=" .env | tail -n 1 | cut -d= -f2- || true; }

# "exit" on the same line: a deploy may copy a longer version over this file while it runs.
main "$@"; exit
