# Container images

Seven images, each built from the repository root (D-101). Nothing is published yet: build them from a clone.

```sh
docker build -f deploy/images/db/Dockerfile   -t pulaski-db .
docker build -f deploy/images/api/Dockerfile  -t pulaski-api .
docker build -f deploy/images/web/Dockerfile  -t pulaski-web .
docker build -f deploy/images/edge/Dockerfile -t pulaski-edge .
docker build -f deploy/images/demo-setup/Dockerfile -t pulaski-demo-setup .
docker build -f deploy/images/demo-history/Dockerfile -t pulaski-demo-history .
docker build -f deploy/images/process-simulator/Dockerfile -t pulaski-process-simulator .
```

They build with the SDK `global.json` pins and run on the matching .NET runtime, as the image's non-root `app` user. CI builds all seven
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

## `pulaski-demo-setup`: the demo only

`tools/Pulaski.DemoSetup`, run once by the root `compose.yaml`. Through the API it adds the edge node `DEMO-EDGE`, a Sparkplug B connection
to the simulators' broker, the machine profile `PACKML-SIM` (from `deploy/simulator/packml-simulator-profile.csv`, built into it) on the
three simulated machines, their tags (state, unit mode, speed; counts at the saw and packer; the extruder's process values) and the edge's user `demo-edge`; and,
for live control charts, characteristics `MELT-TEMP` and `PROFILE-WIDTH` with a specification, manual limits and tag bindings, process
`DEMO-EXTRUDE` and work order `DEMO-0001`, dispatched with its job started on EXT-01. A failure ends it with exit code 1. It writes the edge's sign-in to `/shared/edge.env` (a volume the
edge container reads), then resets and starts the simulated machines. The administrator's password is temporary, so it sets one of its own
for a moment and gives the temporary one back; if the administrator no longer has it, someone has signed in and it changes nothing.

| Variable | Meaning |
| --- | --- |
| `PULASKI_ADMIN_PASSWORD` | The demo administrator's temporary password (required). `PULASKI_ADMIN_USERNAME`, default `admin`. |
| `PULASKI_API_URL`, `PULASKI_EDGE_API_URL` | The API as the setup and as the edge reach it (default `http://api:8080/`). |
| `PULASKI_SITE` | The demo site's code, default `PLT1`. |
| `PULASKI_MQTT_HOST`, `PULASKI_MQTT_PORT` | The simulators' broker, default `broker:1883`. |
| `PULASKI_EDGE_FILE` | Where the edge's sign-in goes, default `/shared/edge.env`. |

## `pulaski-demo-history`: the demo only

`tools/Pulaski.DemoHistory`, run once by the root `compose.yaml` after the demo setup. It gives the demo plant its previous shift and the
current one so far, as if people and machines had recorded them: it runs the application's own command handlers against the database (as
the API's login, with the clock set to each moment), so the rows and events are what the API would write. It adds a shift schedule
`DEMO-3X8` (day 06:00, evening 14:00, night 22:00, crews A, B and C) that the site follows, users and people for each crew (temporary password
`Pulaski-demo-2`), andon call types, edge node `LINE2-EDGE` with EXT-02's tags, process `DEMO-PROFILE` (EXT-02 then PACK1) with BOMs, and
EXT-02's control limits; then work orders `HIST-1001`…`HIST-1006`, production and scrap reports (one corrected, one reversed), resin lots
received, consumed, split, spilled, held, released, rejected and shipped, EXT-02's machine states, stops, counts and alarms, quality results
with SPC alerts, andon calls, labor with breaks, an approval and a correction, and handover notes. EXT-01 runs the live job `DEMO-0001`, so it
only gets people, calls and notes. A plant that has `DEMO-3X8` already is left alone; a failure ends it with exit code 1.

| Variable | Meaning |
| --- | --- |
| `PULASKI_CONNECTION` | The database as the API's login, such as `Host=db;Database=pulaski;Username=pulaski_api;Password=…` (required). |
| `PULASKI_SITE_ZONE` | The demo site's time zone, default `America/Chicago` (as `db/seed-demo.sql`). |

## `pulaski-process-simulator`: the demo only

`tools/Pulaski.ProcessSimulator`, started with the other simulators (`deploy/simulator/compose.yaml`, service `extruder-process`). A Sparkplug B
edge node (`ExtruderProcess`, device `EXT-01-EXT`) publishing `Process/MeltTemperature` (215 °C, sigma 1.2) and `Process/ProfileWidth`
(42.00 mm, sigma 0.04) with normal noise, and for the last `PULASKI_SIM_SHIFT_MINUTES` (10) of every `PULASKI_SIM_SHIFT_EVERY_MINUTES` (30; 0 =
never) the melt temperature `PULASKI_SIM_SHIFT_SIGMA` (1.5) sigma high. It answers rebirth requests and leaves a death certificate.
`PULASKI_MQTT_HOST`/`PORT` (default `broker:1883`), `PULASKI_SIM_INTERVAL_SECONDS` (2), `PULASKI_SIM_SEED` for repeatable noise.
