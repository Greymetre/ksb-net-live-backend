#!/bin/sh
set -e

/app/db-bootstrap.sh

echo "Starting API..."
exec dotnet Api.dll
