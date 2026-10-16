# Pulaski

Open-source MES for process and semi-continuous manufacturing lines (extrusion, molding,
compounding, high-rate packaging). Pulaski is a working codename. Pre-alpha; APIs will change.

## Ground rules

- **Clean-room.** Pulaski is an independent personal project. Never use, reproduce or reference
  code, schemas, naming or documents from any employer, client or commercial MES/ERP product.
  Public standards and general knowledge only.
- **Source of truth:** `docs/charter.md` (scope, non-goals, architecture) and
  `docs/domain-model-decisions.md` (decision log D-001…). Read the relevant one before designing
  anything. Items marked **Decided** are settled; don't reopen them unless asked.
- When we settle something new, add it to the decision log with the next D-number, and move any
  answered open question into it.
- Respect the v1 non-goals in the charter: not an ERP, not SCADA/HMI/historian, no finite-capacity
  scheduling, no Part 11 features, no multi-tenant SaaS.
- Every commit is signed off (DCO): `git commit -s`. License is Apache 2.0.

## Stack

- C# on the current .NET LTS; ASP.NET Core for APIs; a worker service for the edge node.
- Nullable reference types on; treat warnings seriously.
- PostgreSQL 18 only. Native range partitioning, BRIN indexes and worker-maintained rollups.
  No TimescaleDB, no SQL Server.
- Data access is Dapper over Npgsql (D-012); no EF Core.
  - Reads: parameterized SELECTs in per-feature `*Queries` classes (SQL as `const string`).
    Use a `LANGUAGE sql STABLE` function only for complex, reused queries (genealogy trace,
    effective spec/control limits, scoped permission check), called as `SELECT * FROM fn(@x)`.
  - Writes: parameterized INSERT/UPDATE in per-feature `*Repository` classes, run by the command
    handler inside one `IUnitOfWork` transaction together with the `IEventLog` append (D-007).
  - Don't use `CALL` from the app. Procedures are for set-based maintenance jobs only. Npgsql's
    `CommandType.StoredProcedure` sends `CALL`, so never use it for functions either.
  - Domain rules live in C# (`Pulaski.Domain`), not PL/pgSQL. The app role (`pulaski_app`,
    `db/app-role.sql`) has SELECT/INSERT/UPDATE on `core`, SELECT/INSERT only on `evt.event`, no DELETE.
- Optimistic concurrency via `xmin` (`xmin::text::bigint AS version`): the handler reads the row
  `FOR UPDATE`, compares the caller's version (409 on mismatch) and writes; no row_version columns.
- API is CQRS (D-013): commands and queries in `Pulaski.Application` with hand-written handlers
  injected into minimal-API endpoints. No MediatR.
- UI is Blazor Web App, interactive server (D-011); it calls the API only, never the database.
- Identity: OpenID Connect (Entra ID, AD FS, Keycloak). Pulaski stores issuer + subject only.
- Machine data: MQTT Sparkplug B and OPC UA, built on the connector SDK.
- Containers for the demo and Linux sites; Windows service option.

## Domain conventions

- Use ISA-95, ISA-88, PackML, B2MML, OPC UA and Sparkplug B terminology
  (equipment, material lot, segment, work order, job response).
- Orders: manufacturing order → work order → job → run (D-003). Counts, downtime, labor and
  lots attach to the run.
- Machine states are the PackML StateCurrent numbers, unrenamed (D-005). Raw state is always
  stored; OEE categories are a per-site mapping.
- Event-first: a command updates relational state and appends a CloudEvents event in the same
  transaction (D-007, D-008). `evt.event` is append-only; corrections are new events.
- Scheduling (D-014…D-016): the planner owns line + `sequence_rank`; times come from the pure
  projector in `Pulaski.Domain.Scheduling`, computed on read and stored only on publish.
  Rates come from `core.run_standard`, where a modified rate beats the calculated one.
- Work orders are planned, dispatched (= published) and firmed; their jobs follow (D-018). Jobs are
  placed only on lines with a capability for their material (D-020); firmed jobs keep their time (D-019).
- A job may be planned to a group (schedulable, not `can_run_job`, e.g. a department) in a window; the floor
  assigns it to a line below that can run it (D-022). Pulaski checks the window and never picks the line.
- Process routes (D-023): versioned templates of numbered operations, each on lines or departments with
  shares totalling 100%, linked after start / after end. Drafts are one `jsonb` document; release writes
  fixed rows for jobs to reference.
- Work orders are made from a released process (D-024, proposed): each operation's equipment gets a planned job
  for its share, at the end of its line, held until the order's start. The order keeps the version and a note.
- Scheduling writes lock lines, then the work order, then jobs (D-017), so they serialize without
  deadlocks. Read the plan inside the transaction after the lock.
- Outbox relay reads by `(tx_id, position)` below `pg_snapshot_xmin(pg_current_snapshot())`.

## Database (`db/schema-draft.sql`)

- `docs/database.md` documents every table and column. Update it in the same change as the schema.

- IDs are native `uuid` holding UUIDv7, generated in the app with `Guid.CreateVersion7()` behind
  `IIdGenerator` (injectable clock). `uuidv7()` is only a DB default backstop (D-006).
- Never sort GUIDs in memory (.NET `Guid` ordering ≠ RFC byte order); sort in SQL. Never parse
  time out of an ID; use `occurred_at` / `created_at`.
- Schemas: `core` = master data and current state; `ts` = time-partitioned history and rollups;
  `evt` = event log/outbox; `sec` = identity and access.
- Rule (D-010): if it is referenced, it is relational (`core`); if it happened, it is
  time-partitioned (`ts`).
- Partitioned tables have a composite PK `(id, <time column>)`. No foreign key ever targets a
  partitioned table; rows pointing into history carry `(id, occurred_at)` without an FK.
- Status/kind columns are `text` + CHECK, not PG enums.
- Business keys (order, lot, equipment codes) are separate unique columns, never PKs.
- Partitions are created ahead by `ts.create_month_partitions(...)`; there is no default partition.

### Proposed, not yet in the decision log

Treat these as working assumptions; confirm with Bill before building anything that hard-codes them.

- One `core.equipment` table for work centers, work units, machines and storage, typed by ISA-95
  level + role, with capability flags (`tracks_state`, `is_schedulable`, `can_run_job`, `holds_inventory`).
- PackML state 0 `Undefined` records no-signal time so it stays visible in OEE.
- `ts.process_value` keys on `(equipment_tag_id, occurred_at)` with no surrogate id or FKs.
- Outbox ordering uses `xid8` transaction ids.

## Code style

- Standard .NET naming and formatting; `.editorconfig` is authoritative once added.
- Small, readable code over clever code. Add or update tests for any behavior change.
- Commit subjects in the imperative mood, under ~72 characters; explain *why* in the body.

## Local dev

- PostgreSQL 18 via Homebrew (`postgresql@18`); database `pulaski`.
- Load schema: `psql -d pulaski -v ON_ERROR_STOP=1 -f db/schema-draft.sql`, then
  `db/app-role.sql` (re-runnable), then `db/seed-demo.sql` (empty database only), then optionally
  `db/seed-materials.sql` (200 demo materials in 11 classes; re-runnable).
- Layout: production projects in `src/`, test projects in `Tests/` (shared in-memory fakes in
  `Tests/Pulaski.Testing`); `.editorconfig`,
  `Directory.Build.props`, `Directory.Packages.props` (central package versions) and
  `nuget.config` sit at the repo root.
- Build and test: `dotnet build src/Pulaski.slnx`, `dotnet test src/Pulaski.slnx`.
  `Pulaski.Infrastructure.Tests` creates and drops a throwaway database and a login role that is
  a member of `pulaski_app`, and drives the real handlers through it; override the server
  with `PULASKI_TEST_ADMIN_CONNECTION`.
- Run: `dotnet run --project src/Pulaski.Api` (http://localhost:5080, OpenAPI at
  `/openapi/v1.json`) and `dotnet run --project src/Pulaski.Web` (http://localhost:5090).
