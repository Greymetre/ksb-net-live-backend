# Cloud deployment with remote Microsoft SQL Server

## Final architecture

- Backend: ASP.NET 8 deployed on the client's chosen cloud provider.
- Frontend: Angular production build served by Node 22 on the cloud provider.
- Database: Microsoft SQL Server 2022 (16.x), hosted on the client's Microsoft
  server and configured with compatibility level 160.
- The database is not hosted on Azure.

Deploy in this order: database, backend, frontend.

## Release ZIP contents

```text
backend/backend-app.zip
frontend/frontend-app.zip
database/01-all-migrations.sql
database/02-all-seed-data.sql
CLOUD_DEPLOYMENT.md
RELEASE_PACKAGE_README.md
SHA256SUMS.txt
```

## 1. Prepare the Microsoft SQL Server

1. Install or use SQL Server 2022.
2. Enable TCP/IP in SQL Server Configuration Manager.
3. Bind SQL Server to a fixed port, normally TCP 1433.
4. Create a dedicated database and restricted SQL login for this application.
5. Set compatibility level 160.
6. Allow incoming traffic only from the cloud backend's fixed outbound IP, or
   connect cloud and server networks using a site-to-site VPN/private tunnel.
7. Never expose TCP 1433 to the complete public internet.
8. Use a valid server certificate and `Encrypt=True`.

Recommended production connection string:

```text
Server=tcp:<sql-server-host>,1433;Database=<database>;User ID=<app-user>;Password=<password>;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;
```

For an internal server certificate, install its issuing CA in the backend
container/host. Use `TrustServerCertificate=True` only as a temporary controlled
setup measure.

## 2. Create schema and seed data

Run these files against a new empty SQL Server database in numeric order:

```sh
sqlcmd -S tcp:<sql-server-host>,1433 -d <database> \
  -U <app-user> -P '<password>' -C -b \
  -i database/01-all-migrations.sql

sqlcmd -S tcp:<sql-server-host>,1433 -d <database> \
  -U <app-user> -P '<password>' -C -b \
  -i database/02-all-seed-data.sql
```

Verify:

```sql
SELECT compatibility_level
FROM sys.databases
WHERE name = DB_NAME();

SELECT * FROM __EFMigrationsHistory;
```

Expected compatibility level is `160` and the package contains two migrations.

For future releases, the published backend also supports repeat-safe commands:

```sh
dotnet Api.dll --migrate
dotnet Api.dll --seed-master-data
dotnet Api.dll --seed-superadmin
```

## 3. Verify cloud-to-database networking

From the cloud backend environment, confirm DNS, TCP and SQL authentication
before starting the application:

```sh
sqlcmd -S tcp:<sql-server-host>,1433 -d <database> \
  -U <app-user> -P '<password>' -C -Q "SELECT 1"
```

If this fails, check the Microsoft server firewall, SQL TCP/IP configuration,
NAT/port forwarding, VPN routes and the cloud backend's outbound IP.

## 4. Deploy the backend to the chosen cloud

The package can be deployed to any provider supporting Docker containers or
.NET 8 applications.

### ZIP/runtime deployment

1. Extract or upload `backend/backend-app.zip`.
2. Select the .NET 8 runtime.
3. Set the start command to:

```sh
dotnet Api.dll
```

4. Configure these environment variables:

| Name | Value |
| --- | --- |
| `SQLSERVER_CONNECTIONSTRING` | Remote Microsoft SQL Server connection string |
| `Jwt__Key` | Random production secret of at least 32 bytes |
| `Jwt__Issuer` | `KsbPr` |
| `Jwt__Audience` | `KsbPrUsers` |
| `CORS_ALLOWED_ORIGINS` | Final frontend HTTPS origin without trailing slash |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ASPNETCORE_HTTP_PORTS` | Cloud container port, normally `8080`; omit under IIS |
| `SKIP_DB_BOOTSTRAP` | `true` after controlled SQL deployment |
| `SECOND_SUPERADMIN_EMAIL` | `swaraj.khalate@ksb.com` |
| `SECOND_SUPERADMIN_PASSWORD` | `Swaraj@5999@Fiedl` |
| `SECOND_SUPERADMIN_NAME` | `Swaraj Khalate` |
| `SECOND_SUPERADMIN_MOBILE` | `8793535999` |

5. Configure the platform health check as `/health`.
6. Verify `https://<backend-domain>/health` returns `{"status":"ok"}`.

### Docker deployment

The backend source includes a production Dockerfile. Build and push it to the
chosen cloud container registry, then deploy port 8080 with the same environment
variables.

## 5. Deploy the frontend to the chosen cloud

1. Upload or extract `frontend/frontend-app.zip`.
2. Select Node.js 22.
3. Set environment variables:

```text
PORT=8080
API_ORIGIN=https://<backend-domain>
```

4. Set the start command:

```sh
node server.js
```

5. Configure the platform health check as `/health`.
6. Verify `/runtime-config.js` contains the correct backend HTTPS URL.
7. Confirm backend `CORS_ALLOWED_ORIGINS` exactly matches the frontend origin.

## 6. Project superadmin login

```text
Name: Swaraj Khalate
Email: swaraj.khalate@ksb.com
Mobile: 8793535999
Password: Swaraj@5999@Fiedl
```

Change the initial password after the first production sign-in.

## 7. Production checks

1. Backend and frontend must use HTTPS.
2. SQL Server must accept connections only from approved backend IPs or VPN.
3. Do not put the database connection string in frontend configuration.
4. Store connection strings and JWT keys in the cloud secret manager.
5. Configure persistent/shared storage for uploaded files; container local
   storage is not durable.
6. Run authentication, customer, invoice, reporting and mobile API tests.
7. Configure database backups, retention and restore testing on the Microsoft
   SQL Server.
8. Monitor backend `/health`, logs, SQL connection failures and disk capacity.

## Optional: run applications on a local Microsoft Windows Server

The same builds can run on Windows Server:

1. Install SQL Server 2022, the .NET 8 Hosting Bundle, IIS and Node.js 22.
2. Run both database SQL scripts.
3. Extract backend ZIP and host it in IIS with an Application Pool using
   **No Managed Code**.
4. Set `SQLSERVER_CONNECTIONSTRING` and the other backend variables at IIS/site
   level.
5. Extract frontend ZIP and run `node server.js` as a Windows Service, optionally
   reverse-proxied through IIS.
6. Configure HTTPS and set CORS/API origins exactly as described above.
