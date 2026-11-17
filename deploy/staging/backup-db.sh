#!/usr/bin/env bash
# Nightly full backup of the staging database (the "mssql" service, profile localdb) to S3. The "CD staging" workflow
# copies this file to /opt/omts; the host's cron runs it, for example (/etc/cron.d/omts-db-backup):
#   30 2 * * * omts-deploy bash /opt/omts/backup-db.sh >> /opt/omts/backup.log 2>&1
# Steps: BACKUP DATABASE inside the container (WITH CHECKSUM; Express has no backup compression), copy it out, gzip,
# upload with the aws-cli image, keep the last 3 copies on the host. Settings come from /opt/omts/.env:
#   MSSQL_SA_PASSWORD (already there for the service), BACKUP_S3_URI (s3://<bucket>/<prefix>),
#   BACKUP_AWS_ACCESS_KEY_ID, BACKUP_AWS_SECRET_ACCESS_KEY, BACKUP_AWS_REGION.
# The key should only be allowed s3:PutObject under that prefix, so this host can add backups but never read or delete
# them; S3 lifecycle rules expire old ones. A failure leaves /opt/omts/backup.failed, a success removes it. Secrets are
# never printed.
set -Eeuo pipefail   # -E: the ERR trap below also fires inside functions

main() {
  local dir="${OMTS_DIR:-/opt/omts}" db="${BACKUP_DATABASE:-OMTS}"
  local aws_image="amazon/aws-cli:2.39.3"
  cd "$dir"
  trap 'touch "$dir/backup.failed"; log "FAILED"' ERR

  local s3_uri key_id secret region
  s3_uri="$(env_value BACKUP_S3_URI)"
  key_id="$(env_value BACKUP_AWS_ACCESS_KEY_ID)"
  secret="$(env_value BACKUP_AWS_SECRET_ACCESS_KEY)"
  region="$(env_value BACKUP_AWS_REGION)"
  [[ "$s3_uri" =~ ^s3://[a-z0-9.-]+(/[A-Za-z0-9._/-]*)?$ ]] || { log "BACKUP_S3_URI missing or not s3://bucket/prefix"; false; }
  [ -n "$key_id" ] && [ -n "$secret" ] && [ -n "$region" ] || { log "BACKUP_AWS_* settings missing in .env"; false; }

  local name stamp
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  name="${db,,}-$stamp.bak"
  mkdir -p backups

  log "backing up $db to $name"
  # Runs inside the container with the container's own MSSQL_SA_PASSWORD: the password never appears in a command line here.
  docker compose -f compose.yml exec -T mssql bash -c \
    "SQLCMDPASSWORD=\"\$MSSQL_SA_PASSWORD\" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -b -Q \
     \"BACKUP DATABASE [$db] TO DISK = N'/var/opt/mssql/backup/$name' WITH CHECKSUM, INIT, COPY_ONLY\"" > /dev/null
  docker compose -f compose.yml cp "mssql:/var/opt/mssql/backup/$name" "backups/$name"
  docker compose -f compose.yml exec -T mssql rm -f "/var/opt/mssql/backup/$name"
  gzip -f "backups/$name"

  log "uploading $name.gz ($(du -h "backups/$name.gz" | cut -f1)) to $s3_uri"
  AWS_ACCESS_KEY_ID="$key_id" AWS_SECRET_ACCESS_KEY="$secret" AWS_DEFAULT_REGION="$region" \
    docker run --rm -e AWS_ACCESS_KEY_ID -e AWS_SECRET_ACCESS_KEY -e AWS_DEFAULT_REGION \
      -v "$dir/backups:/backups:ro" "$aws_image" \
      s3 cp "/backups/$name.gz" "${s3_uri%/}/$name.gz" --only-show-errors

  # Keep the newest 3 on the host: a quick restore without S3, and the small disk stays small.
  ls -1t backups/*.bak.gz | tail -n +4 | xargs -r rm -f
  rm -f backup.failed
  log "OK: ${s3_uri%/}/$name.gz"
}

log() { echo "[backup $(date -u +%FT%TZ)] $*"; }

# One value from .env without sourcing it (values may hold characters bash would interpret).
env_value() { grep -E "^$1=" .env | tail -n 1 | cut -d= -f2- || true; }

main "$@"
