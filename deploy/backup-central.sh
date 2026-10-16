#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
set -a
source .env
set +a
stamp="$(date +%Y%m%d-%H%M%S)"
mkdir -p backups
docker compose exec -T central-sql /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "${MSSQL_SA_PASSWORD}" -C -b \
  -Q "BACKUP DATABASE VOYPIDIENDO_CENTRAL TO DISK='/var/opt/mssql/backup/VOYPIDIENDO_CENTRAL-${stamp}.bak' WITH INIT,COMPRESSION,CHECKSUM"
find backups -type f -name 'VOYPIDIENDO_CENTRAL-*.bak' -mtime +14 -delete
echo "Backup generado: backups/VOYPIDIENDO_CENTRAL-${stamp}.bak"
