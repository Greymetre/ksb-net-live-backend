# FieldKonnect Backend

Current release: `v6.7`

Backend and frontend tags use the same release number. Version-specific,
idempotent SQL Server scripts are stored under `database/releases` and must be
included with every deployment package.

Build the matching tagged backend with:

```bash
dotnet publish src/Api/Api.csproj -c Release -o publish
```

Never deploy `appsettings*.json`, `web.config`, uploads, logs or other live
environment files from a release build over the server copies.
