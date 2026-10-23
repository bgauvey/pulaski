# Pulaski — Domain Model Decisions

Oct 12, 2026 · Bill Gauvey

## Decision log

This doc tracks every domain-model decision for Pulaski. **Decided** items are settled; **Proposed** items are working assumptions until confirmed. Each section below holds the detail.

| ID | Decision | Status | Date |
| --- | --- | --- | --- |
| D-001 | License: Apache 2.0 with DCO sign-off | Decided | 2026-10-12 |
| D-002 | Storage: PostgreSQL only, no TimescaleDB or SQL Server | Decided | 2026-10-12 |
| D-003 | Order hierarchy: manufacturing order, work order, job, run; orders created in-app or imported from ERP (amended by D-018: orders are planned and dispatched, jobs are their pieces) | Decided | 2026-10-12 |
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
| D-015 | Changeovers: auto-placed dynamic placeholders by default, made real when execution starts; a per-site policy switches to scheduler-placed changeover jobs | Decided | 2026-10-13 |
| D-016 | Run standards: one line/material row holds a calculated rate and an optional modified rate; a modified rate is the gold standard | Decided | 2026-10-13 |
| D-017 | Scheduling writes lock the line row first, then the work order, then jobs; jobs are placed before a named job (or at the end) at a midpoint rank, renumbering the line only when ranks run out | Decided | 2026-10-13 |
| D-018 | Work order lifecycle: plan, dispatch (publishes the baseline), firm, execute, complete. Only orders are dispatched and firmed; their jobs follow. Un-dispatch is allowed until the order is firmed | Decided | 2026-10-13 |
| D-019 | Job rules: a locked job never moves; a planned job moves freely; a dispatched job moves only after un-dispatch; a firmed job may change machine but is anchored at its firmed start; a started job can only be cancelled, optionally replaced by a job for the remaining quantity | Decided | 2026-10-13 |
| D-020 | Eligibility: `core.equipment_capability` says which material can run on which equipment, with its ideal rate and crew size (labor estimate). Jobs can only be placed on, moved to or dispatched on an eligible line. The run standard stays the planning rate | Decided | 2026-10-13 |
| D-022 | Group scheduling: equipment can be a planning target (`is_schedulable`) and runs jobs only with `can_run_job`. A job planned to a group (e.g. a department) waits there in a window until the floor assigns it to a line below the group that can run it; Pulaski checks the window, never picks the line | Decided | 2026-10-14 |
| D-023 | Process routes: a process (ISA-95 operations definition) has numbered operations, each run by one or more lines or departments with shares totalling 100%, linked "after start" or "after end" with a lag. Versioned: drafts are edited, released versions are fixed and are what orders are made from. A process makes all materials, or chosen classes and materials; a material may name one of those as its default | Decided | 2026-10-14 |
| D-024 | Work orders are made from a process: the planner picks the material, a released process that makes it, the quantity, start, due date, priority and a note; each operation's equipment gets a planned job for its share, at the end of its line | Proposed | 2026-10-15 |
| D-025 | Bills of materials: single level, one per material and optionally per process, whose items name the operation that consumes them; versioned like processes (draft, released, obsolete). A work order takes the released BOM and plans each job's components. Pulaski owns lots on the floor; the ERP stays the book of record for stock (inventory amended by D-027) | Proposed | 2026-10-15 |
| D-026 | Units: standard units convert within a dimension by fixed factors for every material; a material's own conversions link dimensions and packaging units, and never give two routes. Orders, BOMs and run rates may each use any unit that converts | Proposed | 2026-10-15 |
| D-021 | Scheduling board: Gantt, calendar, Kanban and table views over one live board. Each view edits only what the planner owns (line, sequence, pins, order status); overlaps are reported by the projector and shown on every view; the board re-reads every 10 s | Proposed | 2026-10-14 |
| D-027 | Inventory: Pulaski keeps lot balances at every `holds_inventory` location in a site (stores, staging, feed points), in the material's base unit. Receive, write up, write off, move, hold and release lots; declare consumption into and production from a job, with job-level genealogy. Materials that aren't lot-tracked keep one bulk balance per location. The ERP stays the financial book of record and gets an event for every movement (amends D-025) | Proposed | 2026-10-16 |
| D-028 | Schema changes ship as numbered, re-runnable migrations in `db/migrations`, applied by `db/migrate.sh` and recorded in `public.schema_migration`; `schema-draft.sql` stays the complete schema for new databases. The API refuses to start without the newest migration, and keeps every partitioned history table three months ahead through `ts.ensure_partitions` | Proposed | 2026-10-16 |
| D-029 | Inventory lookups: a material shortage check sets each open job's remaining components, in the order the jobs are needed, against available stock; a movement journal searches every lot movement by time, kind, material, location, job and person. Neither plans purchases nor nets incoming supply (charter: not MRP) | Proposed | 2026-10-16 |
| D-030 | Inventory completion: split, merge and ship lots; correct a lot's details; reverse a lot's latest movement; count a location and post the differences; maintain reason codes; stock on a past date and an aging report; CSV export. Pallets, labels and lot certificates are deferred | Proposed | 2026-10-16 |
| D-031 | Indistinguishable lots: a location that holds inventory can mix lots (`commingles_lots`), like a silo or tank. Everything that arrives there joins one balance per material, with no lot number; a numbered receipt is kept as its own lot and merged in, so genealogy still reaches the supplier lot. Every balance, lot-tracked or not, has a NULL lot number | Proposed | 2026-10-17 |
| D-032 | Location and expiry rules: a location can take one material at a time (`single_material`); a location holding stock can't be deactivated or stop holding inventory; materials have a shelf life that sets a new lot's expiry; an expired lot can't be consumed, shipped or merged until its expiry is corrected | Proposed | 2026-10-17 |
| D-033 | Materials management: materials and material classes are maintained in Pulaski. For a material from the ERP, its code, name, base unit and type change only through the ERP (an idempotent import by external key); Pulaski owns everything else. A base unit can't change and a local code is fixed once the material is used; lot tracking changes only with nothing on hand. Classes stay flat. Type is a label, no rules. Capabilities, run standards and CSV import come next; imports and label printing will later go through adapters | Proposed | 2026-10-17 |
| D-034 | Capabilities, run standards and bulk import: capabilities are maintained per material and line over half-open periods that never overlap, and are ended rather than deleted. A new capability creates the line's run standard from its ideal rate; a changed ideal rate recalculates it while it is still based on the ideal rate, and never touches a modified rate. Materials load in bulk from a CSV file, checked first and saved all or nothing; the parser yields source-neutral rows, so later adapters feed the same importer | Proposed | 2026-10-17 |
| D-035 | Job execution and production reporting: starting a job opens a run, pausing or completing ends it; one run at a time per line. A production job starts once its order is on the floor (starting firms a dispatched order) and its process links are met; Pulaski never starts a job itself. Good and scrap are reported into a run, live or after it ended until the order is closed, capped at the run's length × the capability's ideal rate × a per-site tolerance. Consumption needs a run; beyond the planned components it warns. The order's in-progress and completed statuses follow its jobs | Proposed | 2026-10-17 |
| D-036 | Batches: a material may have a batch size, a rule (exact or near) and a tolerance, all Pulaski-owned. Orders and jobs of it are whole batches; production is reported batch by batch, each good batch its own lot. A partial batch is an exception, reported only as a run ends with a reason, one per run, in its own lot: accepted under the near rule, on hold for approval under the exact rule (finished goods by default) | Proposed | 2026-10-17 |
| D-037 | Users, roles and permissions: permissions are constants in code; roles are data an administrator maintains, held at a site (only the administrator, fixed in code, holds everything everywhere); viewing is open to every signed-in user; local sign-in with an API token for now, an identity provider later | Proposed | 2026-10-17 |
| D-038 | Shift schedules: each site's schedules repeat over a cycle of days; equipment inherits its parent's working time, follows a schedule of its own, or runs around the clock. Shift times are worked out from the schedule on read, never stored | Proposed | 2026-10-17 |
| D-039 | Viewing: a user sees only the screens and sites their roles allow. Each screen has a view permission, which a change permission brings with it; the API checks it on every read and keeps lists to the caller's sites; the UI leaves out what can't be seen and answers "not found" | Proposed | 2026-10-17 |
| D-040 | Labor: people (not necessarily users) and crews per site; a shift schedule's shifts name their crew. Supervisors check crews onto lines and operators clock themselves on; direct time is shared out to the runs on the line by overlap; a supervisor approves a shift's labor, which then locks, corrections being new sessions. Reports by job (against the standard crew), person and shift | Proposed | 2026-10-18 |
| D-041 | Shop orders are downloadable PDFs with one traveler sheet per job. Each sheet shows the order, operation, projected schedule (or published baseline when no live projection is available), effective run rate, order note, operation description and planned BOM components. A Code 128 barcode encodes `order-number|OP-number`; missing operation numbers use `OP-NA`. Since released product specifications are not maintained in the application, the sheet must say so and direct operators to verify the approved specification | Decided | 2026-10-19 |
| D-042 | Shift overrides: a schedule's shift can be changed for one production day (other times or crew, not running) and an extra shift added, without changing the cycle. Overrides are kept with the schedule, applied wherever shifts are worked out, and locked once that shift's labor is approved | Proposed | 2026-10-23 |
| D-043 | Site settings screen: changeover mode, reporting tolerances and calendar exceptions are maintained on one screen per site, seen with a new view permission (`site_settings.view`) and changed with `site_settings.manage`. Calendar exceptions are switched off rather than deleted; the reporting tolerances gain a version | Proposed | 2026-10-23 |
| D-044 | Production by shift: each production count belongs to the shift running on its line when it occurred, from the line's schedule with its overrides, and to that shift's production day; counts outside shifts belong to the site-local day. Worked out on read; every count lands in one row, so the report reconciles to the runs | Proposed | 2026-10-23 |
| D-045 | Planning rates from run history: worked out on request, not by a worker, as good output ÷ run time over a line's ended runs of the material in a window, with short, empty, unconvertible and unusual (Tukey-fence) runs left out and a minimum of runs and hours. A planner reviews it and accepts it as the calculated rate; it is checked again on accept; a modified rate stays in force | Proposed | 2026-10-23 |
| D-046 | Process links drive the projection: a job of an order made from a process waits for the jobs it follows (after start: the first to start; after end: the last to end; plus the lag), using their projected or actual times. Linked lines are projected together until the times settle; a firmed job keeps its time and warns `link_not_met`, circular waits warn `link_cycle`. Nothing is reordered or moved | Proposed | 2026-10-23 |
| D-047 | Breaks: a person's break is its own interval (paid or unpaid), never a change to their sessions. Its time comes out of the worked hours of every session it overlaps and is reported apart; it starts while the person is on a line, belongs to that session's shift and locks with its approval | Proposed | 2026-10-23 |
| D-048 | Qualifications: per-site qualifications with a default validity; grants for whole site-local days (a renewal is a new grant), revoked rather than deleted; equipment requires them for itself and everything below it. Clock-on and check-in warn, never refuse, when someone lacks one | Proposed | 2026-10-23 |

## Order hierarchy

Orders follow ISA-95 Part 4, with planned work separated from actual execution (D-003).

```
Manufacturing Order   what to make, for whom, by when
 └─ Work Order        a portion; the unit that is planned, dispatched and firmed (D-018)
     └─ Job           the piece of a work order that runs on one line
         └─ Run       actual execution: start, stop, counts, lots
```

| Level | ISA-95 equivalent | Notes |
| --- | --- | --- |
| Manufacturing order | Operations request | Optional; a standalone work order is valid |
| Work order | Work request | Owns planned quantity and window. Planned, dispatched and firmed as a whole (D-018) |
| Job | Job order | A subset of a work order on one line. The planner places and sequences jobs; operators execute them |
| Run | Job response | A job may execute in several runs (interruptions, shifts, resumes); counts, downtime and lots attach here |

- **Two sources, one model:** orders are scheduled in the app or imported from the ERP. Each carries an origin (`Erp` or `Local`), plus an external key and version for ERP records, so imports are idempotent.
- **Field ownership (proposed):** for ERP orders the ERP owns product, quantity and due date; Pulaski owns scheduling, dispatch and status. Local orders own everything.
- **Status flow:** planning states flow down: dispatching or firming a work order carries its jobs (D-018). Execution states roll up (proposed): jobs drive work order status once execution starts, and work orders drive manufacturing order status.
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
3. Time accrues only inside shifts, minus calendar exceptions. The shifts come from the schedule the line follows: its own, or the nearest ancestor's (D-038, which replaced stored shift instances). If nothing sets one, the line is treated as running 24/7 and the timeline says so. Past the last worked-out shift, the projection continues around the clock and flags the segments.
4. Changeover time between consecutive production jobs comes from the changeover matrix (below).
5. Warnings never block: late against the work order due date, no run standard, unit-of-measure mismatch, a missing changeover in scheduler mode, no duration on a non-production job, and beyond the calendar.

The projection is not stored. **Publish** copies the projected times into `job.planned_start` and `planned_end` as the baseline, so schedule adherence (baseline against projection against actual) is a KPI next to OEE.

**Boundary with the charter non-goal (D-014).** Pulaski never picks a line, never reorders jobs on its own and never optimises. The non-goal excludes finite-capacity optimisation. A forward pass over a sequence the planner chose is a calculator, not a scheduler.

**Changeovers (D-015).** `core.scheduling_policy.changeover_mode` is set per site:

| Mode | Behaviour |
| --- | --- |
| `auto` (default) | The projection inserts a dynamic changeover placeholder wherever the material changes, sized from the matrix and recalculated on every read. When execution of the next production job starts, the placeholder becomes a real `changeover` job, so its runs and downtime attach somewhere (built with execution; amended 2026-10-13, it was at dispatch). A changeover job the planner placed explicitly suppresses the placeholder. |
| `scheduler` | Planners place changeover jobs themselves. The projection sizes them from the matrix unless the job has its own `planned_duration`. It warns where the material changes with no changeover job in between. |

`core.changeover_rule` is the matrix. Each side matches a specific material, a material class or anything. A rule may be limited to one line, and the most specific rule wins. Two consecutive jobs on the same material need no changeover. If no rule matches, the capability's `standard_changeover` for the incoming material applies.

**Run standards (D-016).** `core.run_standard` holds one row per line and material:

- `calculated_rate_per_hour` is written by Pulaski only: first from the capability's ideal rate, later from run history.
- `modified_rate_per_hour` is written by a person. While it is set, it is the gold standard: recalculation keeps updating the calculated value, so drift stays visible, but never replaces the modified one.
- `rate_source` is a generated column, `modified` or `calculated`, that tells every reader which value is in force. Clearing the modification returns the row to `calculated`.

A rate rather than a fixed duration is stored, because a job's runtime scales with its quantity. Runtime = quantity ÷ effective rate.

**Work order lifecycle (D-018)**

Only work orders are planned and dispatched. A job is a piece of a work order on one line, and it follows its order:

| Work order | Its jobs | What happens |
| --- | --- | --- |
| `planned` | `planned` | The planner creates jobs, places them on eligible lines and sequences them. |
| `dispatched` | `dispatched` | **Dispatch is publish.** Each line holding one of the order's jobs is projected inside the transaction, and each job's projected times become its baseline (`planned_start`, `planned_end`). The order goes to the floor. Un-dispatch returns the order and its jobs to planned and clears the baseline, as long as nothing has started. |
| `firmed` | `firmed` | The plan is frozen: no un-dispatch, and each job is anchored at its baseline start (D-019). Adherence is measured against this baseline. |
| `in_progress`, `completed`, `closed` | `running`, `paused`, `completed`, `closed` | Execution (D-035): the order follows its jobs once the first one starts. |
| `cancelled` | `cancelled` | A work order with open jobs can't be cancelled; cancel its jobs first. |

- Dispatch, un-dispatch and firm are their own commands (`POST /api/work-orders/{id}/dispatch`, `/undispatch`, `/firm`), because they change the jobs too. The plain status change refuses those three transitions.
- Dispatch refuses an order with no jobs, or with a job whose line can no longer run its material. When the jobs don't add up to the order quantity, dispatch warns (`quantity_mismatch`) but goes ahead. For an order from a process, each operation is checked on its own, since every operation makes the whole quantity (amended 2026-10-16, D-024).
- New jobs can only be added to a planned order.
- The work order status `released` is renamed `dispatched` to match. An ERP "release" arrives as a planned order. The manufacturing order keeps its own statuses.
- Non-production jobs (cleaning, maintenance, trial, changeover) belong to no order. They stay planned until execution and can always be moved unless locked.

**Job rules (D-019)**

| Job status | Change machine | Move in time (resequence) | Cancel |
| --- | --- | --- | --- |
| `planned` | yes | yes | yes |
| `dispatched` | no (un-dispatch the order first) | no | yes |
| `firmed` | yes, to an eligible line; it keeps its time | no | yes |
| `running`, `paused` | no | no | cancel, optionally with replace |

- **Locked means locked.** A locked job can't be moved at all until it is unlocked.
- **Firmed jobs are anchored.** The projection places a firmed job at its firmed start (or now, if that time has passed), whatever runs before it. Work that runs into the anchor gets a `firmed_conflict` warning; it is never pushed past the firmed job. A firmed job moved to another line is placed in that line's sequence by its time, not where it was dropped.
- **Cancel and replace.** Cancelling ends the job's open run. With replace, a new production job (`<job number>-R`, `replaces_job_id` set) takes the same slot for the quantity not yet made (planned minus good). It takes its order's plan state, so under a firmed or in-progress order it is firmed and anchored at the time of the cancel. Runs, counts and lots stay with the cancelled job.

**Eligibility (D-020)**

`core.equipment_capability` is the list of what can run where. A row holds a line, a material, its effective dates, the ideal rate (for OEE performance, and the seed for the run standard), the standard changeover and `standard_crew_size`, the labor estimate. A production job can only be created on, moved to or dispatched on a line with a capability in effect for its material. The run standard (D-016) stays the planning rate. When capability maintenance is built, adding a capability will create its run standard from the ideal rate.

**Planner commands (D-017)**

- **Lock order.** Every scheduling write locks the affected lines' `core.equipment` rows first (`FOR UPDATE`, in id order), then the work order, then the jobs. Commands on the same line or order therefore serialize, and no two commands wait on each other in opposite order. Pinning changes no sequence and locks only the job. Dispatch, firm and the move of a firmed job read the plan inside the transaction, after the lock.
- **Placement.** Create and move both take `beforeJobId`, or null for the end of the line. The new rank is the midpoint of its neighbours (`SequenceRank`). When ten decimal places leave no room, the line's open jobs are renumbered 1..n in their current order, once, and the move is retried. This is the only case where a move writes more than one row.
- **History.** Every job status change is also appended to `ts.job_status_change` (D-010). Each work order planning step appends one event on the order.
- **Run standards and policy.** A modified rate records the actor as `modified_by`, and clearing it takes an optional reason. A site with no `scheduling_policy` row reports the defaults at version 0, and setting the policy from version 0 creates the row.

**Build passes**

1. Foundation: decisions, schema, the domain projector, and a read-only timeline API. *(built)*
2. Commands: create and sequence jobs, lock, cancel and replace, run-standard modify and clear, the scheduling policy, and the work order dispatch (publish), un-dispatch and firm. *(built)*
3. Calculated rates from run history, with drift against the modified standard. *(built on request rather than by a worker, D-045)*
4. Blazor scheduling board (D-021). *(first pass built)*

**Scheduling board (D-021, proposed)**

The schedule page (`/schedule`) shows one board four ways. Every edit goes straight to the API, with no save step and no confirmation except for cancelling a job, and the board is re-read afterwards, because times are always the projector's (D-014).

| View | Shows | Drag does |
| --- | --- | --- |
| Gantt | Lines by projected time; overlapping segments stack in lanes over a hatched band | Resequence a job, or move it to another line, before the job under the drop (or at the end); a marker shows the slot. Dragging a planned order from the *To place* tray onto a line creates a job for its unplaced quantity; while it is dragged, a drop lane under each line previews where it would land |
| Calendar | Jobs by site-local day; a job spanning days shows on each | Hold a planned job until a later day (its `earliest_start`). Dropping on or before the day it already starts only clears a hold: the sequence decides how early it runs |
| Kanban | Work orders by status | Dispatch, un-dispatch, firm or a plain status change (D-018). Columns the order can't reach dim during the drag |
| Table | Every open job | Line, lock and hold-until edit in place |

- **Editor.** Clicking any card opens one editor for a job (line and position, lock, hold, cancel with optional replace) or a work order (status, its jobs, place on a line). Everything a drag does is also there, for the keyboard.
- **Placement preview.** `GET /api/scheduling/lines/{lineId}/placement-preview?workOrderId=&plannedQty=&beforeJobId=` projects the line with the would-be job in its slot and saves nothing. It returns the whole timeline (the new job and its changeover carry `previewJobId`) and the jobs it would push later, and it refuses what creating the job would refuse (order not planned, other site, no capability). The Gantt asks only when the slot under the pointer changes and keeps answers for the rest of the drag.
- **Overlaps.** The projector lays work end to end, so two segments on a line overlap only when work runs into a firmed job's anchor (D-019), including as unstarted work slides later with the clock. The timeline lists each overlapping pair (`overlaps`); `firmed_conflict` on the firmed job stays as it was. The board shows a banner on every view, outlines the cards, and raises a notice the moment a new overlap appears, whether from the planner's own edit, another planner's or time passing.
- **Live.** The page re-reads every line and order every 10 s, highlights cards that changed, and pauses while a drag is in progress. A push from the event outbox (D-008) can replace the polling once the relay is built.
- **Not here.** No dragging of times, no resizing of bars and no automatic fixes: the planner owns line and sequence, Pulaski owns time (D-014).

**Group scheduling (D-022)**

A planner can plan to a department rather than a line: "Extrusion, this weekend, done before Monday". Where it actually runs is decided on the floor, by what is happening there.

- **Two flags.** `is_schedulable` means jobs can be planned to the equipment. `can_run_job` means jobs run on it. A line has both. A department is schedulable but runs nothing, so it is a **group**. A line can run jobs without being schedulable; it then only takes jobs from a group above it. Left out on create, `can_run_job` follows `is_schedulable`.
- **Eligibility (amends D-020).** A production job can be planned to a group when some line below it, at any depth, that can run jobs has a capability for the material. A non-production job is never planned to a group.
- **A window, not a projection (amends D-014).** A job on a group has a window: from `earliest_start` (or now) to `latest_end`, which is required. Nothing is projected on a group. Pulaski estimates the job on each line that could run it and warns, never blocks, when even the fastest line can't finish inside the window, when no line can run it, or when the window has passed. Each job is checked on its own. Sharing a group's capacity between its jobs would be finite-capacity scheduling, which is a charter non-goal.
- **The floor assigns (amends D-019).** `POST /api/scheduling/jobs/{id}/assign` puts the job on a line below its group that can run it (before a job, or at the end), moves it to another such line, or sends it back to the group. This works whatever the job's plan status, because where it runs is the floor's call; a locked or started job doesn't move. `group_equipment_id` keeps the group after assignment. Sequencing never moves such a job. Assigning when execution starts is built with execution.
- **On a line** the job is projected like any other job, and warns when it runs past its finish-by (`missed_finish_by`). A firmed job from a group is not anchored at a start, since it was firmed to a window.
- **Dispatch** publishes a job waiting on a group with its window as the baseline (`planned_start` = window start, `planned_end` = finish-by).
- **The board** shows a department row with each waiting job as a band over its window, labelled with the fastest line's estimate. Each line that could run it shows a faint ghost with the estimate on that line ("~14h here"). Dragging the band or a ghost onto a line assigns the job there; dragging an order from the tray onto the department opens the plan form with a suggested window.

## Process routes (D-023)

A process describes how an order is made, as a template that order creation will copy: "Extrude and Pack" runs operation 10, Extrude, on EXT-01 and EXT-02, and operation 30, Pack, on PACK1, which starts once extrusion has started. Each operation becomes jobs on the order, one per line or department it names, for that one's share of the quantity. This decision covers the model and its editor; order creation is D-024.

**Scope.** The charter's non-goal is the job-shop MES: discrete multi-step routings as the main use case. Process routes for continuous lines (a few operations on whole lines, often running at the same time as material flows between them) are within v1. The model has no work centres within a line, no queue or move times, and no alternative routings chosen by a solver.

| ISA-95 term | Pulaski |
| --- | --- |
| Operations definition | `core.process` with its versions |
| Operations segment | An operation: number, name, the equipment that runs it |
| Segment dependency | A link: `after_start` or `after_end`, with a lag |

- **Parallel and sequential.** Operations with no path of links between them run in parallel; a link makes one follow another. Links flow one way: a loop is refused. A released process with several operations must be connected.
- **Quantity on lines.** Every operation makes the whole order quantity, so the jobs of one operation add up to the order and the jobs of all operations add up to a multiple of it. How much of an order is on lines is what its least-placed operation plans (`OperationQuantities`), for the progress roll-up and the "Not on a line" tile alike (amended 2026-10-16).
- **Equipment and shares.** Each operation names at least one line, or department (D-022), in the process's site. Several entities on one operation run in parallel, each taking its share of the quantity; the shares total 100%. Machines and other equipment that neither runs nor plans jobs are refused.
- **Versions.** A process starts with an empty draft, version 1. The editor saves the draft's whole definition after every change; the draft may be incomplete but never malformed (duplicate numbers, a loop, a share out of range). Releasing checks it is complete, makes the previously released version obsolete, and writes the operations, their equipment and the links out as fixed rows that jobs will reference. A released version never changes: changing the process starts a new draft, a copy with new ids. Orders keep the version they were made from.
- **Storage.** A version's definition is one `jsonb` document, so editing a draft never deletes rows (the application role has no DELETE). The relational rows exist only for released versions, because they are referenced (D-010).
- **What it makes.** A process has a scope: all materials, or chosen material classes and single materials, in any mix. Extrude and Pack fits every extruded profile, so it makes the "Extruded profiles" class rather than a list of items. Order creation will offer every released process whose scope covers the order's material (`GET /api/processes?materialId=`). The scope belongs to the process, not to a version.
- **Default process.** Of the processes that make a material, the material may name one as its default, which order creation pre-selects; the planner can pick another. A default must be inside the process's scope; the editor flags a default the scope no longer covers.
- **Editor** (`/processes/{id}`): a palette of the site's lines and departments (collapsible), a canvas (drag equipment onto an operation, or onto the canvas for a new one; drag an operation by its header; drag from its right-hand handle to the next operation to link them), the shared property grid (collapsible) for the selected operation, link or the process (with what it makes and its defaults; lists are added to from the grid's footer and taken out with each row's ×), and a table of every operation's equipment and shares. A released version opens read-only.

## Work orders from a process (D-024, proposed)

Creating a work order in Pulaski now means making it from a process (D-023); the process assigns the equipment. `POST /api/work-orders` takes the process, order number, material, quantity and unit, start, optional due date, priority and an optional note. One command, in one transaction, creates the order and its jobs.

- **Choosing the process.** The form asks for the material first, then offers the site's active processes with a released version whose scope covers it (`GET /api/processes?siteId=&materialId=`), pre-selecting the material's default. The command refuses a process that is inactive, has no released version or doesn't make the material. The order keeps the released version (`process_version_id`); a later release doesn't change it.
- **Jobs.** Each operation becomes one planned production job per line or department it names, for that one's share of the quantity. Shares round down to six places (`core.qty`) and the operation's last entity takes the remainder, so every operation adds up to the order quantity. Jobs are numbered `<order>-<operation>`, or `<order>-<operation>-<n>` when an operation has several entities, and keep their operation (`process_operation_id`). Each goes at the end of its line's sequence; the planner moves it from there (D-014).
- **Times.** The start is required. Every job is held until it (`earliest_start`), and an operation that follows others is held until their hold plus the link's lag, the longest path winning. That is only a lower bound; the projection also holds each job until the work it follows allows (D-046).
- **Departments.** A job on a department waits in a window from its hold to the order's due date (D-022), so an operation on a department needs a due date after its hold.
- **Checks.** Everything is checked before anything is written, and all problems are reported together, each naming its operation, line and material: the equipment must still be schedulable, active and in the process's site, and the material must be able to run there (a capability on the line, or on a line under the department; D-020, D-022). Nothing is created if any check fails.
- **Capability decides, scope only filters.** A process's scope (D-023) says what it is meant to make; the capabilities (D-020) say whether its lines can actually run it. Scope alone is not enough, so the form checks each offered process with `GET /api/processes/{id}/order-check?materialId=`, which runs the same rules (`ProcessOrderRules`, one shared SQL query) as creating the order. A process that fails shows disabled, with its problems listed under the field; the check also lists department operations, so the form can mark the due date required.
- **Locks (D-017).** The released version is read without a lock to learn its equipment; the lines are locked in id order; then the version is re-read `FOR SHARE` and must still be the released one, or the command returns 409. The order and its jobs are inserted after that.
- **Events.** `work_order.created`, then `job.created` for each job, with a `ts.job_status_change` row per job.
- **Notes.** A free-text note, up to 4000 characters, trimmed; shown on the work order list and the schedule's order editor. It is set at creation; editing it later is not built yet.
- **Dropped from create.** The order's line or area (`target_equipment_id`) and planned end: the process says where the order runs and the projection says when it ends.

## Bills of materials (D-025, proposed)

A bill of materials says what one material is made from: "1000 m of PRF-5100-BR takes 1450 kg of white profile blend and 90 kg of capstock at extrusion, then 84 end caps and half a long pallet at packing". ISA-95 calls the per-operation part a segment's material specification.

- **One level.** A BOM lists a material's direct components only. An intermediate, such as a PVC dry blend, has its own BOM and is made on its own order; the profile order consumes blend lots, and genealogy chains through the lots. Pulaski never explodes a tree down to raw materials; that is MRP, which belongs to the ERP.
- **Per material, optionally per process.** A BOM belongs to a material and may name a process (D-023) whose scope covers the material; there is at most one BOM per material and process, and one with no process. Order creation takes the BOM for the order's material and process, or else the material's BOM with no process, or else none: an order without a BOM plans no components.
- **Items.** Each item has a line number, a component material, a quantity per the BOM's base quantity (in a unit), a scrap factor (the extra share to plan, so 0.02 plans 2% more), how it is consumed (`time_window`, `declared`, `backflush` or `scan`) and, on a BOM for a process, the number of the operation that consumes it. An item with no operation, and every item of a BOM with no process, is consumed by the first operation in flow order. A material can't be its own component.
- **Versions, as processes.** A BOM starts with an empty draft, version 1, holding the base quantity and unit and the items as one `jsonb` document. Releasing checks it is complete (items, real and active components, operation numbers that exist in the process's released version), makes the previous released version obsolete, and writes the items out as fixed `core.bom_item` rows that planned components reference. A released version never changes; changing a BOM starts a new draft, a copy with new item ids. A work order keeps the BOM version it was made with.
- **Planned components.** Creating a work order (D-024) writes one `core.job_material` row (`consume`) per item and job of the consuming operation. An item's total is order quantity ÷ base quantity × quantity per × (1 + scrap factor), rounded to six places; the operation's jobs share it in proportion to their quantities, the last taking the remainder. The order quantity is first converted to the BOM's base unit (D-026).
- **Jobs added later (amended 2026-10-16).** A production job added to an order after it was made (from the schedule, or as the replacement for a cancelled job) gets the same treatment. It takes an operation: a replacement keeps the cancelled job's; a new job takes the first operation in flow order that runs on its line or group, or else the first operation. It is then planned components from the order's BOM version: the items of its operation for its own quantity, with no sharing.
- **Process changes.** A released BOM keeps the operation numbers it was released with. When a process release leaves a BOM naming an operation the new version doesn't have, the release goes ahead but reports each such BOM. The BOMs page, the BOM editor and the order form flag it, and orders for the material made with that process are refused, naming the BOM, until a new BOM version is released.
- **Inventory (amended by D-027).** Pulaski keeps lot balances at the site's stores, staging areas and feed points, receives lots into them, and reports every movement to the ERP as an event. The ERP stays the financial book of record; purchasing, valuation and stock outside the site stay there (charter: not an ERP).
- **Build passes.** 1. BOMs and planned components *(built)*. 2. Lots in the plant: receive, adjust, move, hold, and declared consumption and production with job-level genealogy (D-027, *built*). 3. With execution: loading feed points (`core.material_load`), runs and counts, each item consumed by its method (time window, backflush, scan), and time-window genealogy (charter slice step 4).

## Units of measure and conversions (D-026, proposed)

Quantities are entered in the unit that suits the person entering them: a planner orders in feet, a BOM is written per 1000 m, a rate is in metres per hour, resin arrives in 25 kg bags. Pulaski converts between them in one place.

- **Standard units.** Each unit has a dimension (mass, length, area, volume, count, time) and a factor to that dimension's base unit (kg, m, m², L, ea, s): 1 lb = 0.45359237 kg. Units of one dimension convert for every material. `db/seed-units.sql` loads the metric and US units with their exact factors. A unit's code, dimension and factor never change once created, because quantities are stored in it; it can be renamed or switched off.
- **Packaging units.** Box, bag, pallet, roll and similar have dimension `other` and no factor: how much a box holds depends on what is in it.
- **A material's own conversions.** `core.material_uom_conversion` says, for one material, 1 unit = factor units: 1 m = 1.6 kg of a profile, 1 box = 500 end caps, 1 bag = 25 kg of resin. Each works both ways and combines with the standard factors, so with 1 m = 1.6 kg, feet convert to pounds. A conversion between two standard units of one dimension is refused (they already convert), and so is one that would give a second route between two units, since two routes could disagree. A material's conversions therefore have one answer for every pair of units.
- **Where conversions apply.**
  - A work order may be in any unit that converts to its material's base unit; otherwise it is refused. Its quantity is converted to the BOM's base unit before the components are planned (D-025).
  - A BOM's base unit must convert to its material's base unit, and each item's unit to its component's base unit, before the version can be released, so consumption can later be recorded in stock units.
  - The projector converts a job's quantity to its run standard's unit (D-016). Only when the material has no conversion between them does it warn `uom_mismatch` and leave the duration unknown. A department's lines are compared in the job's unit.
- **Rounding.** Factors are kept exact (decimal) along the route; a converted quantity is rounded to six places, as `core.qty` stores.
- **Screens.** Configure → Units lists the catalog by dimension and maintains each material's conversions, with a calculator to try a conversion. The BOM editor and the order form link to the material's conversions.

## Inventory (D-027, proposed)

Inventory answers "what do we have, where, and what happened to it": 1,200 kg of PVC-K67 lot 24-0917 sits in raw-material stores, 300 kg of it went into job WO-1001-10, and that job made profile lots WO-1001-10-01 and -02. This amends D-025, where the ERP kept every balance outside the line.

**Scope (amends D-025).** Pulaski keeps the balance of every lot at every location in a site that holds inventory: raw-material stores, staging, finished-goods staging and the feed points on a line. The ERP stays the financial book of record. Pulaski posts each receipt, adjustment, move, consumption and production as an event for the ERP to pick up (D-008); purchasing, valuation, costing and stock outside the site stay in the ERP (charter: not an ERP).

- **Locations** are equipment with `holds_inventory` (D-010): a storage zone or unit, or a feed point. A lot is in one location at a time, in its site; an inactive location takes nothing new.
- **Quantities** are kept in the material's base unit (D-026). Every command takes a quantity in any unit that converts for the material, converts it to the base unit (six places) and refuses one that doesn't. Event payloads keep what was entered as well.
- **No negative stock.** A lot can't go below zero. Taking out more than is there is refused, naming what is left.
- **Statuses.** `available`, `on_hold` and `quarantine` are on hand; `consumed` and `scrapped` are a lot taken down to zero by consumption or write-off. Only an available lot can be consumed. A lot on hold or in quarantine can still be moved, written up or written off. `planned`, `in_production` and `shipped` stay in the schema for execution and shipping, which are not built.

**Commands.** Each one locks the lot (and the job, for consumption and production), writes the lot, appends one `ts.lot_event` row with the quantity after it, and appends one event on the lot (`pulaski.lot.<kind>.v1`), in one transaction (D-007).

| Command | What it does | Lot event |
| --- | --- | --- |
| Receive | A new lot of a material at a location, with an optional supplier lot and expiry. The lot number is required for a lot-tracked material and unique per site and material | `received` (+) |
| Write up | Adds found or miscounted stock. A lot at zero comes back as available | `adjusted` (+) |
| Write off | Removes stock (damage, loss, count correction), all of the lot or part of it. At zero the lot is `scrapped` | `scrapped` (−) |
| Move | Moves the whole lot to another location in the site. Splitting a lot is not built | `moved` |
| Hold, quarantine, release | Changes the status between the three on-hand statuses | `status_changed` |
| Consume | Declares quantity of an available lot used by a job | `consumed` (−) |
| Produce | Declares output of a job's material into a new lot at a location | `produced` (+) |

- **Reasons.** Write up and write off need a reason code of kind `inventory_adjustment`; hold and quarantine need one of kind `hold`; release takes none. The reason must be active, selectable, and enterprise-wide or the lot's site. A reason with `requires_comment` refuses a blank comment. `db/seed-reasons.sql` loads a starter set; there is no screen to maintain them yet.
- **Versions.** Write up, write off, move and status changes take the lot's version and return 409 if it changed: the person is looking at that lot. Consume and produce don't: they are declarations against a job on a busy floor, protected by the quantity check under the lock instead.
- **Time.** Every movement is recorded at the server's time. Back-dating is not built.

**Consumption and production (declared against a job).** An operator declares what a job used and made. Since execution (D-035) each declaration goes into one of the job's runs and records its `run_id`, and production is a report of good output with the rate check.

- The job must be a production job in the lot's site that isn't closed or cancelled, and has a run to declare into (amended by D-035).
- Consuming a material the job has no planned component for (`core.job_material`) is allowed, as a substitution, with a `not_planned` warning in the response.
- Production makes the job's material. A blank lot number is generated as `<job number>-<nn>`, counting the job's lots. Producing into an existing lot number adds to it only when that lot was made by the same job at the same location; otherwise it is refused.
- **Job-level genealogy.** Every lot consumed into a job is a parent of every lot the job produced, whichever came first: consuming links the lot to the job's outputs so far, and producing links the job's inputs so far to the new lot (`link_basis` `declared`, with the job). Edges are never duplicated. This over-links on purpose: it is the safe superset until time-window genealogy narrows it with execution.
- **Locks.** Consumption and production lock the job row (`FOR UPDATE`), then the lot, so declarations on one job serialize and both halves of the genealogy see each other. Scheduling locks lines, then the order, then jobs (D-017) and never lots, so the two can't deadlock.

**Bulk balances.** A material that isn't lot-tracked (`is_lot_tracked = false`) keeps one hidden lot per location (`is_bulk`, lot number `@<location code>`), created on its first receipt or production there. Every movement still goes through `core.lot` and `ts.lot_event`, so one code path covers both. Receiving or producing adds to it; moving a bulk balance takes a quantity and transfers it to the destination's bulk lot (two `moved` rows, linked by `related_lot_id`). Screens show the location instead of a lot number.

**Lookups.**

| Lookup | Answers |
| --- | --- |
| Stock by material | Every active material, grouped by class, with on hand, available, held, lots and locations; filter by text, class, location, and in stock only |
| Material | Its lots, its balance per location, and where it is used |
| Where used | The released and draft BOMs that list it as a component (with quantity, unit and operation), and the open jobs planned to consume it, with what they have consumed |
| Lot search | By lot or supplier lot number, material, location, status, and expiring before a date |
| Lot | Its facts, its history (every movement with the quantity after it, who, why and the job) and its direct parents and children |
| Trace | Every lot downstream (forward) or upstream (backward), from `core.trace_forward` / `core.trace_backward`, recursive `LANGUAGE sql STABLE` functions (D-012) |
| Locations | Each location holding inventory, with its lots and materials |
| Job materials | A job's planned components against what it has consumed, and the lots it produced |

**Not built yet:** splitting and merging lots, loading feed points and time-window consumption, backflush, scanning, shipping, cycle-count sessions, back-dated movements, a reason-code screen, and pushing movements to an ERP (the outbox relay).

## Schema migrations and partition maintenance (D-028, proposed)

A database already in use has to follow the schema as it changes, without being reloaded and losing its data; and the history tables need partitions created ahead, or writes stop when the last one fills.

**Migrations.**

- **Two ways to the same schema.** `db/schema-draft.sql` stays the complete current schema, for a new database. Each change also ships as a migration, `db/migrations/NNNN_description.sql`, that brings an existing database to the same place. For 0001 and 0002 this was checked by hand: a database built from the commit before them and migrated has the same columns, indexes, constraints, functions and grants as a fresh load. That comparison isn't automated yet.
- **Recorded.** `public.schema_migration` holds one row per applied migration, named by its file. `schema-draft.sql` inserts every migration it already contains, so a fresh database has nothing to apply. The table is in `public`, outside the four application schemas, and the application role can only read it.
- **Applied by `db/migrate.sh [database]`.** It runs each unrecorded file in name order, in its own transaction with the row that records it, then re-runs `db/app-role.sql` for the grants. Files therefore have no `BEGIN`/`COMMIT`.
- **Re-runnable.** Every migration is guarded (`IF NOT EXISTS`, `CREATE OR REPLACE`, a `DO` block for constraints), so a database changed by hand, or migrated twice, isn't broken. A test re-runs them all on the current schema.
- **Checked at startup.** The API knows the newest migration it needs (`DatabaseMaintenance.RequiredMigration`, tested against the newest file) and refuses to start without it, naming the migration and the command. A database it can't reach is only logged.
- **No down migrations.** Going back is restoring a backup; the event log is append-only anyway.

**Partition maintenance.**

- `ts.ensure_partitions(months)` creates the current month and the months after it for every range-partitioned table in `ts` and `evt`, and returns how many it made. It is `SECURITY DEFINER`, because creating a partition needs ownership of its parent, and only `pulaski_app` may execute it. It is called with `SELECT`, never `CALL` (D-012).
- The API calls it at startup and every 12 hours, keeping three months ready (`Pulaski:PartitionMonthsAhead`). A failure is logged and retried on the next round. The seed calls it once instead of naming each table.
- Every history table is monthly for now. Daily partitions for `ts.process_value`, and retention (dropping old telemetry partitions), come with machine data.

## Shortages and the movement journal (D-029, proposed)

Two lookups on top of inventory (D-027): "will we have the material for the jobs coming up?" and "what moved, when, and who did it?".

**Material shortage check.** For a site, optionally one line or department and everything under it, and optionally a horizon:

- **Demand** is each open production job's planned components (`core.job_material`, `consume`) less what has been declared consumed into it, never below zero. Open means planned, dispatched, firmed, running or paused. Planned quantities are converted to the component's base unit (D-026); a line whose unit doesn't convert is left out and reported, never guessed.
- **Supply** is the component's **available** stock in the site. Held and quarantined stock is shown beside it but not counted: it can't be consumed until released.
- **Order.** Jobs take supply in the order they are needed: by the job's published start (`planned_start`), else its hold-until (`earliest_start`), then job number; jobs with neither come last. Each line shows the running total and how much of it is not covered, so the first job that comes up short is visible. A horizon (for example the next 3 days) leaves out jobs needed after it, and jobs with no time at all.
- **The projected time is not used yet.** A job's live projection (D-014) is calculated per line on read; the check uses the baseline and hold times instead. A job that has slipped is therefore ordered by its published time.
- **Not MRP (charter non-goal).** No purchase or transfer suggestions, no incoming receipts or open purchase orders netted in, no lead times, no multi-level explosion: an intermediate's own components show only on the jobs of its own orders (D-025). The calculation is a pure function (`ShortageCalculator`), redone on every read.

**Movement journal.** Every `ts.lot_event` row of a site, newest first, filtered by a time range (required, the last 7 days by default, so only those partitions are read), movement kind, material, location (where the stock left or arrived), job, and the person who recorded it; and a lot or supplier lot number. Pages of 200 rows continue from the last row read (`occurred_at`, `id`), so a page never repeats or skips a row as new movements arrive. A BRIN index on `occurred_at` keeps a range read cheap (migration 0003).

## Completing inventory (D-030, proposed)

What a plant needs to keep floor stock right without the ERP, beyond receiving and adjusting (D-027). Consumption, production, allocation to jobs and labels are not part of this; they come back with execution.

**Split, merge and ship** (whole lots, never a bulk balance, which is already a quantity).

| Command | Rule | Lot events |
| --- | --- | --- |
| Split | Takes part of an on-hand lot (less than all of it) into a new lot of the same material, status, supplier lot and expiry, at the same or another location in the site. A blank number becomes `<lot>-1`, `-2`…, the first free one. Genealogy: the lot is the new one's parent (`split`) | `split` (−) on the source and (+) on the new lot, each naming the other |
| Merge | Combines lots of one material, at one location, in one status, into a target lot. The sources go to zero (`consumed`, "merged into"); the target takes the earliest expiry of them all, since mixed stock is only as good as its oldest part. Genealogy: each source is the target's parent (`merge`) | `merged` (−) on each source and (+) on the target per source |
| Ship | Sends quantity of an **available** lot or balance out of the site, with a reference (delivery or shipment number, required). At zero a lot is `shipped`. Shipping documents and customers stay in the ERP | `shipped` (−) |

**Corrections.**

- **Correct details.** A lot's number, supplier lot and expiry can be corrected, with a comment. The number stays unique per site and material; a bulk balance's number can't change. The event keeps before and after. Lot event `corrected`.
- **Reverse a movement.** The lot's **latest** movement can be reversed, once, for an adjustment reason: a receipt, write-up, write-off, move, status change or shipment. The lot returns to its quantity, location and status before it, and the reversal names the movement it undoes (`reverses_event_id`, `reverses_occurred_at`; no FK into history, D-010). A reversed receipt that leaves a lot at zero makes it `cancelled`. Consumption and production (job declarations), splits, merges, bulk transfers and corrections aren't reversed: the first two wait for execution, the rest touch two lots or nothing countable, and are put right with another movement. Lot event `reversed`.

**Stock counts.**

- A count is of one location. Starting it takes a snapshot of every on-hand lot there (`core.stock_count_line.expected_qty`). People record what they count, as often as they like, until it is posted.
- **Posting** writes, for each counted lot whose count differs from its quantity now, a write-up (`adjusted`) or write-off (`scrapped`) for the chosen reason (normally *Count correction*), referencing the count number. Every line must be counted first (enter 0 for a lot that isn't there). A lot that moved after its line was counted is refused, named, until it is recounted; one that moved between the snapshot and the count is fine, because the count is what is there.
- Stock found that isn't in the count is received separately. A count can be cancelled until posted. Count numbers are `CNT-<yyyyMMdd>-<n>` per site.

**Reason codes.** A screen lists, adds and changes reasons for inventory (and the other kinds), enterprise-wide or for a site. A code and its kind never change once created, since history refers to them; the name, whether a comment is required, the order and the active flag can. Reason trees (`parent_id`) are not edited yet.

**Reports.**

- **Stock on a date:** each material's on-hand quantity at the end of a day, from each lot's latest movement up to then (`qty_after`). It reads every movement before the date, so it is slower for older dates. Status at the time isn't reconstructed: held stock counts.
- **Aging:** on-hand stock by how long ago each lot was received or made: up to 30, 31–90, 91–180 and over 180 days, per material.
- **Export:** stock, lots, the journal (up to 10,000 rows) and both reports download as CSV.

**Deferred:** pallets or containers (with labeling), lot attributes and certificates of analysis (with quality), allocation and picking suggestions (with execution).

## Indistinguishable lots (D-031, proposed)

A resin silo or a tank can't keep lots apart: what goes in mixes with what is there. Pulaski models that as the location, not the material.

- **The flag.** Equipment that holds inventory can also have **lots are indistinguishable** (`core.equipment.commingles_lots`). It can't be set without `holds_inventory`, and it can't be turned on while the location holds separate lots (merge or move them first). Turned off, an existing balance stays a balance until it is used up; new arrivals are separate lots again.
- **One balance per material.** Stock there is held as a balance (`is_bulk`), exactly as a material that isn't lot-tracked is anywhere (D-027). A balance has no lot number: `lot_number` is NULL exactly when `is_bulk`. This replaces the `@<location code>` numbers D-027 gave balances; screens show *Balance at SILO-1*.
- **Every arrival merges.** A receipt, a move, a split sent there and production into it all end in the balance, created on the first arrival. Only stock in the balance's status can join it (normally available): a held lot is released first, or the balance held too.
- **Traceability kept.** A receipt with a lot number creates that lot, then merges it into the balance (`received`, then `merged` out of the lot and into the balance, genealogy `merge`), so a trace from a finished lot goes through the silo back to the supplier's lot. A lot moved or split there is merged the same way, and so is production with a lot number. A receipt or production with no number goes straight into the balance, the supplier lot kept only in the event. The balance takes the earliest expiry of everything in it.
- **Genealogy over-links here too.** Every lot that ever joined a balance is a parent of everything taken from it afterwards: the silo has no first-in, first-out. Time-window genealogy with residence time narrows it with execution (charter slice step 4).
- **Counting, writing off and shipping** a balance work as for any balance. Merging by hand, splitting and correcting the number don't apply to a balance.

## Location and expiry rules (D-032, proposed)

Rules that keep floor stock honest where D-027 and D-031 left a gap.

**One material at a time.** A location that holds inventory can take **one material at a time** (`core.equipment.single_material`), like a silo dedicated to a resin. While any stock of one material is there (a quantity above zero, held or not), nothing of another material can arrive: receipts, moves, splits sent there, balance transfers and production are refused, naming what is there. Once that quantity is zero, any material can go in. It is independent of lots being indistinguishable (D-031), which only ever merges lots of one material number; a silo normally has both. Arrivals lock the location row first (`FOR UPDATE`) when it takes one material, so two receipts of different materials can't both land in an empty silo. It can't be turned on while two materials are there.

**Locations holding stock.** Equipment holding on-hand stock can't be deactivated or stop holding inventory; move, use or write off the stock first. Otherwise its lots would sit in a place nothing can reach.

**Shelf life and expiry.**

- A material may have a shelf life in days (`core.material.shelf_life_days`). A new lot received or produced without an expiry expires that long after it arrives; an expiry entered by hand wins. A balance takes the earliest expiry of what joins it, as before (D-031).
- An **expired** lot (its expiry is now or past) can't be consumed, shipped, or merged into another lot or a balance. It can still be moved, held, written off or counted. To use it after all, correct its expiry with a comment (D-030): the quality decision is then on record.
- Shelf life is set on the material's inventory page until there is a materials screen; the change is an event on the material.

## Materials management (D-033, proposed)

Materials are the items everything else refers to: BOMs, processes, capabilities, run standards, orders, jobs and lots. This module maintains them; until now they came from a seed script.

**Who owns what.**

| Field | Local material | Material from the ERP |
| --- | --- | --- |
| Code, name, base unit, type | Pulaski (code and base unit subject to the rules below) | **The ERP only**, through the import; read-only on screen |
| Class, description, lot tracking, shelf life, active | Pulaski | Pulaski |
| Default process (D-023), conversions (D-026) | Pulaski | Pulaski |

- **The import.** `POST /api/materials/import` takes one item from an external system: its system and key (`external_system`, `external_key`), an optional version, code, name, base unit code and type. It creates the material (`origin = erp`) or updates the ERP-owned fields of the one with that key; Pulaski-owned fields are never touched by it. Sending the same item twice changes nothing and records no event. It is the way in that CSV import and, later, adapters for other data sources (D-033 next steps) will use.
- **Type** is a label from the ERP's item master (an MRP class or item type, mapped onto Pulaski's list). It drives no rules.

**Rules that keep references valid.**

- **Base unit.** Quantities in lots, BOMs, orders, jobs and run standards are stored in or against it, so it can't change once anything refers to the material. That applies to the ERP too: an import changing the base unit of a material in use is refused.
- **Code.** A local material's code is fixed once the material is used, as a business key people and other systems recognise. The ERP may rename its own items: it is the master, and Pulaski refers to materials by id.
- **Lot tracking** can change only while nothing is on hand, since lots would have to become balances or the other way round (D-027, D-031).
- **Deactivating** is always allowed and stops new use: receipts, new orders, new BOM components and process scope pick only active materials. Whatever already uses it carries on; the change reports what still uses it (stock on hand, open work orders and jobs, released BOMs, capabilities).
- **In use** means referenced by any lot, BOM, BOM item, work order, job, planned component, capability, run standard, process scope or changeover rule.

**Classes** are flat: a code, a name and an active flag. The code is fixed; an inactive class can't be given to a material but stays on the ones that have it. Process scope (D-023) and the changeover matrix (D-015) refer to classes, so they are never deleted.

**Screens.** Configure → Materials lists materials grouped by class, with search and filters, and creates local materials. Each material has a page with its properties (ERP-owned ones read-only), what it is used in, its conversions, the processes that make it, its BOMs, where it is used, its capabilities and its stock. Material classes have a tab of their own.

**Next:** capabilities, run standards and CSV import are D-034; still to come is an adapter interface for external data sources (ERP item masters, label printing such as BarTender).

## Capabilities, run standards and bulk import (D-034, proposed)

**Capabilities (amends D-020).** A capability says a material can run on a line from `effective_from` (included) to `effective_to` (not included, open when null).

- **Where.** Only on active equipment with `can_run_job`. A department gets its eligibility from the lines below it (D-022), so it never has capabilities of its own. The material must be active, and the rate unit must convert to its base unit (D-026).
- **No overlap.** Two periods of one material on one line never share an instant, so "is there a capability now?" always has one answer. The handler checks first and says so in words; the `capability_no_overlap` exclusion constraint is the backstop for a race.
- **Ended, not deleted.** The app role has no DELETE, and history should still say what could run where. *End now* sets `effective_to` to now; a capability that hasn't started yet is changed instead. Ending one under open jobs reports how many stay on the line, unable to be moved there or dispatched until it has a capability again; nothing is moved for the planner (D-014).
- **Lock order.** Each change locks the line row first, as every scheduling write does (D-017), then the material and the capability, so it serializes with the eligibility checks of job placement and dispatch.
- **Events:** `pulaski.capability.added.v1`, `…changed.v1` and `…ended.v1`, each with the capability before and after.

**Run standards follow the ideal rate (amends D-016).** The run standard is one row per line and material, shared by all its capability periods.

| Event | Run standard |
| --- | --- |
| A capability is added and the line has none for the material | Created from the ideal rate, in its unit, basis `ideal_rate` (`pulaski.run_standard.created.v1`) |
| A capability is added, or its ideal rate or unit changes, and the basis is still `ideal_rate` | The calculated rate takes the new ideal rate (`…recalculated.v1`); a modified rate stays in force |
| The basis is `run_history` | Unchanged: measured performance beats a nameplate figure |
| The rate unit would change under a modified rate | Refused: the modified rate is in the old unit, so clear it first |

The material page also sets and clears the modified rate, through the existing run-standard commands.

**Bulk import.** `POST /api/materials/import/csv` takes a CSV file (`text/csv`, at most 5 MB and 5,000 rows); `?dryRun=true` checks it and saves nothing.

- **The file.** The header names the columns, in any order or case, with a few common alternative names; comma or semicolon separators; RFC 4180 quotes. `code`, `name`, `type` and `base_unit` are required. A column left out keeps what each material has; a blank cell clears an optional field (class, description, shelf life). An unknown column is a problem with the file, so a misspelt header can't be silently ignored.
- **Matching.** A row with `external_system` and `external_key` is the ERP's item, matched on them as in the single import (D-033); its Pulaski-owned columns are applied too, because the person loading the file is a Pulaski user. A row without them is a Pulaski material matched on its code; a code that belongs to an ERP material is an error.
- **All or nothing.** Every row goes through the same rules as a single change, in one transaction, so later rows see earlier ones. Any error, or a dry run, rolls the whole file back; the answer lists each row as *create*, *update* (with the fields that change), *unchanged* or *error* (with why). A code or key twice in a file is an error. Each saved row records its usual event, with the reason `CSV import`.
- **The adapter seam.** `MaterialCsv` only turns text into `MaterialImportRow`s (source-neutral; a field the source didn't supply is null). `ImportMaterialsCommand` applies the rules to rows from any source. An adapter for another item master, or for label printing, will produce or consume the same rows; designing that interface is the next step.
- **Not yet:** capabilities in the file, creating missing classes, and exporting a template file (the screen shows the header line).

## Job execution and production reporting (D-035, proposed)

What happens on the floor, recorded by people until the edge connectors bring machine counts (charter slice). Runs are the ISA-95 job response (D-003): counts, lots and later downtime attach to them.

**Runs and statuses.**

| Command | From | Job becomes | Run |
| --- | --- | --- | --- |
| Start | production: `dispatched`, `firmed`; non-production: `planned`, `dispatched`, `firmed` | `running` | A new run, `run_no` + 1 |
| Resume (start) | `paused` | `running` | A new run |
| Pause | `running` | `paused` | Ends, with an optional `run_end` reason |
| Complete | `running`, `paused` | `completed`, `completed_at` set | The open one ends |
| Reopen | `completed`, order not closed | `paused` | — |

- **One run at a time per line**, already enforced by `job_run_no_overlap`; a start on a busy line is refused with what runs there. A pause ends the run, so the line is free; `job_run.status = 'paused'` stays unused.
- **Execution freezes the plan (amends D-018).** Starting a job of a *dispatched* order firms the order and its jobs first, then the order goes to `in_progress`. Its last open job's completion makes it `completed`; reopening a job takes it back. These three transitions are refused as plain status changes. Closing a completed order closes its completed jobs.
- **A department's job (D-022)** starts on the line the floor names: the start assigns it there (`pulaski.job.assigned.v1` with `onStart`) and then starts it.
- **Changeovers (D-015).** In `auto` mode, when the last job that ran on the line was a production job of another material, starting the next production job creates a changeover job (`<job>-CO`) in front of it and starts that instead. Completing it lets the job start; a start with `skipChangeover` skips it. In `scheduler` mode the planner's own changeover jobs run like any other.
- **Locks (D-017).** Start, complete and reopen lock all the order's lines, then the order, then its jobs, because they may firm or roll up the order. Pause locks the job's line and the job. Reports lock the job, then the run, then lots, as declarations did (D-027), and never a line, so the two can't deadlock.

**Readiness, not automation.** The process links (D-023) gate the start: an *after start* link is met once any job of the upstream operation of the same order has started, an *after end* link once all of them are completed (cancelled ones don't count), each plus its lag. A start before then is refused with what it waits for. Starting or completing a job notes which downstream jobs can start now, or in how long. Pulaski never starts a job itself: the floor does (D-014).

**Production reports.** `POST /api/jobs/{id}/production` takes good and scrap in the job's unit (or one that converts), a `scrap` reason when there is scrap, and a location (and optional lot number) for good output.

- **Into a run.** Without a run id it goes into the open run; with one, into that run of the job, also after it ended, until the job is closed (the post-execution report). It counts at the report's time, or at the run's end for a late report; `recorded_at` keeps when it was entered.
- **The rate check.** Good plus scrap in the run, this report included, may not exceed the run's length (so far) × the ideal rate of the capability in effect when it started, converted to the job's unit, × `execution_policy.rate_tolerance_pct` (110% by default). The ideal rate is the line's nameplate; the planning rate (D-016) is usually lower and would refuse honest reports. With no capability, or a rate unit that doesn't convert, the report is recorded with a `not_checked` warning.
- **What it writes.** The run's `good_qty`/`scrap_qty`; `ts.production_count` rows (`processed` = good + scrap, `defective` = scrap, with the reason, source `operator`); good output into a lot through the same code as declared production (D-027, D-031), its `ts.lot_event` row naming the run; one `pulaski.job.production_reported.v1` event. Declaring production on the inventory screens is the same report with no scrap.

**Consumption (amends D-027).** Declared consumption needs a run too: the open run, or the run named for use found afterwards. Its `ts.lot_event.run_id` is set. When the job plans the component, consuming beyond the planned quantity × max(1, made ÷ planned) × `consumption_tolerance_pct` is allowed and warns (`over_planned`). The time rule is hard, the amount soft: the floor knows what it used.

**Screens.** Operate → Floor shows each line's running job and what is next, with readiness, and starts, pauses, completes and reports. `jobs/{id}` shows a job's runs and reports and takes reports into any of its runs. The tolerances have an API (`/api/execution/sites/{id}/policy`) and no screen yet.

**Not yet:** machine counts and state from the edge, downtime, OEE, labor, quality samples; correcting or reversing a report; consumption by time window, backflush and scanning; time-window genealogy.

## Batches and partial batches (D-036, proposed)

Some materials are made a fixed amount at a time: a 50 kg mix, a 200 l tank. Pulaski plans and records them in batches.

**The material.** Three Pulaski-owned fields (D-033): `batch_size` in the base unit (null: made continuously), `batch_rule` and `batch_tolerance_pct`. The rule is a setting of its own, not the material type, which stays a label with no rules (D-033). It defaults from the type when the batch size is first set:

| Rule | Default for | A batch is | A partial batch |
| --- | --- | --- | --- |
| `exact` | finished goods | exactly the batch size | waits on hold for approval: a finished good can't be sold as a partial |
| `near` | everything else | within ± the tolerance (2% by default: 49.3 of 50 yes, 40 no) | is accepted: work in progress will be used |

`PUT /api/materials/{id}/batching` sets them; open orders keep their quantity, and the answer names those that are no longer whole batches.

**Planning in whole batches.** A work order's quantity, converted to the base unit, must be a whole number of batches; the refusal names the two nearest. Shares between equipment (D-024) are whole batches each, largest remainders first, at least one batch per share. A job added by hand is whole batches. A cancelled job's replacement (D-019) rounds what is left **up** to whole batches, to meet the order.

**Reporting batch by batch (amends D-035).** For a batch material a report carries `goodBatches` and `scrapBatches` (each batch's weight in the base unit) instead of quantities; each must be a batch under the rule. Each good batch is its own lot. The rate check and counts work on the total, as before.

**Partial batches.** An exception for a line that stopped:

- **When.** Only with a pause or completion that gives a run-end reason (the partial batch is reported into the run being ended), or later into a run that ended with a reason. Never into an open run on its own, and at most one per run (`partial_batch_run_uq`).
- **What.** More than 0 and less than a batch (below the size less the tolerance). It goes into a lot of its own and a `core.partial_batch` row records it.
- **Exact rule.** The lot goes on hold (reason `PARTIAL` of kind `hold` when it exists) and the partial batch is `pending`. **Approve** releases the lot. **Reject** writes it off with a scrap reason and moves its quantity from good to scrap: the run's totals change and a `defective` count is added at the partial batch's time (good = processed − defective). A pending partial needs a location that keeps lots apart.
- **Near rule.** `accepted` at once; nothing to decide.
- **Locks.** Approve and reject lock the job, the partial batch, its run, then its lot, the reporting order (D-027), so they serialize with reports.
- **Events:** `pulaski.material.batching_set.v1`, `pulaski.partial_batch.reported.v1`, `…approved.v1`, `…rejected.v1`, with the lot's own movements.

**Not yet:** a batch size per line (a 50 kg mixer next to a 200 kg one), a rule for ERP orders that aren't whole batches (orders aren't imported yet), choosing to round a replacement down, and permissions for who may approve (settled by D-037: Supervisor or Quality, never whoever reported it).

## Users, roles and permissions (D-037, proposed)

Who may see and change what. **View permissions** decide which screens and data a user sees (D-039, which replaced "every signed-in user may view everything"); **change permissions** guard changes.

- **Permissions are constants in code** (`Pulaski.Domain.Security.Permissions`), the only place that checks them. Adding one is a code change: a constant, its catalog entry (area and description), and the endpoint that requires it.
- **Roles are data.** An administrator creates roles and chooses their permissions on the **Roles** and **Permissions** tabs of Configure → Users (`sec.role`, `sec.role_permission`). The only role the code knows is the **administrator**: it holds every permission everywhere, including ones added later, and can't be changed, so someone can always manage users.

**Roles.** Every role but the administrator is held **at a site**; a user may hold several, at several sites, and may do the union of them. A new database starts with these roles (`Roles.Defaults`), which the administrator may then change:

| Role | Held | May change |
| --- | --- | --- |
| Administrator | everywhere | everything, including users and roles |
| Planner | at a site | plan jobs; create, dispatch, un-dispatch and firm work orders; cancel and close them; planning rates; materials |
| Supervisor | at a site | start, pause, complete and reopen jobs; report production and consumption; receive, move, ship, hold and release lots; stock counts; approve or reject partial batches |
| Operator | at a site | start, pause and complete jobs; report production and consumption; receive and move lots |
| Quality | at a site | hold and release lots; correct inventory (write up, write off, correct, reverse, split, merge); approve or reject partial batches |
| Manager | at a site | reopen jobs; cancel and close work orders; reason codes; shift schedules (D-038); the site's settings |
| Auditor | at a site | correct inventory; stock counts. Doesn't plan or run jobs |
| Process engineer | at a site | processes, BOMs and capabilities |

Who owns the configuration: equipment, units → Administrator; shift schedules and which one equipment follows → Manager; materials (with classes, conversions, batching, shelf life and import) → Planner; processes, BOMs, capabilities → Process engineer; planning rates → Planner; reason codes and site settings → Manager; users → Administrator.

**Permissions.** 38 codes (12 views, D-039, and 26 changes; D-040 added the labor ones), each in an area (Planning, Floor, Inventory, Configuration, Administration) for the screens. They are mirrored for the UI in `Pulaski.Contracts.Security.PermissionCodes`, and the API copies the catalog into `sec.permission` at every start, so a new permission can be given to roles at once: `schedule.plan`, `work_order.manage`, `work_order.close`, `rate.modify`, `job.execute`, `job.reopen`, `production.report`, `lot.receive`, `lot.move`, `lot.ship`, `lot.hold`, `inventory.correct`, `stock_count.manage`, `partial_batch.decide`, `materials.manage`, `processes.manage`, `units.manage`, `reasons.manage`, `equipment.manage`, `site_settings.manage`, `shifts.manage` (D-038), `security.manage` (users, roles and what each role may do).

**Maintaining roles.**

- A role has a code (lower-case letters, digits and `_`, fixed once created), a name, a description and its permissions. `POST /api/roles` creates one; `PUT /api/roles/{code}` changes its name, description, active flag and all its permissions at once (with its version); `PUT /api/roles/{code}/permissions/{permission}` grants or takes away one (the Permissions screen; doing what is already so changes nothing). Each change moves the role's version on.
- A change applies at once to everyone who holds the role: what a user may do is read with their roles on every request.
- **Switched off, not deleted.** A role someone holds can't be switched off (every user keeps a role); a switched-off role can't be given. A permission taken away keeps its `sec.role_permission` row with `is_granted = false`, since the app role has no DELETE; the events say who changed what.
- Events: `pulaski.role.created.v1`, `pulaski.role.updated.v1` (before and after), `pulaski.role.permission_changed.v1`.

**Where it is checked.** In the API, on every changing endpoint (`.RequirePermission(permission, site)`): the site is found from what the request is about (the job, lot, count, process, partial batch, capability, run standard or work order, or the location or line in the body) by `ISiteLookup`. Something that belongs to no site, such as an enterprise-wide process or reason, needs the permission at any site. A refusal is 403 with the permission in words. The UI hides or switches off what the user may not do, but the API decides.

**Rules.**

- **Every user belongs to a role.** A user is created with one; their last active role can't be taken away (switch them off instead); signing in without a role is refused.
- **Someone stays administrator.** The last active administrator can't lose the role or be switched off, and no one can switch themselves off.
- **Not your own partial batch.** Whoever reported a partial batch can't approve or reject it (`PartialBatchLocks.EnsureNotOwn`, 403).
- **Ended, not deleted.** Role assignments get `valid_to` and `ended_by_user_id`, so history keeps who could do what.

**Identity.** Pulaski signs users in itself for now, with an identity provider to follow:

- **Local users** (issuer `pulaski-local`, subject = lower-case user name). Passwords are PBKDF2-SHA256 with 210,000 iterations and a random salt, stored as `pbkdf2-sha256$iterations$salt$hash` in `sec.local_credential`. At least 10 characters, not containing the user name, with two of lower case, upper case, digits and symbols. Five wrong passwords in a row lock the user for 15 minutes. An administrator sets a temporary password, which must be changed before anything else (the API answers 403 to everything but changing it and `/api/me`).
- **Tokens.** `POST /api/auth/sign-in` returns a bearer token (HMAC-SHA256 JWT, issuer `pulaski-local`, audience `pulaski`, 12 hours) signed with `Security:SigningKey` (at least 32 characters; the API won't start without it). What the user may do is read from the database on every request, so switching a user off or taking a role away applies at once.
- **The UI** keeps the token in an HTTP-only session cookie that ends with it. Signing in and changing a password are plain (not interactive) pages, because they set the cookie. When the API stops accepting the token, the UI signs the user out and asks them to sign in again.
- **The first administrator** is created at API start when there are no users at all, from `Security:BootstrapAdmin` (user name and a temporary password).
- **Identity providers** (Okta, Entra ID, AD FS, Keycloak; the charter's OpenID Connect) come later as another JWT bearer scheme in the API. `sec.app_user` already keys users by `(idp_issuer, idp_subject)`, and roles stay in Pulaski; `idp_group` on `sec.role_assignment` is reserved for mapping a provider's groups to roles.

**Events:** `pulaski.user.signed_in.v1`, `…sign_in_failed.v1`, `…password_changed.v1`, `…password_reset.v1`, `…created.v1`, `…updated.v1`, `…role_assigned.v1`, `…role_ended.v1`, on the user. Event actors are the signed-in user (local users by user name).

**Not yet:** a role held everywhere other than the administrator; signing in through an identity provider; correcting reported production or consumption (no command exists yet, so the Auditor's "correct production values" waits for it); site settings screens (the API is guarded).

## Shift schedules (D-038, proposed)

When each line works, for planning. Plants may run several schedules (a two-shift packing hall next to a continuous extrusion line), so each piece of equipment chooses its own or inherits its parent's.

**A schedule** (`core.shift_schedule`) belongs to a site, so it uses the site's time zone. It has a code (upper case, fixed once created), a name, a cycle of 1 to 56 days (7 for a weekly schedule, 28 for a four-crew rotation) and the date the cycle starts (`cycle_start`). Its shifts are one `jsonb` document, saved whole like a process draft so editing never deletes rows: each shift has a code, a name, a start time ("HH:mm", site-local), a length (15 minutes to 24 hours) and the days of the cycle it runs on (0 is the first day). Shifts may run past midnight; they may not overlap, also across the end of the cycle. A shift belongs to the production day it starts on.

**Equipment's working time** (`core.equipment.calendar_mode`, `shift_schedule_id`):

| Mode | Means |
| --- | --- |
| `inherit` (default) | Its parent's working time. At the top of the tree with nothing set, around the clock |
| `schedule` | A schedule of its own site, which must be active |
| `around_the_clock` | No shifts, whatever its parent follows (a furnace that never stops) |

A line's working time comes from the nearest equipment, the line itself first, that doesn't inherit. The enterprise sits above the sites and has no schedules; it can only inherit or run around the clock.

**Worked out on read, not stored (amends D-014).** Shift times are computed from the schedule by a pure function (`ShiftSchedule.Expand` in `Pulaski.Domain.Shifts`), like the projected job times, for the planning window plus a month. So a change to a schedule, or to which one equipment follows, moves the projection at once, and nothing has to be generated ahead, regenerated or deleted. Clock times hold across daylight saving: a shift starts at its clock time; one that would start in the hour the clocks skip starts when they have gone forward; its length is elapsed time. `core.shift_instance` is no longer read by planning; it is kept for recording shifts as worked (reporting by shift, crews), which isn't built. The never-filled `core.shift_pattern` and `core.shift_definition` tables were replaced.

**Rules.** A schedule equipment follows can't be switched off; give the equipment another first. A switched-off schedule can't be chosen. Schedules are switched off, not deleted. The timeline names where a line's time comes from ("2X8 · Two shifts, from PLT1") and no longer warns about a line set to run around the clock on purpose.

**Who.** `shifts.manage` (D-037), held by the Manager role to start with: maintaining schedules (at their site) and choosing which one equipment follows (at its site). The latter is saved apart from the rest of the equipment (`PUT /api/equipment/{id}/calendar`), so a manager can set it without maintaining equipment.

**API.** `GET /api/shift-schedules?siteId=`, `GET /api/shift-schedules/{id}`, `POST /api/shift-schedules`, `PUT /api/shift-schedules/{id}` (with its version), `POST /api/shift-schedules/preview` (works out a schedule, saved or not, for the next days; saves nothing), `PUT /api/equipment/{id}/calendar`. Events: `pulaski.shift_schedule.created.v1`, `pulaski.shift_schedule.updated.v1` (before and after), `pulaski.equipment.calendar_set.v1`.

**Not yet:** reports, counts and OEE by shift; the Overview's strip by shift rather than a rolling 24 hours. One-off changes to a shift are D-042.

## Seeing only what you may (D-039, proposed)

Amends D-037, where every signed-in user could view everything. A user now sees only the screens, and the sites, their roles allow, and Pulaski doesn't advertise the rest: no menu entry, link or button for it.

**View permissions.** One per screen, in the *Viewing* area of the catalog, given to roles like any other:

| Permission | Screen |
| --- | --- |
| `overview.view` | Overview |
| `work_orders.view` | Work orders |
| `schedule.view` | Schedule |
| `floor.view` | Floor and job pages |
| `inventory.view` | Inventory, all its pages |
| `equipment.view` | Equipment |
| `materials.view` | Materials (with classes, capabilities and rates) |
| `processes.view` | Processes and BOMs |
| `units.view` | Units |
| `reasons.view` | Reasons |
| `shifts.view` | Shifts |

Users, roles and permissions stay behind `security.manage`.

**A change brings its views** (`Permissions.Implied`): whoever may change something may see where it is changed. For example `schedule.plan` brings the schedule and work orders; `processes.manage` brings processes and materials (capabilities are edited on the material page); `shifts.manage` brings shifts and equipment (a line's shifts are chosen there). The Permissions and Roles tabs show such a view ticked and locked.

**Starting roles.** Administrator, Auditor and Manager see everything. Planner: Overview, Work orders, Schedule, Floor, Inventory, Materials, Processes. Supervisor and Operator: Overview, Floor, Inventory, Work orders. Quality: Overview, Floor, Inventory, Materials. Process engineer: Equipment, Materials, Processes, Units. Migration 0013 adds these to the starting roles; other roles gain nothing, so an administrator decides what they see.

**The API checks every read.** Each GET endpoint needs one of its views, at the site the request is about (`.RequireAny(site, …)`). A list for one site needs the view there; one without a site is a request for every site and needs the view everywhere (`.RequireAnyAtSite`). Lookups that many screens need (the equipment tree, units and conversions, materials, classes, reason codes, processes and BOM lists) need only some view (`.RequireAnyView()`). Lists that span sites keep to the caller's sites (`Visible.At`): the equipment tree, work orders and their progress, processes, shift schedules and reason codes. A refusal is 403 with the view in words.

**The UI.** Each page names the views that open it (`[RequiresView]`). The layout checks it once the user is known: a page the user may not see answers **Page not found** (so its feature isn't named), and someone whose roles allow no screen at all gets a page saying so, with a sign-out button. The start page is the Overview, or the first screen in the menu the user can see. The menu shows only the screens the user can see, and leaves out a group with none. Links to other screens (`ScreenLink`) show as plain text, or not at all for buttons, when the user can't open them; actions the user can't take (selecting work orders to move, an empty action menu) are left out.

**Not yet:** the site chosen on a screen can be one where the user may see something else but not that screen; the API then refuses the data and the screen says so.

## Labor (D-040, proposed)

Labor for production: who worked where, on what, for how long, against the standard crew. Not time and attendance or payroll (charter: not an ERP); hours export as CSV for HR or the ERP.

**People and crews** (`core.person`, `core.crew`). A person belongs to a site, has an employee number (unique per site), a name, an optional crew, and optionally the Pulaski user they sign in as, so they can clock themselves on; most operators needn't sign in. A crew has a code (upper case, fixed once created) and a name. Both are switched off, not deleted; someone clocked on can't be switched off. Maintained on Configure → People (`people.manage`).

**Crews on shifts (amends D-038).** Each shift in a schedule may name the crew that works it, by code; a rotation is several shifts at the same time on different days of the cycle, each with its crew. So Pulaski knows which crew is on a line now. A schedule may only name active crews of its site.

**Sessions** (`core.labor_session`): a person, a line (or other equipment), an optional job, a type, a start and an end (open while working), a share of the person's time (default 100%), and how it was recorded (`self`, `supervisor`, `crew`, `correction`).

| Type | Means | Counts towards |
| --- | --- | --- |
| `direct` | Running the line | The runs on the line while it lasted, by overlap; a session naming a job only that job's runs. Time with nothing running is kept as idle |
| `setup` | Getting a job ready | Its job (required), running or not |
| `indirect` | Cleaning, meetings, waiting | No job; needs an `indirect_labor` reason |
| `training` | Training | No job |

Sharing out by overlap means nobody re-clocks when the job on a line changes. Rules: a person is on a line at most once at a time; their open sessions together are at most 100% (someone tending two machines is 50% on each); times can't be ahead of now; setup needs a job, indirect a reason. Starting locks the person, so two clock-ons serialize; a crew check-in locks its people in id order (in SQL).

**Shifts and approval.** A session belongs to the shift running when it started, or the one starting within two hours (someone clocking on early), from the schedule the line follows (D-038); with none, to the site-local day. The shift is stored on the session (`production_date`, `shift_schedule_id`, `shift_code`). A supervisor approves a shift once it has ended (`core.labor_approval`, one per site, day and shift): open sessions in it end when the shift ended, and from then on its sessions are locked. A correction is a new session (`corrects_session_id`) that replaces one, or with `is_void` removes it, and needs a reason; the original stays for history and leaves the hours. Approving and correcting need `labor.approve`.

**Recording.** A supervisor (`labor.record`) checks the crew onto a line in one action (the shift's crew is ticked, minus anyone already on another line), adds someone, ends time, checks everyone out, and fixes sessions until the shift is approved. An operator who is a person and has `labor.record_own` clocks themselves on and off from the Floor. There is no background service: an open session simply runs until someone ends it or the shift is approved.

**Reports** (`labor.view`), for up to 93 production days: by job (run hours, direct and setup hours, standard hours = run hours × the capability's `standard_crew_size`, efficiency = standard ÷ direct), by person (direct, of which idle, setup, indirect, training) and by shift (sessions, hours, approval). Each exports as CSV.

**Permissions** (D-037, D-039): `labor.view` (the Labor screen), `labor.record_own`, `labor.record`, `labor.approve`, `people.manage`. Starting roles: Operator clocks on; Supervisor sees, records and approves; Manager approves and maintains people; Auditor and Manager see everything.

**Events:** `pulaski.crew.created.v1`, `…updated.v1`, `pulaski.person.created.v1`, `…updated.v1`, `pulaski.labor.started.v1`, `…ended.v1`, `…changed.v1`, `…corrected.v1`, `…shift_approved.v1`.

**Not yet:** badges and terminals (`sec.badge`); people from HR or the ERP; labor by shift on the Overview; a person working at another site than their own; editing a session's job or type on the review screen (only times and share). Breaks are D-047, qualifications D-048.

## Shift overrides (D-042, proposed)

Amends D-038. A plant changes its shifts for a day without changing its schedule: an extra Saturday shift for a rush order, a night shift that starts late after a stocktake, a day shift off on a local holiday for one line's schedule.

**An override** is one production day and one shift code of a schedule: the shift runs at a start time ("HH:mm", site-local) for a length (15 minutes to 24 hours) with an optional crew and name, or (`isWorking` false) doesn't run that day. A code the cycle runs that day is replaced for that day; a code it doesn't run is added as an extra shift. A day off needs a shift the cycle runs that day. A note (up to 500 characters) says why. Start, length, name and crew default to the cycle's shift of that code.

**Kept with the schedule.** Overrides are a second `jsonb` array on `core.shift_schedule` (`overrides`), one entry per day and code, saved whole like the shifts, so removing one never deletes a row and the schedule's version covers both. At most 1000 per schedule.

**Applied wherever shifts are worked out.** `ShiftSchedule.Expand` applies them, so the projection (D-014), labor's shift of a session (D-040), labor approval and reports by shift all see the same shifts. An override's shifts may not overlap their neighbours on that day or the days either side (checked on clock time, like the cycle); changing the cycle is checked with the overrides too. Shifts from an override are marked (`isOverride`) in the preview.

**Locked by approved labor.** An override can't be set or removed for a shift on a day whose labor is approved (D-040), so approved hours never move under a changed shift.

**Who and API.** `shifts.manage`, at the schedule's site. `PUT /api/shift-schedules/{id}/overrides` (version, date, code, isWorking, and optional name, start, minutes, crew, note) sets one; `DELETE /api/shift-schedules/{id}/overrides/{date}/{code}?version=` removes it. Schedules carry them (`overrides`), and the preview takes them. Events: `pulaski.shift_schedule.override_set.v1` (before and after), `pulaski.shift_schedule.override_removed.v1`.

**Not calendar exceptions.** A calendar exception (`core.calendar_exception`, D-043) takes time out of everything below a piece of equipment, whatever schedule it follows: a plant shutdown. An override changes one schedule's shift.

## Site settings (D-043, proposed)

Amends D-015, D-035 and D-039. The settings a site has, which until now could be set only through the API, are maintained on **Configure → Site settings** (`/site-settings`), one part per setting, each saved on its own.

| Part | Stored in | Rules |
| --- | --- | --- |
| Changeovers | `core.scheduling_policy.changeover_mode` (D-015) | `auto` or `scheduler`; version 0 while the site uses the default |
| Reporting tolerances | `core.execution_policy` (D-035) | Output and consumption, 100 to 1000%; version 0 while the site uses the defaults (110%) |
| Calendar exceptions | `core.calendar_exception` | A window on a site or anything in it (not the enterprise), up to 366 days, of kind holiday, planned shutdown, no demand or other, with a note |

**Calendar exceptions.** They take time out of every line at or below their equipment, whichever schedule it follows, and out of around-the-clock time too (the projection already read them; D-014). They are switched off (`is_active`), never deleted, so the app role needs no DELETE and a removed one can be put back; the projection reads only active ones. A new or changed one needs its equipment active, and an exception's equipment never changes (remove it and add another). They may overlap. `GET /api/calendar-exceptions?siteId=&from=&includeRemoved=`, `POST /api/calendar-exceptions`, `PUT /api/calendar-exceptions/{id}` (with its version; `isActive` false removes). Events: `pulaski.calendar_exception.created.v1`, `…updated.v1`, `…removed.v1`.

**Reporting tolerances are versioned.** `PUT /api/execution/sites/{siteId}/policy` now takes the version it read (`xmin`, 0 for the defaults) and answers 409 on a mismatch, like every other change (it used to overwrite).

**Viewing (amends D-039).** A thirteenth view permission, `site_settings.view`, opens the screen and the policy reads; `site_settings.manage` brings it. Planner, Supervisor, Manager and Auditor have it to start with (migration 0016); a viewer sees every value with the inputs switched off. The policy reads also stay open to `schedule.view` and `floor.view`, and the exception list to `schedule.view` and `shifts.view`, since those screens show their effects.

**The screen explains its effects.** The output tolerance is a limit (a report beyond the run's length × ideal rate × tolerance is refused, D-035); the consumption tolerance is a warning. Each shows a worked example as it is typed.

**Not here:** the changeover rule matrix (`core.changeover_rule`) still has no screen.

## Production by shift (D-044, proposed)

Amends D-038 ("reports by shift, not yet"). Plants run their shift meetings on what each shift made, so good and scrap are reported by shift, with line, job and material.

**Attribution.** A row of `ts.production_count` (D-035) belongs to the shift running on its line at its `occurred_at`: from the schedule the line follows (its own or the nearest ancestor's, D-038), with that schedule's overrides (D-042), worked out by `ShiftSchedule.Expand`. A report into an open run occurs when it is entered; one into an ended run occurs at the run's end (D-035), so it counts in the shift the run ended in. The shift's production day is the day it starts, so output after midnight on a night shift counts towards the evening it began. A count between shifts, or on a line with no shifts or running around the clock, belongs to the site-local day with no shift. `ShiftProduction.Total` in `Pulaski.Domain.Execution` is the pure function; nothing is stored, so changing a schedule re-attributes past counts too (as with the projection, D-014). `core.shift_instance` stays unused.

**Counts are points, not spans.** A count is attributed whole to one shift; a long run reported once at its end isn't spread across the shifts it ran through. Reporting at each shift's end splits it. Spreading by run time would invent figures nobody reported.

**Reconciling.** Every count lands in exactly one row (per production day, shift, line, job and unit). Good = processed − defective, as on the job page, so a rejected partial batch (D-036) moves good to scrap in the shift it was made. Each job's rows are set against its runs' totals (`core.job_run`), and match when all its reports fall in the chosen days.

**API and screen.** `GET /api/execution/sites/{siteId}/production-by-shift?from=&to=` (production days, at most 93 apart, `floor.view`); the result's times are in the site's time zone. Floor → **Production by shift** (`/floor/production`), with CSV export. No new tables.

**Not yet:** OEE and downtime by shift (machine state and downtime aren't recorded); the stop Pareto (issue #21) waits for them.

## Planning rates from run history (D-045, proposed)

Amends D-016 and its build pass 3, which planned a worker. The calculated rate can come from what the line actually made, but only when a planner asks, sees how it was worked out, and accepts it: a rate that moved by itself would move every projection with it.

**The calculation** (`RunHistoryRate` in `Pulaski.Domain.Scheduling`, pure and repeatable). The sample is the ended runs (`core.job_run`) of production jobs of the run standard's material on its line that ended in the last 7 to 365 days (90 by default). Each run's good output is converted to the run standard's unit (D-026); its rate is good ÷ elapsed hours. Left out, each with its reason: runs under 30 minutes, runs with no good output, runs whose unit doesn't convert, and, with at least four runs, runs outside Tukey's fences (Q1 − 1.5 IQR, Q3 + 1.5 IQR), widened to at least 10% either side of the median so a very steady line keeps its normal spread. With fewer than 5 runs or 8 hours left there is no rate. Otherwise the rate is the used runs' total good ÷ their total hours (time-weighted, rounded to 2 places). Scrap doesn't count.

**Review, then accept.** `GET /api/scheduling/run-standards/{id}/history-rate?days=` returns the window, every run with its rate or why it was left out, the fences, the totals and the suggested rate next to the calculated, ideal, modified and effective rates; it saves nothing (`materials.view` or `schedule.view`). `POST …/history-rate` (version, days, the reviewed rate; `rate.modify`) locks the run standard, works the rate out again and refuses with 409 if it is no longer the reviewed one; otherwise it sets `calculated_rate_per_hour` with basis `run_history` (D-016 `Recalculate`). The modified rate is never touched, so drift between them stays visible. Event `pulaski.run_standard.recalculated.v1`, with the window and sample counts.

**Back to the ideal rate.** `POST …/ideal-rate` (version) sets the calculated rate to the capability's ideal rate in effect now, in its unit, with basis `ideal_rate`, so it follows the capability again (D-034). Refused when no capability is in effect; a unit change under a modified rate is refused as before.

**Run time is elapsed time.** Without downtime capture a run's stops can't be taken out, so the rate is a realistic planning rate (it includes the stops a run usually has) rather than a performance rate. When downtime is recorded the sample can be refined.

**Not yet:** a scheduled recalculation or a notice when history drifts far from the rate in force; per-shift or per-crew rates.

## Process links in the projection (D-046, proposed)

Amends D-014, D-019 and D-024. On the floor a job can't start until its process links are met (D-035); the projection now plans it that way, so a Pack job is never projected before the Extrude job it follows.

**The rule, as on the floor** (`JobReadiness`). For each link into a job's operation, the jobs of the same work order on the link's earlier operation (not cancelled) decide: `after_start`, the first of them to start; `after_end`, the last of them to end; then the link's lag, as elapsed time. A job with several links waits for the latest. The times are the projected ones for jobs on a line, actual ones once a job has started or ended, and a group job's window (start; finish-by for `after_end`) while it waits on a department (D-022). The result is the job's `LinkReadyAt`, which works like a hold: an unstarted, unfirmed job starts no earlier.

**Linked lines are projected together** (`LinkedProjection` in `Pulaski.Domain.Scheduling`, pure). A line's plan is loaded with the lines its jobs follow, and theirs in turn (upstream only, at most 40 lines); every caller of the projection (the line timeline, the board, the Overview, work order progress, shop orders, the placement preview, the move of a firmed job and dispatch) goes through it, so all agree and the published baseline matches the board. The projector runs on every line, the holds from that pass feed the next, and it stops when nothing moves. Holds only move later, so it settles unless jobs wait on each other in a circle.

**Infeasible placements are reported, never fixed** (D-014: Pulaski doesn't reorder or move jobs).

| Warning | When |
| --- | --- |
| `link_not_met` | A firmed job's anchored start (D-019) is before its link allows. It keeps its time |
| `link_cycle` | After 16 passes the job's hold still moves: it waits, through the lines' sequences, on work that waits for it (e.g. Pack sequenced before Extrude on the same line). Its times are the last pass's |

A running or paused job has started, so no link applies to it. The timeline carries each linked job's `linkReadyAt`; the board's side panel shows it.

**Not here:** links don't constrain a group's window (its jobs are checked on their own, D-022), and the projection still doesn't share a group's capacity.

## Breaks (D-047, proposed)

Amends D-040. Labor reports separate working time from breaks, so payroll and efficiency don't count a lunch as time on the line.

**A break is its own interval** (`core.labor_break`): a person, a start, an end (open while on break), paid or unpaid, a note. It never changes the person's sessions: their start, end and share stay as recorded, so allocation (D-040), approval and the sharing out to runs keep working on what was recorded.

**Worked time.** A session's worked hours are its elapsed time outside the person's breaks × its share (`LaborRules.WorkedHours`, `WorkingPieces`); the run shares (`LaborRules.Attribute`) are worked out from the same pieces, so a break also comes out of direct time on the job running then. Someone on two lines at 50% who takes a 30-minute break loses 15 minutes on each. Break hours (`LaborRules.BreakHours`) count each break once, only while the person was on, split into paid and unpaid. Reports show worked hours with paid and unpaid break hours beside them; paid breaks are for payroll to add back.

**Rules.** A break starts while the person is on a line (inside a session that counts) and belongs to that session's shift (its production day, schedule and code). It lasts at most 4 hours, isn't ahead of now, and doesn't overlap another of their breaks; at most one is open per person. It ends when the person ends it, when they leave their last line (clock-off or check-out), or when the shift is approved. Approved, it is locked like the sessions: a correction (`corrects_break_id`, `labor.approve`, with a reason) replaces it or with `is_void` removes it.

**Who and API.** An operator takes their own (`labor.record_own`, `POST /api/labor/me/breaks`, `…/me/breaks/{id}/end`); a supervisor records and changes others' (`labor.record`, `POST /api/labor/breaks`, `…/{id}/end`, `PUT /api/labor/breaks/{id}`); `POST /api/labor/breaks/{id}/correct` needs `labor.approve`. Sessions carry `hours` (worked) and `breakHours`; shifts and the by-person report carry paid and unpaid break hours; a shift's review lists its breaks. Events: `pulaski.labor.break_started.v1`, `…break_ended.v1`, `…break_changed.v1`, `…break_corrected.v1`.

**Not yet:** breaks planned on a shift schedule (a fixed lunch), filled in on their own.

## Qualifications (D-048, proposed)

Amends D-040 ("qualifications, not yet") and replaces the planned `core.qualification`, `person_qualification` and `equipment_qualification` tables, which were never written (migration 0017).

**A qualification** belongs to a site: a code (upper case, fixed), a name, a description and how long a grant lasts by default (`validity_months`, or no expiry). Switched off, not deleted.

**A grant** gives one to a person of the same site for whole site-local days, `valid_from` to `expires_on` inclusive (no expiry when null). Without days it runs from today for the qualification's validity. A renewal is a new grant, so the history stays; a person holds the qualification on a day when any grant is valid then. A grant's days and note can change; it is revoked (with a reason, from now) rather than deleted, and a revoked grant can't change. States: valid, not yet, expired, revoked (`QualificationRules.Status`).

**Requirements.** Equipment requires a qualification of its site for itself and everything below it (`equipment_qualification`, read through `equipment_closure`), so requiring forklift on an area covers its lines. Switched off with `is_required`, not deleted.

**Warn, never refuse.** Clocking someone on or checking a crew in still happens when they lack a required qualification on the session's site-local day; the response's notes say what is missing and why (not held, expired, revoked, not valid yet), and the Labor screen marks them *Not qualified* while they are on (`QualificationRules.Missing`). Like consumption beyond plan (D-035), the floor knows best; the warning makes the gap visible.

**Who and API.** `people.manage` maintains qualifications and grants (`GET/POST /api/qualifications`, `PUT /api/qualifications/{id}`, `GET/POST /api/qualifications/grants`, `PUT …/grants/{id}`, `POST …/grants/{id}/revoke`); the grants list filters by person, qualification and `expiringWithinDays`. `equipment.manage` sets requirements (`GET/PUT /api/equipment/{id}/qualifications`). Reads need `labor.view` (and `equipment.view` for the list and requirements). Events: `pulaski.qualification.created.v1`, `…updated.v1`, `…granted.v1`, `…grant_changed.v1`, `…revoked.v1`, `pulaski.equipment.qualification_set.v1`.

**Not yet:** refusing unqualified people where a plant wants it; qualifications from HR; notices before a grant expires.

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
- Events record the signed-in user as their actor (D-037): a local user by user name, anyone else as `issuer|subject`. Only signing in itself runs as `anonymous`.

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
