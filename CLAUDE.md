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
- **User guide.** `docs/user_guide.md` is the end-user guide, for readers from novice to expert. Update it in
  the same change as anything a user can see: a new or changed screen, step, button label, message, rule or
  warning. Each topic keeps numbered steps for beginners and an "In detail" part for experienced users.
  Keep the "What Pulaski doesn't do yet" section current: add known gaps and bugs, remove them when fixed.
  Describe only what the app does today, checked against the code, and bump the "Last updated" date.

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
- Identity (D-037, proposed): local sign-in now (`pulaski-local`, PBKDF2 hashes in `sec.local_credential`, API-issued
  HMAC JWT from `Security:SigningKey`); OpenID Connect (Okta, Entra ID, AD FS, Keycloak) later as another bearer scheme.
  Users are keyed by issuer + subject. The UI keeps the token in a cookie session and sends it on every API call.
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
- BOMs (D-025, proposed) are single level, per material and optionally per process (items name the consuming
  operation), versioned like processes. Order creation plans each job's components (`job_material`).
- Inventory (D-027, proposed): Pulaski keeps lot balances at `holds_inventory` locations in a site, in the
  material's base unit, never below zero; the ERP stays the financial book of record. Every movement writes
  `core.lot` + one `ts.lot_event` row + one event. Consumption and production are declared against a job (lock
  the job, then lots) with job-level `declared` genealogy. Materials that aren't lot-tracked keep one bulk lot per
  location (`@<location code>`).
- Inventory lookups (D-029) and completion (D-030, proposed): shortages are a pure calculator over open jobs' remaining
  components; lots split, merge (lock lots in id order, in SQL), ship, correct and reverse only their latest movement;
  counts snapshot a location and post differences. Job declarations, allocation and labels are deferred.
- Indistinguishable lots (D-031, proposed): equipment with `commingles_lots` (needs `holds_inventory`) keeps one balance per
  material; every arrival merges into it (`Commingling.MergeArrivalAsync`), a numbered receipt first kept as its lot.
  Balances (`is_bulk`) have a NULL `lot_number`; name them by location (`Lot.Name`, `InventoryView.LotLabel`).
- Location and expiry rules (D-032, proposed): `single_material` admits another material only when the one there is at
  zero (`InventoryRules.EnsureAcceptsAsync`, locks the location); a location with stock can't be deactivated; shelf life
  sets new lots' expiry; expired lots can't be consumed, shipped or merged (`EnsureNotExpired`) until corrected.
- Materials management (D-033, proposed): for `origin = 'erp'` the ERP owns code, name, base unit and type (changed only
  by the idempotent import, matched on external system + key); Pulaski owns the rest. Base unit is fixed once in use, a
  local code too; lot tracking changes only with nothing on hand; deactivation is always allowed and reports what still
  uses it. Type is a label with no rules. Classes are flat, code fixed, switched off rather than deleted.
- Capabilities and bulk import (D-034, proposed): capabilities change on the material page, locking the line first (D-017);
  periods never overlap and are ended, not deleted. Adding one creates the run standard from the ideal rate; a new ideal
  rate recalculates it while its basis is `ideal_rate` (`RunStandard.FollowIdealRate`). CSV import is all or nothing
  (`?dryRun=true` checks); `MaterialCsv` yields source-neutral `MaterialImportRow`s for `ImportMaterialsCommand`, the
  seam future adapters plug into.
- Execution (D-035, proposed): start opens a `core.job_run`, pause/complete end it; one run per line. Starting firms a
  dispatched order; in progress/completed follow the jobs. Downstream jobs are *ready* when process links are met
  (`JobReadiness`), never auto-started. Reports go into a run (open, or an ended one until the order closes), capped by
  `RateCap` (elapsed x ideal rate x site tolerance); consumption needs a run and warns beyond plan (`ConsumptionCheck`).
- Batches (D-036, proposed): `core.material` batch size/rule/tolerance (`Batching`, rule defaults from type: finished →
  exact). Orders and jobs are whole batches; reports list batch weights, one lot per good batch. A partial batch only with a
  pause/complete reason (or into a run that ended with one), one per run (`core.partial_batch`); exact rule → lot on hold
  until approved (release) or rejected (write off, good → scrap).
- Roles and permissions (D-037, proposed): permissions are constants in `Pulaski.Domain.Security.Permissions` (catalog
  with areas; mirrored in `Contracts.Security.PermissionCodes`; synced into `sec.permission` at API start). Roles are data
  (`sec.role`, `sec.role_permission.is_granted`), maintained on the Roles/Permissions tabs of Configure → Users; only `administrator` is in
  code (all permissions, everywhere, unchangeable); `Roles.Defaults` seeds a new database. Every other role is held at a
  site; every changing endpoint carries `.RequirePermission(permission, Site.…)`.
- Viewing (D-039, proposed): 13 view permissions (`*.view`) decide screens and data; a change brings its views
  (`Permissions.Implied`). Reads use `.RequireAny(site, views…)`, `.RequireAnyAtSite` (no site = every site) or
  `.RequireAnyView()` for lookups; lists filter with `.At(Visible.SitesAsync(...))`. Web pages carry
  `[RequiresView(...)]`, checked in `MainLayout` (not found, or the no-access panel); cross-screen links use `ScreenLink`.
  Every user keeps a role; a held role can't be switched off.
- Shift schedules (D-038, proposed): `core.shift_schedule` per site (cycle of 1–56 days from `cycle_start`, shifts as one
  `jsonb` document); equipment `calendar_mode` inherit / schedule / around_the_clock, nearest non-inherit ancestor wins.
  Shift times are computed on read (`ShiftSchedule.Expand`, site time zone, DST-aware), never stored; `shift_instance` is
  only for shifts as worked (planned). Permission `shifts.manage`; equipment's setting saves via `PUT /equipment/{id}/calendar`.
- Labor (D-040, proposed): `core.person` (not necessarily a user; `app_user_id` to clock on oneself), `core.crew`; shifts name
  their crew (`ShiftDto.Crew`). `core.labor_session` direct/setup/indirect/training with a share; direct time is shared
  out to runs by overlap on read (`LaborRules.Attribute`); each session stores its shift (`EquipmentShifts.ShiftAt`).
  `core.labor_approval` locks a shift; corrections are new sessions (`corrects_session_id`, `is_void`). Clock-on locks
  the person; crew check-in locks people in id order in SQL. No background service.
- Shift overrides (D-042, proposed): one production day's change to a schedule's shift (other times, off, or an extra shift) in
  `core.shift_schedule.overrides` (`jsonb`, saved whole); `ShiftSchedule.Expand` applies them everywhere. Locked once that shift's
  labor is approved. Not calendar exceptions, which take time out of everything below a piece of equipment.
- Site settings (D-043, proposed): Configure → Site settings holds changeover mode, reporting tolerances (now versioned) and
  calendar exceptions (switched off with `is_active`, never deleted); `site_settings.view` opens it, `site_settings.manage` changes it.
- Production by shift (D-044, proposed): a count belongs to the shift running on its line at `occurred_at` and to that shift's
  production day (`ShiftProduction.Total`), worked out on read; outside shifts, the site-local day. Rows reconcile to the runs.
- Rates from run history (D-045, proposed): `RunHistoryRate.Calculate` (good ÷ run hours, short/empty/unconvertible and
  Tukey-fence outliers left out, at least 5 runs and 8 h) is reviewed, then accepted (re-checked under the lock, 409 if changed)
  as the calculated rate; a modified rate stays in force. No worker.
- Links in the projection (D-046, proposed): `LinePlanLoader` loads a line with the lines its jobs follow; every projection goes
  through `TimelineMapping.Project` → `LinkedProjection` (repeat until settled). Jobs wait for after-start/after-end plus lag;
  firmed jobs keep their time (`link_not_met`), circular waits warn `link_cycle`. Nothing is reordered.
- Breaks (D-047, proposed): `core.labor_break` per person (paid/unpaid), never a change to sessions; worked hours =
  session time outside the person's breaks × share (`LaborRules.WorkedHours`, also in `Attribute`). A break starts inside a
  session, takes its shift, locks with approval (corrections like sessions) and ends when the person leaves their last line.
- Qualifications (D-048, proposed): per-site `core.qualification`; grants (`person_qualification`) for inclusive site-local days,
  renewed by a new grant, revoked not deleted; `equipment_qualification` applies down the closure. Clock-on/check-in warn
  (`QualificationRules.Missing`), never refuse.
- Scheduling writes lock lines, then the work order, then jobs (D-017), so they serialize without
  deadlocks. Read the plan inside the transaction after the lock.
- Outbox relay reads by `(tx_id, position)` below `pg_snapshot_xmin(pg_current_snapshot())`.

## Database (`db/schema-draft.sql`)

- `docs/database.md` documents every table and column. Update it in the same change as the schema.
- Every schema change goes in `db/schema-draft.sql` **and** a new `db/migrations/NNNN_description.sql` (D-028):
  re-runnable (`IF NOT EXISTS`, `CREATE OR REPLACE`, `DO` blocks), no `BEGIN`/`COMMIT`. Add its name to the
  `public.schema_migration` insert at the end of `schema-draft.sql` and set `DatabaseMaintenance.RequiredMigration`.
- New partitioned tables need nothing else: the API keeps every range-partitioned `ts`/`evt` table three months
  ahead through `ts.ensure_partitions`.

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
- Existing database after pulling schema changes: `db/migrate.sh pulaski` (the API won't start until it's current).
- Load schema: `psql -d pulaski -v ON_ERROR_STOP=1 -f db/schema-draft.sql`, then
  `db/app-role.sql` (re-runnable), then `db/seed-demo.sql` (empty database only), then optionally
  `db/seed-materials.sql` (200 demo materials in 11 classes; re-runnable), then `db/seed-units.sql` (standard units
  and demo material conversions, D-026; re-runnable), then `db/seed-reasons.sql` (starter adjustment and hold
  reasons, D-027; re-runnable).
- Layout: production projects in `src/`, test projects in `Tests/` (shared in-memory fakes in
  `Tests/Pulaski.Testing`); `.editorconfig`,
  `Directory.Build.props`, `Directory.Packages.props` (central package versions) and
  `nuget.config` sit at the repo root.
- Build and test: `dotnet build src/Pulaski.slnx`, `dotnet test src/Pulaski.slnx`.
  `Pulaski.Infrastructure.Tests` creates and drops a throwaway database and a login role that is
  a member of `pulaski_app`, and drives the real handlers through it; override the server
  with `PULASKI_TEST_ADMIN_CONNECTION`.
- First sign-in on an empty database: the API creates `admin` with the temporary password in
  `src/Pulaski.Api/appsettings.Development.json` (`Security:BootstrapAdmin`); change it at the first sign-in.
- Run: `dotnet run --project src/Pulaski.Api` (http://localhost:5080, OpenAPI at
  `/openapi/v1.json`) and `dotnet run --project src/Pulaski.Web` (http://localhost:5090).
