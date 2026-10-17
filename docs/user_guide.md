# Pulaski User Guide

*Last updated: 2026-10-17 · Pulaski is pre-alpha: screens and behaviour will change, and this guide changes with them.*

This guide explains how to use Pulaski to set up your plant, plan production and follow your orders. It is written for everyone who uses the application. If you are new to manufacturing software, start with [Getting started](#1-getting-started) and [Key ideas](#2-key-ideas) and follow the numbered steps in each section. If you already know MES and ERP systems, skim the key ideas, then jump to the section you need; the **In detail** parts and the [reference](#12-reference) tables give the exact rules.

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
11. [Inventory](#11-inventory)
11. [Reference](#12-reference)
12. [What Pulaski doesn't do yet](#13-what-pulaski-doesnt-do-yet)
13. [Troubleshooting and questions](#14-troubleshooting-and-questions)
14. [Glossary](#15-glossary)

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

Recording what actually happens on the machines (running jobs, counts, downtime) is still being built; see [What Pulaski doesn't do yet](#13-what-pulaski-doesnt-do-yet).

### Opening Pulaski

Open Pulaski in a web browser at the address your administrator gives you (on a developer's machine it is `http://localhost:5090`). Pulaski works best in a current version of Chrome, Edge, Firefox or Safari, on a desktop or a tablet in landscape.

There is no sign-in yet. Everyone who can reach the address can see and change everything, so only run Pulaski where that is acceptable.

### The screen

- **Menu (left).** Two groups of pages:
  - **Operate:** **Overview**, **Work orders**, **Schedule**: the pages you use day to day.
  - **Configure:** **Equipment**, **Processes**, **BOMs**, **Units**: the setup you do once and change now and then.
  - The ☰ button at the top left collapses the menu to icons, to make room on small screens.
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

A **material** is anything you make or use: a finished profile, a resin, a dry blend, a pallet, an end cap. Each material has a code (its item number), a base unit (metres, kilograms, each) and usually a **material class** that groups similar materials, such as *Extruded profiles*.

**Capabilities** say which material can run on which line, and how fast (its ideal rate). Pulaski never puts a material's job on a line without a capability for it.

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
| 3 | Materials and material classes | *Your administrator* (no screen yet) | Orders and BOMs need materials. |
| 3a | Units, and conversions for materials you order or use in other units | [Units](#6-units-and-conversions) | So orders, BOMs and rates can use any unit that converts. |
| 4 | Capabilities: which material runs on which line, and how fast | *Your administrator* (no screen yet) | Without one, a line can't take a material's job. |
| 5 | Shift calendar (optional) | *Your administrator* (no screen yet) | Without one, Pulaski assumes lines run around the clock and marks them **24/7**. |
| 6 | Processes: the routes, what they make, released | [Processes](#5-processes) | Every work order is made from a process. |
| 7 | BOMs for the materials you make (optional, recommended) | [BOMs](#7-bills-of-materials-boms) | Without a BOM, an order plans no components. |
| 8 | Work orders | [Schedule → New work order](#8-creating-a-work-order) | Now you can plan. |
| 9 | Inventory locations: stores, staging areas and feed points, with **Holds inventory** ticked | [Equipment](#4-equipment) | Lots have to be somewhere. |
| 10 | Reason codes for write-ups, write-offs and holds | [Reasons](#reason-codes) (a starter set comes with `db/seed-reasons.sql`) | Every adjustment and hold needs a reason. |

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

**This shift** will show OEE (with availability, performance and quality against target), good output against plan, scrap and downtime with its biggest reason. All four need machine data.

**Lines** has one row per line:

- **State:** the machine state. Until the line is connected it says **No signal**.
- **Now or next:** the job the schedule has on the line now, or the next one and when it starts, with how much of it is made.
- **The strip** covers six hours back to eighteen hours ahead, with a black line at *now*. The top lane will show what the line actually did (hatched while there's no signal); the bottom lane shows the planned jobs: outlined for planned, solid for dispatched or firmed, hatched for changeovers. Hover over a job to see it; click it to open its order on the Schedule. If nothing is planned in the window, the lane says when the next work starts.
- **OEE** for the line, once there's machine data.
- **Flags** at the end: a red mark if work overlaps on the line, an amber clock if a job has a schedule warning (hover for the messages).

At the bottom, **Work orders** counts orders at each stage, Planned → Dispatched → Firmed → In progress → Completed (click one to list them), and **Starting next** lists the next six jobs to start across the plant.

---

## 4. Equipment

**Configure → Equipment.** This is where you describe your plant as a tree.

### The screen

- **Left: the tree.** Each row shows the code, name, level, role and chips for its settings: **State** (tracks machine state), **Schedulable** or **Plans for children** (a department), **Runs jobs**, **Inventory**, and **Inactive** in grey.
  - Click the arrow beside a row to open or close it, or use **Expand all** / **Collapse all**.
  - Type in **Filter by code or name** to find equipment; matching rows stay visible with their parents.
  - Arrow keys move through the tree when it has focus.
  - The bottom line counts the items (and the matches while filtering).
- **Right: the properties** of the selected equipment, grouped into *Identity*, *Classification*, *Capabilities*, *Site*, *Status* and *Hierarchy*.
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
| **Lots are indistinguishable** | Shown once **Holds inventory** is ticked. Lots here mix and can't be told apart, like a silo or tank: everything that arrives joins one balance per material. See [Inventory](#11-inventory). It can't be turned on while separate lots are there; merge or move them first. | Silos, tanks, mixing hoppers |

**Lines vs departments.** A line has *Schedulable* and *Can run jobs*. A department (an area that you plan to, with lines below it) has *Schedulable* only; the tree shows it as **Plans for children**. A line can also have *Can run jobs* without *Schedulable*: it then only takes jobs from a department above it.

### Deactivate equipment

Set **Active** to *false*. Inactive equipment stays in the tree and in history, greyed out, but can no longer be used for new work. Pulaski never deletes equipment.

### In detail

- The levels, top to bottom: enterprise, site, area, work center, work unit, equipment module, control module. A child always sits below its parent's level.
- Roles allowed at each level: work center: production line, process cell, production unit, storage zone; work unit: work cell, unit, machine, storage unit; equipment module: machine, feed point, module; control module: module.
- Codes are unique within a site.
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
- Today the link's *after start/after end* and lag set the **earliest** time each operation's jobs may start, counted from the order's start. The schedule doesn't yet wait for the earlier operation to actually start or finish. See [What Pulaski doesn't do yet](#13-what-pulaski-doesnt-do-yet).

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
5. **Planned quantity** and **Unit:** how much, in any unit that converts to the material's base unit (the hint under the field links to its conversions).
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
| Actions | The row's menu (below). |

### Details

Click ⓘ to open a row. You see the due date, the published baseline, how much is on lines, the process and version, the BOM version, the origin, the notes, every open job with its line, status, projected times and remaining quantity, any warnings, and the **planned components** for each job.

### Act on orders

- **One order:** open the row's **Actions** menu (the ⌄ button) and choose **Dispatch**, **Un-dispatch**, **Firm**, **Cancel** and so on (only what the order can do next is shown), or **Show on the schedule**.
- **Several orders:** tick **Select** on each (or **Select all**), open **Actions** at the top of the list and pick an action. Each choice shows how many of the selected orders it applies to (*3 of 5*); the others are skipped. **Cancel…** asks you to confirm.

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
- **Line labels** show the code, name and chips: **24/7** (no shift calendar: times assume the line runs around the clock) and **Plans for N lines** (a department).

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

### Overlaps

Pulaski lays work end to end on each line, so two jobs on a line only overlap when work runs into a **firmed** job's fixed start. When that happens, a red banner appears on every view: *1 overlap: a line can't run these at the same time*, listing each pair with a hint and a **Show** button. The jobs are outlined, and a notice pops up the moment a new overlap appears, whether from your edit, another planner's, or simply time passing. Pulaski never fixes an overlap itself: move or resequence work, or un-firm the order.

---

## 11. Inventory

**Inventory** (in the side menu under *Operate*) shows what material you have, where it is and what happened to it. It has eight tabs: **Stock**, **Lots**, **Locations**, **Jobs**, **Shortages**, **Journal**, **Counts** and **Reports**. If your company has more than one site, pick the site at the top right; Pulaski remembers it while you move between the tabs.

### Ideas to know first

- **Quantities are in the material's base unit.** You can enter a quantity in any unit that converts (pounds for a resin stocked in kilograms, for example); Pulaski converts it and keeps the base unit. If a unit doesn't convert you'll see *"… doesn't convert to …"*: add a [conversion](#6-units-and-conversions).
- **Stock can't go below zero.** Pulaski refuses to take out more than a lot holds and tells you how much is left.
- **Statuses.** *Available* lots can be used. *On hold* (waiting for a decision) and *quarantine* (kept apart, suspected bad) lots count as on hand but can't be consumed until released. A lot taken down to zero becomes *consumed* or *scrapped*; it stays on record for genealogy.
- **Lot-tracked or not.** For a lot-tracked material every receipt is its own numbered lot. For one that isn't, such as end caps, there is one **balance** at each location (shown as *Balance at RM-STORE*, with no lot number); receipts add to it.
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

The material page shows tiles for on hand, available, held, lots and locations, then:

- **Lots on hand**, with status, quantity, location, supplier lot, expiry and where each came from. **Used-up lots** at the top lists the consumed ones.
- **By location**: the balance at each location. Click one to list its lots.
- **Where used**: every released or draft **BOM** that lists the material as a component (with the quantity per base quantity and the operation that consumes it), and every **open job** planned to consume it, with how much it has consumed so far. **Made from** links to the material's own BOMs.

### Find a lot: the Lots tab

1. Type a **lot number**, a **supplier lot** number or a material code, and/or choose a material, location, status or expiry window (*Expired*, *within 7, 30 or 90 days*).
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
| **Reverse** (on the latest row of the history) | Undoes the lot's latest movement: a receipt, write-up, write-off, move, hold or release, or shipment. The lot goes back to how it was; a reversed receipt of a whole lot makes it *cancelled*. | An adjustment reason. Only the latest movement, once. |

Some reasons ask for a comment; the form says *(required)* when they do.

### Record what a job used and made: the Jobs tab

Until Pulaski records production from the machines, operators declare it here.

1. Open the **Jobs** tab and click a job (search by job, order or material). Only open production jobs are listed.
2. The job page shows the **components** planned for it (from its order's BOM) with how much has been consumed and how much is still to use, and the **lots it made**.
3. To record material used: click **Consume a lot**, choose the material (planned ones are marked), choose the lot, enter the quantity and click **Record consumption**.
4. To record output: click **Declare production**, choose the location the output goes to, enter the quantity (the job's unit is filled in) and click **Record production**. Leave **Lot number** blank and Pulaski numbers it after the job: *WO-1001-10-01*, *-02*… Enter an existing number of a lot this job made at the same place to add to it.

You can also consume from a lot's own page, and declare production from the Stock tab (choose the job there).

### Trace a lot

On a lot's page, **Genealogy** first shows the lots it was **made from** and the lots made from it (**used in**). Click **Trace back** to see everything it was made from, all the way up, or **Trace forward** to see everything made from it, all the way down. Each lot is indented under the one it was reached from; click any lot to open it.

### Check for shortages

The **Shortages** tab answers *"will we have the material for the jobs coming up?"*.

1. Open **Inventory → Shortages**. It lists every component the site's open jobs still need, short ones first.
2. Narrow it to a line or department (and everything under it) with the equipment picker, or to jobs needed soon: the next 24 hours, 3, 7 or 14 days.
3. Clear **Short only** to see covered components too.
4. Click a component to see each job that needs it, in the order they are needed: what it still needs, the running total, and whether it is **covered** or how much is short.
5. Click a job to open it, or **stock** to open the component.

Each row shows **Needed** (what the jobs still need: planned less consumed), **Available**, **Held** (on hold or quarantine, in amber: releasing it might cover the gap), **Short** (in red), and the **first short** job with when it is needed.

**In detail**

- Jobs take stock in the order they are needed: their published start, else their hold-until time, then job number; jobs with neither come last. The live projected time isn't used yet, so a job that has slipped is ordered by its published time.
- With a horizon, jobs needed after it, and jobs with no time at all, are left out.
- Only **available** stock counts. Incoming deliveries and purchase orders aren't counted, and nothing is suggested: ordering stays in the ERP.
- A component planned in a unit that doesn't convert to its base unit is left out, with an amber note naming the job.

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

### Reports and exports

**Inventory → Reports** has two reports:

- **Stock on a date:** each material's on-hand quantity at the end of the day you choose. It is worked out from every lot's last movement up to then, so older dates take longer. Held stock is included: a lot's status at that time isn't reconstructed.
- **Aging:** on-hand stock by how long ago each lot was received or made: up to 30 days, 31–90, 91–180 and over 180 (in amber). Balances of materials that aren't lot-tracked aren't aged, and a split lot counts from the day it was split.

**Export CSV** downloads a spreadsheet file from the **Stock** tab (what is on screen, with its filters), the **Lots** tab (every matching lot, up to 10,000), the **Journal** (every matching movement, up to 10,000) and both reports. Lot search shows 200 at a time: click **Show more** for the next.

### Reason codes

**Configure → Reasons** lists the reasons people choose, one tab per kind: inventory adjustments, holds, and the kinds later screens will use (downtime, scrap…).

1. To add one, type a **code** (letters, digits, `-`, `_` or `.`), a **name**, choose **Every site** or one site, tick **Needs a comment** if people must explain, and click **Add**.
2. To change one, edit its name, comment setting, order or **Active** box, then click **Save** on its row (**Undo** throws the change away).
3. A code and its kind can't change once created, because history refers to them. To stop using a reason, untick **Active**.

### Locations

The **Locations** tab lists every piece of equipment that holds inventory, with how many lots and materials are there. **Stock** and **Lots** open those lists for that location. To add a location, add equipment (for example a storage zone) on the [Equipment](#4-equipment) page and tick **Holds inventory**.

### In detail

- **One site per lot.** Moves stay within the lot's site; consumption needs a job in the same site.
- **Concurrency.** Write up, write off, move, hold and release check that the lot hasn't changed since you opened it; if it has you'll see *"changed by someone else"*: reload and try again. Consumption and production don't check, so several people can declare against a busy line; the no-negative-stock rule still protects them.
- **Substitutions.** Consuming a material the job has no planned component of is allowed, and recorded as a **substitution** (amber on the job page and in the confirmation).
- **Genealogy is per job for now.** Every lot consumed into a job is recorded as a parent of every lot the job made, whichever came first. That can link more than really mixed (a resin lot used in the first hour shows as a parent of the last pallet). It narrows to time windows once Pulaski records production from the machines.
- **Times** are the moment you click; you can't yet back-date a movement.
- **What the ERP sees.** Each movement is an event in Pulaski's event log (`pulaski.lot.received.v1`, `…consumed.v1` and so on), ready to be sent to the ERP once the connection is built.

---

## 12. Reference

### Work order statuses

| Status | Meaning | Can go to |
| --- | --- | --- |
| **Planned** | Being arranged; jobs move freely. | Dispatched, Cancelled |
| **Dispatched** | Published to the floor with a baseline. | Planned (un-dispatch), Firmed, Cancelled |
| **Firmed** | Plan frozen; jobs anchored at their start. | In progress, Cancelled |
| **In progress** | Being made. | Completed |
| **Completed** | Finished. | In progress (reopen), Closed |
| **Closed** | Done and closed; no more changes. | — |
| **Cancelled** | Not going ahead. Cancel its open jobs first. | — |

### Job rules

| Job status | Change line | Change place in sequence | Cancel |
| --- | --- | --- | --- |
| Planned | Yes | Yes | Yes |
| Dispatched | No: un-dispatch the order first | No | Yes |
| Firmed | Yes, to a line that can run it; it keeps its time | No | Yes |
| Running, paused | No | No | Yes, optionally replaced |

- A **locked** job can't be moved at all until it is unlocked.
- A **firmed** job is placed at its firmed start (or now, if that has passed) whatever runs before it.
- **Cancel and replace** creates `<job number>-R` for the quantity not yet made, in the same slot.
- Non-production jobs (changeover, cleaning, maintenance, trial) belong to no order and can always be moved unless locked.

### Schedule warnings

Warnings never block anything; they tell you something needs a look.

| Warning | Means | What to do |
| --- | --- | --- |
| **Late** | Projected to finish after the order's due date (or a department window closes after it). | Resequence, move to a faster line, or agree a new date. |
| **No run standard** | There is no rate for this material on this line, so its length can't be calculated. | Ask your administrator to set a rate. |
| **Uom mismatch** | The job's unit and the line's rate unit don't convert for this material, so its length is unknown. | Add a [conversion](#6-units-and-conversions) for the material. |
| **Beyond calendar** | The job runs past the last shift in the calendar, so it is placed around the clock. | Ask for more shifts to be generated. |
| **No shift calendar** | No shift calendar covers the line: times assume 24/7. | Ask for a shift calendar. |
| **Missing changeover** | In *scheduler* changeover mode: the product changes with no changeover job between. | Add a changeover job. |
| **No duration** | A non-production job has no planned length. | Give it a duration. |
| **Firmed conflict** | Work runs into a firmed job's anchored start (an overlap). | See [Overlaps](#overlaps). |
| **Missed finish by** | Projected to finish after its finish-by time. | Resequence or move it. |
| **Window too short** | Even the fastest line can't finish the department job in its window. | Widen the window or split the job. |
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

Between two production jobs on different materials, a line needs a changeover. Pulaski sizes it from the plant's changeover rules (for example *white to grey: 30 minutes*, *grey to white: 2 hours*), or from the line's standard changeover for the incoming material. By default Pulaski inserts these automatically as placeholders and recalculates them as you resequence. A plant can instead choose to have planners place changeover jobs themselves; Pulaski then warns where one is missing. Your administrator sets which.

---

## 13. What Pulaski doesn't do yet

Pulaski is pre-alpha. These are known gaps, so you don't spend time looking for them:

- **No screens yet for** materials and material classes, capabilities, run standards (rates), shift calendars or the changeover setting. Your administrator sets these up.
- **No execution yet:** starting and stopping jobs, counts, downtime, OEE and quality charts are planned. Order statuses after Firmed exist but nothing records production yet.
- **Inventory is declared by hand.** Consumption and production are entered by people; loading lots at feed points, consumption by time window, backflush and scanning come with execution. Genealogy is per job (see [Inventory, in detail](#in-detail-4)).
- **Not in inventory yet:** pallets or containers that hold several lots, lot labels and barcode scanning, lot attributes and certificates of analysis, setting stock aside for a job, and suggestions of which lot to use first. Movements can't be back-dated.
- **Reversing is limited** to a lot's latest movement, and not consumption, production, splits, merges, transfers between balances or corrections: put those right with another movement.
- **Reason trees** (a reason grouped under another) can't be edited.
- **Where lots mix, genealogy is approximate:** every lot that ever joined a silo's balance counts as a parent of everything taken from it afterwards. Residence time and first-in, first-out through a silo come with machine data.
- **Inventory movements aren't sent to the ERP yet:** they are recorded as events, but the connection that delivers them isn't built.
- **Shortages use published times, not the live schedule,** and don't count incoming deliveries.
- **Process links don't drive the schedule yet:** a Pack job can be projected to start before the Extrude job it follows. The link only sets the earliest start, counted from the order's start.
- **The Overview** shows no machine state, counts, OEE, downtime, quality, lots or labor yet: those parts are hatched placeholders until the data exists. Its line strip is a rolling 24 hours rather than the shift, since there are no shift calendars on screen yet.
- **Notes** can only be written when the order is created.
- **No sign-in or permissions** yet; changes are recorded as *anonymous*.

---

## 14. Troubleshooting and questions

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

**"Only … latest movement can be reversed".**
Someone recorded another movement on the lot since you opened it. Reload the lot: **Reverse** shows on the row that can be undone.

**I can't post a count.**
Every lot must have a count (enter 0 if it isn't there), the counts must be saved, and a lot marked *changed since counted* must be counted again.

**I can't merge two lots.**
They must be the same material, at the same location, and in the same status. Move or release one first.

**A BOM won't release.**
Read the yellow box: a component that doesn't exist or is inactive, an operation number the process's released version doesn't have, a unit with no conversion, or no components at all. Save the draft after fixing.

---

## 15. Glossary

| Term | Meaning |
| --- | --- |
| **Balance** | The one quantity of a material at a location, with no lot number: for a material that isn't lot-tracked, or at a location where lots mix. |
| **Base unit** | The unit a material is stocked in; also the base unit of a kind of unit (kg, m, ea…). |
| **Baseline** | A job's times as published when its order was dispatched; the plan you're measured against. |
| **BOM** | Bill of materials: what one material is made from, per a base quantity. |
| **Capability** | A line's permission to run a material, with its ideal rate. |
| **Changeover** | Time to switch a line from one product to another. |
| **Conversion** | How a quantity in one unit becomes another: standard for units of one kind, or set per material. |
| **Count** | Checking what is really at a location, then posting the differences. |
| **Department / group** | Equipment you can plan to that doesn't run jobs itself; its lines do. |
| **Dispatch** | Publish an order to the floor, fixing its baseline. |
| **Draft / released / obsolete** | Versions of a process or BOM: being edited / in use for new orders / replaced. |
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
| **Material class** | A group of similar materials, such as *Extruded profiles*. |
| **Merge** | Combine lots of one material into one. |
| **Operation** | One step of a process, run on one or more lines or departments. |
| **Packaging unit** | A unit like box, bag or pallet whose size depends on the material; it converts only through a material conversion. |
| **Process (route)** | The operations an order goes through, and where each runs. |
| **Projection** | Pulaski's calculation of when each job will run, redone on every read. |
| **Reverse** | Undo a lot's latest movement. |
| **Run standard** | The rate Pulaski plans with for a material on a line. |
| **Scrap factor** | Extra component quantity planned for losses. |
| **Sequence** | The order of jobs on a line. |
| **Share** | The part of an operation's quantity one line or department takes. |
| **Ship** | Send stock out of the site. |
| **Shortage** | What open jobs still need of a component beyond the stock available. |
| **Site** | A plant, with its own time zone. |
| **Split** | Divide part of a lot into a new lot. |
| **Substitution** | Consuming a material a job had no planned component of. |
| **Trace** | Following genealogy back to everything a lot was made from, or forward to everything made from it. |
| **Where used** | The BOMs and open jobs that use a material as a component. |
| **Window** | The time span a department job must be run in. |
| **Work order** | A request to make a quantity of one material by a due date. |
| **Write up / write off** | Add found stock to a lot / remove damaged, lost or miscounted stock, each for a reason. |
