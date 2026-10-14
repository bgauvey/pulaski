# Pulaski — Domain Model Decisions

Oct 12, 2026 · Bill Gauvey

## Decision log

This doc tracks every domain-model decision for Pulaski. **Decided** items are settled; **Proposed** items are working assumptions until confirmed. Each section below holds the detail.

| ID | Decision | Status | Date |
| --- | --- | --- | --- |
| D-001 | License: Apache 2.0 with DCO sign-off | Decided | 2026-10-12 |
| D-002 | Storage: PostgreSQL only, no TimescaleDB or SQL Server | Decided | 2026-10-12 |
| D-003 | Order hierarchy: manufacturing order, work order, job, run; orders created in-app or imported from ERP | Decided | 2026-10-12 |
| D-004 | Full append-only event log for audit, with adapters for streams such as Kafka | Decided | 2026-10-12 |
| D-005 | Machine states and events map closely to PackML (ISA-TR88.00.02) | Decided | 2026-10-12 |
| D-006 | All ID columns are UUIDv7 | Decided | 2026-10-12 |
| D-007 | Hybrid persistence: event and relational state written in one transaction, not full event sourcing | Proposed | 2026-10-12 |
| D-008 | CloudEvents as the event envelope; transactional outbox feeds adapters | Proposed | 2026-10-12 |
| D-009 | Activities modelled as job types (production, changeover, cleaning, trial, maintenance) | Proposed | 2026-10-12 |
| D-010 | History (state changes, samples, lot events, counts) is time-partitioned; current records such as lots and jobs stay relational | Decided | 2026-10-12 |
| D-011 | UI: Blazor Web App (interactive server) that talks only to the API | Decided | 2026-10-12 |
| D-012 | Data access: Dapper with parameterized SQL; reads are SELECTs (functions only for complex reused queries), writes run in the command handler's transaction; no procedures from the app | Decided | 2026-10-12 |
| D-013 | API: CQRS with separate Domain, Application, Contracts, Infrastructure and Api projects | Decided | 2026-10-12 |
| D-014 | Scheduling: the planner owns line and sequence; Pulaski projects times forward through the calendar. No solver, no automatic assignment | Decided | 2026-10-13 |
| D-015 | Changeovers: auto-placed placeholders by default; a per-site policy switches to scheduler-placed changeover jobs | Decided | 2026-10-13 |
| D-016 | Run standards: one line/material row holds a calculated rate and an optional modified rate; a modified rate is the gold standard | Decided | 2026-10-13 |

## Order hierarchy

Orders follow ISA-95 Part 4, with planned work separated from actual execution (D-003).

```
Manufacturing Order   what to make, for whom, by when
 └─ Work Order        a portion, routed to an area or line
     └─ Job           one dispatchable unit on one line or unit
         └─ Run       actual execution: start, stop, counts, lots
```

| Level | ISA-95 equivalent | Notes |
| --- | --- | --- |
| Manufacturing order | Operations request | Optional; a standalone work order is valid |
| Work order | Work request | Owns planned quantity and window |
| Job | Job order | The level the scheduler places and operators receive |
| Run | Job response | A job may execute in several runs (interruptions, shifts, resumes); counts, downtime and lots attach here |

- **Two sources, one model:** orders are scheduled in the app or imported from the ERP. Each carries an origin (`Erp` or `Local`), plus an external key and version for ERP records, so imports are idempotent.
- **Field ownership (proposed):** for ERP orders the ERP owns product, quantity and due date; Pulaski owns scheduling, dispatch and status. Local orders own everything.
- **Status rollup (proposed):** jobs drive work order status; work orders drive manufacturing order status.
- **Job types (D-009, proposed):** production, changeover, cleaning or purge, trial, and maintenance window, so non-production activity is planned on the same timeline.

## Event log, audit and streaming

Every business change is recorded as an immutable event in PostgreSQL; the same rows feed external streams through adapters (D-004, D-007, D-008).

**Write path**

1. A command updates relational state and appends its event in one transaction.
2. The event row doubles as a transactional outbox.
3. A relay worker reads new events in order and passes them to each configured sink, tracking a checkpoint per sink.
4. Sinks publish at-least-once; consumers deduplicate on event `id`.

**Envelope (CloudEvents)**

| Field | Purpose |
| --- | --- |
| `id` | UUIDv7 |
| `type` | Versioned name, e.g. `pulaski.job.started.v1` |
| `source` | Site and emitting edge node or service |
| `subject` | Aggregate ID: job, lot or equipment |
| `time` | When it happened on the floor |
| `sequence` | Per-aggregate order |
| `actor` | User, service or device, with on-behalf-of |
| `correlationid`, `causationid` | Links a chain of events to its originating command |
| `reason` | Optional reason code or text |
| `data` | Versioned payload; master-data changes carry before and after values |

**Audit guarantees**

- Application role has INSERT-only rights on event tables; a trigger rejects UPDATE and DELETE. Corrections are new events referencing the original.
- Optional per-site hash chain for tamper evidence.
- Event tables are time-partitioned; audit events are archived, never dropped by retention.

**Adapters**

- `IEventSink` interface: publish a batch, acknowledge, checkpoint.
- First-party sinks: Kafka (also Azure Event Hubs and Redpanda), MQTT for Unified Namespace, webhooks.
- Kafka partition key is `subject`, preserving order per job, lot or equipment.
- Payload schemas published as JSON Schema; the version lives in the event type.
- Future option: Postgres logical replication through Debezium, with the same envelope.

**Boundary:** high-rate telemetry (process values) is stored in separate partitioned tables and does not pass through the audit log.

## Machine state model (PackML)

Machine states and events follow PackML closely, so controls engineers recognise the model and any PackML machine binds without translation (D-005).

- **States:** the 17 PackML states as a fixed enum, unrenamed.
- **Unit modes:** Production, Maintenance, Manual and user-defined modes, tracked alongside state. By default, non-Production time is treated as planned time and excluded from OEE.
- **OEE categories (configurable per site):** each state rolls up to a reporting category. The raw PackML state is always stored, so a policy change recalculates OEE without data loss.

| Default category | PackML states |
| --- | --- |
| Producing | Execute |
| Waiting, external | Suspending, Suspended, Unsuspending |
| Held, internal | Holding, Held, Unholding |
| Stopped or faulted | Stopping, Stopped, Aborting, Aborted, Clearing |
| Ready or transitioning | Idle, Starting, Resetting, Completing, Complete |

**Events and source PackTags**

| Event | PackTags |
| --- | --- |
| `equipment.state.changed` | `Status.StateCurrent`, `Status.UnitModeCurrent` |
| `equipment.mode.changed` | `Status.UnitModeCurrent` |
| `equipment.stop.reason` | `Admin.StopReason` |
| `equipment.alarm.raised` / `.cleared` | `Admin.Alarm[]`, `Admin.Warning[]` |
| `production.count.reported` | `Admin.ProdProcessedCount`, `Admin.ProdDefectiveCount`, `Admin.ProdConsumedCount` |
| `equipment.speed.changed` | `Status.CurMachSpeed`, `Status.MachSpeed` |

- **Stop reasons:** the PackML first-out reason maps into Pulaski's reason tree; operators can confirm or override, and both values are kept.
- **Two binding paths at the edge:** native PackTags where the PLC exposes them, or a rules engine that synthesises the nearest PackML state from discrete signals for non-PackML equipment. Downstream, both look identical.

## Identifiers (UUIDv7)

Every primary and foreign key is a UUIDv7 (D-006).

- **Generated in the application** with `Guid.CreateVersion7()` (.NET 9 and later), so edge nodes create IDs offline without collisions. Generation sits behind an `IIdGenerator` with an injected clock for deterministic tests.
- **Database default** of `uuidv7()` (PostgreSQL 18) as a backstop for manual inserts.
- **Stored as native `uuid`**, never text. Postgres compares bytewise, which matches UUIDv7 time ordering and keeps index inserts append-friendly.
- **Partitioned tables** use a composite key such as `(id, occurred_at)`, because Postgres requires the partition key in every unique constraint.
- **ID order is not business order.** Ordering comes from `occurred_at` and the per-aggregate `sequence`.
- **No parsing timestamps out of IDs;** `created_at` and `occurred_at` are explicit columns.
- **Business keys stay separate:** ERP order numbers, lot numbers and equipment tags are their own unique columns.
- **Sort in SQL, not in memory;** .NET `Guid` comparison does not follow RFC byte order.

## Storage

PostgreSQL holds both relational and time-series data; there is no TimescaleDB and no SQL Server (D-002).

- **Rule (D-010):** history is time-partitioned, current state is relational. If something is referenced, it is relational; if it happened, it is time-partitioned.
- **Relational tables:** equipment, materials, specs, reason codes, and the current record of each lot, manufacturing order, work order, job and run. Lots stay relational because genealogy, quality holds and complaints reference them for years.
- **Time-partitioned tables:** machine state changes (PackML transitions, modes, stop reasons); job and run state changes; spec and quality samples; lot events (created, consumed, transferred, split, merged, status changed); production counts; the audit event log; and continuous process values (assumed, with shorter retention).
- **Techniques:** native range partitioning by day or month, BRIN indexes on time columns, rollup tables maintained by a Pulaski worker, and retention by dropping old telemetry partitions. `pg_partman` is optional for partition automation.
- **Schemas:** relational and time-series data in separate schemas, so a large site can later move telemetry to its own instance.

## Scheduling

The plan is a sequence; times are calculated. The planner decides which line a job runs on and in what order. Pulaski works out when each job will happen and keeps that answer current from live production (D-014, D-015, D-016).

**What the planner owns**

- The line: `job.equipment_id`, which must be schedulable.
- The order: `job.sequence_rank`, a fractional rank, so a move writes one row.
- Pins: `is_locked` keeps a job's position when others are resequenced; `earliest_start` holds a job until a time, for example when material arrives.

**What Pulaski calculates: the timeline projection**

A pure function in `Pulaski.Domain.Scheduling` takes one line's open jobs in sequence and returns a segment for each job and each changeover, with warnings. It has no I/O, makes one pass and is recalculated on every read, so the timeline is never stale.

1. The cursor starts at *now*. The running job is projected from its remaining quantity (planned minus good) at its run rate.
2. Production time = remaining quantity ÷ the line/material run rate (D-016).
3. Time accrues only inside staffed shift instances, minus calendar exceptions. Shift instances come from the nearest ancestor of the line that has any. If no instances exist, the line is treated as running 24/7 and the timeline says so. Past the last generated shift, the projection continues around the clock and flags the segments.
4. Changeover time between consecutive production jobs comes from the changeover matrix (below).
5. Warnings never block: late against the work order due date, no run standard, unit-of-measure mismatch, a missing changeover in scheduler mode, no duration on a non-production job, and beyond the calendar.

The projection is not stored. **Publish** copies the projected times into `job.planned_start` and `planned_end` as the baseline, so schedule adherence (baseline against projection against actual) is a KPI next to OEE.

**Boundary with the charter non-goal (D-014).** Pulaski never picks a line, never reorders jobs on its own and never optimises. The non-goal excludes finite-capacity optimisation. A forward pass over a sequence the planner chose is a calculator, not a scheduler.

**Changeovers (D-015).** `core.scheduling_policy.changeover_mode` is set per site:

| Mode | Behaviour |
| --- | --- |
| `auto` (default) | The projection inserts a changeover placeholder wherever the material changes, sized from the matrix. When the next production job is dispatched, the placeholder becomes a real `changeover` job, so its runs and downtime attach somewhere. A changeover job the planner placed explicitly suppresses the placeholder. |
| `scheduler` | Planners place changeover jobs themselves. The projection sizes them from the matrix unless the job has its own `planned_duration`. It warns where the material changes with no changeover job in between. |

`core.changeover_rule` is the matrix. Each side matches a specific material, a material class or anything. A rule may be limited to one line, and the most specific rule wins. Two consecutive jobs on the same material need no changeover. If no rule matches, the capability's `standard_changeover` for the incoming material applies.

**Run standards (D-016).** `core.run_standard` holds one row per line and material:

- `calculated_rate_per_hour` is written by Pulaski only: first from the capability's ideal rate, later from run history.
- `modified_rate_per_hour` is written by a person. While it is set, it is the gold standard: recalculation keeps updating the calculated value, so drift stays visible, but never replaces the modified one.
- `rate_source` is a generated column, `modified` or `calculated`, that tells every reader which value is in force. Clearing the modification returns the row to `calculated`.

A rate rather than a fixed duration is stored, because a job's runtime scales with its quantity. Runtime = quantity ÷ effective rate.

**Build passes**

1. Foundation: decisions, schema, the domain projector, and a read-only timeline API.
2. Commands: create and sequence jobs, lock, run-standard modify and clear, the scheduling policy, dispatch with changeover materialization, and publish.
3. Calculated rates from run history (a worker), with drift against the modified standard.
4. Blazor scheduling board: per-line timeline with drag-to-sequence.

## Application architecture

The UI, the API and the database each have one job, and only the API reaches the database (D-011, D-012, D-013).

```
Pulaski.Web (Blazor)  --HTTP/JSON-->  Pulaski.Api  -->  Application (CQRS)  -->  Infrastructure (Dapper, parameterized SQL)  -->  core / evt
```

**UI (D-011).** A Blazor Web App in interactive server mode. It calls the API through a typed client that shares DTOs from `Pulaski.Contracts`, and never holds a connection string. Prerendering is off, so each page loads its data once.

**Data access (D-012).** Dapper over Npgsql replaces EF Core. The SQL is parameterized, lives next to the code that uses it, and is versioned and tested with that code.

- **Reads: direct SELECT by default.** Each feature has a `*Queries` class in Infrastructure holding its SQL as `const string`. Each query opens a connection, takes no lock and maps rows straight to Contracts DTOs (snake_case columns to PascalCase properties).
- **Reads: functions for a few cases.** Use a function only when the logic is complex, reused and better kept close to the data. Candidates are the recursive genealogy trace (`core.trace_forward(@lotId)`), the effective spec or control limit for a characteristic and product at a time, and the scoped permission check. Write them as `LANGUAGE sql STABLE` where possible so the planner can inline them, and call them with `SELECT * FROM core.trace_forward(@lotId)` as plain command text.
- **No `CALL` from the application.** PostgreSQL procedures don't return result sets; they need OUT parameters or refcursors, which Dapper handles awkwardly. Since Npgsql 7, `CommandType.StoredProcedure` sends `CALL`, so it is not used at all. Procedures are kept for set-based maintenance jobs such as partition creation or rollup rebuilds.
- **Writes: direct SQL in the command handler's transaction.** A handler opens an `IUnitOfWork` and reads the aggregate `FOR UPDATE`, or `FOR SHARE` for a referenced parent. It checks the version, applies the domain rules in C#, writes through a `*Repository`, appends the event through `IEventLog`, and commits. The state change and its event commit together (D-007). The application generates the UUIDv7s (D-006), chooses the event type and builds the CloudEvents envelope. Domain rules therefore live only in C#, where they are unit-tested, not split between C# and PL/pgSQL.
- **Concurrency.** The version is `xmin` (`xmin::text::bigint AS version`). The caller sends the version it read; a mismatch is a 409. Unique, foreign-key and check violations are mapped to 409 and 422 in Infrastructure.
- **Privileges.** `db/app-role.sql` creates the NOLOGIN group role `pulaski_app`, which a deployment's login joins. It has SELECT, INSERT and UPDATE on `core`, and only SELECT and INSERT on `evt.event`, so the log is append-only by privilege as well as by trigger. Nothing has DELETE. The integration tests connect as a member of this role.

**API (D-013).** Separate commands and queries with hand-written handlers, and no mediator library.

| Project | Holds | Depends on |
| --- | --- | --- |
| `Pulaski.Domain` | Entities and rules that need no I/O: `Equipment` (hierarchy placement, owning site), `WorkOrder` (status transitions) | — |
| `Pulaski.Contracts` | Request and response DTOs shared with the UI | — |
| `Pulaski.Application` | Commands, queries, handlers, read-store and repository interfaces, unit of work, event envelope, validation | Domain, Contracts |
| `Pulaski.Infrastructure` | Per-feature `*Queries` (reads) and `*Repository` (writes) with their SQL, the transaction session, the event log, PostgreSQL error mapping | Application |
| `Pulaski.Api` | Minimal-API endpoints, ProblemDetails mapping | Application, Infrastructure |
| `Pulaski.Web` | Blazor UI | Contracts |

- Reads and writes are separate interfaces: queries take no locks and no transaction, so the query side can later move to read models or a replica without touching commands.
- Errors use RFC 9457 problem responses: 400 validation, 404 not found, 409 version conflict or duplicate key, 422 business rule.
- Until OIDC sign-in is wired up, the actor recorded on events is `anonymous`.

## Open questions

These need an answer before the related area is built; tick each one as it moves into the decision log.

**Orders**

- [ ] Does a typical ERP send only manufacturing orders, or work orders too?
- [ ] Can one work order span multiple lines, or does splitting happen only at the manufacturing order level?
- [ ] Should an ERP change after release update the order automatically, or flag it for a planner to accept?

**Events and audit**

- [ ] Should reads of sensitive data (traces, reports) be audited, or only changes?
- [ ] Is telemetry streamable to Kafka in v1, or only business events?

**Machine state**

- [ ] How is a line's state derived from its machines: constraint machine, worst state, or configured per line?
- [ ] On target lines, are PackML-native PLCs common, or will synthesised states be the norm?

**Storage**

- [ ] Confirm continuous process values (temperatures, speeds, pressures) are time-partitioned with shorter retention.

**Genealogy**

- [ ] Is a fixed residence time per line and product good enough, or must it vary with line speed?
