# Container images

Four images, each built from the repository root (D-101). Nothing is published yet: build them from a clone.

```sh
docker build -f deploy/images/db/Dockerfile   -t pulaski-db .
docker build -f deploy/images/api/Dockerfile  -t pulaski-api .
docker build -f deploy/images/web/Dockerfile  -t pulaski-web .
docker build -f deploy/images/edge/Dockerfile -t pulaski-edge .
```

They build with the SDK `global.json` pins and run on the matching .NET runtime, as the image's non-root `app` user. CI builds all four
on every pull request.

## `pulaski-db`: create or upgrade the database

A one-shot container: it creates the database if needed, loads the schema (a new database) or applies the migrations (an existing one),
loads the starter units and reasons, creates or updates the API's login as a member of `pulaski_app`, and exits. Run it before the API,
on every upgrade too; it is re-runnable.

| Variable | Meaning |
| --- | --- |
| `PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD` | A PostgreSQL 18 login that can create databases and roles; it owns the schema. |
| `PULASKI_DATABASE` | Database name, default `pulaski`. |
| `PULASKI_APP_LOGIN` | The API's login, default `pulaski_api`. It gets exactly the application's privileges (`db/app-role.sql`). |
| `PULASKI_APP_PASSWORD` | Its password (required), set again on every run. |
| `PULASKI_SEED` | `demo` loads the demo site, lines and materials into a new database; `none` (default) loads starter data only. |

## `pulaski-api`

Listens on port 8080 (HTTP; put TLS in front of it). Settings are the API's configuration as environment variables:

| Variable | Meaning |
| --- | --- |
| `ConnectionStrings__Pulaski` | For example `Host=db;Database=pulaski;Username=pulaski_api;Password=…`: the login `pulaski-db` made. |
| `Security__SigningKey` | At least 32 characters, kept secret: it signs every sign-in. |
| `Security__BootstrapAdmin__Username`, `__Password` | The first administrator, made when there are no users; the password is temporary and is changed at the first sign-in. |

The API won't start until the database is up to date, so run `pulaski-db` first. Mount a volume at `/home/app/.aspnet/DataProtection-Keys`
so its keys survive a new container.

## `pulaski-web`

The UI, on port 8080 (HTTP; put TLS in front of it). It needs `PulaskiApi__BaseUrl`, such as `http://api:8080/`. Mount a volume at
`/home/app/.aspnet/DataProtection-Keys`, or everyone is signed out whenever the container is replaced.

## `pulaski-edge`

The edge node (`deploy/edge/README.md`). Its buffer and OPC UA certificates are in `/data`: mount a volume there, so what wasn't forwarded
survives a new container. Settings: `Edge__ApiUrl`, `Edge__EdgeNodeId`, `Edge__Username`, `Edge__Password` (from the edge node's **Setup**
tab), and any connection secrets.
