# Pulaski User Guide

*Last updated: 2026-10-24 · Pulaski is pre-alpha: screens and behaviour will change, and this guide changes with them.*

This guide explains how to use Pulaski to set up your plant, plan production and follow your orders. It is written for everyone who uses the application. If you are new to manufacturing software, start with [Getting started](#1-getting-started) and [Key ideas](#2-key-ideas) and follow the numbered steps in each section. If you already know MES and ERP systems, skim the key ideas, then jump to the section you need; the **In detail** parts and the [reference](#21-reference) tables give the exact rules.

## Contents

1. [Getting started](#1-getting-started)
2. [Key ideas](#2-key-ideas)
3. [Setting up: what to do first](#3-setting-up-what-to-do-first)
4. [Equipment](#4-equipment)
5. [Processes](#5-processes)
6. [Units and conversions](#6-units-and-conversions)
7. [Bills of materials (BOMs)](#7-bills-of-materials-boms)
8. [Creating a work order](#8-creating-a-work-order)
9. [The Work orders screen](#9-the-work-orders-screen)
10. [The Schedule](#10-the-schedule)
11. [The Floor: running jobs and reporting production](#11-the-floor-running-jobs-and-reporting-production)
12. [Inventory](#12-inventory)
13. [Materials](#13-materials)
14. [Users, roles and signing in](#14-users-roles-and-signing-in)
15. [Shift schedules](#15-shift-schedules)
16. [Labor, people and crews](#16-labor-people-and-crews)
17. [Site settings](#17-site-settings)
18. [Machine profiles](#18-machine-profiles)
19. [Edge nodes: collecting machine data](#19-edge-nodes-collecting-machine-data)
20. [Machines: live status and stops](#20-machines-live-status-and-stops)
21. [Reference](#21-reference)
22. [What Pulaski doesn't do yet](#22-what-pulaski-doesnt-do-yet)
23. [Troubleshooting and questions](#23-troubleshooting-and-questions)
24. [Glossary](#24-glossary)

---

## 1. Getting started

### What Pulaski is

Pulaski is a manufacturing execution system (MES) for lines that run continuously or in long batches: extrusion, moulding, compounding and high-speed packaging. It sits between your business system (the ERP, which handles sales, purchasing and accounting) and the machines on the floor.

Today you can use Pulaski to:

- describe your plant: sites, areas, lines and departments;
- describe how products are made (**processes**) and what they are made from (**bills of materials**);
- create **work orders**, which Pulaski turns into **jobs** on your lines;
- plan and sequence those jobs on a live **schedule**, and see when each one is expected to run;
- publish the plan to the floor and freeze it;
- keep **inventory**: receive material lots into stores, write stock up or off, move and hold lots, record what each job used and made, and trace any lot back to what it was made from or forward to what was made from it.

Recording what actually happens on the machines (running jobs, counts, downtime) is still being built; see [What Pulaski doesn't do yet](#22-what-pulaski-doesnt-do-yet).

### Opening Pulaski

Open Pulaski in a web browser at the address your administrator gives you (on a developer's machine it is `http://localhost:5090`). Pulaski works best in a current version of Chrome, Edge, Firefox or Safari, on a desktop or a tablet in landscape.

Pulaski asks you to **sign in** with the user name and password your administrator gave you; the first time, you choose your own password. Your **roles** decide which screens you see and what you can change on them. See [Users, roles and signing in](#14-users-roles-and-signing-in).

### The screen

- **Menu (left).** Two groups of pages:
  - **Operate:** **Overview**, **Work orders**, **Schedule**, **Floor**, **Inventory**, **Labor**: the pages you use day to day.
  - **Configure:** **Equipment**, **Materials**, **Processes**, **BOMs**, **Units**, **Reasons**, **Shifts**, **Site settings**, **Integrations**, **People** (and **Users**, for administrators, with tabs for users, roles and permissions): the setup you do once and change now and then.
  - The ☰ button at the top left collapses the menu to icons, to make room on small screens.
- **You (top right).** Your name and roles; the padlock changes your password, and the door signs you out.
- **Theme (top right).** The sun/moon button switches between light and dark.
- **Main area.** The page you picked.

### How saving works

Pulaski saves in two different ways, depending on the screen. Each section says which one it uses.

| Screen | How changes are saved |
| --- | --- |
| Equipment properties | Straight away, when you press **Enter**, leave the field, or pick a choice. **Esc** undoes the edit before it is saved. |
| Process editor | Straight away, after every change. The top of the editor says **Saving…** and then **All changes saved**. |
| BOM editor | When you press **Save draft**. The button says **Saved** when there is nothing left to save. |
| Forms (new work order, new process, new BOM) | When you press the form's button (**Create**, **Create and edit**). |
| Schedule | Every drag and every button acts straight away. Only cancelling a job asks you to confirm. |

### Times and time zones

Every time you see or type in Pulaski is in the **site's time zone**, not your computer's. Each site has its own time zone, set when the site is created. Forms remind you which zone they use, for example *Times are in the site's time zone (America/Chicago)*.

### When someone else changed the same thing

Several people can work in Pulaski at once. If you save something that another person changed after you opened it, Pulaski refuses your change and tells you, for example *Work order WO-1001 was changed by someone else. Reload and try again.* Nothing is lost: reload the page (or wait for the schedule's next refresh), look at the current version, and make your change again.

### Messages and errors

- **Red or yellow boxes** at the top of a page explain why something was refused, in plain words: *Material PRF-5100-BR is not set up to run on EXT-02; add a capability for it first.* Fix what it says and try again.
- **Pop-up notices** at the bottom of the Schedule confirm actions and warn about new problems; they disappear after a few seconds, or close them with ×.

---

## 2. Key ideas

You will meet these words throughout Pulaski. Most follow international standards for manufacturing systems (ISA-95 and ISA-88), so they match what other systems and consultants use.

### Your plant: equipment

Everything physical is **equipment**, arranged in a tree from the top of the company down to individual machines:

```
Enterprise          DEMO     the company
└─ Site             PLT1     one plant, with its own time zone
   ├─ Area          EXT      Extrusion area (a department)
   │  ├─ Line       EXT-01   Extrusion line 1
   │  │  ├─ Unit    EXT-01-EXT   the extruder
   │  │  └─ Unit    EXT-01-SAW   the saw
   │  └─ Line       EXT-02   Extrusion line 2
   └─ Area          PKG      Packaging
      └─ Line       PACK1    Packer 1
```

Two settings on equipment matter most for planning:

- **Can run jobs:** work actually runs here. Lines (and cells) have this.
- **Schedulable:** work can be *planned* to it.

A line is both. A **department** (for example the Extrusion area) is schedulable but doesn't run jobs itself: you can plan work to "Extrusion, by Monday", and the floor decides which of its lines runs it. Pulaski calls this a **group**; see [Planning to a department](#planning-to-a-department).

### What you make: materials

A **material** is anything you make or use: a finished profile, a resin, a dry blend, a pallet, an end cap. Each material has a code (its item number), a base unit (metres, kilograms, each) and usually a **material class** that groups similar materials, such as *Extruded profiles*. Materials are kept on the [Materials](#13-materials) page; those your ERP sends keep their code, name, base unit and type in the ERP.

**Capabilities** say which material can run on which line, and how fast (its ideal rate). Pulaski never puts a material's job on a line without a capability for it. You add them on the material's page ([Where it can run](#where-it-can-run-capabilities-and-rates)).

### Units of measure

Every quantity has a **unit**: kilograms, metres, each, boxes. Each material has a **base unit**, the one its stock is counted in. You don't have to use it everywhere: you can order a profile in feet even though it is stocked in metres. Pulaski converts:

- **Standard units** of the same kind convert for every material: kg ↔ lb, m ↔ ft, ea ↔ dozen.
- **A material's own conversions** cover the rest: 1 m of a profile weighs 1.6 kg; 1 box holds 500 end caps; 1 bag holds 25 kg of resin.

### How you make it: processes

A **process** (a "route") is a reusable recipe for *where* an order runs: "Extrude and Pack" runs operation 10, *Extrude*, on EXT-01 and EXT-02, then operation 30, *Pack*, on PACK1. Each operation names one or more lines or departments, each taking a share of the quantity. Processes are versioned: you edit a draft and release it, and orders are made from the released version.

### What you make it from: bills of materials

A **bill of materials (BOM)** lists what one material is made from: "1000 m of door frame profile takes 1450 kg of blend and 90 kg of capstock at extrusion, then 84 end caps at packing". When you create an order, Pulaski uses the BOM to work out how much of each component every job will need.

### Orders and jobs

- A **work order** is a request to make a quantity of one material, by a due date, using one process.
- A **job** is the piece of a work order that runs on one line (or waits on one department). One order usually has several jobs: one per line in each operation.
- Later, a **run** will record the actual execution of a job.

### What you have: lots and locations

A **lot** is one batch of a material with its own number: a delivery of resin, a pallet of profile. Pulaski keeps every lot's quantity, where it is (its **location**: a store, a staging area, a hopper on a line) and its **status** (*available*, *on hold*, *in quarantine*, or used up). Every change is recorded, so each lot has a full history, and Pulaski knows which lots were made from which (**genealogy**). Some materials, such as end caps or stretch wrap, aren't tracked by lot: Pulaski keeps one **balance** of them per location instead. Your ERP still holds the financial stock figures; Pulaski tells it about every movement.

### Planning: you decide where and in what order; Pulaski works out when

This is the most important idea in the schedule:

- **You own the line and the sequence**: which line each job is on, and what runs before what.
- **Pulaski owns the times.** It calculates when each job will start and finish from its quantity, the line's rate, the shift calendar and the changeovers between products. This calculation is the **projection**, and it is redone every time the schedule is read, so it always reflects the latest situation.

You never drag a job to a time. You drag it to a place in a line's sequence, and the times follow. Pulaski never moves jobs by itself and never chooses lines for you.

### Plan, dispatch, firm

A work order goes through three planning stages:

1. **Planned:** you are still arranging it. Its jobs can move freely.
2. **Dispatched:** you have published it to the floor. Pulaski records each job's projected times as the **baseline**, the plan you will be measured against.
3. **Firmed:** the plan is frozen. Firmed jobs keep their start time.

Then execution: **in progress**, **completed**, **closed**. An order can be **cancelled** at most stages. See [Work order statuses](#work-order-statuses).

---

## 3. Setting up: what to do first

If you are starting from an empty Pulaski, work through this list in order. Each step depends on the ones before it.

| # | What | Where | Why |
| --- | --- | --- | --- |
| 1 | The enterprise and at least one site, with its time zone | [Equipment](#4-equipment) | Everything else belongs to a site. |
| 2 | Areas (departments) and lines under the site | [Equipment](#4-equipment) | Jobs are planned on lines and departments. |
| 3 | Material classes, then materials (one by one, from a [CSV or B2MML file](#import-materials-from-a-file), or from the ERP) | [Materials](#13-materials) | Orders and BOMs need materials. |
| 3a | Units, and conversions for materials you order or use in other units | [Units](#6-units-and-conversions) | So orders, BOMs and rates can use any unit that converts. |
| 4 | Capabilities: which material runs on which line, and how fast | [The material's page](#where-it-can-run-capabilities-and-rates) | Without one, a line can't take a material's job. |
| 5 | Shift schedules, and which one each site, area or line follows (optional) | [Shifts](#15-shift-schedules), then [Equipment](#4-equipment) | Without one, Pulaski assumes lines run around the clock and marks them **24/7**. |
| 5a | Crews and people, if you record labor (optional) | [People](#16-labor-people-and-crews) | So supervisors can check crews onto lines, and shifts can name their crew. |
| 6 | Processes: the routes, what they make, released | [Processes](#5-processes) | Every work order is made from a process. |
| 7 | BOMs for the materials you make (optional, recommended) | [BOMs](#7-bills-of-materials-boms) | Without a BOM, an order plans no components. |
| 8 | Work orders | [Schedule → New work order](#8-creating-a-work-order) | Now you can plan. |
| 9 | Inventory locations: stores, staging areas and feed points, with **Holds inventory** ticked | [Equipment](#4-equipment) | Lots have to be somewhere. |
| 10 | Reason codes for write-ups, write-offs and holds, and utilization reasons for machine time | [Reasons](#reason-codes) (a starter set comes with `db/seed-reasons.sql`) | Every adjustment and hold needs a reason; machine profiles map stop codes to utilization reasons. |
| 11 | Machine profiles, and which one each machine uses | [Machine profiles](#18-machine-profiles), then [Equipment](#4-equipment) | So the machine's state values and stop codes mean something in Pulaski. |
| 12 | Edge nodes, their connections, and tags binding machine signals to equipment (optional) | [Edge nodes](#19-edge-nodes-collecting-machine-data) | So Pulaski hears machine state and counts. |

The demo data that comes with Pulaski already contains steps 1 to 4, and 9 (plant PLT1 with EXT-01, EXT-02 and PACK1, the stores RM-STORE and staging FG-STAGE, 200 materials, and the standard units with conversions for the demo materials), so you can start at step 6.

### The Overview page

**Operate → Overview** is the home page: a live board for the plant. In a few seconds it answers *which lines are running, which aren't and why, and what needs a person*. It refreshes itself every 30 seconds; the pill at the top right says when it was last updated (and turns grey, with a warning, if Pulaski can't reach the server for over a minute). The refresh button updates it at once. With several sites, pick one at the top.

**Diagonal hatching means "no data yet".** Several parts of the board need machine data, quality checks, lot tracking, labor tracking or an ERP connection, none of which Pulaski collects yet. Those parts already sit where they'll be, hatched and greyed, with a plug symbol saying what will fill them, so the page won't move around when the data arrives.

**Needs attention** (the grid at the top) counts what needs a person, most urgent first. A count above zero is marked with a coloured bar and icon; click it to go to the list behind it.

| Cell | Counts | Today |
| --- | --- | --- |
| Uncoded stops | Stops this shift with no reason given | Needs machine data |
| Open SPC alerts | Control-chart alerts to respond to | Needs quality checks |
| Orders behind | Open orders projected late or slipping past their baseline | Live → Work orders |
| Lots on hold | Lots on hold or in quarantine | Needs lot tracking |
| Schedule conflicts | Open orders with a schedule warning or an overlap | Live → Schedule |
| Lines with no signal | Lines that should report their state but don't | Live: every line, until machines are connected |
| Not on a line | Planned orders with quantity not yet placed | Live → Schedule |
| ERP changes to accept | Orders the ERP changed after dispatch | Needs an ERP connection |
| Labor to approve | Labor sessions from the last shift to approve | Needs labor tracking |

**This shift** shows OEE and its availability, performance and quality components from the current shift's machine and production reports. Site OEE combines the active production lines from their underlying time and ideal-output components; it does not average line percentages. When there is no rated production to calculate OEE, it shows a dash. Good output against plan, scrap and downtime are not shown here yet.

**Lines** has one row per line:

- **State:** the machine state. Until the line is connected it says **No signal**.
- **Now or next:** the job the schedule has on the line now, or the next one and when it starts, with how much of it is made.
- **The strip** covers six hours back to eighteen hours ahead, with a black line at *now*. The top lane will show what the line actually did (hatched while there's no signal); the bottom lane shows the planned jobs: outlined for planned, solid for dispatched or firmed, hatched for changeovers. Hover over a job to see it; click it to open its order on the Schedule. If nothing is planned in the window, the lane says when the next work starts.
- **OEE** for the line's current shift, with small bars for availability, performance and quality. The value comes from the machine/OEE report and refreshes with the Overview. A dash means there is no rated production data for that shift.
- **Flags** at the end: a red mark if work overlaps on the line, an amber clock if a job has a schedule warning (hover for the messages).

At the bottom, **Work orders** counts orders at each stage, Planned → Dispatched → Firmed → In progress → Completed (click one to list them), and **Starting next** lists the next six jobs to start across the plant.

---

## 4. Equipment

**Configure → Equipment.** This is where you describe your plant as a tree.

### The screen

- **Left: the tree.** Each row shows the code, name, level, role and chips for its settings: **State** (tracks machine state), **Schedulable** or **Plans for children** (a department), **Runs jobs**, **Inventory**, **Mixes lots** (lots are indistinguishable), **One material**, and **Inactive** in grey.
  - Click the arrow beside a row to open or close it, or use **Expand all** / **Collapse all**.
  - Type in **Filter by code or name** to find equipment; matching rows stay visible with their parents.
  - Arrow keys move through the tree when it has focus.
  - The bottom line counts the items (and the matches while filtering).
- **Right: the properties** of the selected equipment, grouped into *Identity*, *Classification*, *Capabilities*, *Site*, *Status*, *Machine data* (equipment that tracks state), *Working time* and *Hierarchy*.
  - Click a property to see what it means in the description area at the bottom.
  - Switch between grouped (**Categorized**) and **Alphabetical** order, or search the properties by name.

### Add equipment

1. In the tree, click the equipment the new one goes **under** (the parent). For the very first item there is nothing to select: the button reads **Add enterprise**.
2. Click **Add child**. A new row marked **Unsaved** appears under the parent, and its properties open on the right.
3. Fill in the properties:
   - **Code** (required): a short, unique business key such as `EXT-03`. It can't be changed later.
   - **Name** (required): what people call it, such as *Extrusion line 3*.
   - **Level**: where it sits in the tree. Only levels below the parent are offered.
   - **Role**: what kind of equipment it is at that level, for example *Production line* or *Storage zone*. Picking a role sets sensible capability defaults (below), which you can change.
   - **Time zone** (sites only, required): for example `America/Chicago` or `Europe/Berlin`.
   - The **capabilities** (below).
4. Click **Create** at the bottom of the properties, or **Cancel** to throw the new row away.

### Change equipment

Select it and edit a property on the right. Text saves when you press **Enter** or leave the field; a choice saves as soon as you pick it. **Esc** undoes a text edit before it saves. **Code**, **Level**, **Role** and the site's **Time zone** can't be changed after creation.

### Capabilities

| Setting | Turn it on when… | Default for |
| --- | --- | --- |
| **Tracks state** | Machine signals for this equipment should be recorded (running, stopped…), for downtime and OEE. | Lines, cells, units, machines |
| **Schedulable** | Work should be planned to it. | Lines, process cells, production units |
| **Can run jobs** | Work actually runs on it. | Same as Schedulable |
| **Holds inventory** | Material lots can be stored or loaded here. | Feed points, storage zones and units |
| **One material at a time** | Shown once **Holds inventory** is ticked. Another material can arrive only when the quantity of the one there is 0 (stock on hold still counts). It can't be turned on while two materials are there. | Silos and tanks dedicated to one material |
| **Lots are indistinguishable** | Shown once **Holds inventory** is ticked. Lots here mix and can't be told apart, like a silo or tank: everything that arrives joins one balance per material. See [Inventory](#12-inventory). It can't be turned on while separate lots are there; merge or move them first. | Silos, tanks, mixing hoppers |

**A location holding stock** can't be deactivated or stop holding inventory: move, use or write off the stock first.

**Lines vs departments.** A line has *Schedulable* and *Can run jobs*. A department (an area that you plan to, with lines below it) has *Schedulable* only; the tree shows it as **Plans for children**. A line can also have *Can run jobs* without *Schedulable*: it then only takes jobs from a department above it.

### Machine data

Equipment with **Tracks state** set shows a **Machine data** group:

1. **Machine profile**: how its controller reports state and stop codes. Choose one of its site's [machine profiles](#18-machine-profiles), or *None*.
2. **Micro-stop threshold**: stops shorter than this many minutes are micro-stops. They are logged but not queued for a reason, and they don't count against availability. Default 5 minutes; 0 to 60, in steps of half a minute. An extruder may use 2, a pack cell more.
3. **State from**: where its state comes from.
   - *Its own state tag* (default).
   - *Worst of its machines (series)*: for a line whose machines run one after another, so one stopped machine stops the line.
   - *Best of its machines (parallel)*: for machines running side by side, so the line runs while any of them does.
   - *Its constraint machine*: the line's state is one machine's. A **Constraint machine** row appears; Pulaski first picks the first machine below that tracks state, and you can choose another.

Each saves as soon as you change it. The profile reads the machine's data as it arrives through an [edge node](#19-edge-nodes-collecting-machine-data); the threshold decides which stops are micro-stops, and *State from* how a line shows on [Machines](#20-machines-live-status-and-stops).

### Deactivate equipment

Set **Active** to *false*. Inactive equipment stays in the tree and in history, greyed out, but can no longer be used for new work. Pulaski never deletes equipment.

### In detail

- **Required qualifications:** under the properties, a site, area or line lists the qualifications working on it requires, its own and those from above it (*from …*). **Require** adds one; **×** stops requiring it. They apply to everything below. Pulaski warns when someone without one is put on a line, but doesn't stop them. See [Qualifications](#qualifications).
- The levels, top to bottom: enterprise, site, area, work center, work unit, equipment module, control module. A child always sits below its parent's level.
- Roles allowed at each level: work center: production line, process cell, production unit, storage zone; work unit: work cell, unit, machine, storage unit; equipment module: machine, feed point, module; control module: module.
- Codes are unique within a site.
- The **Working time** group: **Shifts** chooses when it works for planning (*Inherit from parent*, one of its site's shift schedules, or *Around the clock*), and **Shifts in effect** shows what it ends up with and where from, such as *2X8 · Two shifts, from PLT1*. See [Shift schedules](#15-shift-schedules). Changing **Shifts** needs permission to maintain shift schedules, not equipment.
- **Machine data** needs permission to maintain equipment. A machine profile must be active and of the equipment's own site, and only equipment that tracks state takes one. A constraint machine must be below the line and track state.
- The **Hierarchy** group shows the parent, the owning site, the number of children and Pulaski's internal id (useful for support and the API).

---

## 5. Processes

**Configure → Processes.** A process describes where an order runs: its operations, the lines or departments that run each one, and how the operations follow each other.

### Example

"Extrude and Pack" (EXT-PACK):

- **Operation 10, Extrude**, runs on EXT-01 (50%) and EXT-02 (50%).
- **Operation 30, Pack**, runs on PACK1 (100%), and starts once extrusion has started.

A 2,400 m order made from it gets three jobs: 1,200 m on EXT-01, 1,200 m on EXT-02 and 2,400 m on PACK1.

### The process list

Each row shows the process code and name, its **Released** and **Draft** versions, what it **Makes**, and the materials it is the **Default for**. Click the code to open the editor.

### Create a process

1. Click **New process**.
2. Pick the **Site** (only shown when there are several), and enter a **Code** (for example `EXT-PACK`) and a **Name** (*Extrude and Pack*).
3. Click **Create and edit**. The editor opens on an empty draft, version 1.

### The editor

- **Top:** the versions (click one to view it), the save state, and the release button.
- **Left: Lines and departments** of the site. Hide or show it with the panel button at the top left of the canvas.
- **Middle: the canvas**, where operations are boxes and links are arrows.
- **Right: properties** of what is selected: an operation, a link, or (with nothing selected) the process itself. Hide or show it with the panel button at the top right.
- **Bottom: a table** of every operation, the equipment it runs on, each share, the total, and what it follows.

A draft saves after every change; you never need a save button.

### Build the route

1. **Add an operation.** Either drag a line or department from the left onto an empty part of the canvas (a new operation is created with it), or click **Add operation**.
2. **Name and number it.** With the operation selected, set its **Number** (10, 20, 30… leave gaps so you can insert steps later), **Name** and, optionally, **Description**.
3. **Add more equipment to it.** Drag another line or department onto the operation box, or use **Add a line or department…** below its properties. Each one appears with its **share**.
4. **Set the shares.** Edit each share in the properties, or click **Split evenly**. The shares of an operation must total exactly **100%**. Each entry becomes its own job for its share of the quantity. Remove an entry with its × in the properties.
5. **Link the operations.** Drag from the small handle on the right edge of an operation to the operation that follows it. An arrow appears with a label such as *after start*.
6. **Set how the link works.** Click the arrow's label and set:
   - **Starts:** *after start* (material flows straight through, so both run together, as with extrude and pack) or *after end* (the next operation waits for the earlier one to finish: a hand-off).
   - **Lag:** extra minutes to wait after that start or end.
7. Move boxes by dragging their header, or click **Arrange** to lay them out automatically.

Delete an operation or link by selecting it and clicking **Delete operation** / **Delete link**.

### Say what the process makes

With nothing selected (click an empty part of the canvas), the properties show the process itself:

- **All materials:** *true* if it can make anything. Otherwise, list what it makes with **Make a material class…** (every material in that class, for example *Extruded profiles*) and **Make a single material…**. Remove entries with ×.
- **Default for a material…:** makes this the process that new orders for that material pick automatically. A material can have one default.
- **Active:** an inactive process can't be picked for new orders.

A process that makes nothing yet is flagged; it won't be offered when creating orders.

### Release it

While the draft has problems, a yellow box lists them (click one to select the operation it is about), and the release button is disabled. Typical problems: an operation with no equipment, shares that don't add up to 100%, operations not linked to the rest, a loop in the links.

1. Click **Release version 1…**.
2. Optionally type what changed, then click **Release**. (**Not yet** goes back.)

The released version is what new orders are made from. It can never be changed.

### Change a released process

1. Open the process. If there is no draft, click **Change: start version 2** (the number follows on). This copies the released version into a new draft.
2. Edit the draft and release it. The previous version becomes **obsolete**.

Orders already made keep the version they were made from. If a draft already exists, click **Open the draft** instead.

### In detail

- Operations with no path of links between them run in parallel; links make them sequential. Links must flow one way; a loop is refused.
- A released process with more than one operation must be connected.
- Only lines and departments of the process's own site can be used; machines and other equipment that neither runs nor plans jobs are refused.
- The link's *after start/after end* and lag set the **earliest** time each operation's jobs may start, counted from the order's start. The schedule then also waits for the earlier operation's jobs as they are projected: see [Process links on the schedule](#process-links-on-the-schedule).

---

## 6. Units and conversions

**Configure → Units.** Here you see every unit Pulaski knows, and say how a material converts between units that aren't of the same kind.

### Why it matters

People work in different units. A customer orders 10,000 ft of profile, the BOM is written per 1000 m, the line's rate is in metres per hour, and the blend arrives in 25 kg bags. Pulaski converts between all of these, as long as it knows how. When it doesn't, you'll see a message such as *ft doesn't convert to kg for this material; add a conversion for it*, and this page is where you fix it.

### Two kinds of conversion

1. **Standard conversions** come with the unit. Units of the same kind always convert, for every material, by fixed factors: 1 lb = 0.45359237 kg, 1 ft = 0.3048 m, 1 dozen = 12 each. You never enter these.
2. **Material conversions** are true for one material only:
   - between kinds: 1 m of PRF-5100-BR weighs **1.6 kg** (a lighter profile weighs less);
   - to a packaging unit: 1 **box** of end caps holds **500 each**; 1 **bag** of resin holds **25 kg**.

A material conversion works both ways, and combines with the standard ones. With *1 m = 1.6 kg*, Pulaski also knows that 1 ft = 0.48768 kg and 1 ft = 1.07515 lb.

### The units list (right side)

Units are grouped by kind: **Area**, **Count**, **Length**, **Mass**, **Packaging (per material)**, **Time** and **Volume**; within each kind, smallest first. For each unit you see its code, name and what one of it is in the kind's base unit (*= 0.3048 m*). The base unit itself says **base unit**; packaging units say **per material**.

- **Rename** a unit by typing over its name; it saves when you leave the field.
- **Switch a unit off** with its **Active** switch. Inactive units disappear from the lists where you choose a unit, but quantities already in them still convert.
- **Add a unit** with **New unit**: enter the **Code** (for example `bdl`), the **Name**, the **Kind** and, for anything but packaging, how many of the base unit one of it is. Click **Create**.

A unit's code, kind and factor can't be changed after it is created, because quantities are stored in it. If one is wrong, switch it off and create a new one.

### Add a conversion for a material

1. In **Material conversions** (left side), choose the **Material**. The note under it says which unit it is stocked in.
2. In the row under the table, fill in **1 [unit] = [quantity] [unit]**. For example: 1 `m` = `1.6` `kg`, or 1 `box` = `500` `ea`.
3. Click **Save conversion**. It appears in the table with the reverse worked out (*1 kg = 0.625 m*).

To change a conversion, save it again with the new quantity (in either direction; Pulaski replaces the old one). To remove one, click **Remove**.

**Pulaski refuses a conversion when:**

| Message says… | Because… |
| --- | --- |
| *…already convert by their standard factors* | Both units are of the same kind (kg and lb): they already convert for every material. |
| *…already converts to … through its other conversions; change one of those instead* | The material can already get from one unit to the other. Two routes could give two different answers, so Pulaski allows only one. Change the existing conversion instead. |

### Try a conversion

Under the conversions, **Try a conversion** works out any quantity: enter a **Quantity**, choose **From** and **To**, and click **Convert**. With a material chosen, its own conversions are used too, so you can check, for example, what one bundle of profile weighs in pounds. Without a material, only standard conversions work.

### Where conversions are used

| Where | What converts |
| --- | --- |
| [Creating a work order](#8-creating-a-work-order) | The order may be in any unit that converts to the material's base unit. It is converted to the BOM's unit to plan the components. |
| [BOMs](#7-bills-of-materials-boms) | The BOM's unit must convert to the material's base unit, and each component's unit to that component's base unit, before the BOM can be released. |
| [The Schedule](#10-the-schedule) | A job's length is worked out from the line's rate even when the job is in another unit (an order in feet on a line rated in metres per hour). The side panel shows the rate in the job's unit. |

### In detail

- Base units: kg (mass), m (length), m² (area), L (volume), ea (count), s (time). The standard factors are the exact legal definitions, not approximations.
- Conversions are kept exact along the way; a converted quantity is rounded to six decimal places.
- A material's conversions can never offer two routes between two units, so every conversion has exactly one answer.

---

## 7. Bills of materials (BOMs)

**Configure → BOMs.** A BOM lists what one material is made from, so that every order plans the components each job will use.

### Ideas to know first

- **One level.** A BOM lists only the direct components. If your profile is made from a dry blend, and the blend from resin and additives, the profile's BOM lists the *blend*, and the blend has its own BOM. Each is made on its own orders.
- **For a process, or for any process.** A BOM can be tied to one process. Then each component can name the **operation** that consumes it: blend at *Extrude*, end caps at *Pack*. A BOM for "any process" is used when the order's process has no BOM of its own; all its components go to the first operation.
- **Quantities are per a base quantity.** For example, "per 1000 m of PRF-5100-BR". Orders can be in any unit that [converts](#6-units-and-conversions) to it.
- **Versions**, as with processes: edit a draft, release it, start a new draft to change it.

### Create a BOM

1. Click **New BOM**.
2. Pick the **Material** the BOM is for (raw materials and consumables aren't offered, since you don't make them).
3. **For process:** leave **Any process**, or pick a process. Only processes that make this material are offered.
4. Click **Create and edit**.

There can be one BOM per material and process, plus one for any process.

### Fill it in

1. **Quantities are per:** enter the base quantity, for example `1000`, and choose its unit (it starts as the material's base unit). It must convert to the material's base unit. The link beside it opens the material's [unit conversions](#6-units-and-conversions).
2. Click **Add component** for each line, and fill in:

| Field | What to enter |
| --- | --- |
| **Line** | The line number. New lines go up in tens (10, 20, 30) so you can insert between them. |
| **Component** | The material consumed. Its unit is filled in for you. |
| **Quantity** | How much is used per the base quantity. |
| **Unit** | The unit of that quantity. |
| **Scrap %** | Extra to plan for losses. `2` plans 2% more. |
| **Consumed by** | How it will be taken from stock once execution is built: **Backflush on count** (automatically, in proportion to good output), **Time window (loaded lot)** (whatever lot is loaded at the feed point while the job runs, typical for resin and blend), **Declared by operator** or **Scanned lot**. |
| **Operation** | Only on a BOM for a process: which operation consumes it. **First operation** if you leave it. |

3. Remove a line with its red ×.
4. Click **Save draft**. A yellow box lists anything that stops the release, and the lines concerned are highlighted.

### Release it

When the draft is saved and has no problems, type an optional **Release note** and click **Release version N**. From then on, new orders for that material (with that process) plan their components from it.

### Change it

Open the BOM and click **New draft**. It copies the released version; edit, save, release. The old version becomes obsolete. Orders already made keep the components they were planned with.

### Worked example

BOM for PRF-5100-BR, for EXT-PACK, **per 1000 m**:

| Line | Component | Quantity | Scrap | Consumed by | Operation |
| --- | --- | --- | --- | --- | --- |
| 10 | CMP-PVC-PRF-WH (white blend) | 1450 kg | 2% | Time window | 10 Extrude |
| 20 | CMP-PVC-PRF-CAP (capstock) | 90 kg | 0 | Time window | 10 Extrude |
| 30 | PKG-ENDCAP | 84 ea | 0 | Backflush | 30 Pack |
| 40 | PKG-PAL-LONG | 0.5 ea | 0 | Declared | 30 Pack |

A 2,400 m order, extruded 50/50 on EXT-01 and EXT-02, plans:

- Blend: 2400 ÷ 1000 × 1450 × 1.02 = **3,549.6 kg**, so 1,774.8 kg for each extruder job.
- Capstock: 2400 ÷ 1000 × 90 = **216 kg**, so 108 kg each.
- End caps: **201.6**, all on the PACK1 job. Pallets: **1.2**, on the PACK1 job.

### In detail

- A material can't be its own component. Components must exist and be active to release.
- To release, each component's unit must convert to that component's base unit (so its use can be taken from stock later), and the BOM's unit must convert to the material's base unit. The yellow box says which conversion is missing.
- Operation numbers must exist in the process's **released** version. A draft may name an operation that isn't released yet, but can't be released until it is.
- Each component's total is rounded to six decimal places. The jobs of an operation share it in proportion to their quantities, and the last job takes any rounding remainder, so the jobs always add up to the total.
- Planned components never change after the order is made, even if the BOM changes later.
- A job added to the order later (from the Schedule, or the replacement when a job is cancelled and replaced) gets its components too: those of its operation, for its own quantity, from the BOM version the order was planned with.

### When the process changes

A released BOM names operations by number. If the process is later released with different operations (Pack renumbered from 30 to 40, say), the BOM still names 30. Pulaski doesn't stop the process release, but:

- the process editor shows which BOMs the release broke;
- the BOMs list and the BOM editor say which operation the BOM names that the process no longer has;
- the new-order form greys out the process for that material, and creating the order is refused with the same reason.

To fix it, open the BOM, start a new draft, move those lines to operations the process has, and release it.

---

## 8. Creating a work order

**Schedule → New work order** (the button at the top right of the Schedule).

You choose *what*, *how much* and *when*. The process you choose decides *where*: each of its operations becomes jobs on its lines or departments.

### Step by step

1. **Site:** the plant (the first one is picked for you).
2. **Order number:** your reference, for example `WO-1001`. Unique within the site.
3. **Material produced:** what to make. The unit fills in with the material's base unit.
4. **Process:** the processes at this site that make this material and have a released version.
   - The material's **default** process (marked *· default*) is picked automatically, or the only one if there is just one.
   - A process whose lines can't run this material is **greyed out** with *can't run this material*, and the reason is listed underneath, for example *Operation 10 (Extrude): EXT-02 is not set up to run FLM-HDPE-20-BLK; add a capability for it first.*
5. **Planned quantity** and **Unit:** how much, in any unit that converts to the material's base unit (the hint under the field links to its conversions). For a material made in batches it must be a whole number of batches; the hint shows the batch size.
6. **Start:** when the order may start. No job will be planned before it. It defaults to the next whole hour at the site.
7. **Due:** when it must be finished (optional). Jobs projected to finish later are flagged **late**. If the process uses a department, the due date is **required** (the label says so): it is the department's finish-by time.
8. **Priority:** a number; higher is more urgent. Shown as an arrow on the Work orders screen.
9. **Notes:** anything the floor or other planners should know (up to 4000 characters).
10. Check the preview at the bottom:
    - **Jobs this order will create:** each operation, its line or department, its share and its quantity.
    - **Components:** which BOM will plan the components, or a warning that the material has no released BOM (the order then plans none).
11. Click **Create**. You go to the Schedule with the new order open in the side panel.

### What happens

- The order is created as **planned**.
- Each operation's lines and departments get one **job** each, numbered from the order: `WO-1001-30` for an operation with one line, `WO-1001-10-1` and `WO-1001-10-2` for an operation with two.
- Each job goes **at the end** of its line's sequence. Move it on the schedule if it should run sooner.
- Jobs are held until the start time (later for an operation that follows another with a lag).
- A job on a department waits there in a window from its start to the due date, until the floor assigns it to a line.
- If the material has a released BOM, each job's components are planned.

### If it is refused

Everything is checked before anything is created, and all problems are listed together. Nothing is created until they are fixed. Common reasons:

| Message says… | What to do |
| --- | --- |
| *…is not set up to run…; add a capability for it first* | Ask your administrator to add a capability for the material on that line, or pick another process. |
| *…is a department, so its job needs a finish-by; give the order a due date* | Enter a **Due** date. |
| *…doesn't make…* | The process's scope doesn't include the material: add it on the [process](#say-what-the-process-makes), or pick another process. |
| *…doesn't convert to…, the base unit of…* | Enter the quantity in another unit, or add a [conversion](#6-units-and-conversions) for the material. |
| *…was changed by someone else* / *…was released again* | Try again: the process changed while you were filling in the form. |
| Duplicate order number | Use another order number. |

---

## 9. The Work orders screen

**Operate → Work orders.** A list of orders with how each one stands: when it is projected to run, how much is made, and whether it is behind.

### Choose what you see

- **Equipment picker (top right):** show orders for the whole plant, one site, one department or one line. Search for equipment by typing.
- **Tiles:** counts of orders that are **Starting today**, **Finishing today**, **On track**, **Behind**, **Need attention** and **Not on a line**. Click a tile to show only those orders; click it again (or **Show all**) to clear. Hover over a tile for its exact meaning (see [Tile meanings](#tile-meanings)).
- **Find by:** search by **Order**, **Material** or **Line or department**, then type in **Search**.
- **Status:** **Open orders** (the default), one status, or **All statuses**.
- **Refresh** reloads the list.

### Read a row

| Column | Shows |
| --- | --- |
| Flag | A red mark when the order is behind; hover for why (*Projected to finish 3h after its due date*). |
| Order | The order number; an up arrow marks priority. |
| Status | Planned, Dispatched, Firmed… |
| Material | Code and name. |
| ⓘ | Opens the details (below). |
| Schedule | Projected start and finish. |
| Quantity | Planned and made so far. |
| Select | Select one or more orders to use the shared Actions menu. |

### Details

Click ⓘ to open a row. You see the due date, the published baseline, how much is on lines, the process and version, the BOM version, the origin (for an ERP order, its key, version and what the ERP sent), the notes, a held quantity or a change from the ERP to decide on, every open job with its line, status, projected times and remaining quantity, any warnings, and the **planned components** for each job.

### Act on orders

- **One order:** select its checkbox, then use the shared **Actions** menu above the list. It shows the permitted status changes, **Print shop order**, and **Show on the schedule** when available.
- **Several orders:** select their checkboxes, or use **Select visible** to select the filtered rows. The same **Actions** menu shows bulk status changes with the number eligible (*3 of 5*); other selected orders are skipped. **Cancel…** asks you to confirm. **Clear selection** removes the current selection.

### Orders from the ERP

Your ERP can send work orders to Pulaski (whoever runs the integration sets this up; see *In detail*). An ERP order is made like one you create: from its process, with a job per operation and its components planned. Its row says **ERP** under **Origin**, with the ERP's key.

1. **The quantity isn't whole batches.** For a material made in batches, Pulaski follows the material's rule, or the site's (**Configure → Site settings**): refuse the order, round up or down to whole batches, or import it **held**. The ERP's own quantity is shown on the order beside Pulaski's (*The ERP sent 1,025 kg: rounded up to whole batches*).
2. **A held order** shows *held* in the list and can't be dispatched. Open its row, type a whole-batch quantity, and click **Set quantity**; its jobs are re-planned.
3. **The ERP changes an order.** While it is still planned, the change applies at once and its jobs are re-planned. Once dispatched, the row shows *ERP change*: open it to see what would change, then click **Accept** (the change applies and its jobs are re-planned) or type a reason and click **Reject** (the order stays as it is).
4. **The ERP cancels an order.** While planned, it is cancelled with its jobs; after that the cancellation waits for you like any change.

**In detail**

- **Sending once is enough.** Orders are matched on the ERP's system and key, so sending one again never makes a second; the same version again changes nothing. An order number already used by an order made in Pulaski is refused.
- **Which process.** The one the ERP names, else the material's default process, else the only active process at the site whose released version makes it. If none or several fit, the order is refused and the answer says why.
- **What the ERP owns:** product, quantity, start, due date and priority. Pulaski owns the schedule, dispatch and status. The ERP can't change an order's product; cancel it and send a new one.
- **Re-planning** changes each job's quantity in place, shared between the lines as when it was made (whole batches of each line's size), scales its components, and moves its hold-until by as much as the start moved. It is refused once a job has started, or when the order's jobs were changed by hand on the schedule; change them there instead.
- **Every step is recorded:** imported, changed, change pending, accepted (with the ERP's values) and rejected (with your reason).
- **For the integration:** `POST /api/work-orders/import` with a list of orders (`externalSystem`, `externalKey`, `externalVersion`, `siteCode`, `orderNumber`, `materialCode`, `quantity`, `uomCode`, `plannedStart`, `dueAt`, `priority`, `processCode`, `notes`, `cancelled`). Each comes back `created`, `updated`, `unchanged`, `pending`, `cancelled` or `refused` with why, and each is saved on its own. The caller needs *work_order.manage* at the order's site.

### Print a shop order

1. Find the work order and open its row's **Actions** menu.
2. Choose **Print shop order**. Pulaski downloads one PDF with a traveler sheet for each job.
3. Print the PDF for the floor. Check the schedule and the approved product specification before running.

**In detail.** Each sheet identifies the work order, job and process operation; shows the material, planned quantity, line or waiting department, scheduled time, run rate, work-order due date, status and priority, special instructions, operation description and that job's planned BOM components; and carries a Code 128 barcode containing `order-number|OP-number` (for example, `WO-1001|OP-10`). A currently projected schedule is labelled **Projected schedule**; jobs without a live projection show their published baseline. Orders without jobs get a summary page with the specification warning. Pulaski does not yet maintain released product specifications, so every sheet explicitly says to verify the approved specification elsewhere; it does not print or imply spec limits.

---

## 10. The Schedule

**Operate → Schedule.** The live plan: every line and department, every open job and order, and when each is projected to run.

### Before you start

- The schedule only shows equipment marked **Schedulable**. If it says *No schedulable lines*, mark your lines as schedulable on [Equipment](#4-equipment).
- **Live updates.** The board re-reads everything every 10 seconds; the dot and *Live · updated 14:05 site time* at the top show it. Cards that changed since the last update are briefly highlighted, so you notice other planners' changes. Updates pause while you drag. Click the refresh button to update now.
- **Site:** choose the site at the top if you have more than one.
- **Views:** **Gantt**, **Calendar**, **Kanban** and **Table** show the same plan in four ways. Each view lets you change only what the planner owns (line, sequence, pins, order status).
- **The side panel** opens when you click any job or order, on any view. Everything you can do by dragging, you can also do there, with the keyboard. **Esc** closes it.

### Gantt view

Lines down the left, time across.

- **Move through time:** **Earlier**, **Today** and **Later**; zoom to **Day**, **3 days**, **Week** or **2 weeks**. The vertical line marks *now*.
- **Bars** are jobs, coloured by status (see the legend under the chart). Changeovers between products appear as their own bars, in the changeover style, before the job that needs them. Late and overlapping work is marked too.
- **Line labels** show the code, name and chips: the shift schedule the line follows, such as **2X8** (hover to see where it comes from), **24/7** (no shift schedule, or set to run around the clock) and **Plans for N lines** (a department).

**Resequence or move a job:** drag its bar. A marker shows the slot it will go into: before the job under the pointer, or at the end of the line. Drop it on another line to move it there. Times then recalculate.

**Place an order that isn't on a line yet:** planned orders with quantity still to place are in the **To place** tray on the left. Drag one onto a line. While you drag, each line shows a **drop lane** previewing where the new job would land and which jobs it would push later; a line that can't take it shows why. Dropping creates a job for the unplaced quantity.

**Departments:** a department's row shows each waiting job as a band over its window, with the fastest line's estimate. Each line that could run it shows a faint **ghost** ("~14h here"). Drag the band or a ghost onto a line to assign the job there. Dragging an order from the tray onto a department opens the planning form with a suggested window.

### Calendar view

A month of work by day; a job that spans several days appears on each. The number in each day counts the jobs running that day.

- **Hold a job until a later day:** drag a planned job to that day. It won't start before then.
- Dragging to an earlier day only clears a hold: to run something sooner, change its place in the line on the Gantt.

### Kanban view

Work orders as cards in columns by status: Planned, Dispatched, Firmed, In progress and Completed. Turn on **Closed and cancelled** to show those columns too. Click a card to open the order.

- **Change an order's status:** drag its card to another column: **Dispatch**, **Un-dispatch**, **Firm** and so on. Columns it can't move to dim while you drag.

### Table view

Every open job in one sortable list.

- Type in **Find job, order or material** to filter; click a column heading to sort.
- Edit in place: **Lock**, **Hold until** and **Finish by**.
- Warnings and overlaps show as chips; hover for the message.

### The side panel: a job

- **Alerts** at the top: overlaps and warnings for this job.
- **Facts:** projected (or window) times and duration, the department it was planned to, the finish-by, the baseline, its work order, material, quantity still to make, the rate used and where that rate came from, the due date.
- **Line and sequence:** pick a **Line** and where it **Runs** (*Before* another job, or *At the end*), then **Move**. For a firmed job you can only change the line; it keeps its start time and slots in by time.
- **Pins:**
  - **Locked:** the job keeps its place when others move, and can't itself be moved until unlocked.
  - **Hold until:** it won't start before this time (for example, waiting for material). **Clear hold** removes it.
  - **Finish by:** Pulaski warns if it is projected to run past this. **Clear finish-by** removes it.
  - Click **Set** to apply.
- **Cancel:** click **Cancel job…**, give a reason, and for a production job optionally tick **Replace it with a job for the quantity not yet made**, then confirm. **Keep it** backs out.

For a job waiting on a **department**, the panel lists the **Lines that could run it**, each with an estimate and whether it **Fits the window** or **Won't fit**; **Run on …** puts it at the end of that line. For a job already taken from a department, **On the floor** lets you move it to another of the department's lines, or **Send back** to the department.

### The side panel: a work order

- **Facts:** material, quantity, due date, process and version, origin, notes.
- **Status:** buttons for what it can do next: **Dispatch**, **Un-dispatch**, **Firm**, **Cancel**…
- **Jobs:** its open jobs with their lines and times (click one to open it); overlaps are marked.
- **Place on a line or plan to a department** (planned orders): shows how much isn't on a line yet. Pick a line and a quantity and click **Add job at the end of the line**, or pick a department, set the window (**Window opens** and **Finish by**) and click **Plan to …**.

### Planning to a department

Use this when you want a department to finish work in a time window but the floor should pick the line ("Extrusion, this weekend, done before Monday").

1. Plan the job to the department, from the order's side panel or by dragging the order onto the department's row. A department job always needs a **finish-by**.
2. The job waits on the department as a band over its window. Pulaski estimates it on every line below that could run it and warns, without blocking, if even the fastest can't finish in the window, if no line can run it, or if the window has passed.
3. The floor (or you) assigns it to a line: **Run on …** in the side panel, or drag the band or a ghost onto the line. It can move between the department's lines, or go back to the department, whatever its plan status, until it starts.

Pulaski checks each department job on its own; it doesn't share out a department's capacity between jobs.

### Dispatching and firming

- **Dispatch** publishes the order: Pulaski calculates every job's times on its line right now and saves them as the **baseline**. The floor sees the order, and later you can compare what happened with what was published.
  - Dispatch is refused if the order has no jobs, or if a job's line can no longer run its material.
  - A job waiting on a department is published with its window as the baseline.
  - If an operation's jobs don't add up to the order quantity, dispatch goes ahead but warns, for example *Operation 30 (Pack) plans 1,500 against an order quantity of 2,000.* Each operation is checked on its own, because each makes the whole quantity.
- **Un-dispatch** returns the order and its jobs to planned and clears the baseline. You can do this until the order is firmed.
- **Firm** freezes the plan: no more un-dispatch, and each job is anchored at its baseline start (see [Job rules](#job-rules)).
- New jobs can only be added to a **planned** order.

### Process links on the schedule

For an order made from a [process](#5-processes), a job waits on the schedule for the operation it follows, as it would on the floor.

- **After start:** the job can't start before the first job of the earlier operation is projected to start (or actually started), plus the lag.
- **After end:** it can't start before the last job of the earlier operation is projected to end (or actually ended), plus the lag.
- The side panel shows **Process links: Can start from …** for such a job. When the earlier operation moves, by resequencing, a line change or production running slow, the later one moves with it on the next refresh, on every view, and so does the work queued behind it on its line.
- An earlier operation waiting on a department counts by its window: from its start, and until its finish-by for *after end*.
- **Pulaski never moves a job to another line or changes the order of a line** to meet a link. Two warnings tell you when it can't be met:
  - **Link not met:** a firmed job keeps its firmed start, but the operation it follows won't allow it by then. Move the earlier operation's work forward on its line, or move that work to a line that can start it sooner.
  - **Link cycle:** the job waits for work that, through the sequence of the lines, waits for it. For example, a Pack job sequenced before the Extrude job it follows on the same line. Resequence one of them.
- **Dispatching** publishes the times with the links applied, so the baseline agrees with the board.

### Overlaps

Pulaski lays work end to end on each line, so two jobs on a line only overlap when work runs into a **firmed** job's fixed start. When that happens, a red banner appears on every view: *1 overlap: a line can't run these at the same time*, listing each pair with a hint and a **Show** button. The jobs are outlined, and a notice pops up the moment a new overlap appears, whether from your edit, another planner's, or simply time passing. Pulaski never fixes an overlap itself: move or resequence work, or un-firm the order.

---

## 11. The Floor: running jobs and reporting production

**Operate → Floor.** Where operators start, pause and complete jobs and report what they make. The schedule says what should run; the floor records what does. Each line shows how many people are on it, and someone set up to clock themselves on sees **Clock on** and **Clock off** on each line (see [Labor](#16-labor-people-and-crews)).

### Ideas to know first

- **Runs.** A job runs in one or more **runs**: starting a job opens a run, pausing or completing it ends the run, and resuming opens the next. Good and scrap are reported into a run, and so are the lots the job uses and makes.
- **Only one run at a time on a line.** Pause or complete what is running before starting the next job.
- **When a job can start.** Its work order must be on the floor (dispatched or firmed). If its order was made from a process, the process links must be met: a Pack operation set to start *after Extrude ends* can't start until every Extrude job of the order is completed, plus any lag. Pulaski never starts a job by itself; it tells you when one can start.
- **The rate check.** A run can't hold more than the line could have made in it: its length × the line's **ideal rate** for the material × the site's tolerance (110% unless a manager changed it on [Site settings](#17-site-settings)). Reporting 1000 m after one hour on a line rated 100 m/h is refused. The check uses the ideal rate from the line's capability, not the planning rate, which is usually lower.
- **The order follows its jobs.** Starting an order's first job firms the order (if it was only dispatched) and puts it **in progress**; completing its last open job **completes** it; reopening a job takes it back to in progress.

### The board

Each line is a card. A running line shows its job with the run's length, good quantity against the plan (with a bar), scrap, and how much more the run can hold at the line's rate. Below it, **Next** lists the line's other open jobs in sequence, each with its status and, when it can't start yet, why (*Waiting for operation 10 Extrude to end*) and when (*Ready at 14:30*). Departments with jobs waiting on them have a card of their own. The board refreshes every 15 seconds while no form is open.

### Start a job

1. Find the job under **Next** on its line's card.
2. Click **Start** (or **Resume** for a paused job). The button is active only when the job can start and the line is free.
3. For a job waiting on a department, choose the **Line** it runs on next to it, then click **Start**.

If the material changes on a line in automatic changeover mode, Pulaski starts a **changeover** job first (named after the job, such as *WO-5002-10-CO*) and says so. Complete the changeover when the line is ready, then start the job. If no changeover is needed, complete it straight away.

### Report production

1. On the running line's card, click **Report**.
2. The **Run** is the open run. Enter the **Good** quantity and the **Scrap** quantity (either can be zero), in the job's unit.
3. For good output, choose where it **goes to** and, optionally, a **Lot number**; left blank, Pulaski numbers it after the job (*WO-5001-10-01*, *-02*…).
4. For scrap, choose a **Scrap reason**.
5. Click **Report**. The card updates with the new totals.

If the report is more than the run can hold, Pulaski refuses it and says how much more fits.

### Pause, resume and complete

- **Pause** stops the job for now. Choose why (*End of shift*, *Waiting for material*…) or leave it blank, then click **Pause**. The run ends and the line is free for another job. Click **Resume** later to open a new run.
- **Complete** ends the job. Jobs waiting on it are named when they can now start (*WO-5001-20 on PACK1 can start now.*). If it was the order's last open job, the order is completed.

### Report production after the run

Production found after a run has ended (counted at the end of a shift, or a pallet missed) is reported into that run.

1. Open the job's page: click its number on the board, or **Runs and reports** on its inventory page.
2. Click **Report production**.
3. Choose the **Run** it belongs to. A run that has ended is marked *Recorded late*.
4. Enter the quantities as above and click **Report**.

The run's rate check still applies: an hour-long run can't take more than an hour's output. Late reports are allowed until the work order is closed, and show as *late* in the job's reports.

### The job page

`jobs/<job>` shows the job's status, planned, good and scrap quantities, its **runs** (line, start, end, length, good, scrap, the most the run can hold, and why it ended) and its **production reports** (when entered, which run, good, scrap, scrap reason, who and any comment; a reversed report is marked *reversed*). The buttons offer what the job's status allows: **Start** or **Resume**, **Pause**, **Complete**, **Reopen** for a completed job whose order isn't closed, and **Report production**. **Materials and lots** opens the job's inventory page.

### Reverse a production report

A report entered on the wrong job, or with the wrong quantity, is reversed whole and then reported again correctly.

1. Open the job's page and find the report under **Production reports**.
2. Click **Reverse…** on its row.
3. Choose an adjustment **Reason**, write a **Comment** saying why, and click **Reverse report**.
4. The report is marked *reversed*. Its good and scrap come off the run, and every lot it made gives its quantity back (a lot that only held that is *cancelled*).

**In detail**

- **Nothing is deleted.** Each of the report's counts is offset by a correction at the same time, so production by shift and OEE see it as never made; the lots get reversal movements, and the report stays in the list.
- **Its lots must still hold what it gave them.** If one has been consumed, split, shipped or written down since, Pulaski names what happened so that is reversed first. Output that went straight into a silo's balance is taken out of the balance, which must still hold it.
- **Partial batches.** A partial batch the report made no longer counts, so the run can report another. A rejected partial batch was written off: reverse that first.
- **Until the job is closed**, as with reporting. Needs *production.correct* (Supervisor and Auditor to start with).
- Reports entered before this version can't be reversed whole; they show no **Reverse…** button.

### Production by shift

What each shift made, for a supervisor's shift report or the morning meeting.

1. On **Operate → Floor**, click **Production by shift**.
2. Choose **From** and **to** (production days, up to 93) and click **Show**. It opens on the last seven days.
3. Each shift is a block, headed by its day, name, code, schedule, times and crew, with its good and scrap per unit. Below it, one row per line and job: work order, material, good, scrap, unit and how many reports it adds up.
4. **Against the jobs' runs** lists each job's good and scrap in these days next to the totals on its job page. *Matches* means every report of the job is in these days; otherwise part of it was reported on other days.
5. **Export CSV** saves every row, with the shift's start and end.

**In detail**

- **Which shift a report counts in:** the shift running on its line when the report was made, from the [shift schedule](#15-shift-schedules) the line follows with its one-off changes. A report into a run that had already ended counts when the run ended.
- **Which day:** a shift's production day is the day it starts, so a night shift's output after midnight counts towards the evening it began. A report between shifts, or on a line without shifts, counts towards the site's calendar day and shows as *outside shifts*.
- **Good** is everything a report made less its scrap. A rejected partial batch moves its quantity from good to scrap in the shift the batch was made.
- **Every report is counted once,** so a job's rows add up to its runs' totals when the days cover all its reports.
- **Reports are counted when they are made, not spread over the run:** a single report for a 12-hour run counts in the shift running when it was entered. Report at the end of each shift to split a run between shifts.
- **Who:** anyone who can see the Floor at the site.

### Materials made in batches

A material with a **batch size** (set on its [material page](#batches)) is planned and reported in whole batches.

- **Orders and jobs are whole batches.** With a batch size of 50 kg, an order for 125 kg is refused (*125 kg is 2.5 batches of 50 kg… Plan 100 or 150 kg*). A process that shares an order between lines gives each line whole batches.
- **Each line's own size.** Where a line has a [batch size of its own](#batches), its jobs are whole batches of that size, its reports are batches of that size, and a partial batch there is less than one of them. An order shared between lines of different sizes gives each line whole batches of its own size, as near its share as possible and adding up exactly; when no split adds up, the order is refused with the nearest quantities that do (*Plan 1000 or 1100 kg, or change the shares*).
- **Moving a job.** A job moves, or is assigned from a department, only to a line where it is whole batches of that line's size; otherwise the move is refused with the size and the nearest whole quantities. A job planned to a department uses the material's size until a line is chosen. Changing a line's size leaves its open jobs as they are; new jobs, moves and reports use the new size.
- **Report batch by batch.** The report form lists **Good batches**, each prefilled with the batch size: change a weight if it differs, and click **Add batch** for each more. Rejected batches go under **Rejected batches**, with a scrap reason. Each good batch becomes its own lot.
- **How close a batch must be.** Under the **near** rule a batch may differ from the batch size by the tolerance (2% by default: 49 to 51 kg for 50 kg), so 49.3 kg is a batch and 40 kg isn't. Under the **exact** rule, the default for finished goods, every batch is exactly the batch size.

**A partial batch (the line went down)**

A batch cut short is an exception, so it is reported only as the line stops:

1. Click **Pause** (or **Complete**, if the job ends there).
2. Choose **why** it stopped: a partial batch needs a reason.
3. Tick **A batch was in progress**, enter its **Quantity** and where it **Goes to**.
4. Click **Pause** or **Complete**.

The partial batch goes into a lot of its own. Under the near rule it is **accepted**: it will be used. Under the exact rule it goes **on hold until approved**, because a finished good can't be sold as a partial; the floor shows *A partial batch waits for approval*. Each run can have one partial batch. If you forgot it, open the job's page and report it into that run: the form offers **Partial batch** for a run that ended with a reason.

**Approve or reject it** on the job's page, under **Partial batches**: **Approve** releases the lot; **Reject** asks for a scrap reason, writes the lot off and moves its quantity from good to scrap. Both record who decided and when. Only a Supervisor or Quality user at the site can decide, and never the person who reported it.

### In detail

- **Statuses.** A production job starts from *dispatched* or *firmed* (starting firms a dispatched order with all its jobs, since execution freezes the plan), and resumes from *paused*. Non-production jobs (cleaning, maintenance, trial, changeover) also start from *planned*. A completed job can be reopened to *paused* until its order is closed; closing the order closes its completed jobs.
- **The order's status** can't be set to in progress or completed by hand any more: it follows its jobs. Closing a completed order is still done on the Work orders screen.
- **The rate check** uses the capability in effect when the run started, converted into the job's unit, and counts good plus scrap across the whole run, including late reports. If the line had no capability for the material, or its rate unit doesn't convert, the report is recorded with a warning that it wasn't checked. The tolerance is set per site, between 100% and 1000%, through `PUT /api/execution/sites/<site>/policy`; there is no screen for it yet.
- **When a report counts.** A report into the open run counts at the moment it is entered; one into an ended run counts at the end of that run. Both keep the time they were entered.
- **Consumption** needs a run too: the job's open run, or the run chosen (see [Record what a job used and made](#record-what-a-job-used-and-made-the-jobs-tab)). Using more of a planned component than the plan allows is recorded with a warning. The plan's quantity is scaled up when the job makes more than planned, and gets the site's consumption tolerance (also 110% by default).
- **The changeover check** compares the job's material with the last job that ran on the line. If that was a production job of another material, a changeover job is started first. In *scheduler* mode, and on a line that hasn't run anything yet, no changeover is added.
- **Process links.** *After start* is met once any job of the upstream operation has started; *after end* once all of them are completed. Each is plus its lag. Cancelled upstream jobs don't count.
- **Events:** `pulaski.job.started.v1`, `…paused.v1`, `…completed.v1`, `…reopened.v1` and `…production_reported.v1`; the counts are kept in `ts.production_count`.
- **Batches in detail.** Batch weights are in the material's base unit. A partial batch is more than 0 and less than a batch (under the near rule, less than the size minus the tolerance). The rate check counts it like any output. Under the exact rule a partial batch needs a location that keeps lots apart (not a silo or tank where lots mix). The lot is held with the reason *Partial batch awaiting approval*. When a job of a batch material is cancelled and replaced, what is left is rounded **up** to whole batches. Partial batches are recorded as `pulaski.partial_batch.reported.v1`, `…approved.v1` and `…rejected.v1`.
- **Concurrency.** Start, pause, complete and reopen check the job hasn't changed since the screen read it; reports don't, so several people can report into a busy line, protected by the rate check under a lock.

---

## 12. Inventory

**Inventory** (in the side menu under *Operate*) shows what material you have, where it is and what happened to it. It has eight tabs: **Stock**, **Lots**, **Locations**, **Jobs**, **Shortages**, **Journal**, **Counts** and **Reports**. If your company has more than one site, pick the site at the top right; Pulaski remembers it while you move between the tabs.

### Ideas to know first

- **Quantities are in the material's base unit.** You can enter a quantity in any unit that converts (pounds for a resin stocked in kilograms, for example); Pulaski converts it and keeps the base unit. If a unit doesn't convert you'll see *"… doesn't convert to …"*: add a [conversion](#6-units-and-conversions).
- **Stock can't go below zero.** Pulaski refuses to take out more than a lot holds and tells you how much is left.
- **Statuses.** *Available* lots can be used. *On hold* (waiting for a decision) and *quarantine* (kept apart, suspected bad) lots count as on hand but can't be consumed until released. A lot taken down to zero becomes *consumed* or *scrapped*; it stays on record for genealogy.
- **Lot-tracked or not.** For a lot-tracked material every receipt is its own numbered lot. For one that isn't, such as end caps, there is one **balance** at each location (shown as *Balance at RM-STORE*, with no lot number); receipts add to it.
- **Expiry and shelf life.** A material can have a **shelf life** (set on its inventory page): a new lot received or produced without an expiry date expires that many days later. An **expired** lot is marked in amber; it can't be consumed, shipped or merged (into another lot or a silo's balance). It can still be moved, held, written off or counted. If quality extends it, use **Correct…** on the lot to set the new expiry, with a comment saying why.
- **One material at a time.** A location marked **One material at a time** refuses another material, whether received, moved, split there or produced, until the quantity of the material already there is 0.
- **Locations where lots mix.** A silo or tank marked **Lots are indistinguishable** on the Equipment page keeps one balance of each material, whatever it is. Everything that arrives joins it: receipts, moves, splits sent there and production. A receipt with a lot number is kept as that lot first, then merged in, so a trace still leads back to the supplier's lot; with no number it goes straight into the balance. Only available stock can join an available balance (release a held lot first), and the balance takes the earliest expiry of what joins it. A confirmation says when a lot has joined a balance. A balance that is moved somewhere else stays a balance there: mixed stock stays mixed.

### Find what you have: the Stock tab

1. Open **Inventory**. Every active material is listed, grouped by **material class** (*Resins*, *Extruded profiles*…). Each class header shows how many of its materials are in stock.
2. Type in **Find a material** to filter by code or name, or pick a class.
3. Pick a **location** to see only the stock held there.
4. Tick **In stock only** to hide materials with nothing on hand.
5. Click a group header to fold it; use the two buttons at the end of the toolbar to unfold or fold them all.
6. Click a material's code to open it.

Each row shows **On hand** (everything not used up), **Available**, **Held** (on hold or in quarantine, in amber), the number of **lots** and **locations**, and the **next expiry** (amber when it is within 14 days or past).

### A material: lots, locations and where used

The material page shows tiles for on hand, available, held, lots and locations, and its **shelf life** in days: click **Change** to set it, or leave it blank for none (lots already on hand keep their expiry). Then:

- **Lots on hand**, with status, quantity, location, supplier lot, expiry and where each came from. **Used-up lots** at the top lists the consumed ones.
- **By location**: the balance at each location. Click one to list its lots.
- **Where used**: every released or draft **BOM** that lists the material as a component (with the quantity per base quantity and the operation that consumes it), and every **open job** planned to consume it, with how much it has consumed so far. **Made from** links to the material's own BOMs.

### Find a lot: the Lots tab

1. Type a **lot number**, a **supplier lot** number or a material code, and/or choose a material, location, status or expiry window (*Expired*, *within 7, 30 or 90 days*). To find lots by a [lot attribute](#lot-attributes-and-certificates), choose it and give a value: a range (**From**, **To**) for a number or date, a choice or yes/no, or text it contains.
2. Click **Search**. Without a status, only lots with stock on hand are found; choose *Consumed* or *Scrapped* to find used-up lots.
3. Click a lot to open it. At most 500 lots are listed; narrow the search if you hit that.

### Receive a lot

1. Click **Receive** (on the Stock tab, or on a material's page, where the material is filled in).
2. Choose the **material** and the **location** it goes to.
3. For a lot-tracked material, enter the **lot number** (required, and unique for that material in the site; optional where lots mix), and if you have them the **supplier lot** and **expiry date**.
4. Enter the **quantity** and its **unit**, and an optional comment.
5. Click **Receive**. A green bar confirms it, with a link to the lot.

### A lot's page

The lot page shows its facts (status, quantity, location, where it came from, supplier lot, expiry), its **genealogy** and its **history**: every movement with the time, what happened, the change, the quantity after it, where, the job, the reason and comment, and who did it. The buttons at the top act on the lot:

| Button | Does | Needs |
| --- | --- | --- |
| **Consume** | Records that a job used some of the lot. | An available lot with stock. Choose the job; jobs planned to use this material come first, marked *planned*. |
| **Move** | Moves the whole lot to another location in the site. For a balance, enter how much to move; it joins the balance there. | A lot on hand. |
| **Write up** | Adds stock that was found or miscounted. A used-up lot comes back as available. | A quantity and an adjustment reason. |
| **Write off** | Removes stock: damage, loss, a count correction. Leave the quantity blank to write off the whole lot. At zero the lot becomes *scrapped*. | An adjustment reason. |
| **Split** | Splits part of the lot into a new lot with the same material, status, supplier lot and expiry, here or at another location. A blank number becomes *R-24-0917-1*, *-2*… | Less than the whole lot (to move all of it, use **Move**). |
| **Merge…** | Takes other lots into this one: tick them in the list. They go to zero (*merged into*), and this lot takes the earliest expiry of them all. | Lots of the same material, at the same location, in the same status. |
| **Ship** | Sends stock out of the site, for example to a customer or another plant. At zero the lot is *shipped*. | An available lot, and a delivery or shipment number. |
| **Hold…** / **Change hold…** | Puts the lot **on hold** or in **quarantine**, or switches between the two. | A hold reason. |
| **Release** | Makes a held or quarantined lot available again. | — |
| **Correct…** | Fixes the lot number, supplier lot or expiry, for example after a typo on receipt. | A comment saying why. A balance's number can't change. |
| **Reverse** (on a row of the history) | Undoes a movement with a new one that puts it back, as if it never happened; nothing recorded is changed. See [Reverse a movement or a declaration](#reverse-a-movement-or-a-declaration). | An adjustment reason and a comment. Once per movement. |

Some reasons ask for a comment; the form says *(required)* when they do.

### Reverse a movement or a declaration

Something recorded wrongly is put right by reversing it, then recording what really happened. A reversal is a new movement that undoes the old one; the old one stays in the history, marked as reversed, with who reversed it, the reason and the comment.

1. Open the lot's page and find the movement in its **History**.
2. Click **Reverse** on its row. Rows that can't be reversed (a reversal, or a movement reversed already) have no button.
3. Read what the reversal will do, choose an adjustment **Reason** and write a **Comment** saying why. Both are required.
4. Click **Reverse**. If something since depends on it, Pulaski says what (for example *Since then: consumed into WO-1001-10*) so you can reverse that first.
5. If an amount was wrong rather than the whole movement, record the right amount again as usual.

What can be reversed on a lot's page:

| Movement | What the reversal does | When it can't |
| --- | --- | --- |
| The **latest** receipt, write-up, write-off, move, hold or release, or shipment | Puts the lot back as it was; a reversed receipt of a whole lot makes it *cancelled*. | Only the latest, for moves, holds and shipments. |
| An **earlier** receipt, write-up or write-off | Puts back only the quantity, at the time it counted. | The lot's stock would go below zero at some time since, or a posted count came after it. |
| **Consumption** into a job | The quantity goes back to the lot; it stops being a parent of what the job made unless more of it went into that job. | The job is closed or cancelled, or the lot was shipped since. |
| A **split** | The new lot is cancelled and its quantity goes back to the lot it came from. | Anything has happened to the new lot since. |
| A **merge** | The lot that was merged gets its quantity and status back; the lot it joined goes down by that much. | It joined a balance where lots mix (write the balance down instead), or the lot it joined no longer holds that much. |
| A **transfer between balances** | The quantity goes back to the balance it came from. | The receiving balance no longer holds it. |
| **Production** | Reversed with its production report, on the job's page (below). | — |

**In detail**

- **As if it never happened.** A reversal counts at the time the movement it undoes did, so stock on a past date, aging and a silo's genealogy come out as if the movement had never been made. That is also why it must fit the lot's history from then on.
- **Genealogy.** Reversing consumption, a split or a merge ends the genealogy link it made; the link stays in the record as ended, and traces no longer follow it.
- **Who.** Reversing on a lot's page needs *inventory.correct* (Quality and Auditor to start with).
- **Production recorded before this version** has no report to reverse with: its row on the lot's page can be reversed on its own, which takes the quantity back but leaves the job's reported output as it was. Pulaski says so when you do it.


### Lot attributes and certificates

Below the facts, **Attributes and certificates** keeps what quality knows about the lot: measured properties such as melt index, and certificates of analysis or other documents. Recording them never changes the lot or its history.

1. **Record an attribute:** type or pick its value next to its name and click **Save**. Numbers use a decimal point (`11.2`, not `11,2`); dates are picked from the calendar. Clear the box and click **Save** to remove a value. The attributes offered are those defined for the material's class on [Configure → Materials → Lot attributes](#lot-attributes).
2. **Attach a certificate:** choose its **Kind** (*CoA*, *CoC*, *Test report*, *SDS* or *Other*), give it a **Title**, and either pick a **File** (PDF, PNG or JPEG, up to 10 MB), or give its **Document no.** or a **Link** to where it is kept (a document system or supplier portal), or both. Add who issued it and when if you know. Click **Attach**.
3. Click a file's name to download it, or **Open link** to open the reference.
4. **Withdraw…** a certificate that was wrong or has been replaced: give the reason. It stays on the lot, marked withdrawn with who did it and why, and its file can still be downloaded.

**In detail**

- **Who:** anyone who can see inventory sees attributes and certificates; recording values and attaching or withdrawing documents needs permission to record lot attributes and certificates (the Quality role has it).
- **Nothing is deleted.** A certificate is withdrawn, never removed, and a stored file can't be changed afterwards (the database refuses it). Pulaski keeps a fingerprint (SHA-256) of each file. A file is recognised by its contents, so a renamed file of another kind is refused.
- **Split and merge.** A lot split off takes its parent's certificates and attribute values with it. A merged lot, or a balance a lot joins where lots mix, takes the certificates of every lot that went into it, marked *Carried from …*; its own attribute values stay as they are.
- **An attribute no longer offered** for the material (switched off, or its classes changed) still shows a value already recorded, which can only be cleared.
- **Concurrency.** If someone else changed the same value since you opened the page, you'll see *"changed by someone else"*: reload and try again.
- **Audit.** Each change is an event: `pulaski.lot.attribute_set.v1` (with the value before and after), `pulaski.lot_document.attached.v1` and `…withdrawn.v1`.

### Record what a job used and made: the Jobs tab

Until Pulaski records production from the machines, operators declare it here. Material is used and made while a job runs, so everything goes into one of the job's runs (see [The Floor](#11-the-floor-running-jobs-and-reporting-production)).

1. Open the **Jobs** tab and click a job (search by job, order or material). Production jobs that are running, paused, or completed but not yet closed are listed; start a job on the Floor first.
2. The job page shows the **components** planned for it (from its order's BOM) with how much has been consumed and how much is still to use, and the **lots it made**.
3. To record material used: click **Consume a lot**, choose the material (planned ones are marked), choose the lot, check the **Run** (the open run is chosen; pick an ended one to record use found afterwards), enter the quantity and click **Record consumption**. Using more than the job's plan allows is recorded with a warning to check it.
4. To record output: click **Declare production**, check the **Run**, choose the location the output goes to, enter the quantity (the job's unit is filled in) and click **Record production**. Leave **Lot number** blank and Pulaski numbers it after the job: *WO-1001-10-01*, *-02*… Enter an existing number of a lot this job made at the same place to add to it.

You can also consume from a lot's own page, and declare production from the Stock tab (choose the job there). Declaring production here is the same as reporting good output on the Floor, with the same rate check; report scrap on the Floor or the job's page.

### Allocate lots to a job

Allocating sets stock aside for a job before it runs, so the floor knows which lots to bring and other jobs don't count on them. It doesn't move or use anything: consumption is still recorded as above.

1. Open the job's inventory page (from the **Jobs** tab, or a job on the **Shortages** tab, which lists jobs not started yet too).
2. Under **Allocations and suggested lots**, each component shows what it needs, what has been consumed and allocated, and what is **still to allocate**.
3. The **suggested lots** cover what is still needed, the one that expires first first, then the oldest; each says why (*Expires 3 Nov 2026, the earliest; received 12 Oct 2026; 40 kg free*).
4. Click **Allocate** on a suggestion, or **Allocate all suggested** for every component.
5. **Release** gives an allocation back. **Refresh** puts the job's allocations right after stock or the schedule has changed (see below).
6. On the **Shortages** tab, **Refresh allocations** does the same for every open job of the site, the job needed soonest first.

**In detail**

- **What is suggested.** Only available lots of the component at the job's site, with stock not set aside for other jobs. Under **lots not suggested**, each other lot says why: on hold or in quarantine, expires before the job needs it, all allocated to other jobs, or not needed because the lots before it cover the need. *When the job needs it* is its planned start, or now once that has passed or it is running.
- **What allocated means.** The stock stays where it is and can still be moved. Other jobs' suggestions and the **Shortages** tab treat it as taken: it covers its own job first. Consuming the lot into its job uses the allocation up. Consuming it into **another** job is allowed, with a warning naming the job it was allocated to; refresh that job afterwards.
- **When an allocation goes wrong.** If the lot is put on hold, would expire before the job needs it, or no longer holds what is allocated (written off, shipped, consumed elsewhere), the allocation shows why in amber. Nothing changes until someone clicks **Refresh**. Refresh then releases what can't be used, trims what a lot can only partly give, and allocates what is still needed from the suggestions.
- One allocation per lot and job: allocating more of the same lot adds to it. Only a job's planned components are allocated, in their base unit, and not to a job that is completed, closed or cancelled.
- **Who.** Allocating, releasing and refreshing need *lot.allocate* (Planner, Supervisor and Quality to start with). Anyone who can see inventory sees allocations and suggestions, and a lot's page lists the jobs it is allocated to. Each change is recorded as an event.

### Trace a lot

On a lot's page, **Genealogy** first shows the lots it was **made from** and the lots made from it (**used in**). Click **Trace back** to see everything it was made from, all the way up, or **Trace forward** to see everything made from it, all the way down. Each lot is indented under the one it was reached from; click any lot to open it.

**Through a silo or tank.** Where lots mix, a trace keeps only the lots that, by how the location [flows](#how-a-silo-flows), went into the withdrawals that matter: back from a lot a job made, the arrivals that fed that job's withdrawals; forward from an arrival, what was made from withdrawals it fed. Each such lot carries a chip saying how sure that is (point at it for why):

- **Exact:** only one arrival could have been in the silo then.
- **Estimated:** worked out from the declared quantities by the flow model; likely, not measured.
- **Possible:** the declared movements don't settle it, for example more was taken than was put in (often after a count correction), or material would have left before the minimum residence time.

A balance's own page shows **Where what was taken came from**: every withdrawal with the lots it came from, their quantities and how sure each is.

#### How a silo flows

On **Inventory → Locations**, a location where lots mix shows how it flows; click **Change** (needs permission to maintain equipment):

- **Mixed** (the default): everything blends, as in a tank or a mixed silo. A withdrawal takes a share of every arrival still there, by quantity.
- **First in, first out:** plug flow, as in a mass-flow silo or bin. A withdrawal takes the oldest material first.
- **Minimum residence** (optional, in minutes): the least time material stays before it can leave. Material taken before then is only *possible*.

Everything is worked out from the movements as recorded (by when they count, so [back-dated](#back-date-a-movement) ones fit in), not from measurements: a silo's level isn't read yet. Changing the flow changes every trace through the silo at once; nothing is stored.

### Check for shortages

The **Shortages** tab answers *"will we have the material for the jobs coming up?"*.

1. Open **Inventory → Shortages**. It lists every component the site's open jobs still need, short ones first.
2. Narrow it to a line or department (and everything under it) with the equipment picker, or to jobs needed soon: the next 24 hours, 3, 7 or 14 days.
3. Choose the timing: **Jobs as the schedule has them now** (the default, as the Schedule screen shows them) or **Jobs at their published times**.
4. Clear **Short only** to see covered components too.
5. Click a component to see each job that needs it, in the order they are needed: what it still needs, how much comes **from stock** and how much **incoming**, whether it is **covered** or how much is short, and when it is needed (*as scheduled now*, *published start*, or *hold-until or window*).
6. Click a job to open it, or **stock** to open the component.

Each row shows **Needed** (what the jobs still need: planned less consumed), **Available**, **Held** (on hold or quarantine, in amber: releasing it might cover the gap), **Incoming** (open [expected deliveries](#incoming-deliveries), with the next one's time, in red when late), **Short** (in red), and the **first short** job with when it is needed.

**In detail**

- **Order.** Jobs take supply in the order they are needed, then job number; jobs with no time come last. With live timing a job's time is where the schedule projects it now, so a job that has slipped or moved up is ordered by its new time (point at *as scheduled now* to see its published start). Jobs planned to a department, and jobs not on the schedule, keep their published start or hold-until.
- With a horizon, jobs needed after it, and jobs with no time at all, are left out; the horizon uses the same timing.
- **Allocations.** Stock [allocated](#allocate-lots-to-a-job) to a job covers that job first and isn't free for the others.
- **Supply.** Each job takes **available** stock first, then incoming deliveries due by the time it needs them. A delivery due after a job needs the material doesn't cover that job, however much arrives. A job already under way needs what it has left from now, and a late delivery counts as arriving now. Held stock isn't counted.
- **Not purchasing.** Nothing is suggested, ordered or rescheduled: deliveries are only what the ERP or a person said is coming, and ordering stays in the ERP.
- A component planned in a unit that doesn't convert to its base unit is left out, with an amber note naming the job.

### Incoming deliveries

The **Incoming** tab lists purchases and transfers expected to arrive, for the shortage check. Pulaski doesn't order anything; the ERP sends them, or you enter them.

1. Open **Inventory → Incoming**. Open deliveries are listed by when they are expected; late ones in red.
2. **Expect a delivery:** choose the **Material**, enter the **Quantity** and **Unit**, when it is **Expected**, and optionally the **Reference** (PO, transfer or ASN number), **Supplier** and a **Note**. Click **Add delivery**.
3. **Change** an open one when the date or quantity changes, or **Cancel** it if it won't come.
4. When it arrives, **Receive** the lot as usual and choose it under **Against expected receipt**: it closes, and its row links to the lot. Nothing more is expected of it, whatever quantity arrived; if more is still coming, add another.
5. Tick **Show received and cancelled** to see closed ones.

**In detail**

- **Who:** anyone who can see inventory sees them; adding, changing and cancelling needs permission to receive lots.
- **From the ERP:** `POST /api/inventory/expected-receipts/import` takes one delivery with the ERP's name and its key there, the site, the material and unit codes, the quantity and expected time, the reference and supplier, and whether it is cancelled. Sending the same one again changes nothing; a change updates it while it is open; once received or cancelled in Pulaski it stays so. It is listed with the ERP's name beside the reference.
- The unit must convert to the material's base unit, so the shortage check can count it.
- **Events:** `pulaski.expected_receipt.created.v1`, `…updated.v1`, `…cancelled.v1` and `…received.v1`.

### The movement journal

The **Journal** tab lists every movement in the site, newest first: receipts, write-ups, write-offs, moves, holds, consumption and production.

1. Open **Inventory → Journal**. It shows the last 7 days.
2. Change the **From** and **to** dates (whole days at the site; at most a year), or filter by movement, material, location, lot or supplier lot number, and **Who** (part of the person's name).
3. Click **Search**. Click **Show older** at the bottom for the next 200.
4. Click a lot, a material or a job to open it.

A material's and a job's pages have a **Movements** button that opens the journal filtered to them.

### Count a location

A **count** checks what is really at a location and puts the records right.

1. Open **Inventory → Counts**, choose the **location**, add a note if you like (*month end*), and click **Start a count**. Pulaski takes a snapshot of every lot there and opens the count.
2. Count each lot and type what you find in **Counted** (in the lot's unit; enter **0** for a lot that isn't there). The **Difference** shows how far it is from what the lot holds now.
3. Click **Save counts** as you go; you can come back and carry on later.
4. When every lot is counted, choose the **reason** (normally *Count correction*) and click **Post**. Each lot that differs is written up or written off, with the count number as its reference. Lots that match aren't touched.
5. **Cancel count** drops it without changing anything.

**In detail**

- If a lot moves after you counted it (someone consumed or moved it), posting is refused for it, marked *changed since counted*: count it again. A lot that moved after the count started but before you counted it is fine: the count is what is there.
- **Now** turns amber when a lot changed since the count started.
- Stock you find that isn't in the count is received separately. Count numbers are *CNT-* and the date the count started (UTC), then a number.

### Back-date a movement

When a receipt, consumption, production, write-up or write-off is entered late, it can count from when it really happened.

1. Open the movement as usual (**Receive**, **Consume**, **Write up**, **Write off**, or production on the Jobs tab).
2. Fill in **Counts from** with the date and time it happened, in the site's time zone. The field shows only if you may back-date.
3. Write a **comment** saying why it is late (required), and save.

**In detail**

- **Who:** back-dating needs permission to back-date inventory movements (the Quality and Auditor roles have it), as well as permission for the movement itself.
- **Limits:** at most 31 days back, and never in the future. A movement can't count before the lot had any stock, before a posted stock count that counted the lot, or so that the lot would have been below zero at any time since. The message says when it would have gone short.
- **Runs.** Back-dated consumption and production go into the run of the job that was going on then (or the run you choose, which must have been going on then); if the job wasn't running then, it is refused. Back-dated production counts in the shift it happened in, for production by shift and OEE.
- **Expiry** is checked when the movement counts: a lot that was in date then can be consumed then, even if it has expired since. A back-dated receipt's or production's expiry runs from when it counts.
- **Both times are kept.** The lot's history lists a back-dated movement at the time it counts, with *recorded …* beneath it; the journal lists it when it was recorded, with *counts from …*, and its export has both. Genealogy links made by back-dated consumption or production show *since …* with *(back-dated)*. The event log records it now, with `EffectiveAt` in its data.
- **Reversing** a back-dated movement undoes it at the time it counted, so every past date comes out as if it had never happened.
- **Only quantity movements** can be back-dated: moves, holds, splits, merges, shipments and counts always count when they are recorded.

### Reports and exports

**Inventory → Reports** has two reports:

- **Stock on a date:** each material's on-hand quantity at the end of the day you choose. It adds up every movement that counts by then, including [back-dated](#back-date-a-movement) ones entered later, so a past date can change after a correction, and gives the same answer whatever order movements were entered in. Older dates take longer. Held stock is included: a lot's status at that time isn't reconstructed.
- **Aging:** on-hand stock by how long ago each lot was received or made (from when its first movement counts, so a back-dated receipt ages from its real day): up to 30 days, 31–90, 91–180 and over 180 (in amber). Balances of materials that aren't lot-tracked aren't aged, and a split lot counts from the day it was split.

**Export CSV** downloads a spreadsheet file from the **Stock** tab (what is on screen, with its filters), the **Lots** tab (every matching lot, up to 10,000), the **Journal** (every matching movement, up to 10,000) and both reports. Lot search shows 200 at a time: click **Show more** for the next.

### Reason codes

**Configure → Reasons** lists the reasons people choose, one tab per kind: inventory adjustments, holds, scrap, run ends, **utilization** (what a machine's time counts as), and kinds later screens will use.

1. To add one, type a **code** (letters, digits, `-`, `_` or `.`), a **name**, choose **Every site** or one site, tick **Needs a comment** if people must explain, and click **Add**.
2. To change one, edit its name, comment setting, order or **Active** box, then click **Save** on its row (**Undo** throws the change away).
3. A code and its kind can't change once created, because history refers to them. To stop using a reason, untick **Active**.
4. A **utilization** reason also has a **Class**: *Runtime* (producing or running slow), *Downtime* (an unplanned stop, counted against availability) or *Planned (not in OEE)* (setup, teardown, cleaning, planned maintenance, breaks). Choose it when you add the reason; you can change it later on its row and **Save**.
5. To keep a long list tidy, file reasons under **groups**. Add a group by ticking **Group** before you click **Add**: it holds reasons (and other groups) but is never chosen itself. Choose **Under** to add a reason or group straight into a group.
6. To move a reason or group, choose another group in its row's **Under** list (or **At the top**) and click **Save**. Everything inside a group moves with it.

**In detail**

- **Where people see it.** Wherever a reason is chosen (writing stock up or off, holds, scrap, ending a run, indirect labor, coding a machine stop), it is listed with its groups, such as *Mechanical › Jam*, in the order of the tree. Groups aren't offered.
- **History doesn't change.** Movements, stops, reports and labor keep pointing at the reason itself. Moving it to another group, or renaming a group, changes nothing they recorded; they go on showing the reason's code and name.
- **Being a group is fixed.** Like the code and kind, whether a reason is a group can't change once it is created, so a reason history used never becomes a heading. To regroup, add a new group and move reasons into it.
- **The rules.** A reason goes only under a group of its own kind, never inside itself, and at most five levels deep. A reason for every site goes only under a group for every site; a site's reason goes under a group for every site or for that site. The **Under** list offers only groups that fit.
- **Switching off.** A group can be switched off only when everything in it is off. Nothing can be switched on, or moved, into a group that is off.
- **Order** sorts a reason among the others in the same group; the tree is shown group by group.
- A group has no class and never needs a comment: only the reasons in it are chosen.
- Each move is recorded as an event (`pulaski.reason_code.moved.v1`) with where it was and where it went.

### Locations

The **Locations** tab lists every piece of equipment that holds inventory, with how many lots and materials are there. **Stock** and **Lots** open those lists for that location, and **Label** prints a label to stick at the location, so moving stock there is one scan. To add a location, add equipment (for example a storage zone) on the [Equipment](#4-equipment) page and tick **Holds inventory**.

### Containers

Pallets, totes, boxes and bins that hold lots. Each lot inside keeps its own number, history and genealogy; moving the container moves them all.

1. Open **Inventory → Containers** and choose the **kind**, an optional **code** (blank makes one such as *PAL-1042*) and **where it is**. Click **Add container**.
2. **Pack a lot** into it from the lot's page (**Container → Pack…**, then choose the container) or by [scanning](#scan-labels). A lot elsewhere is moved to the container's location as it is packed. To pack part of a lot, split it first.
3. **Move…** a container to move it and every lot in it.
4. Click a container to see its lots. **Label** prints its label.
5. **Unpack** a lot on its page to take it out; it stays where it is. **Retire** a container once it is empty.

**In detail**

- **Leaving a container.** A lot moved on its own, split off, or used up (consumed, shipped or written off to zero) leaves its container. A part split off is loose.
- **Where containers can be:** any location that holds inventory at the site, except one where lots mix into balances (a silo), since a container keeps its lots apart. A location that takes one material at a time only takes a container whose lots are that material.
- **Mixed contents** are allowed: a container can hold lots of several materials. Balances of materials that aren't lot-tracked can't be packed. No containers inside containers.
- **History.** Packing, unpacking and moving with a container are ordinary movements in each lot's history (*Packed into …*, *Unpacked out of …*, *Moved with …*), and `pulaski.lot.packed.v1`, `…unpacked.v1` and `pulaski.container.created.v1` / `…moved.v1` / `…retired.v1` in the event log. Genealogy isn't touched: a container isn't a lot.
- **Who:** containers, packing and unpacking need permission to move lots.

### Lot, container and location labels

**Print label** on a lot's page, **Label** on a container or location, or on the Scan page, gives a PDF label 4 × 6 in (about A6) to print on any printer: the lot or container number, a barcode, the same code in text beneath it (to type if the barcode won't scan), and the material, quantity, supplier lot and expiry for a lot.

- The barcode is Code 128 and holds a short Pulaski code: `PUL1L` and a number for a lot, `PUL1C` and a number for a container, `PUL1E:` and the code for a location. Each lot and container keeps its number for good, so a scan always names exactly that one.
- A label shows the lot as it was when printed; print a new one after a change you want on it.

### Scan labels

The **Scan** tab is for a barcode scanner that types like a keyboard (most USB and Bluetooth scanners do), or for typing a code.

1. Open **Inventory → Scan**; the box is ready for a scan. Scan a lot, container or location label, or type a lot, supplier lot or container number and press **Enter**.
2. If more than one thing matches typed text (say a lot and a container with the same number), choose the one you mean. A Pulaski label always matches exactly one.
3. Choose what to do:
   - **A lot:** **Move** (then scan where it goes), **Pack** (then scan the container), **Unpack**, **Consume…** (into a job), or **Open lot**.
   - **A container:** **Move with its lots** (then scan where), or **Open container**.
   - **A location:** **Receive here…** (the receive form opens with the location filled in), or **What is here**.
4. A green message confirms each movement; scan the next thing, or **Start again**.

**In detail**

- A scan that names nothing at this site says so: a Pulaski label from another site, or a lot used up, isn't found. Scanned typed text matches lots on hand by lot or supplier lot number, active containers by code, and locations by code, ignoring case.
- When a second scan is asked for (where to move, or which container), only that kind counts; scanning something else says so.
- Every movement made by scanning is the same as one made from the lot's or container's page, with the same rules, history and permissions.

### Send movements to the ERP

Pulaski can send every inventory movement (received, moved, consumed, produced, written up or off, shipped, held and so on) to your ERP as it happens, so the ERP's stock follows the floor's. It is switched on in the API's configuration by whoever runs Pulaski (see *In detail*); **Configure → Integrations** shows how it is doing and is where failures are put right.

1. Open **Configure → Integrations**. The top panel says whether the ERP webhook is **On** and where it sends, how many movements are waiting, how many have **failed**, and how many were delivered in the last 24 hours.
2. The list starts on **Failed**. Each row shows the movement, the lot, how many attempts were made and the ERP's last answer (its status code and message).
3. Put the cause right (the ERP rejected an unknown material, say, or a lot it doesn't know), then choose **Retry** on the row, or **Retry all failed** in the panel. It goes again within a few seconds.
4. If it shouldn't be sent at all (you have booked it in the ERP by hand, for example), choose **Skip…**, give the reason and choose **Skip it**. The ERP won't get that movement.
5. Choose **Refresh** to see the latest; switch the list to **Queued, retrying or held**, **Delivered**, **Skipped** or **All** to see the rest.

**In detail**

- **Nothing is lost in an outage.** Movements are queued in Pulaski's database in the order they were committed. While the ERP can't be reached, or answers with a server error, a timeout (408), *too early* (425) or *too many requests* (429), Pulaski keeps trying, waiting 10 s, then 20 s, 40 s and so on up to an hour between tries. After 12 attempts it stops and marks the delivery **failed**.
- **A refusal fails at once.** Any other 4xx answer (400, 401, 404, 422 …) means trying again won't help until something is fixed, so the delivery is marked failed straight away.
- **A lot's movements arrive in order.** While one of a lot's movements is waiting or failed, that lot's later movements are **held** behind it (shown as *Held*); other lots carry on. Retrying or skipping the failed one lets the held ones go.
- **Sent twice is safe.** Pulaski may send a movement again if it didn't hear the ERP's answer (at least once). Every message carries the movement's event id as its `Idempotency-Key`; the receiver should ignore an id it has already booked.
- **The message** is a CloudEvents 1.0 JSON document (`application/cloudevents+json`) holding the event as Pulaski recorded it: its type (`pulaski.lot.received.v1` …), the lot as its subject, the time, who did it, the reason and the movement's details. It is signed: `X-Pulaski-Signature` is `sha256=` and the hex HMAC-SHA256, with the shared secret, of the `X-Pulaski-Timestamp` value, a full stop, and the body. A receiver should check the signature and refuse old timestamps.
- **Switching it on.** In the API's settings (or environment), section `Integrations:Erp`: `Enabled` true, `Url` (the HTTPS address to post to) and `Secret` (shared with the receiver; keep it in a secret store or environment variable, not in a file in source control). Optional: `EventTypes` (type prefixes to send, default `pulaski.lot.`), `MaxAttempts` (12), `TimeoutSeconds` (15), `PollSeconds` (5) and `StartFromNow` (true: the first time it is switched on, only movements from then on are sent; false sends the whole history). Restart the API after a change.
- **Several API servers** share the work safely: only one sends at a time.
- **Who.** Anyone who can see Integrations (Administrator, Manager and Auditor to start with) sees the status and the deliveries; retrying and skipping need *integrations.manage* (Administrator to start with). Each retry and skip is recorded as an event with who did it, and a skip keeps its reason on the delivery.

### In detail

- **One site per lot.** Moves stay within the lot's site; consumption needs a job in the same site.
- **Concurrency.** Write up, write off, move, hold and release check that the lot hasn't changed since you opened it; if it has you'll see *"changed by someone else"*: reload and try again. Consumption and production don't check, so several people can declare against a busy line; the no-negative-stock rule still protects them.
- **Substitutions.** Consuming a material the job has no planned component of is allowed, and recorded as a **substitution** (amber on the job page and in the confirmation).
- **Genealogy is per job for now.** Every lot consumed into a job is recorded as a parent of every lot the job made, whichever came first. That can link more than really mixed (a resin lot used in the first hour shows as a parent of the last pallet). It narrows to time windows once Pulaski records production from the machines.
- **Times** are the moment you click, unless the movement is [back-dated](#back-date-a-movement). A movement recorded into a run that has ended keeps the time you entered it and names the run it belongs to.
- **What the ERP sees.** Each movement is an event in Pulaski's event log (`pulaski.lot.received.v1`, `…consumed.v1` and so on), sent to the ERP's webhook when that is switched on (see [Send movements to the ERP](#send-movements-to-the-erp)).

---

## 13. Materials

**Configure → Materials.** Every item and part Pulaski knows about: what you make, what you make it from, and what you pack it in. The page has three tabs, **Materials**, **Classes** and **Lot attributes**.

### Ideas to know first

- **Where a material comes from.** A material is either created here (origin **Pulaski**) or **imported from the ERP** (its origin chip shows the ERP's name, such as **ERP1**). For an imported material the ERP owns the **code, name, base unit and type**: you change those in the ERP, and the next import brings them in. Pulaski owns everything else: description, class, lot tracking, shelf life and whether it is active.
- **Type** is a label from the item master, such as the ERP's MRP class: *raw*, *intermediate*, *finished*, *packaging*, *consumable* or *rework*. It is for finding and grouping; no rule depends on it.
- **Classes** group similar materials, such as *Extruded profiles*, so a process or a changeover rule can name a class instead of each material. Classes are one level deep: a class has no parent class.
- **In use** means something refers to the material: a lot, an order, a job, a BOM, a capability or rate, a process or a changeover rule. Some fields are fixed once it is in use (below).

### Find a material: the Materials tab

1. Open **Configure → Materials**. Materials are listed by class, with *No class* last. Click a class heading to fold it.
2. Narrow the list: type in **Find by code, name or description**, or pick a class, a type or an origin (**Pulaski** or **ERP**).
3. Inactive materials are hidden; tick **Show inactive** to see them.
4. Click a material's code to open its page.

Each row shows the type, base unit, tracking (**Lots** for numbered lots, **Balance** for one quantity per location), shelf life and origin.

### Create a material

1. On the Materials tab, click **New material**.
2. Fill in **Code** (the item or part number, unique) and **Name**, both required.
3. Choose the **Type**, the **Class** (optional; only active classes are offered) and the **Base unit**, the unit stock is counted in (required).
4. Optionally enter a **Shelf life** in days, untick **Lot-tracked** for a material kept as one balance per location, and add a **Description**.
5. Click **Create and open**. The new material's page opens.

Materials from the ERP don't use this form: they arrive through the import (see *In detail*).

### The material page

The left side lists the material's properties, grouped into *Identity*, *Classification*, *Stock* and *Status*. Edit one the same way as equipment: text saves when you press **Enter** or leave the field, and a choice saves as soon as you pick it. Click a property to read what it means and, when it is greyed out, why it can't change.

The rest of the page shows what the material is connected to:

- **What uses it:** stock on hand (with the number of lots), open work orders, open jobs, released BOMs that use it as a component, and the lines that can run it. Click the stock tile, or **Stock** at the top, for its inventory.
- **Where it can run:** each line with a capability for it, and its planning rate; see [below](#where-it-can-run-capabilities-and-rates).
- **How it is made:** the processes that make it (**default** marks the one order creation picks first) and its BOMs.
- **Where it is used:** the BOMs that list it as a component, and how many open jobs plan to use it.
- **Its own conversions,** such as *1 m = 1.2 kg*. Click **Conversions** at the top to add one on the Units page.

### Where it can run: capabilities and rates

The **Where it can run** table lists each line that can run the material: its ideal rate, the **planning rate** Pulaski schedules with and where that comes from (*calculated* or *modified*; point at the word to see who set a modified rate and why), the standard changeover and crew, and the days it applies. ★ marks a preferred line. A capability that has ended is hidden; tick **Show ended** to see it in grey.

**Add a line**

1. Click **Add line**.
2. Choose the **Line**. Only active lines that run jobs are offered; a department gets its lines' capabilities.
3. Enter the **Ideal rate, per hour**, the best the line can do, and its **Rate unit**. The unit must convert to the material's base unit; for metres of profile on a line rated in kilograms, add a [conversion](#6-units-and-conversions) first.
4. Optionally enter the **Standard changeover** in minutes (used when no changeover rule matches), the **Crew size**, and tick **Preferred line**. For a material made in batches, enter the **Batch size on this line** when the line's equipment makes a different amount at a time (a 200 kg mixer next to the 50 kg ones); leave it blank for the material's batch size.
5. **Starts** is today unless you pick another day. **Ends on** is optional: leave it empty for a capability with no end.
6. Click **Add**. If the line has no planning rate for the material yet, the ideal rate becomes it, and a note says so.

**Change or end a capability**

- Click **Change** on its row, edit the values and click **Save**. A new ideal rate also updates the calculated planning rate; a modified rate stays as it is, and the note says so.
- Click **End now** when the line can no longer run the material. The capability stays in the list as *Ended*. If open jobs for the material are on that line, a note says how many: they stay, but can't be moved to that line or dispatched on it until it has a capability again.

**Set the planning rate yourself**

1. Click **Rate** on the line's row. The form shows the calculated rate and what it is based on.
2. Enter the **Modified rate** and a note saying why (for example *trial on new die*), then click **Set modified rate**. Pulaski now plans with your rate.
3. To go back, click **Rate** again, then **Back to calculated**. Whatever is in the note box is kept as the reason.

**Calculate the planning rate from run history**

What the line really makes, rather than its ideal rate.

1. Click **Rate** on the line's row. Under **From run history**, choose how far back to look (**Runs that ended in the last** 30 to 365 days; 90 to start with) and click **Work it out**.
2. Pulaski shows the rate the runs give, how many runs and hours it used, and how it compares with the rate in force. Below it, every run it looked at: its job, when it ended, its hours, good output and rate, and whether it was **Used** or **Left out**, with why.
3. If the rate looks right, click **Use … as the calculated rate**. Nothing changes until you do.
4. To go back to the ideal rate later, click **Rate**, then **Back to the ideal rate**.

**In detail**

- **The rate** is the good output of the used runs ÷ their hours, so a long run counts for more than a short one. Scrap doesn't count: the planning rate is for good output.
- **Which runs:** ended runs of production jobs of the material on that line, ended in the chosen days. Runs under 30 minutes, runs with no good output, and runs whose unit doesn't convert to the planning rate's unit are left out.
- **Unusual runs:** with four or more runs, a run much slower or faster than the rest (outside 1.5 times the spread of the middle half of the runs, and never closer than 10% either side of the middle run) is left out, so a breakdown or a report put into the wrong run doesn't move the rate. The limits are shown under the list.
- **Enough history:** a rate needs at least 5 used runs and 8 hours. With less, Pulaski says how much it has and offers no rate.
- **A modified rate stays in force.** Using a history rate changes the calculated rate only; if a modified rate is set, planning keeps using it, and the comparison shows how far it has drifted. Clear the modified rate to plan with the history rate.
- **Checked again when you accept.** If a run ended or was reported since you looked and the rate is different, Pulaski says so and shows the new figures; review them and accept again.
- **It doesn't update by itself.** A rate from run history stays until you work it out again or go back to the ideal rate, and a new ideal rate on the capability no longer changes it.
- **Who:** permission to set the planning rate (Planner, to start with).

- **Periods.** A capability applies from the start of its first day to the start of its end day, in the line's site time zone. One that starts today starts the moment you add it, so it can follow straight on from one you ended earlier today. Two periods for the same material on the same line can't overlap: end or change the other one first.
- **Nothing is deleted.** Ending sets the end to now. A capability that hasn't started yet can't be ended; change its dates instead.
- **The planning rate** is one per line and material, whatever the capability periods. Pulaski calculates it from the ideal rate until you use a rate from run history (above); from then on the ideal rate no longer changes it, until you go back to it. You can't change a capability's rate unit while the planning rate has a modified rate in the old unit: clear the modified rate first.
- **Events:** `pulaski.capability.added.v1`, `…changed.v1`, `…ended.v1`, `pulaski.run_standard.created.v1` and `…recalculated.v1`, besides the existing `…modified.v1` and `…modification_cleared.v1`.

### Import materials from a file

Load many materials at once, for example when setting up a new site, from a spreadsheet saved as CSV or from an ERP's ISA-95 export in B2MML XML.

1. On the Materials tab, click **Import file**.
2. Leave **Import** on **Materials** and pick the **Format**: **CSV** or **B2MML** (see [B2MML files](#b2mml-files) below). For B2MML, type the **External system** the items come from, such as `ERP1`, if they are the ERP's items.
3. Tick **Create missing classes** if the file may name classes that aren't on the Classes tab yet. Left unticked, such a row is an error, so a misspelt class can't slip in.
4. Prepare the file: click **Download a template** for an empty one in the chosen format. For CSV, its first line names the columns; the panel shows them all. **code**, **name**, **type** and **base_unit** are required; the rest are optional:

   | Column | Holds |
   | --- | --- |
   | `code`, `name` | Item number and name. |
   | `type` | `raw`, `intermediate`, `finished`, `packaging`, `consumable` or `rework`. |
   | `base_unit` | A unit code Pulaski has, such as `kg`, `m` or `ea`. |
   | `class` | A class code from the Classes tab (or a new one, with **Create missing classes**). |
   | `class_name` | The name for a class the import creates; the code when blank. Ignored for a class that exists. |
   | `description` | Notes. |
   | `lot_tracked`, `active` | `yes` or `no` (also `true`/`false`, `1`/`0`). |
   | `shelf_life_days` | A whole number of days. |
   | `external_system`, `external_key`, `external_version` | For items from the ERP: its name, the item's key there, and an optional version. |

5. Click **Choose File** and pick the file. Pulaski checks it straight away and saves nothing yet. Each row shows what would happen: **Create**, **Update** (with the fields that change), **Unchanged**, or **Error** with the reason. The summary names any classes the import would create.
6. If there are errors, fix the file and pick it again. Tick **Only rows with errors** to see just those. Changing the format, the external system or **Create missing classes** checks the same file again.
7. When every row is fine, click **Import N materials**. The list below updates.

**In detail**

- **All or nothing.** If any row has an error, nothing is imported. A check run never saves anything.
- **Which material a row is.** A row with `external_system` and `external_key` is the ERP's item: matched on them, created if new, and its code, name, base unit and type updated (as in the [ERP import](#in-detail-6)). A row without them is a Pulaski material, matched on its code. A row whose code belongs to an ERP material is an error: give its external columns or change it in the ERP.
- **Left out or blank.** A column the file doesn't have keeps what each material already has (or the default for a new one: lot-tracked, active, no class). A blank cell in `class`, `description` or `shelf_life_days` clears it; a blank `lot_tracked` or `active` keeps it.
- **The rules are the same as on the material page:** a base unit that is in use can't change, lot tracking can't change while stock is on hand, an inactive class can't be given to a material, and a code or ERP key can appear only once in a file.
- **New classes.** With **Create missing classes**, a class code the file names that doesn't exist is created once (active, named from `class_name` or its code) and used by every row that names it; each is recorded as `pulaski.material_class.created.v1` with the import as its reason. An inactive class is never switched back on by an import. In B2MML, a `ClassName` property gives the name.
- **A CSV file:** comma or semicolon separators, quotes around a value that contains them (or line breaks), column names in any order and case (`Base unit` and `base-unit` both work, as do `item_code` and `uom`). An unknown column stops the import, so a misspelt name isn't ignored. At most 5 MB and 5,000 rows per file.
- Each imported row is recorded in the event log as usual, with the reason *CSV import* or *B2MML import*. The same import is `POST /api/materials/import/csv` or `…/import/b2mml` (`?dryRun=true` to check, `&externalSystem=ERP1` for B2MML, `&createClasses=true`), for scripts; `GET /api/materials/import/sources` lists the formats and `GET /api/materials/import/{csv|b2mml}/template` downloads a template.

#### B2MML files

B2MML is the XML form of the ISA-95 standard that many ERPs and integration tools can export. Pulaski reads its material information: each `MaterialDefinition` is one material.

| In the file | In Pulaski |
| --- | --- |
| `ID` | Code; with an external system, also the item's key there |
| The first `Description` | Name (required) |
| The first `MaterialClassID` | Class |
| A `MaterialDefinitionProperty` with the `ID` `MaterialType`, `BaseUnit` (or `BaseUoM`), `LotTracked`, `ShelfLifeDays`, `Active`, `Description` or `ExternalVersion` | That field, from the property's `Value/ValueString` (for the base unit, its `UnitOfMeasure` also works) |

For example:

```xml
<MaterialInformation xmlns="http://www.mesa.org/xml/B2MML">
  <MaterialDefinition>
    <ID>PP-1</ID>
    <Description>Polypropylene</Description>
    <MaterialClassID>RESIN</MaterialClassID>
    <MaterialDefinitionProperty><ID>MaterialType</ID><Value><ValueString>raw</ValueString></Value></MaterialDefinitionProperty>
    <MaterialDefinitionProperty><ID>BaseUnit</ID><Value><ValueString>kg</ValueString></Value></MaterialDefinitionProperty>
  </MaterialDefinition>
</MaterialInformation>
```

- **External system.** A B2MML file doesn't say which system its items belong to, so you name it on import. With it, each material is that system's item, matched on its `ID` exactly as a CSV row with `external_system` and `external_key` is. Without it, they are Pulaski materials matched on their code.
- **Other properties are skipped.** Properties Pulaski has no field for (density, colour…) are ignored, unlike an unknown CSV column. Property IDs are compared ignoring case, spaces and underscores, so `Shelf life days` and `shelf_life_days` work too. Giving a field twice, such as a class by `MaterialClassID` and by a property, is an error on that material.
- **Left out or blank** works as in CSV: a property that isn't there keeps what the material has, and one with an empty value clears an optional field.
- Any B2MML namespace version (or none) works, inside `MaterialInformation`, a wrapper such as `SyncMaterialInformation`, or as a bare `MaterialDefinition`. The *Line* is where the material's `MaterialDefinition` starts. Further class IDs and descriptions (other languages) are ignored. Files with a `DOCTYPE` are refused. At most 5 MB and 5,000 materials per file.

### Import capabilities from a CSV file

Set up which lines can run which materials, and how fast, for many materials at once.

1. On the Materials tab, click **Import file** and set **Import** to **Capabilities**. (You need permission to maintain processes at the lines' sites.)
2. Click **Download a template**, and fill in one row per material and line:

   | Column | Holds |
   | --- | --- |
   | `material`, `line` | The material's code and the line's code. Required. |
   | `site` | The line's site code; needed only when two sites use the same line code. |
   | `ideal_rate`, `rate_unit` | The ideal rate per hour and its unit, such as `900` and `m`. Required, except on a row that ends a capability. |
   | `changeover_minutes`, `crew_size` | The standard changeover in minutes and the standard crew. |
   | `preferred` | `yes` if it is the preferred line for the material. |
   | `batch_size` | The line's own batch size in the material's base unit, for a material made in batches; blank for the material's. |
   | `active` | `no` ends the capability now. |

3. Click **Choose File** and pick the file. Each row shows **Create** (a new capability from now), **Update** (with what changes), **Unchanged** or **Error**, with what happens to the line's planning rate.
4. When every row is fine, click **Import N capabilities**.

**In detail**

- **Which capability a row is.** The one for that material and line in effect now. If there is one, its values change in place and its period stays. If there is none, a new one starts now with no end. `active` = `no` ends the one in effect now. Capabilities that start later or ended earlier are left alone, and nothing is ever deleted. To plan a future period, use the material page.
- **The planning rate follows** as on the material page: a new capability sets it from the ideal rate when the line has none, and a new ideal rate recalculates it while it is still based on the ideal rate. A modified rate stays in force.
- **Left out or blank.** A column the file doesn't have keeps what the capability has. A blank `changeover_minutes`, `crew_size` or `batch_size` clears it.
- **The rules are the material page's:** the line must be active and run jobs, the material active, and the rate's unit must convert to the material's base unit. A material and line can appear only once in a file. Every line must be at a site where you may maintain processes.
- **Numbers** use a decimal point and no thousands separator (`1500.5`), so `1,5` from a spreadsheet in another language is an error rather than 15.
- **All or nothing**, with a check first, as for materials. Each change is recorded as `pulaski.capability.added.v1`, `…changed.v1` or `…ended.v1` with the reason *CSV import*. For scripts: `POST /api/capabilities/import/csv` (`?dryRun=true` to check) and `GET /api/capabilities/import/template`.

### Batches

A material made in batches, such as a dry blend mixed 50 kg at a time, has a batch size. Set it in the property grid under **Batches**:

1. Enter the **Batch size** in the material's base unit and press **Enter**. Leave it blank for a material made continuously.
2. Check the **Batch rule** Pulaski picked: **exact** for finished goods (every batch exactly the batch size; partial batches need approval), **near** for everything else (batches may differ by the tolerance; partial batches are accepted). You can change it.
3. For the near rule, set the **Batch tolerance** (2% by default).
4. Optionally set **ERP orders not whole batches**: what happens when the ERP sends an order of this material that isn't whole batches. *The site's rule* uses the one on Site settings (see [Orders from the ERP](#orders-from-the-erp)).

From then on orders and jobs must be whole batches and production is reported batch by batch (see [Materials made in batches](#materials-made-in-batches)). Orders already made keep their quantity; Pulaski names any open order that is no longer whole batches.

**A line with its own batch size.** Equipment of different sizes can make the same material: give a line its own batch size on its row under [Where it can run](#where-it-can-run-capabilities-and-rates). Only the size changes; the batch rule and tolerance stay the material's, because they describe the product. A line can have its own size only once the material has one.

### What can change, and when

| Field | Pulaski material | ERP material |
| --- | --- | --- |
| Code | Until it is in use | In the ERP only |
| Name, type | Always | In the ERP only |
| Base unit | Until it is in use | In the ERP, until it is in use |
| Lot-tracked | While none of it is on hand | Same |
| Description, class, shelf life, active | Always | Same |
| Batch size, rule, tolerance | Always; open orders keep their quantity | Same |

- **Base unit.** Quantities are stored in the base unit, so it can't change once the material is in use, not even from the ERP: an import that tries is refused.
- **Lot-tracked.** Switching between lots and balances needs no stock on hand: use, move out or write off what is there first.
- **Shelf life** applies to lots that arrive from now on; lots already on hand keep their expiry.
- **Switching a material off** (setting **Active** to *false*) is always allowed. Afterwards it can't be received, put on new orders or added to BOMs, but what already uses it carries on. A yellow notice lists what still uses it, for example *a capability on 3 lines*. Pulaski never deletes a material.

### Classes

1. Open the **Classes** tab. Each class shows its code, name, how many materials are in it, and whether it is active.
2. **Add a class:** type a **CODE** (up to 30 characters, unique) and a name, then click **Add class**.
3. **Rename or switch off a class:** change its name or untick **Active**. **Save** and **Undo** appear on the row; click **Save**.
4. Click a class's material count to see its materials (inactive ones included).

A class's code can't change and a class is never deleted: switch it off instead. An inactive class isn't offered for new materials, and a material can't be moved into one, but materials already in it stay there.

### Lot attributes

The properties a site records on its lots, such as melt index, moisture or colour grade, recorded on each [lot's page](#lot-attributes-and-certificates).

1. Open the **Lot attributes** tab and, with more than one site, choose the site.
2. Type a **Code** (up to 30 characters, unique in the site; it can't change later) and a **Name**.
3. Pick its **Type**: *Text*, *Number* (with an optional **Unit**, for display), *Date*, *Yes / no* or *Choice* (type the choices, one per line). The type can't change later.
4. Leave **For every material** ticked, or untick it and tick the material classes it is for.
5. Set the **Order** it is listed in, and click **Add attribute**.
6. To change one, click **Change**: its name, unit, choices, classes, order and whether it is **Active** can change. Click **Save attribute**.

An attribute is never deleted: untick **Active** to stop it being recorded; values already recorded stay and can be searched. The list shows how many lots have a value of each. Removing a choice doesn't change values already recorded. Maintaining lot attributes needs permission to maintain lot attribute definitions (the Quality and Manager roles have it).

### In detail

- **The ERP import** is `POST /api/materials/import`, one item at a time, with the ERP's name, the item's key in the ERP, an optional version, and the code, name, base unit code and type. The first import of a key creates the material; later ones update the code, name, base unit and type, and change nothing (no new version, no event) when nothing differs. The ERP may rename an item's code even while it is in use, since the ERP's key, not the code, identifies it. The base unit must be a unit Pulaski has. To load many at once, [import a CSV or B2MML file](#import-materials-from-a-file).
- **Codes** are unique across all materials, whatever their origin; an import whose code is already used by another material is refused.
- **Events.** Each change is recorded in the event log as `pulaski.material.created.v1`, `…updated.v1` or `…imported.v1` (and `pulaski.material_class.created.v1` / `…updated.v1`), with the material before and after.
- **Concurrency.** As elsewhere, if someone else changed the material or class since you opened it, you'll see *"changed by someone else"*: reload and try again.

---

## 14. Users, roles and signing in

Everyone signs in to Pulaski. Your **roles** decide what you **see** and what you can **change**: you see only the screens, and only the sites, your roles allow, and the menu shows nothing else. A role is a set of **permissions**, such as *See inventory*, *Receive lots* or *Plan jobs*. An administrator decides what each role may do and gives each user their roles, each at a **site** (only Administrator is everywhere).

### Sign in

1. Open Pulaski. It shows **Sign in**.
2. Type your **User name** and **Password** and click **Sign in**.
3. The first time, or after an administrator reset your password, Pulaski asks you to **Change your password**: type the temporary password, then your new one twice, and click **Change password**.
4. To sign out, click the door at the top right.

Your session lasts up to 12 hours. When it ends, or an administrator switches you off, Pulaski shows **Your session ended. Sign in again to carry on.**

### Change your password

1. Click the padlock at the top right.
2. Type your **Current password**, then the **New password** twice.
3. Click **Change password**.

A password needs at least 10 characters, with two of: lower case, upper case, digits, symbols, and must not contain your user name. Five wrong passwords in a row lock you out for 15 minutes; an administrator can unlock you sooner by resetting your password.

### What each role can change

A new Pulaski starts with these roles. Your administrator may have changed them or added others: the **Roles** and **Permissions** tabs of **Configure → Users** show what each role may do today.

| Role | Can change |
| --- | --- |
| **Administrator** | Everything, at every site, including users and roles. |
| **Planner** | The schedule (place, sequence, pin, assign and cancel jobs); work orders (create, dispatch, un-dispatch, firm, cancel, close); planning rates; materials; allocating lots to jobs. |
| **Supervisor** | Start, pause, complete and reopen jobs; report production and reverse reports; record and approve labor; code machine stops; receive, move, ship, hold and release lots; allocate lots to jobs; stock counts; approve or reject partial batches. |
| **Operator** | Start, pause and complete jobs; report production; code machine stops; receive and move lots; clock themselves on and off. |
| **Quality** | Hold and release lots; correct inventory (write up, write off, correct, reverse, split, merge); allocate lots to jobs; approve or reject partial batches; record lot attributes and certificates; maintain lot attribute definitions; back-date inventory movements. |
| **Manager** | Reopen jobs; cancel and close work orders; approve labor; reason codes; shift schedules; people and crews; shop-floor terminals; lot attribute definitions; the site's settings. |
| **Auditor** | Correct inventory, including reversing consumption, splits, merges and transfers; reverse production reports; stock counts; back-date inventory movements. Can't plan or run jobs. |
| **Process engineer** | Processes, BOMs and which lines can run each material. |
| **Edge node** | Nothing on screen: it is for the user a Pulaski Edge node signs in as, to read its configuration and forward machine data. |

Equipment (with machine profiles and edge nodes), units and retrying or skipping deliveries to the ERP (**Integrations**) belong to the Administrator. Someone with two roles can do what either allows.

When you can't change something, Pulaski leaves out the button, or switches the fields off and says *You can view this, but changing it needs permission to …*. If you should be able to, ask an administrator.

### What each role can see

You see a screen, and its entry in the menu, only if one of your roles lets you see it at your site. A permission to change something includes seeing it: someone who may start jobs sees the Floor. A new Pulaski starts with these:

| Role | Sees |
| --- | --- |
| **Administrator**, **Auditor**, **Manager** | Every screen (Users only for Administrator). |
| **Planner** | Overview, Work orders, Schedule, Floor, Machines, Inventory, Materials, Processes and BOMs, Site settings (to look at). |
| **Supervisor** | Overview, Work orders (to look at), Floor, Machines, Inventory, Labor, Site settings (to look at). |
| **Operator** | Overview, Work orders (to look at), Floor, Machines, Inventory. |
| **Quality** | Overview, Floor, Machines, Inventory, Materials. |
| **Process engineer** | Equipment, Materials, Processes and BOMs, Units. |

- **Start page.** After signing in you land on the Overview, or, if you can't see it, the first screen in the menu you can.
- **A screen you can't see** isn't in the menu, links to it show as plain text and buttons for it are left out. Typing its address shows **Page not found**.
- **No screens at all.** Someone whose roles let them see nothing lands on **Nothing to see here yet**, with a button to sign out. Ask an administrator for a role.
- **Sites.** A role held at one site shows that site's data only: other sites' work orders, lots, lines and schedules aren't listed.

### Add a user (administrators)

1. Open **Configure → Users** (the **Users** tab) and click **New user**.
2. Fill in **User name** (letters, digits and `. - _ @`), **Name**, an optional **E-mail** and a **Temporary password**.
3. Choose a **Role** and, for every role but Administrator, the **site** they hold it at.
4. Click **Create user**, and tell them the user name and temporary password yourself: Pulaski sends nothing.

### Change a user's roles, details or password (administrators)

1. On **Configure → Users**, **Users** tab, click **Change** on the user's row.
2. **Details:** change the name or e-mail, or untick **Can sign in** to switch them off; click **Save details**.
3. **Roles:** choose a role (and site) under **Add a role…** and click **Add**; click **Take away** to end one.
4. **Password:** type a **Temporary password** and click **Reset password**. They must change it when they next sign in; this also unlocks them.

### Add or change a role (administrators)

1. Open **Configure → Users** and the **Roles** tab.
2. Click **New role**, or **Change** on a role's row.
3. For a new role, type a **Code** (lower-case letters, digits and `_`, such as `line_lead`; it can't change later) and a **Name**. Add what it's for if you like.
4. Under **May change**, tick each permission the role should have. They are grouped by area: Planning, Floor, Inventory, Configuration, Administration.
5. Click **Create role** or **Save role**. Then give the role to people on the **Users** tab.

To stop using a role, untick **Active** and save. A role someone holds can't be switched off: give them another role and take this one away first. **Administrator** has every permission and can't be changed.

### Give roles permissions, one at a time (administrators)

1. Open **Configure → Users** and the **Permissions** tab. Each row is a permission, grouped by area; each column is a role.
2. Tick a box to give the role that permission; untick it to take it away. It is saved straight away and applies to everyone who holds the role.

### In detail

- **Viewing permissions** are listed first on the Permissions tab, under *Viewing*: one per screen (Overview, Work orders, Schedule, Floor, Inventory, Equipment, Materials, Processes and BOMs, Units, Reasons, Shifts). A view a role gets with a change permission shows ticked and locked, with what it comes with when you point at it.
- **Permissions belong to Pulaski; roles belong to you.** The list of permissions is fixed by the version of Pulaski you run, and grows when new screens need one. Roles, their names and which permissions they hold are yours to change. Administrator always holds every permission, including new ones.
- **Changes to a role apply at once** to everyone who holds it, on their next click.
- **Every user needs a role.** A new user comes with one, and a user's last role can't be taken away: add another first, or switch them off.
- **Someone stays administrator.** The last active administrator can't lose the role or be switched off, and nobody can switch themselves off.
- **Roles are held at a site.** A Supervisor at PLT1 can start jobs at PLT1 only. Something that belongs to no site, such as a reason for every site or an enterprise-wide process, needs the permission at any site.
- **Partial batches:** whoever reported a partial batch can't approve or reject it; the job page says *You reported it: someone else decides*.
- **History** names who did what by user name, such as *oli.operator* on a lot's history.
- **Changes apply at once.** Switching someone off or taking a role away takes effect on their next click, without waiting for their session to end.
- **Ended, not deleted.** Taking a role away from someone ends it, and a role or permission is switched off rather than deleted; Pulaski keeps who held which role when, and who changed a role.
- **The first administrator** is created when Pulaski starts with no users, from the `Security:BootstrapAdmin` settings, with a temporary password to change at the first sign-in.
- **For integrators:** the API takes a bearer token from `POST /api/auth/sign-in`; every other call needs one, and a call without the permission is refused with 403 and the permission in words.

---

## 15. Shift schedules

**Configure → Shifts.** When each site works. Planning places jobs only inside shifts, so a job that would run into a weekend waits for Monday's first shift. A site can have several schedules: two shifts on weekdays for packing, around the clock for extrusion.

### Ideas to know first

- **A schedule** belongs to a site and uses its time zone. It repeats every so many days: 7 for a weekly schedule, 14 or 28 for a rotation. It has a day the cycle starts on, and shifts, each with a start time, a length and the days of the cycle it runs on.
- **Equipment follows a schedule, inherits one or runs around the clock.** On [Equipment](#4-equipment), each site, area or line has **Shifts**: *Inherit from parent* (the default), one of its site's schedules, or *Around the clock*. A line uses the nearest setting, its own first, then its area's, then its site's. With nothing set anywhere, it runs around the clock and the schedule marks it **24/7**.
- **Changes apply at once.** Change a schedule, or which one a line follows, and the schedule shows the new times on its next refresh.

### Add a shift schedule

1. Open **Configure → Shifts**. With more than one site, choose the site at the top.
2. Click **New schedule**. It starts as one day shift, 06:00 for 8 hours, Monday to Friday.
3. Type a **Code** (such as `2X8`; it can't change later) and a **Name**.
4. Set **Repeats every (days)** and **Cycle starts on**. For a weekly schedule leave 7 and a Monday.
5. For each shift, set its **Shift** code, **Name**, **Starts** time, **Hours** and, if you record labor, the **Crew** that works it, and tick the days it runs. **Add shift** adds one that starts when the last one ends, on the same days; **×** removes one.
6. Check **The next 7 days** below the grid: it lists the shifts as they will fall, and the hours per cycle. Problems, such as two shifts overlapping, show there instead.
7. Click **Create schedule**.

### Make a line follow it

1. Open **Configure → Equipment** and select the site, area or line.
2. Under **Working time**, set **Shifts** to the schedule. Everything below it that inherits now follows it too.
3. **Shifts in effect** shows the result. To make one line run around the clock under a site that has shifts, set that line's **Shifts** to *Around the clock*.

### Change one day: one-off changes

For an extra Saturday, a shift that starts late one day, or a shift that doesn't run on one day, without changing the repeating schedule.

1. On **Configure → Shifts**, click **One-off changes** on the schedule's row. The panel lists the changes from today on (**Show earlier changes too** lists the rest).
2. Choose the **Day** and the **Shift**. Pick one of the schedule's shifts, or type a new code for an extra shift. The line under the form says whether you are replacing that day's shift or adding one.
3. Under **That day it**, choose **Runs** and set **Starts**, **Hours** and **Crew**, or choose **Doesn't run**. The times start as the schedule's.
4. Add a **Note** if you like, such as why, and click **Save change**. Planning and labor use it straight away.
5. To change one, click **Change** on its row, adjust it and click **Save change**. To go back to the schedule for that day, click **Remove**.

The schedule list says how many one-off changes are coming up, and the preview when you change a schedule marks the shifts they set *(one-off)*.

### Change or switch off a schedule

1. On **Configure → Shifts**, click **Change** on the schedule's row.
2. Change its shifts, cycle or name and click **Save schedule**.
3. To stop using it, untick **Active**. A schedule that equipment follows can't be switched off: give the equipment another setting first. **Followed by** lists that equipment.

### In detail

- **A shift belongs to the day it starts on:** a night shift from 22:00 Monday to 06:00 Tuesday is Monday's. Shifts may run past midnight but may not overlap, also from the last day of the cycle into the first.
- **Days in the grid** are weekdays (*Mon*, *Tue*) when the cycle is a whole number of weeks, with the week number (*W2 Mon*) beyond one week, and *Day 1*, *Day 2* otherwise.
- **Daylight saving:** shifts keep their clock times. A shift that would start in the hour the clocks skip starts when they have gone forward; a shift's length is real time, so a night shift is an hour longer or shorter on the night the clocks change.
- **Hours a cycle** is the working time in one cycle, the sum of each shift's length times the days it runs.
- **One-off changes** apply to the one production day only: a shift on that day keeps the day it starts on, so a changed night shift still belongs to the day it starts. A changed shift can't overlap the shifts before or after it, and *Doesn't run* needs a shift the schedule runs that day. Changing the schedule keeps its one-off changes, and they are checked together. Once a supervisor has approved that shift's labor on that day, its one-off change can't be set or removed.
- **One-off changes and calendar exceptions** are different: a one-off change alters one schedule's shift on one day; a [calendar exception](#calendar-exceptions-holidays-and-shutdowns) takes time out of everything below a site, area or line, whichever schedule it follows.
- **Who:** maintaining schedules, and choosing which one equipment follows, needs permission to maintain shift schedules (the Manager role has it to start with). Seeing them needs permission to see shift schedules, or to see equipment.

---

## 16. Labor, people and crews

Who worked on which line, for how long, and against the standard crew. Supervisors check their crew onto a line at the start of a shift, operators can clock themselves on, and at the end a supervisor approves the shift's labor. Pulaski doesn't do payroll or attendance: export the hours for that.

### Ideas to know first

- **People and crews.** Everyone whose time is recorded is a **person**, with an employee number and usually a **crew**. A person needn't sign in to Pulaski; link one to a Pulaski user to let them clock themselves on.
- **The crew on shift.** Each shift in a [shift schedule](#15-shift-schedules) can name its crew, so Pulaski knows who should be on. A rotation is several shifts at the same time on different days, each with its crew.
- **Kinds of time.** *Direct* is running the line, *setup* is getting a job ready (it names the job), *indirect* is time off production such as cleaning (it needs a reason), *training* is training.
- **Jobs change, people don't re-clock.** Direct time on a line counts towards whatever ran on it while the person was there. Time on a line with nothing running is shown as *idle*.
- **A share of their time.** Someone tending two lines is on each at 50%. A person's time adds up to at most 100%.
- **Shifts and approval.** Time belongs to the shift running when it started (or starting within two hours). Once a shift has ended, a supervisor approves it: open time ends when the shift ended, and the shift is locked. After that, a change is a **correction** with a reason.
- **Breaks** are recorded on their own, paid or unpaid. A break's time comes out of the hours of every line the person is on, and is shown apart in the reports; the time they were clocked on stays as it was.
- **Terminals and badges.** A shared tablet or panel PC can be a **terminal** where people badge to clock on and off and take breaks, without signing in to Pulaski. Each person has one badge; a terminal can also ask for a PIN.
- **Qualifications** are the skills and certificates a line needs, such as a forklift licence. Each person holds them for a set of days. Someone checked onto a line without a qualification it needs is still checked on, and Pulaski warns.

### Set up people and crews

1. Open **Configure → People**. With more than one site, choose the site at the top.
2. On **Crews**, type a **Code** (such as `RED`; it can't change later) and a **Name**, and click **Add crew**.
3. On **People**, fill in **Employee no.**, **Name**, the **Crew** and, if they will clock themselves on, the Pulaski **User name** they sign in as. Click **Add person**.
4. In **Configure → Shifts**, set the **Crew** of each shift.

### Import people from HR or the ERP

Load everyone at once, and keep them up to date, from a file your HR system or ERP produces: a report saved as CSV, or SCIM 2.0 users in JSON (the standard format HR and identity systems use to share people).

1. On **Configure → People**, choose the site, then click **Import file** next to **Add person**.
2. Pick the **Format**: **CSV** or **SCIM**.
3. Type the **External system** the people come from, such as `HR1`. Leave it blank only for a list of people kept in Pulaski.
4. Prepare the file. For CSV, the first line names the columns; **employee_number** and **name** are required:

   | Column | Holds |
   | --- | --- |
   | `employee_number`, `name` | Their employee number and name. |
   | `external_key` | Their key in the HR system or ERP, such as a worker ID. Every later import finds them by it. |
   | `active` | `yes` or `no`: whether they work here. |
   | `crew`, `username` | A crew code on the Crews tab, and the Pulaski user they sign in as. |
   | `origin` | `hr` (the default) or `erp`. |
   | `external_system`, `external_version` | The system, if the file names it per row, and the source's version for reference. |

   For SCIM, each user's `employeeNumber` (from the enterprise extension) is the employee number, `displayName` (or the parts of `name`) the name, `active` whether they work here, and `externalId` (or `id`) their key.
5. Click **Choose File** and pick the file. Pulaski checks it straight away and saves nothing yet: each row shows **Create**, **Update** (with what changes), **Unchanged** or **Error** with the reason.
6. Fix any errors and pick the file again, then click **Import N people**. The list updates, and imported people show their system's name next to their employee number.

Import the same file, or next week's, as often as you like: people are matched on their key, so nobody is added twice.

### Check a crew onto a line

1. Open **Operate → Labor**. **On now** shows each line with its shift, crew, the job running and who is on (with the standard crew, when there is one).
2. Click **Check crew in** on the line. The crew on shift is ticked; people already on another line are listed but not ticked.
3. Untick anyone who isn't there, tick anyone else, and click **Check … in**.
4. To add one person for other time, click **Add someone**, choose who, the kind of time, the reason (for indirect) and their share, and click **Clock on**.
5. Click **End** next to someone to end their time, or **Check everyone out** at the end of the shift.

### Clock yourself on (operators)

1. Open **Operate → Floor**.
2. Click **Clock on** on the line you are working on. Pulaski refuses if you are already on another line with all your time: clock off there first. If the line needs a qualification you don't hold, a note says so.
3. Click **Start break** when you go on a break, and **End break** when you are back. One break covers all the lines you are on.
4. Click **Clock off** when you leave it. A break you are still on ends with it.

### Badge at a terminal (everyone)

1. Hold your badge to the reader (or type its number and press Enter). If the terminal asks, type your **PIN** and press **Go**.
2. The terminal greets you and shows what you are on. Press **Clock on** and a line (the highlighted one is this terminal's usual line), **Clock off**, **Start break**, **Start paid break** or **End break**.
3. It says what it did, and any warning (such as a missing qualification), then waits for the next person. **Not me / cancel** goes straight back.

### Set up a terminal (managers)

1. Open **Configure → Terminals**. Type a **Code** (it can't change later) and a **Name** such as where it hangs. Under **Offers the lines of**, choose the site, an area or a line, then the **First line offered**, and whether people identify with **Badge** or **Badge and PIN**. Click **Register terminal**.
2. Click **Enrolment code** on its row. A ten-character code shows, once, with the time it runs out (15 minutes).
3. On the device, open the address the page shows (ending `/terminal/enrol`), type the code and press **Enrol**. The device now shows the badge screen; leave it there.
4. To stop a terminal working (lost, stolen, moved), click **Change**, untick **Active** and save. To move it to another device, issue a new code and enrol the new device: the old one stops working.

### Issue badges

1. On **Configure → People**, click **Issue** in the person's **Badge** column.
2. Scan the badge into **Scan badge** (or type its number) and, for terminals that ask for one, type a **PIN** of 4 to 8 digits. Click **Issue**.
3. **Replace** gives a new badge (the old one stops working); **Revoke** takes it away, for a lost badge.

### Breaks (supervisors)

1. On **Operate → Labor → On now**, click **Break** (unpaid) or **Paid break** next to someone. They show *On break since …*.
2. Click **End break** when they are back. Leaving their last line ends a break too.
3. To record a break afterwards, open **Shift review**, choose the shift, and under **Breaks** choose **Who**, **From**, **To** and **Paid**, then click **Add break**.
4. To fix one, click **Change** on its row. After the shift is approved, click **Correct** or **Remove** and say why.

### Qualifications

1. Open **Configure → People → Qualifications**. Type a **Code** (such as `FORK`; it can't change later), a **Name** and, if a grant should expire, **Months valid**. Click **Add qualification**.
2. On **Configure → Equipment**, select a site, area or line and, under **Required qualifications**, choose the qualification and click **Require**. It applies to everything below that equipment too.
3. Back on **Qualifications**, under **Who holds what**, choose the person and the qualification. **Valid from** is today and **until** is filled in from the months valid; change them if you need to. Click **Grant**.
4. To extend one, click **Renew**: it starts the day after the current grant ends. **Change** fixes a grant's days; **Revoke** ends it, with a reason.
5. Tick **Expiring in 30 days** to see who needs renewing soon, or click **Holders** on a qualification to see who holds it.

### Review and approve a shift

1. Open **Operate → Labor → Shift review** and choose the **Day**. Each shift that has labor is a button; one with *approved* is locked.
2. Check the people, times and hours. Click **Change** to fix a start, end or share, or **End** for someone still on.
3. Once the shift has ended, add a note if you like and click **Approve shift**. Anyone still on is clocked off at the end of the shift.
4. To fix an approved shift, click **Correct** (change the times or share) or **Remove**, say why, and click **Save**. The original row stays, struck through; the hours count the correction.

### Labor reports

1. Open **Operate → Labor → Reports**, choose **From** and **to** (production days, up to 93), and click **Show**.
2. **By job** shows run hours, direct and setup hours, the standard (run hours × the line's standard crew size for the material) and **efficiency** (standard ÷ direct hours: under 100% took more people than the standard).
3. **By person** shows each person's worked hours by kind, how much of their direct time was idle, and their **paid** and **unpaid breaks**. **By shift** shows each shift's worked hours, its breaks and whether it is approved.
4. **Export CSV** on each table saves it, for payroll or the ERP.

### In detail

- **Who:** seeing Labor needs permission to see labor; checking crews in and changing time needs permission to record labor; approving and correcting needs permission to approve labor; People needs permission to maintain people and crews. Clocking yourself on needs permission to clock yourself on, and being set up as a person.
- **One line at a time per person**, and their open time at most 100% of their time in total.
- **Times can't be ahead of now.** Setup needs a job (the job running on the line, when added from **On now**); indirect needs an indirect labor reason.
- **Switching someone off** is refused while they are clocked on.
- **Imported people.** For someone imported with an external system and key, that system owns their **employee number, name and whether they are active**: those can't be changed on the People tab (change them there and import again). Pulaski keeps their crew, sign-in, badge and qualifications; a file may set the crew and user name too, and a column it leaves out keeps what they have.
- **Which person a row is.** A row with an external key is found by its system and key, even when their employee number or name has changed. The first time, a person already on the People tab with the same employee number is linked to the system instead of added again, and keeps their history, crew and badge. A row without a key is a Pulaski person found by employee number; a row whose employee number belongs to an imported person is an error. A key already used by someone at another site is an error: a person works at one site.
- **All or nothing.** If any row has an error, nothing is imported; a check never saves anything. Switching someone off from a file is refused while they are clocked on, like on the screen. An employee number or key can appear only once in a file. At most 5 MB and 5,000 people per file.
- **The file:** CSV columns in any order and case, with a few other names accepted (`Employee No`, `Full name`, `crew_code`). In CSV, the external system typed on the panel is used for rows that have an `external_key` but no `external_system` of their own. In SCIM, attribute names are matched without case, other attributes (department, e-mail…) are skipped, and the file may be a `ListResponse`, an array of users or a single user.
- **Audit.** Every created or changed person is recorded in the event log as `pulaski.person.created.v1` or `…updated.v1`, with them before and after and the reason *CSV import* or *SCIM import*. The same import is `POST /api/people/import/csv` or `…/scim` with `?siteId=` (and `&dryRun=true` to check, `&externalSystem=HR1`); `GET /api/people/import/sources` lists the formats.
- **Nothing runs on its own.** Time stays open until someone ends it, checks the line out, or approves the shift.
- **Hours** are worked time: elapsed time outside the person's breaks × share; an open session counts until now. Someone on two lines at 50% who takes a 30-minute break loses 15 minutes on each, and has 30 minutes of break.
- **Breaks:** a break must start while the person is on a line, belongs to that line's shift, lasts at most 4 hours and can't overlap another of theirs. Paid and unpaid breaks both come out of worked hours; paid ones are reported for payroll to add back. Breaks lock with their shift's approval, like time on a line.
- **Qualifications** belong to a site. A grant is valid from its first day to its last, both included, in the site's time zone; a renewal is a new grant, and the old one stays in the list. A revoked grant no longer counts from the moment it is revoked. Switching a qualification off stops it being granted or required. The Labor screen marks someone on a line without what it requires **Not qualified**; point at it to see what is missing and why.
- **Terminals:** a terminal records only the badged person's own time: direct time on a line it offers, ending their own sessions, and their breaks, under the same rules as clocking yourself on. Shift review shows *Badged at* and the terminal for that time. A badge that is unknown, revoked or from another site is just *not recognised*. Five wrong PINs in a row lock the badge for 5 minutes. A badge scan is good for 2 minutes; after that, badge again. Pulaski keeps only a scrambled form of badge numbers and PINs, so they can't be read back: to change a PIN, issue the badge again.
- **Who:** registering terminals needs permission to register and enrol terminals (Manager); issuing badges, permission to maintain people and crews. People badging at a terminal need no Pulaski permission of their own: the terminal is trusted for that.
- **Who:** qualifications and grants need permission to maintain people and crews; requiring them on equipment needs permission to maintain equipment. Recording breaks for others needs permission to record labor; taking your own, permission to clock yourself on.

---

## 17. Site settings

**Configure → Site settings.** Three things that apply to a whole site: who places changeovers on the schedule, how far production reports may go, and the holidays and shutdowns when the site doesn't work. Each part has its own **Save** button.

### Ideas to know first

- **Changeovers** are the time a line needs between two materials. *Automatic* (the default) lets Pulaski add them to the schedule; *Placed by planners* leaves it to the planners, and Pulaski warns where one is missing. See [Changeovers](#changeovers).
- **Reporting tolerances** keep production reports believable. The output tolerance is a **limit**: a report that would take a run past its length × the line's ideal rate × the tolerance is refused. The consumption tolerance is a **warning**: using more of a component than planned × the tolerance is recorded, with a warning to check it. Both are 110% to start with.
- **Calendar exceptions** take time out of the calendar: a holiday, a planned shutdown, a week with no demand. One set on the site covers everything in it; one on an area or line covers that and everything below it, whichever [shift schedule](#15-shift-schedules) it follows. Planning places no work in it.

### Choose who places changeovers

1. Open **Configure → Site settings**. With more than one site, choose the site at the top.
2. Under **Changeovers**, choose **Automatic** or **Placed by planners**.
3. Click **Save changeovers**. The schedule uses it on its next refresh.

### Choose what happens to ERP orders that aren't whole batches

1. Under **ERP orders that aren't whole batches**, choose **Refuse the order** (the default), **Round up**, **Round down** or **Import it held until a planner sets whole batches**.
2. Click **Save rule**. A material can have a rule of its own under **Batches** on its page (**ERP orders not whole batches**), which wins over the site's.

### Set the reporting tolerances

1. Under **Reporting tolerances**, type the **Output** and **Consumption** percentages, from 100 to 1000. The example under each box shows what the number allows.
2. Click **Save tolerances**. **Use the defaults** puts both back to 110%; click **Save tolerances** to keep that.

### Calendar exceptions: holidays and shutdowns

1. Under **Calendar exceptions**, the list shows those from now on; **Show past and removed ones too** shows the rest.
2. To add one, choose what it **Applies to** (the site, an area, or a line or department), its **Kind**, **From** and **Until** (site time), and a **Note** if you like, then click **Add exception**. A new one starts as tomorrow, all day.
3. To change one, click **Change**, adjust it and click **Save exception**. What it applies to can't change: remove it and add another.
4. To stop it taking time out, click **Remove**. A removed one stays in the list (with **Show past and removed ones too**), and **Put back** restores it.

### In detail

- **Who:** seeing this screen needs permission to see site settings; the Planner, Supervisor, Manager and Auditor roles have it to start with. Changing anything on it needs permission to change site settings (Manager). Someone who can only see it finds the boxes switched off and a note saying why.
- **Changeovers:** in *Automatic*, a changeover job a planner placed still replaces the one Pulaski would add. In *Placed by planners*, a changeover job without its own length is sized from the changeover rules.
- **Output tolerance:** good and scrap together count towards the limit. It uses the line's *ideal* rate for the material (from its capability), not the planning rate, so honest reports from a fast run aren't refused.
- **Consumption tolerance:** the planned amount grows with what the job has made, so a job that made more than planned may use proportionally more before a warning.
- **A change applies from the next report or refresh.** Reports already made aren't checked again.
- **Calendar exceptions** are in site time, may last up to 366 days, and may overlap one another. They apply to every schedule, and to a line running around the clock. Work that runs into one waits until it ends. A line that is switched off can't get a new one.
- **Not shift changes:** to change one shift on one day (an extra Saturday), use [one-off changes on Shifts](#change-one-day-one-off-changes).
- **Two people at once:** if someone else saved first, Pulaski says so and shows their values; make your change again.

---

## 18. Machine profiles

**Configure → Machine profiles.** A machine profile describes how one kind of machine reports itself: the values its state tag takes and the PackML state each one means, and its own stop, alarm and warning codes with the utilization reason each one counts as. Identical machines share a profile; machines from different vendors or of different models each get their own. You give each machine its profile on the [Equipment](#machine-data) screen.

Machine data arrives through [edge nodes](#19-edge-nodes-collecting-machine-data): the profile turns what each machine sends into PackML states.

### Ideas to know first

- **PackML states.** Pulaski records machine state as the 18 standard PackML states (0 Undefined, 2 Stopped, 4 Idle, 5 Suspended, 6 Execute, 11 Held…). Many controllers send those numbers as they are; some number their states another way. A profile says what each value means.
- **Stop codes.** When a machine stops, it reports a first-out code: the first thing that went wrong. These codes are the vendor's own, so each profile lists them, with their message and category.
- **Utilization reasons.** Each code, and each state, can count as a [utilization reason](#reason-codes), such as *Jam* (downtime) or *Setup* (planned). A code with no reason shows as uncoded, for an operator to code.

### Add a profile

1. Open **Configure → Machine profiles**. With more than one site, choose the site at the top.
2. Type a **Code** (such as `PACKER-A`; it can't change later), a **Name**, and optionally the **Vendor** and **Model**. Click **Add profile**.
3. The profile opens. It starts with PackML's own 18 states.

### Import the machine's definitions from a CSV file

1. On the profile, click **Import CSV**.
2. Choose the file. Pulaski checks it and shows what it would do, row by row: *Add*, *Change*, *Switch off* or *Unchanged*, or the errors.
3. If every row is valid, click **Import**. Nothing is saved until every row is valid.

The file's first line names the columns, in any order and case:

| Column | Means |
| --- | --- |
| `kind` | `state`, `stop_reason`, `alarm` or `warning`. Required. |
| `value` | The number the machine sends. Required. |
| `name` | The state's name, or the code's message. Required. |
| `packml_state` | State rows only: the PackML state it means, as a number (0 to 17) or a name such as `Execute`. Leave it empty on code rows. |
| `category` | The vendor's grouping, such as *Mechanical*. Optional. |
| `reason` | The code of the utilization reason it counts as, such as `JAM`. Optional. |

**Each kind in the file replaces that list:** its rows are added or changed, and entries of that kind the file leaves out are switched off. A kind the file doesn't mention stays as it is, so a file of stop reasons only keeps the states. An example file is `docs/samples/machine-profile-example.csv`. **Export CSV** saves the profile's active entries in the same format, to edit and import again.

### Change a profile

1. **States**: change a value's **Name**, **PackML state** or **Reason with no stop code** (what time in that state counts as when the machine gives no code, such as Suspended = *Starved*), or untick **Active**, then click **Save** on the row. To add a value, fill in the row above the table and click **Add state**.
2. **Stop reasons**, **Alarms** and **Warnings**: change the **Message**, **Category** or **Reason**, or untick **Active**, and click **Save**. A stop code without a reason is marked **No reason**. Add a code with the row above the table.
3. **Reasons offered**: tick the utilization reasons operators may choose when they code a stop on this profile's machines, and click **Save offered reasons**. With none ticked, every utilization reason of the site is offered; once you tick some, operators see those and the ones its codes and states map to (marked *mapped*).
4. The name, vendor, model and **Active** box at the top save with **Save**.

### One machine is different

If one machine's code means something else than on the others with the same profile:

1. Open the profile's **Machines** tab. It lists the equipment using the profile.
2. Under the machine, choose the **Stop code** and its reason on that machine (or *Leave uncoded*), and click **Override**.
3. Click **Use the profile's** to remove it again.

### In detail

- **Who:** seeing profiles needs permission to see equipment; changing them, and the machine data on equipment, needs permission to maintain equipment at the site.
- **A profile belongs to a site**, and maps only to reasons for every site or for that site. Its code is unique in the site.
- **Nothing is deleted.** States and codes are switched off; an unticked reason stays recorded as unticked.
- **Switching a profile off** is refused while active equipment uses it: give those machines another profile first.
- **Two people at once:** every change to a profile's lists counts as a change to the profile, so someone saving after another's change is asked to reload.
- **Errors in a file** name the line: a value that isn't a whole number, an unknown kind, a missing or unknown PackML state, a PackML state on a code row (usually a missing column), a value that appears twice, a name too long, or a reason that isn't an active utilization reason of the site.

## 19. Edge nodes: collecting machine data

**Configure → Edge nodes.** A Pulaski Edge node is a small program on plant hardware, near the machines. It reads them through an MQTT broker they publish to in **Sparkplug B**, keeps what it reads in a buffer on its own disk, and forwards it to Pulaski. If the network or Pulaski goes down, it keeps reading and forwards everything once it can, so nothing is lost.

### Ideas to know first

- **Connections** are where an edge node reads from: today, an MQTT broker. Pulaski listens as a Sparkplug B host application.
- **Tags** bind a signal to equipment: *this* metric is PACK1's state, *that* one its processed count. A Sparkplug B address reads `group/node/device:metric`, for example `Plant1/Packing/PACK-01:Status/StateCurrent`.
- **What Pulaski does with them.** A state value goes through the equipment's [machine profile](#18-machine-profiles) to its PackML state, and every change of state is kept, with the value the machine sent. The machine's first-out stop code is kept until it runs again. Counter readings become counts, in the job running on the equipment. Losing the signal is recorded as *Undefined*.

### Add an edge node

1. Open **Configure → Edge nodes**. With more than one site, choose the site.
2. Type a **Code** (such as `GW-PACK`; it can't change later) and a **Name**, and click **Add edge node**. Its page opens.
3. On **Connections**, give the broker a **Code** and **Name**, its **Broker host** and **Port**, tick **TLS** if it uses it, and optionally the Sparkplug **Group id** (to hear only one group), a **Host id** (Pulaski then announces itself, so nodes know it is listening), a **Client id** and a **User name**. Click **Add connection**.
4. On **Configure → Users**, add a user for the edge (such as `edge-gw-pack`) with the **Edge node** role at the site. Sign in as that user once and set its password, then sign out.
5. On the node's **Setup** tab, copy the configuration into the edge's `appsettings.json` on the edge machine, fill in the user's password (and the broker's, if it has a user name), and start Pulaski Edge.
6. To remove a node from use, click **Delete** on its row or page and confirm. Its connections and tags are switched off with it.

### Bind tags

1. Once the edge runs, open its **Seen** tab: every metric its brokers have announced, with its last value. Type in **Filter addresses** to find one.
2. Click **Bind** next to a metric. Pulaski guesses the role from the name; choose the **Equipment** (only equipment of the site that tracks state is offered) and check the **Role**.
3. For a count, set the **Scale** (how many units one count is) and, if the counter wraps, its **Rollover** value. Click **Bind tag**.
4. To type an address instead, use the same form on the **Tags** tab. To change a tag, edit its address, scale, rollover or **Active** checkbox and click **Save**. To delete a tag completely, whether active or inactive, click **Delete** on its row and confirm.

The edge reads its tags every minute, so new and changed ones take effect without restarting it.

### Watch it work

- The node's heading shows **Forwarding** while it is heard from, **Quiet** after 2 minutes without anything (offline, or nothing changed), and when it was last heard from and its version.
- **Live** shows each bound machine's PackML state (and the value it sent, where different), since when, its unit mode, latched stop code and speed. Click **Refresh** to update. Its stops are on [Machines](#20-machines-live-status-and-stops).
- **Tags** shows each tag's last value.

### In detail

- **Who:** seeing edge nodes needs permission to see equipment; adding and changing them, their connections and tags needs permission to maintain equipment at the site. The edge's own user needs only the **Edge node** role and sees no screens.
- **Sending once.** The edge numbers everything it buffers and Pulaski applies each number once, so forwarding again after a failure changes nothing. A new buffer file starts a new numbering, which Pulaski recognises.
- **What the edge forwards:** a tag's value when it changes, when it is lost, and the last value seen when the tag is newly bound; a speed at most every 5 seconds. A connection it hears from without a birth certificate is asked for one (a Sparkplug *rebirth*), as is every node its tags name when it connects, so an idle machine's state is known.
- **Stop codes** are latched: a machine that clears its code once acknowledged keeps it in Pulaski until it runs (Execute) again.
- **Counts** are taken across the rollover value; a counter going back without one is taken as reset to zero. Machine counts go into the run open on the equipment but don't change the job's reported quantities yet: production is still reported on the Floor.
- **Switching a node off** stops Pulaski taking its data and the edge collecting. Switching a tag or connection off stops it being read.
- **Delete behavior.** Deleting a node deactivates it and all its connections and tags. Deleting a tag physically removes its binding, so it disappears from the Tags tab; this works whether the tag is active or inactive. Any quality characteristic using it as an automatic source is kept but detached. The deletion event and historical machine data are retained. Add a new binding if the signal is needed again. Reactivating a node later does not reactivate its connections or tags; switch those back on individually if needed.
- **Without Pulaski**, the edge starts with the last configuration it kept.
- **The demo simulators.** `docker compose -f deploy/simulator/compose.yaml up -d` starts a local-only MQTT broker and three PackML simulators: group `Plant1`, nodes `Extruder`, `Saw` and `Packer`, and devices `EXT-01-EXT`, `EXT-01-SAW` and `PACK1`. The Spruiktec simulators publish standard PackML state numbers; `deploy/simulator/packml-simulator-profile.csv` is an example profile with the matching state values. The broker is anonymous and has no TLS: keep it on your development machine.
- **Connect the edge.** Add a Sparkplug B connection on the edge node with broker host `localhost`, port `1883` (or the value of `PULASKI_MQTT_PORT` if 1883 is occupied), and group id `Plant1`. If Pulaski Edge runs in the same Docker Compose network as the broker, use host `broker` and port `1883` instead. Bind the three simulator addresses from the Seen tab, for example `Plant1/Extruder/EXT-01-EXT:Status/StateCurrent`, `Plant1/Saw/EXT-01-SAW:Status/StateCurrent` and `Plant1/Packer/PACK1:Status/StateCurrent`. Useful companion metrics include `Status/UnitModeCurrent`, `Status/CurMachSpeed`, and the `Admin/Prod*Count/0/AccCount` counters.
- **Start a simulated machine.** After a fresh start, wait for its initial `Clearing` transition to finish at `Stopped`. Then Reset takes 1–10 seconds to reach `Idle`; the simulator command tool's `--delay-seconds 12` allows for that before it sends Start. For example, run `dotnet run --project tools/Pulaski.SparkplugCommand -- --host localhost --port 1883 --delay-seconds 12 spBv1.0/Plant1/DCMD/Extruder/EXT-01-EXT Command/Reset=true Command/Start=true`. Change `Extruder/EXT-01-EXT` to `Saw/EXT-01-SAW` or `Packer/PACK1` for the other devices; use the configured broker port if it is not 1883. Start takes another 1–10 seconds to reach `Execute`.

## 20. Machines: live status and stops

**Operate → Machines.** What every machine is doing now, as its [edge node](#19-edge-nodes-collecting-machine-data) reports it, its stops to code, and its utilization and OEE over past days.

### Ideas to know first

- **A stop** is time a machine isn't executing: it starts when the machine leaves Execute (or loses its signal) and ends when it executes again. The states it passes through on the way (Stopping, Stopped, Resetting…) are one stop.
- **Its reason.** A stop takes the reason its first-out stop code maps to in the machine's [profile](#18-machine-profiles); with no code, the reason of the first state on its way that has one (Suspended = *Starved*, say). An operator confirms that reason or gives another, and both are kept.
- **Micro-stops** are stops shorter than the machine's micro-stop threshold (5 minutes unless set otherwise on the Equipment screen). They are listed but not coded, and don't count against availability.

### The status board

1. Open **Operate → Machines**. With more than one site, choose the site.
2. Each tile is a piece of equipment that tracks state, coloured by what it is doing: **Producing** (green), **Waiting** (starved or blocked), **Held**, **Stopped**, **Ready** (idle, starting, resetting…), and **No signal** or **Not reporting** (hatched).
3. A tile shows the PackML state and for how long, the unit mode when it isn't Production, the stop in progress with its reason (or **uncoded**) and stop code, the job running with its good count so far, and the speed.
4. A line sits first in its row, with the machines below it. A line set to take its state from its machines says so: **Worst of its machines** (series), **Best of its machines** (parallel) or **Its constraint machine**.
5. **… stops to code** on a tile opens that machine's stops. **… quiet** means its edge node hasn't been heard from for 2 minutes.

The board refreshes every 5 seconds.

### Code stops

1. Open the **Stops** tab. The heading shows how many are waiting. **Only stops to code** is ticked: stops with no reason, longer than the micro-stop threshold, including one still going on.
2. Choose the **Equipment** and **Period** (8 hours, 24 hours or 7 days) to look at. Untick **Only stops to code** to see every stop, with its reason; tick **Show micro-stops** to see those too.
3. Click **Code** on a stop. Choose the **Reason**, add a **Comment** if you like (some reasons need one), and click **Save**. Where the stop code mapped a reason, **Confirm …** accepts it in one click; until then the list shows it as *to confirm*.
4. **Change** on a coded stop gives it another reason. The list shows who coded it, their comment, and the reason the code gave when the two differ.

### Utilization and OEE reports

1. Open the **Utilization and OEE** tab.
2. Choose the production days (**From** and **to**, at most 31 days) and, if you like, one piece of **Equipment** (it and everything below it); click **Show**. It opens on the last 7 days, all equipment.
3. **By equipment** lists each piece of equipment that tracks state, machines indented under their line, with its scheduled, planned, run and down hours, stops, and **availability**, **performance**, **quality** and **OEE**. Down time not coded yet shows as *uncoded* under the hours.
4. **By shift** shows the same for each shift worked, for the line (the top of each group); choose one machine to see its own shifts.
5. **By product** shows each material made: where it ran, how many runs, good and scrap as reported, and its OEE over the time inside its runs.
6. **Where the time went** breaks one piece of equipment's scheduled time into run, micro-stops, down (by reason, **Uncoded** or **No signal**), planned stops (by reason) and non-production modes, longest first. Choose the equipment in the section's list.
7. **Export CSV** on each section saves it as a file.

A dash means there is nothing to measure that figure against, such as performance on a machine that doesn't run jobs.

### In detail

- **Who:** seeing Machines needs permission to see machines; coding stops needs permission to code machine stops (Operators and Supervisors have both).
- **Which reasons are offered:** with reasons ticked on the machine's profile (**Reasons offered**), those and the ones its codes and states map to; with none ticked, or no profile, every utilization reason of the site.
- **Stop code:** the first one the machine reports during the stop is the stop's, even if the machine reports another later. A machine's own override (on the profile's **Machines** tab) beats the profile.
- **Unit mode:** a stop keeps the unit mode it began in; one in Maintenance or Manual is shown with that mode.
- **Counts on the board** are the machine's processed less defective counts in the run open on it.
- **Each coding is recorded** as an event, with the reason before and after and whether it confirmed the mapped one.
- **Scheduled time** is what the equipment's [shift schedule](#15-shift-schedules) gives it: its shifts, counted towards the day they start on; equipment without shifts (around the clock) is scheduled the whole site-local day. Time outside its shifts isn't counted, and neither is time still to come.
- **How each moment counts:** in a unit mode that isn't production time (Maintenance, Manual) it is *non-production*; in a stop it counts by its reason's class: **planned** reasons (setup, changeover, cleaning) are planned stops, **runtime** reasons are run time, **downtime** reasons and uncoded stops are down. A micro-stop is run time. Otherwise executing is run time and anything else is down. Before a machine has sent anything, or while its signal is lost, it is down with *no signal*. Time in a job that counts as planned downtime (planned maintenance) is a planned stop.
- **Planned time** is scheduled time less non-production modes and planned stops. **Availability** = run time ÷ planned time.
- **Performance** = output at the ideal rate ÷ run time in jobs. The ideal rate is the line's capability for the material when the run started. Run time with no job running isn't in it (its output can't be reported). **Quality** = good ÷ good and scrap. **OEE** = availability × performance × quality. Output comes from production reports; machine counts aren't reports and aren't used.
- **Several products** add up by their time at the ideal rate (quantity ÷ ideal rate), so different units don't matter. A run without an ideal rate (no capability, or a rate whose unit doesn't convert) is listed in a note above the tables; its output shows in the product's good and scrap but not in performance or quality.
- **Lines:** a line set to take its state from its machines takes its time from theirs: in series, at each moment the worst (down, then planned, then non-production, then run); in parallel, the best; or its constraint machine's. Its jobs and reports are its own, so the line has full OEE; a machine below it that runs no jobs has availability only.
- **Stops** count the stops that started in the period (a stop already going on when it began isn't counted again); micro-stops are counted separately.
- **Worked out when you ask:** nothing is stored, so coding a stop, changing a reason's class or correcting a shift schedule changes the report at once.

---

## 21. Reference

### Work order statuses

| Status | Meaning | Can go to |
| --- | --- | --- |
| **Planned** | Being arranged; jobs move freely. | Dispatched, Cancelled |
| **Dispatched** | Published to the floor with a baseline. | Planned (un-dispatch), Firmed, Cancelled |
| **Firmed** | Plan frozen; jobs anchored at their start. | In progress (when its first job starts), Cancelled |
| **In progress** | Being made: its first job has started. | Completed (when its last open job completes) |
| **Completed** | All its jobs are done; production can still be reported into their runs. | In progress (a job is reopened), Closed |
| **Closed** | Done and closed: its jobs are closed and take no more reports. | — |
| **Cancelled** | Not going ahead. Cancel its open jobs first. | — |

### Job rules

| Job status | Change line | Change place in sequence | Cancel |
| --- | --- | --- | --- |
| Planned | Yes | Yes | Yes |
| Dispatched | No: un-dispatch the order first | No | Yes |
| Firmed | Yes, to a line that can run it; it keeps its time | No | Yes |
| Running, paused | No | No | Yes, optionally replaced |
| Completed, closed | No | No | No |

- A **locked** job can't be moved at all until it is unlocked.
- A **firmed** job is placed at its firmed start (or now, if that has passed) whatever runs before it.
- **Cancel and replace** creates `<job number>-R` for the quantity not yet made, in the same slot.
- Non-production jobs (changeover, cleaning, maintenance, trial) belong to no order and can always be moved unless locked.
- In progress and completed follow the order's jobs: they are set by starting and completing jobs on the [Floor](#11-the-floor-running-jobs-and-reporting-production), not by hand.

### Schedule warnings

Warnings never block anything; they tell you something needs a look.

| Warning | Means | What to do |
| --- | --- | --- |
| **Late** | Projected to finish after the order's due date (or a department window closes after it). | Resequence, move to a faster line, or agree a new date. |
| **No run standard** | There is no rate for this material on this line, so its length can't be calculated. | Add a capability for the line on the [material's page](#where-it-can-run-capabilities-and-rates); it sets the rate. |
| **Uom mismatch** | The job's unit and the line's rate unit don't convert for this material, so its length is unknown. | Add a [conversion](#6-units-and-conversions) for the material. |
| **Beyond calendar** | The job runs more than a month past the schedule's horizon, so that part is placed around the clock. | Nothing; it is far ahead. |
| **No shift calendar** | No shift schedule covers the line: times assume 24/7. | Choose a schedule for the line or something above it on [Equipment](#4-equipment), or set it to *Around the clock* on purpose. |
| **Missing changeover** | In *scheduler* changeover mode: the product changes with no changeover job between. | Add a changeover job. |
| **No duration** | A non-production job has no planned length. | Give it a duration. |
| **Firmed conflict** | Work runs into a firmed job's anchored start (an overlap). | See [Overlaps](#overlaps). |
| **Missed finish by** | Projected to finish after its finish-by time. | Resequence or move it. |
| **Window too short** | Even the fastest line can't finish the department job in its window. | Widen the window or split the job. |
| **Link not met** | A firmed job keeps its time, but the operation it follows won't allow it to start then. | Move the earlier operation's work forward, or to a line that can start it sooner. See [Process links on the schedule](#process-links-on-the-schedule). |
| **Link cycle** | The job waits for work that, through the lines' sequences, waits for it. | Resequence one of them so the earlier operation comes first. |
| **Window passed** | The window has closed and no line has taken the job. | Assign it or re-plan it. |
| No runner | No line in the department can run this material any more. | Add a capability, or plan elsewhere. |

### Tile meanings

| Tile | Counts open orders that… |
| --- | --- |
| **Starting today** | have their first job projected to start today and not running yet. |
| **Finishing today** | have their last job projected to finish today. |
| **On track** | are on a line, projected to finish by the due date, and starting when published. |
| **Behind** | are projected to finish after the due date, or to start more than 15 minutes after the published baseline. |
| **Need attention** | have a job with a warning or an overlap. |
| **Not on a line** | are planned with quantity not yet on a line or department. |

An order can count in several tiles (starting today *and* behind, for example).

### Changeovers

Between two production jobs on different materials, a line needs a changeover. Pulaski sizes it from the plant's changeover rules (for example *white to grey: 30 minutes*, *grey to white: 2 hours*), or from the line's standard changeover for the incoming material. By default Pulaski inserts these automatically as placeholders and recalculates them as you resequence. A plant can instead choose to have planners place changeover jobs themselves; Pulaski then warns where one is missing. A manager chooses which on [Site settings](#17-site-settings).

---

## 22. What Pulaski doesn't do yet

Pulaski is pre-alpha. These are known gaps, so you don't spend time looking for them:

- **No screen yet for the changeover rules** (how long each material change takes): they are set up in the database.
- **Labor:** people come from HR or the ERP only as a file you import: nothing fetches them on a timer, someone missing from a file isn't switched off (send them with `active` = no), and qualifications aren't imported. At a terminal people record only direct time, their own clock-off and breaks: setup, indirect and training time, and production reports, are recorded on the signed-in screens. A terminal can't be used to sign in as a user. Breaks aren't planned on shift schedules (a fixed lunch is recorded like any other break), and a missing qualification only warns: it never stops someone being put on a line. Pulaski sends no notice before a grant expires; use *Expiring in 30 days*. A person works only at their own site. On **Shift review** only a session's times and share can be changed, not its job or kind (end it and add a new one instead). The Overview's *Labor to approve* is still a placeholder.
- **Materials import from files only:** CSV or B2MML on the screen, or the ERP one item at a time through the API. Nothing fetches items from an ERP on a timer, and there are no adapters for label printing. Capabilities import from CSV only, and only for now: a future or past period is set on the material page. B2MML material lots and classes aren't read as records of their own, and Pulaski doesn't export B2MML.
- **Planning rates from run history are worked out when you ask,** not kept up to date on their own, and use run time as recorded: stops inside a run aren't taken out.
- **Machine data:** stops can't be split, merged or entered by hand. Machine counts don't replace production reports, so OEE uses only what is reported. The OEE report has no charts (a stop Pareto, say: **Where the time went** lists the reasons longest first, as a table) or targets yet, isn't stored (each view works it out again, so a long period on many machines takes a moment), and covers at most 31 days. The Overview still shows no machine state. Only Sparkplug B is read (no OPC UA or EtherNet/IP yet), alarms, warnings and process values aren't recorded, and Pulaski Edge runs as a console program or container, not yet as a Windows service.
- **Production is reported by hand.** Machine counts are recorded but aren't production reports, and quality isn't recorded yet. A wrong report is reversed whole and entered again: there is no editing a report's quantities in place.
- **Batches:** a line's own batch size changes only the size; the tolerance stays the material's. The order form's batch hint shows the material's size, not each line's. A replacement job is always rounded up to whole batches; to make fewer, cancel it. A rejected partial batch shows in the job's reports as a correction (good down, scrap up).
- **Inventory is declared by hand.** Consumption and production are entered by people; loading lots at feed points, consumption by time window, backflush and scanning aren't built. Genealogy is per job (see [Inventory, in detail](#in-detail-5)).
- **Not in inventory yet:** containers inside containers, GS1 labels (SSCC, GTIN) and printing straight to a label printer without a PDF, and scanning 2D codes or supplier barcodes beyond reading their text. Moves, holds, splits, merges, shipments and counts can't be back-dated (only quantity movements can, by up to 31 days), and a lot's location and status on a past date aren't reconstructed. Lot attributes aren't in the lots CSV export, can't be imported, and aren't checked against specification limits; certificates aren't sent to the ERP or a document system.
- **Reversing:** moves, holds and shipments only when they are the lot's latest movement; corrections of lot details, packing and unpacking aren't reversed (put them right with another movement). A lot that joined a balance where lots mix can't be taken back out as itself. Reversal is one movement at a time: there is no undoing a chain of dependent movements in one step.
- **Reason groups** only tidy the lists people choose from: reports (OEE *Where the time went*, stops, the journal) still list each reason, not totals by group. A reason can't be turned into a group, or a group into a reason.
- **Genealogy through a silo is an estimate** from the declared movements: a level tag can be bound to a silo, but its readings aren't used yet to confirm when it was empty or how it flowed. A reversed movement and its reversal are left out of the estimate, as if neither happened.
- **Sending to the ERP** is one HTTPS webhook, for inventory movements only, configured in the API's settings (not on a screen). Pulaski doesn't read anything back from it, so the ERP's answers don't change anything in Pulaski; there is no adapter for a particular ERP's own API, no second receiver, and no message queue (MQTT, Kafka and the like). The delivery list shows the newest 200 of a kind, with no search; delivered rows are kept for good, with nothing to clear them out yet.
- **Allocations are kept up to date by hand:** nothing refreshes them when stock moves or the schedule changes; click **Refresh**. Suggestions don't prefer lots near the line, lots already in a container, or whole lots, and there are no picking lists; a substitution (a material the job doesn't plan) isn't allocated. Allocating doesn't stop another job consuming the stock: it only warns.
- **Shortages don't plan.** Incoming deliveries are counted only as entered or sent by the ERP; there are no lead times, purchase suggestions or partial receipts against one delivery (receiving closes it). The live timing is the schedule as projected now, not a forecast of slips.
- **The Overview** still shows no live machine state, reported counts, good output, scrap, downtime, quality, lots or labor. OEE is available for the current shift from the machine/OEE report; its timeline strip remains a rolling 24 hours rather than the current shift.
- **Notes** can only be written when the order is created.
- **Released product specifications** are not maintained in Pulaski yet. Shop-order sheets flag this gap and require operators to verify the approved specification elsewhere.
- **Signing in through your company's identity provider** (Okta, Entra ID, AD FS, Keycloak) isn't built yet: users have Pulaski passwords, and an administrator resets forgotten ones (Pulaski sends no e-mail).
- **The schedule shows its buttons to everyone who can see it;** someone who can see the schedule but not plan sees them, and a click explains that planning needs permission instead of changing anything. Switched-off buttons elsewhere can look like ordinary ones.
- **Only Administrator is held everywhere;** every role you create is held at a site.
- **The site picker** on a screen lists every site where you can see something, not only the sites where you can see that screen; choosing one where you can't gives a "may not see this at this site" message.

---

## 23. Troubleshooting and questions

**"You may not do this…" or a button I expected isn't there.**
Your roles don't allow that change, or not at this site. The message names the permission; ask an administrator (see [What each role can change](#what-each-role-can-change)).

**I can't sign in.**
Check the user name and password (the password is case-sensitive). After five wrong tries you are locked out for 15 minutes; an administrator can reset your password, which unlocks you. A user who is switched off or has no role can't sign in.

**"Your session ended".**
Sessions last up to 12 hours, and end straight away if an administrator switches you off. Sign in again.

**I can't find a process when creating an order.**
The process must be at the order's site, be active, have a released version, and make the material (all materials, its class, or the material itself). If it is listed but greyed out, read the reason under the field: usually a missing capability.

**The schedule is empty.**
Mark lines as **Schedulable** (and **Can run jobs**) on the Equipment page. Then create a work order.

**My new job is at the end of the line, but I want it sooner.**
That is where new jobs go. Drag it earlier on the Gantt, or use **Line and sequence** in its side panel.

**I dragged a job to an earlier day on the Calendar and nothing changed.**
Dragging earlier only clears a hold. The sequence on the line decides when it runs: move it up the line on the Gantt.

**A job won't move.**
Check its side panel: it may be **locked**, **dispatched** (un-dispatch the order first), **firmed** (it can change line but keeps its time) or already running.

**The job's bar looks the wrong length.**
Its length comes from the rate (run standard) for that material on that line; *No run standard* means there is none. Check the **Rate** in its side panel.

**Times look a few hours off.**
Times are in the site's time zone, not your computer's.

**I see "changed by someone else".**
Another person saved first. Reload, check what they did, and make your change again.

**I can't cancel a work order.**
Cancel its open jobs first (from each job's side panel), then the order.

**"…doesn't convert to…" when creating an order or saving a BOM.**
Pulaski doesn't know how this material converts between those units. Open **Units**, choose the material, and add a conversion (for example 1 m = 1.6 kg). Then try again.

**I can't receive a lot: "No location holds inventory".**
Tick **Holds inventory** on a store or staging area on the Equipment page.

**"Lot … has only 12 kg".**
You tried to take more than the lot holds. Check the quantity and unit, or write the lot up first if the count was wrong.

**I can't consume a lot.**
Only an **available** lot with stock can be consumed. Release a held lot first; a used-up lot has nothing left.

**"Choose an adjustment reason" with an empty list.**
No reasons are set up: ask your administrator to load `db/seed-reasons.sql`.

**"Only the latest movement … can be reversed when it is a …".**
A move, hold or shipment is reversed only while it is the lot's latest movement. Reverse the later movements first, newest first, or record a new movement instead.

**I can't post a count.**
Every lot must have a count (enter 0 if it isn't there), the counts must be saved, and a lot marked *changed since counted* must be counted again.

**I can't merge two lots.**
They must be the same material, at the same location, and in the same status. Move or release one first.

**A BOM won't release.**
Read the yellow box: a component that doesn't exist or is inactive, an operation number the process's released version doesn't have, a unit with no conversion, or no components at all. Save the draft after fixing.

---

## 24. Glossary

| Term | Meaning |
| --- | --- |
| **Allocation** | Stock of a lot set aside for a job's component. Other jobs see it as taken; consuming it into its job uses it up. |
| **Balance** | The one quantity of a material at a location, with no lot number: for a material that isn't lot-tracked, or at a location where lots mix. |
| **Base unit** | The unit a material is stocked in; also the base unit of a kind of unit (kg, m, ea…). |
| **Baseline** | A job's times as published when its order was dispatched; the plan you're measured against. |
| **Batch** | A fixed amount of a material made at a time, such as a 50 kg mix. Orders of a batch material are whole batches. |
| **BOM** | Bill of materials: what one material is made from, per a base quantity. |
| **Capability** | A line's permission to run a material, with its ideal rate. |
| **Changeover** | Time to switch a line from one product to another. |
| **Crew** | A team of people who work the same shifts. A shift schedule names the crew on each shift. |
| **Conversion** | How a quantity in one unit becomes another: standard for units of one kind, or set per material. |
| **Count** | Checking what is really at a location, then posting the differences. |
| **Department / group** | Equipment you can plan to that doesn't run jobs itself; its lines do. |
| **Dispatch** | Publish an order to the floor, fixing its baseline. |
| **Draft / released / obsolete** | Versions of a process or BOM: being edited / in use for new orders / replaced. |
| **Edge node** | Pulaski Edge, running near the machines: reads them, buffers what it reads and forwards it to Pulaski. |
| **Firm** | Freeze an order's plan; its jobs keep their start times. |
| **Genealogy** | Which lots were made from which. |
| **Hold / quarantine** | Statuses that keep a lot on hand but stop it being consumed until released. |
| **Hold until** | The earliest a job may start. |
| **Job** | The part of a work order on one line (or department). |
| **Journal** | The list of every lot movement in a site. |
| **Line** | Equipment that runs jobs, such as an extrusion line. |
| **Location** | Equipment that holds inventory: a store, staging area or feed point. |
| **Lock** | Pin a job so it keeps its place. |
| **Lot** | One numbered batch of a material, with its quantity, location and history. |
| **Machine profile** | How one kind of machine reports itself: what its state values mean in PackML, and its own stop, alarm and warning codes with their utilization reasons. |
| **Material class** | A group of similar materials, such as *Extruded profiles*. |
| **Material type** | A label from the item master, such as its MRP class (raw, finished, packaging…); it drives no rules. |
| **Origin** | Where a material is maintained: in Pulaski, or in the ERP, which then owns its code, name, base unit and type. |
| **Merge** | Combine lots of one material into one. |
| **Micro-stop** | A stop shorter than the equipment's threshold (5 minutes by default): logged, but not coded and not counted against availability. |
| **Operation** | One step of a process, run on one or more lines or departments. |
| **Packaging unit** | A unit like box, bag or pallet whose size depends on the material; it converts only through a material conversion. |
| **Partial batch** | A batch cut short when the line stopped. Accepted for work in progress; held for approval for finished goods. |
| **Process (route)** | The operations an order goes through, and where each runs. |
| **Projection** | Pulaski's calculation of when each job will run, redone on every read. |
| **Rate check** | A run can't hold more than its length × the line's ideal rate × the site's tolerance. |
| **Ready** | A job that can start: its order is on the floor and its process links are met. |
| **Reverse** | Undo a movement or declaration with a new movement that puts it back; the original stays in the history. |
| **Permission** | One kind of change Pulaski lets people make, such as *Receive lots*. Roles hold permissions. |
| **Role** | A set of permissions, such as Planner or Quality, that an administrator maintains and gives to users at a site (Administrator everywhere). See [Users, roles and signing in](#14-users-roles-and-signing-in). |
| **Run** | One stretch of a job running on a line, from a start to a pause or completion. Production and consumption go into a run. |
| **Labor session** | A stretch of time one person spent on a line: direct, setup, indirect or training. See [Labor](#16-labor-people-and-crews). |
| **Shift schedule** | When a site works: shifts repeating over a cycle of days. Equipment follows one, inherits its parent's or runs around the clock. See [Shift schedules](#15-shift-schedules). |
| **Run standard (planning rate)** | The rate Pulaski plans with for a material on a line: calculated from the ideal rate, or modified by a person. |
| **Scrap factor** | Extra component quantity planned for losses. |
| **Sequence** | The order of jobs on a line. |
| **Share** | The part of an operation's quantity one line or department takes. |
| **Ship** | Send stock out of the site. |
| **Shortage** | What open jobs still need of a component beyond the stock available. |
| **Site** | A plant, with its own time zone. |
| **Split** | Divide part of a lot into a new lot. |
| **Stop** | Time a machine isn't executing, from leaving Execute until it executes again, with the reason it is coded with. |
| **Substitution** | Consuming a material a job had no planned component of. |
| **Trace** | Following genealogy back to everything a lot was made from, or forward to everything made from it. |
| **Sparkplug B** | A standard way for machines to publish their data over MQTT, with birth certificates naming each metric. |
| **Tag** | A machine signal bound to equipment, with the role it plays (state, a count…). |
| **Utilization reason** | What a machine's time counts as: *runtime*, *downtime* (against availability) or *planned* (left out of OEE). |
| **Where used** | The BOMs and open jobs that use a material as a component. |
| **Window** | The time span a department job must be run in. |
| **Work order** | A request to make a quantity of one material by a due date. |
| **Write up / write off** | Add found stock to a lot / remove damaged, lost or miscounted stock, each for a reason. |
