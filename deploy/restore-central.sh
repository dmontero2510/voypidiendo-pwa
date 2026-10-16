#!/usr/bin/env bash
set -euo pipefail
if [ "$#" -ne 1 ]; then echo "Uso: ./restore-central.sh backups/archivo.bak"; exit 1; fi
cd "$(dirname "$0")"
set -a
source .env
set +a
file="$(basename "$1")"
test -f "backups/$file" || { echo "No existe backups/$file"; exit 1; }
docker compose exec -T central-sql /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "${MSSQL_SA_PASSWORD}" -C -b \
  -Q "ALTER DATABASE VOYPIDIENDO_CENTRAL SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE VOYPIDIENDO_CENTRAL FROM DISK='/var/opt/mssql/backup/${file}' WITH REPLACE; ALTER DATABASE VOYPIDIENDO_CENTRAL SET MULTI_USER"
echo "Base central restaurada desde backups/$file"
