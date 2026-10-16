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
| D-025 | Bills of materials: single level, one per material and optionally per process, whose items name the operation that consumes them; versioned like processes (draft, released, obsolete). A work order takes the released BOM and plans each job's components. Pulaski owns lots on the floor; the ERP stays the book of record for stock | Proposed | 2026-10-15 |
| D-021 | Scheduling board: Gantt, calendar, Kanban and table views over one live board. Each view edits only what the planner owns (line, sequence, pins, order status); overlaps are reported by the projector and shown on every view; the board re-reads every 10 s | Proposed | 2026-10-14 |

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
3. Time accrues only inside staffed shift instances, minus calendar exceptions. Shift instances come from the nearest ancestor of the line that has any. If no instances exist, the line is treated as running 24/7 and the timeline says so. Past the last generated shift, the projection continues around the clock and flags the segments.
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
| `in_progress`, `completed`, `closed` | `running`, `paused`, `completed`, `closed` | Execution, built in a later pass. |
| `cancelled` | `cancelled` | A work order with open jobs can't be cancelled; cancel its jobs first. |

- Dispatch, un-dispatch and firm are their own commands (`POST /api/work-orders/{id}/dispatch`, `/undispatch`, `/firm`), because they change the jobs too. The plain status change refuses those three transitions.
- Dispatch refuses an order with no jobs, or with a job whose line can no longer run its material. When the jobs don't add up to the order quantity, dispatch warns (`quantity_mismatch`) but goes ahead.
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
3. Calculated rates from run history (a worker), with drift against the modified standard.
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
- **Times.** The start is required. Every job is held until it (`earliest_start`), and an operation that follows others is held until their hold plus the link's lag, the longest path winning. That is only a lower bound: the projection does not yet hold a job until the work before it has started or ended. Making the projector follow the links is a later pass.
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
- **Planned components.** Creating a work order (D-024) writes one `core.job_material` row (`consume`) per item and job of the consuming operation. An item's total is order quantity ÷ base quantity × quantity per × (1 + scrap factor), rounded to six places; the operation's jobs share it in proportion to their quantities, the last taking the remainder. The order's unit must be the BOM's base unit; unit conversion comes later.
- **Inventory.** The ERP is the book of record for warehouse stock. Pulaski tracks lots on the floor, from issue or receipt at the line through loading, consumption and production, and reports consumption and output back to the ERP as events. Receiving and putaway, balances outside the plant floor and purchasing stay in the ERP (charter: not an ERP).
- **Build passes.** 1. BOMs and planned components (this pass). 2. Lots on the floor: receive or issue a lot to a location (equipment with `holds_inventory`) and load it onto a feed point (`core.material_load`). 3. Consumption, with execution: runs and counts, then each item consumed by its method, and genealogy (charter slice step 4).

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
