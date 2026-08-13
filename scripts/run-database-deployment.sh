#!/bin/sh
set -eu

if [ -z "${SQLSERVER_CONNECTIONSTRING:-}" ]; then
  echo "SQLSERVER_CONNECTIONSTRING is required." >&2
  exit 1
fi

export SUPERADMIN_EMAIL="${SUPERADMIN_EMAIL:-gajendra@greymetre.io}"
export SUPERADMIN_PASSWORD="${SUPERADMIN_PASSWORD:-Grey@2028@Field}"
export SUPERADMIN_NAME="${SUPERADMIN_NAME:-Gajendra}"
export SUPERADMIN_MOBILE="${SUPERADMIN_MOBILE:-9713113280}"
export SECOND_SUPERADMIN_EMAIL="${SECOND_SUPERADMIN_EMAIL:-swaraj.khalate@ksb.com}"
export SECOND_SUPERADMIN_PASSWORD="${SECOND_SUPERADMIN_PASSWORD:-Swaraj@5999@Fiedl}"
export SECOND_SUPERADMIN_NAME="${SECOND_SUPERADMIN_NAME:-Swaraj Khalate}"
export SECOND_SUPERADMIN_MOBILE="${SECOND_SUPERADMIN_MOBILE:-8793535999}"

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
APP_DIR=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)

cd "$APP_DIR"
dotnet Api.dll --migrate
dotnet Api.dll --seed-master-data
dotnet Api.dll --seed-superadmin

echo "Microsoft SQL Server migrations and idempotent seeders completed."
