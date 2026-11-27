# Pulaski User Guide

*Last updated: 2026-11-27 · Pulaski is pre-alpha: screens and behavior will change, and this guide changes with them.*

This guide explains how to use Pulaski to set up your plant, plan production and follow your orders. It is written for everyone who uses the application. If you are new to manufacturing software, start with [Getting started](#1-getting-started) and [Key ideas](#2-key-ideas) and follow the numbered steps in each section. If you already know MES and ERP systems, skim the key ideas, then jump to the section you need; the **In detail** parts and the [reference](#22-reference) tables give the exact rules.

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
13. [Quality: specifications and results](#13-quality-specifications-and-results)
14. [Materials](#14-materials)
15. [Users, roles and signing in](#15-users-roles-and-signing-in)
16. [Shift schedules](#16-shift-schedules)
17. [Labor, people and crews](#17-labor-people-and-crews)
18. [Site settings](#18-site-settings)
19. [Machine profiles](#19-machine-profiles)
20. [Edge nodes: collecting machine data](#20-edge-nodes-collecting-machine-data)
21. [Machines: live status and stops](#21-machines-live-status-and-stops)
22. [Reference](#22-reference)
23. [What Pulaski doesn't do yet](#23-what-pulaski-doesnt-do-yet)
24. [Troubleshooting and questions](#24-troubleshooting-and-questions)
25. [Glossary](#25-glossary)

---

## 1. Getting started

### What Pulaski is

Pulaski is a manufacturing execution system (MES) for lines that run continuously or in long batches: extrusion, molding, compounding and high-speed packaging. It sits between your business system (the ERP, which handles sales, purchasing and accounting) and the machines on the floor.

Today you can use Pulaski to:

- describe your plant: sites, areas, lines and departments;
- describe how products are made (**processes**) and what they are made from (**bills of materials**);
- create **work orders**, which Pulaski turns into **jobs** on your lines;
- plan and sequence those jobs on a live **schedule**, and see when each one is expected to run;
- publish the plan to the floor and freeze it;
- keep **inventory**: receive material lots into stores, write stock up or off, move and hold lots, record what each job used and made, and trace any lot back to what it was made from or forward to what was made from it;
- run jobs on the floor and report what they made, take machine states, counts and stops from the machines, and see **OEE** by line, shift and product;
- record labor, quality results against specifications, and watch processes on **control charts** that raise alerts.

For what is missing, see [What Pulaski doesn't do yet](#23-what-pulaski-doesnt-do-yet).

### Opening Pulaski

Open Pulaski in a web browser at the address your administrator gives you (on a developer's machine it is `http://localhost:5090`). Pulaski works best in a current version of Chrome, Edge, Firefox or Safari, on a desktop or a tablet in landscape.

Pulaski asks you to **sign in** with the user name and password your administrator gave you; the first time, you choose your own password. Your **roles** decide which screens you see and what you can change on them. See [Users, roles and signing in](#15-users-roles-and-signing-in).

### Try the demo

To try Pulaski on your own computer, you need Docker (Docker Desktop on Windows or a Mac) and a copy of Pulaski's source.

1. In the folder with the source, run `docker compose up --build`. The first time it builds Pulaski and downloads what it needs, which takes a few minutes.
2. Wait about a minute after it has started: the demo connects an edge node to its simulated machines and starts them.
3. Open `http://localhost:5090`. Sign in as **admin** with the password **Pulaski-demo-1**, then choose a new password.
4. The demo plant is ready: site PLT1 with extrusion lines EXT-01 and EXT-02 and packing line PACK1, stores, 200 materials and their units. On **Operate → Machines**, the extruder and saw of EXT-01 and the packer PACK1 are running, as the simulators report them, and the **Process values** tab shows the extruder's melt temperature and profile width as they change. Work order **DEMO-0001** is running on EXT-01.
5. On **Operate → Quality**, click **Control charts**, choose **MELT-TEMP** (or **PROFILE-WIDTH**) and material **PRF-4410-WH**, and click **Show**. A point is added every minute (the first about 3 minutes after the start), against control limits, with the process's capability below. Around 20 minutes after the start, the melt temperature shifts for 10 minutes: points beyond the limits or breaking a run rule appear, and an [SPC alert](#respond-to-spc-alerts) opens.
6. The plant also has a history: the previous shift and the current one so far, worked by crews on EXT-02 and PACK1. Look at **Operate → Overview**, **Operate → Machines** (EXT-02's stops, alarms and its **Utilization and OEE**), **Operate → Andon**, **Operate → Handover**, the lots on **Inventory**, work orders **HIST-1001** to **HIST-1006** on **Operate → Work orders**, and the crews' labor on **Labor → Shift review**.
7. To plan more, start at [Setting up: what to do first](#3-setting-up-what-to-do-first), step 6.
8. To stop it, press Ctrl+C (or run `docker compose down`). It keeps your data for the next start, and starts the simulated machines again; `docker compose down -v` deletes it.

**In detail**

- **What runs.** PostgreSQL, Pulaski's API (`http://localhost:5080`) and screens, the machine simulators (an MQTT broker with three simulated PackML machines, a process simulator for the extruder's melt temperature and profile width, and an OPC UA server: see [Edge nodes](#20-edge-nodes-collecting-machine-data), *The demo simulators*), and an edge node reading them. A mail catcher (Mailpit) takes every e-mail Pulaski sends, and nothing leaves your computer.
- **E-mail.** Open `http://localhost:8025` to read what Pulaski sent: [My alerts](#get-e-mails-when-something-needs-you-my-alerts), qualification expiry notices, and password reset links (**Forgotten your password?** on the sign-in page works in the demo). Pulaski e-mails only users with an e-mail address, and the demo's users have none. Give yours one on **Configure → Users** first, any address will do.
- **What the demo sets up.** Through Pulaski itself, as the administrator: the edge node **DEMO-EDGE** with a Sparkplug B connection to the broker, the machine profile **PACKML-SIM** (the simulators number their states their own way) on the three machines, their tags (every machine's state, unit mode and speed; the saw's and packer's processed and defective counts; the extruder's melt temperature and profile width), and the user **demo-edge** the edge signs in as. Then it resets and starts each simulated machine, as an operator would. Everything it did is in the history like anyone's work, and you can change it on **Configure → Edge nodes**.
- **The control charts.** Also as the administrator, the setup adds the characteristics **MELT-TEMP** (melt temperature, °C in its name, as Pulaski has no temperature units) and **PROFILE-WIDTH** (mm), both individuals and moving range with the Western Electric rules; a released specification for PRF-4410-WH (melt temperature 209 to 221 °C, capable; width 41.85 to 42.15 mm, marginal); manual control limits at ±3 sigma of the simulator's design (for a real process, work them out from a baseline instead: see [Set control limits](#set-control-limits)); and samples each from its tag every minute. Results are taken only while a job runs on the line, so it also releases the process **DEMO-EXTRUDE** (operation 10 on EXT-01), and dispatches work order **DEMO-0001** for 50,000 m of PRF-4410-WH and starts its job. Results are recorded as source *Automatic*, against the job, and checked against the specification.
- **The history.** After the setup, the demo records the previous shift and the current one up to the moment it starts, as if its people and machines had worked them. It goes through Pulaski's own rules, each step at its time, so everything is in the history and the event log like anyone's work. It adds:
  - a shift schedule **DEMO-3X8** that the site follows (Day 06:00, Evening 14:00, Night 22:00, eight hours each, with a planned lunch), crews **A**, **B** and **C**, and their people. Each crew has a supervisor and an operator who can sign in, and three people who only badge. **k.nguyen** (quality), **t.brandt** (maintenance) and **j.hale** (planner) work across the shifts. All of them have the temporary password **Pulaski-demo-2**.
  - the andon call types **MAINT**, **QUALITY**, **MATERIAL** and **SUPERVISOR**.
  - the edge node **LINE2-EDGE**, with EXT-02's state, unit mode, length counter and alarm tags. Nothing reads it live, so EXT-02 has no signal after the history ends.
  - the process **DEMO-PROFILE** (extrude on EXT-02, then bundle on PACK1 from 30 minutes after it starts), its BOMs (PVC-K67 and TIO2-MB), and EXT-02's own control limits for MELT-TEMP and PROFILE-WIDTH.

  Then the shifts are worked:
  - **Orders:** work orders HIST-1001 (white, completed and closed), HIST-1002 (gray, from one shift into the next) and HIST-1003 run. HIST-1004 waits dispatched, HIST-1006 is planned and HIST-1005 is canceled. EXT-02 runs a changeover job between colors, and its first job is paused for a breakdown.
  - **Production:** reports with scrap and its reasons; one report is corrected and one packer report is reversed.
  - **Inventory:** resin lots received, consumed, split into EXT-01's hopper and spilled. EXT-02's lots are packed on PACK1, two of them are held, one released and one rejected, and a packed lot is shipped.
  - **Machine data:** EXT-02's machine states, counts and alarms. Its stops include ones the machine named, a micro-stop, ones coded by operators, a split one and one still uncoded, plus a stop on PACK1 entered by hand.
  - **Quality:** manual results every 20 minutes, with an SPC alert closed and one left open.
  - **Andon:** calls answered in both shifts, plus, raised in the last half hour, one being answered and two waiting.
  - **Labor:** check-ins, breaks, training, the previous shift approved and one of its sessions corrected.
  - **Handover:** notes and open items, some of them closed.

  EXT-01 runs the live job DEMO-0001, so its history is only people, calls and notes. The history is recorded once: a later start leaves it as it is, and the shifts it filled become older ones. The current shift has what happened up to the start, so starting near the beginning of a shift gives it little; the previous shift is always complete.
- **The administrator's password.** For the setup, the demo signs in as admin, sets a password of its own for a few seconds, and then gives admin back the temporary password, so your first sign-in still chooses yours. That is why you wait a minute before signing in. Once you have chosen a password, a later start leaves the plant as it is and only starts the simulated machines again. So after getting a newer version of Pulaski, start the demo afresh (`docker compose down -v`, then `docker compose up --build`) to have everything the setup now does.
- **The process simulator.** Melt temperature varies around 215 °C (sigma 1.2) and the profile's width around 42.00 mm (sigma 0.04). For the last 10 minutes of every 30, the melt temperature runs 1.5 sigma high, then recovers: a special cause for control charts to catch. Set `PULASKI_SIM_SHIFT_EVERY_MINUTES` to change how often, or to 0 for never.
- **Ports.** If something on your computer already uses port 5090, 5080, 1883, 50000 or 8025, set `PULASKI_WEB_PORT`, `PULASKI_API_PORT`, `PULASKI_MQTT_PORT`, `PULASKI_OPCUA_PORT` or `PULASKI_MAIL_PORT` before starting it.
- **Passwords and keys** in the demo are for trying Pulaski only. Don't use it as an installation for real work.

### The screen

- **Menu (left).** **Inbox** at the top, when your role decides anything it gathers, with how many items wait (see
  [Your inbox](#your-inbox)). Then two sections, with like pages grouped under their main one:
  - **Operate**, the pages you use day to day: **Overview**; **Planning** (**Work orders**, **Schedule**); **Floor** (with **Andon** and
    **Handover**); **Machines**; **Inventory**; **Quality** (with **Nonconformances**); **Labor**.
  - **Configure**, the setup you do once and change now and then: **Equipment** (with **Machine profiles**, **Edge nodes**, **Terminals**);
    **Materials** (with **Units**, **Specifications**); **Processes** (with **BOMs**, **Work instructions**, **Changeovers**, **Planning
    rates**); **Site settings** (with **Setup checklist**, **Shifts**, **Reasons**); **People** (with **Users**, for administrators, with tabs for users, roles and
    permissions); **Integrations** (with **System health**, **Audit trail**, **Languages**).
  - Click a group's name to open its main page; its other pages unfold below it. The arrow beside it folds or unfolds a group without
    leaving the page you're on, the group of the page you're on too. That group unfolds when you arrive in it, and the browser remembers
    which others you left open.
    **Planning** has no page of its own: its name only folds and unfolds.
  - You see only the pages your roles let you open. If you may open some of a group's pages but not its main one, the group's name only
    folds and unfolds them.
  - The ☰ button at the top left collapses the menu to icons, to make room on small screens; then every page you may open shows as an
    icon, without groups.
- **Help (?, top right).** Opens this guide's section for the screen you're on, beside it; it follows you as you move to another screen.
  **Open the whole guide** opens all of it in a new tab, at that section. Esc or ✕ closes it. The sign-in page, terminals and operator
  stations have a **Help** link instead, which opens the guide in a new tab. The guide is in English.
- **Favorites (☆, top right).** Click the star to add the page you're on to **Favorites** at the top of the menu, named as the page is;
  click the filled star to take it off. Up to 20, kept for you on any device.
- **You (top right).** Your initials, name and roles. Click them for your menu:
  - **Preferences…** opens a dialog with your [language and region](#your-language-and-region), the **color theme** (*Like the device*,
    *Light* or *Dark*) and the [touch layout](#use-a-touch-screen) (*Automatic*, *On* or *Off*).
  - **Make this page my home page**: where Pulaski opens for you when you sign in or come back to it (the Pulaski logo goes there too); the
    Overview is still in the menu. On your home page, **Start at the Overview again** undoes it.
  - **My alerts** (e-mails when something needs you), **Change your password** and **Sign out**.
- **Filters are remembered.** List pages open with the filters you last used, on any device and after you sign out: **Stock**, **Lots**,
  the **movement journal**, **Containers**, **Shortages**, **Materials**, **Work orders**, the **Schedule** (its view), **Quality results**,
  **Nonconformances**, the **OEE report** on Machines, **deliveries** on Integrations and the **Audit trail**. Words you type as you go (a quick
  search box) and exact dates aren't kept: a date range stays a recent one (the journal's last week, the OEE report's same number of days up
  to today). A link that names its own filters (from the Overview, a lot's page) wins. **Clear filters** on the Lots list goes back to the
  start. Favorites, your home page and filters are yours alone, and aren't part of the audit trail.

  Press Esc or click elsewhere to close the menu. Theme and touch layout are kept on that device; language and region on your user.
- **Main area.** The page you picked.
- **With the keyboard.** The first Tab on any page shows **Skip to content**: press Enter to go straight to the page, past the header and
  menu. Everything else is reached with Tab and Shift+Tab, and the control that has focus is outlined.

### Your language and region

Pulaski's screens can show another language than English, and write dates and numbers the way your region does.

1. Click your name at the top right, then **Preferences…**.
2. Choose your **Language** and, if you like, your **Region** (such as *Español (México)*); the page shows how dates and numbers will look.
3. Click **Save language and region**. The page reloads in your language.

In detail:

- The choice is kept on your user, so it follows you to any computer when you sign in.
- Every screen is translated, and Pulaski ships Spanish for all of them: the menu, the header, signing in and resetting a password, these
  pages, the floor screens (Floor, reporting production, pausing and completing, Andon, Handover) and the terminals (badge kiosk, operator
  station, line boards), the Overview, Inbox, Machines (status, stops, utilization and OEE, alarms, process values), Quality, Labor and
  Inventory (stock, lots and their actions, locations, containers, feed points, scanning, counts, shortages, deliveries, the journal and
  reports), planning (work orders, the schedule, processes, BOMs, materials and classes, units, changeover rules, planning rates and
  job pages), and Configure (equipment, machine profiles, edge nodes, reason codes, shift schedules and one-off changes, site settings,
  integrations and connections, people, crews, qualifications, terminals, users, roles and permissions) and My alerts. A term your
  language has no translation for yet shows in English.
- Exported CSV files keep English column names, so spreadsheets and scripts that read them work whatever language exported them; the
  column names, addresses and codes in help text stay as they are too, with the words around them translated.
- Your plant's own names (lines, reasons, roles, call types) show as they were entered, and *Choose File* on a file picker is the browser's
  own text.
- When Pulaski refuses something (*EXT-01 can't make that much in 5 minutes…*), the message is in your language too.
- Without a region, numbers and dates keep Pulaski's usual form (*2026-11-22 15:04*, *1,234.5*); with one, the screens that follow regions
  use its form (so far the shared ones: times, quantities and percentages).
- Before you sign in, the sign-in page follows your browser's language.

### Your inbox

**Inbox** gathers what waits for you to decide, at every site where your role decides it:

1. Click **Inbox** at the top of the menu; its number says how many items wait.
2. Items are grouped by what they are, oldest first: **Partial batches to approve or reject**, **SPC alerts to close**, **Labor to approve**,
   **ERP changes to work orders**, **Planning rates to review**, **Maintenance requests waiting**, **Checklists to sign off**, **Releases to
   approve** and **NCR actions to do**. Each says
   where it is and how long it has waited.
3. Click an item to open the page where it is decided, and decide it there. It leaves the inbox once decided.

In detail: each kind shows only where you hold the permission that decides it (*partial batches: decide*, *quality: record*, *labor: approve*,
*work orders: manage*, *rates: modify*, *Work maintenance requests*, *Give setup and first-article checklists their second sign-off*). A maintenance request shows by its
number and machine with what was asked, until someone starts it; it opens the **Maintenance** tab of Machines. A checklist shows by its job and
instruction, with the line and who answered it, until it is signed off or rejected; it opens the job's page. **NCR actions to do** lists
every open action of a [nonconformance report](#nonconformance-reports-ncrs) that you own, whatever your role, with its due day, and, where
you may work NCRs, every overdue one at the site; it opens the NCR. Labor counts shifts of the last two weeks that have ended with labor nobody approved, as the Overview
does. The page updates [by itself](#screens-that-update-themselves) when something arrives, and every 30 seconds; the menu's number every minute,
and when you move to another page.

### Screens that update themselves

The floor screens show what happens elsewhere within a few seconds, without reloading: a report entered at one terminal, a job started at a
station, a machine stopping, an andon call. This works on the **Overview**, **Floor**, a job's page, **Machines**, **Andon**, **Handover**, the
**Inbox**, [line boards](#show-a-line-board-on-a-tv) and [operator stations](#use-an-operator-station).

In detail:

- **What triggers it:** anything recorded (every change Pulaski keeps as an event) and anything a machine reports (its state, a stop, its
  counts, an alarm) at a site you can see. Screens then read their figures again, so they show only what you may see, as they always do.
- **While you are typing,** nothing is reloaded under you: the Floor waits while a form is open, the job page while you report or stop a job
  or reverse a report, Machines while you code or adjust a stop.
- **How often:** a page reloads at most once a second (Machines every 2 seconds, the Overview every 10), however much changes. The timers stay
  as well (every 15 or 30 seconds), so a page catches up by itself if the live connection drops; when it comes back, every open page reloads once.
- Process values alone don't trigger a reload.
- **Boards and stations** reload the whole screen, at most every 3 seconds, for changes at their terminal's site. A station never reloads while
  someone is typing in one of its forms (a box has the cursor, or something was typed since it last loaded): it waits until the form is sent
  or the station has been left alone for its idle time (15 minutes). While a PIN is being typed it doesn't reload at all. A screen whose
  browser runs no scripts keeps reloading on its timer.

### How saving works

Pulaski saves in two different ways, depending on the screen. Each section says which one it uses.

| Screen | How changes are saved |
| --- | --- |
| Equipment properties | Straight away, when you press **Enter**, leave the field, or pick a choice. **Esc** undoes the edit before it is saved. |
| Process editor | Straight away, after every change. The top of the editor says **Saving…** and then **All changes saved**. |
| BOM editor | When you press **Save draft**. The button says **Saved** when there is nothing left to save. |
| Forms (new work order, new process, new BOM) | When you press the form's button (**Create**, **Create and edit**). |
| Schedule | Every drag and every button acts straight away. Only canceling a job asks you to confirm. |

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

A **material** is anything you make or use: a finished profile, a resin, a dry blend, a pallet, an end cap. Each material has a code (its item number), a base unit (meters, kilograms, each) and usually a **material class** that groups similar materials, such as *Extruded profiles*. Materials are kept on the [Materials](#14-materials) page; those your ERP sends keep their code, name, base unit and type in the ERP.

**Capabilities** say which material can run on which line, and how fast (its ideal rate). Pulaski never puts a material's job on a line without a capability for it. You add them on the material's page ([Where it can run](#where-it-can-run-capabilities-and-rates)).

### Units of measure

Every quantity has a **unit**: kilograms, meters, each, boxes. Each material has a **base unit**, the one its stock is counted in. You don't have to use it everywhere: you can order a profile in feet even though it is stocked in meters. Pulaski converts:

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

Then execution: **in progress**, **completed**, **closed**. An order can be **canceled** at most stages. See [Work order statuses](#work-order-statuses).

---

## 3. Setting up: what to do first

If you are starting from an empty Pulaski, work through this list in order. Each step depends on the ones before it.

**The setup checklist does this with you.** Open **Configure → Site settings → Setup checklist** (the Overview links to it until the site is
ready). It lists the steps below in order, each marked **Done** (with how many there are), **To do** or **Not needed**, and each opens the
screen where it is done:

1. Work down the list; a step is done as soon as what it asks for exists, so come back to the checklist and it has moved on.
2. Steps marked *(optional)* aren't needed for a first job. If one doesn't apply to the site, click **Not needed here** (and **Needed after
   all** to take it back); marking needs permission to change site settings.
3. Once every required step is done (a site with its time zone, a line that runs jobs, a material, a capability, a released process, an
   inventory location, a work order, and a job started), the checklist says the site is set up for production and the Overview stops prompting.

In detail: the checklist is worked out each time from what the site has, so nothing has to be ticked off by hand. Materials, unit conversions
and BOMs count across the enterprise; everything else is the site's own (users count when they hold a role at the site). A step that is done
shows as done even if it was marked not needed. Anyone who can see site settings sees the checklist; a step's link opens only for those who
may see that screen.

| # | What | Where | Why |
| --- | --- | --- | --- |
| 1 | The enterprise and at least one site, with its time zone | [Equipment](#4-equipment) | Everything else belongs to a site. |
| 2 | Areas (departments) and lines under the site | [Equipment](#4-equipment) | Jobs are planned on lines and departments. |
| 3 | Material classes, then materials (one by one, from a [CSV or B2MML file](#import-materials-from-a-file), or from the ERP) | [Materials](#14-materials) | Orders and BOMs need materials. |
| 3a | Units, and conversions for materials you order or use in other units | [Units](#6-units-and-conversions) | So orders, BOMs and rates can use any unit that converts. |
| 4 | Capabilities: which material runs on which line, and how fast | [The material's page](#where-it-can-run-capabilities-and-rates) | Without one, a line can't take a material's job. |
| 5 | Shift schedules, and which one each site, area or line follows (optional) | [Shifts](#16-shift-schedules), then [Equipment](#4-equipment) | Without one, Pulaski assumes lines run around the clock and marks them **24/7**. |
| 5a | Crews and people, if you record labor (optional) | [People](#17-labor-people-and-crews) | So supervisors can check crews onto lines, and shifts can name their crew. |
| 6 | Processes: the routes, what they make, released | [Processes](#5-processes) | Every work order is made from a process. |
| 7 | BOMs for the materials you make (optional, recommended) | [BOMs](#7-bills-of-materials-boms) | Without a BOM, an order plans no components. |
| 8 | Work orders | [Schedule → New work order](#8-creating-a-work-order) | Now you can plan. |
| 9 | Inventory locations: stores, staging areas and feed points, with **Holds inventory** ticked | [Equipment](#4-equipment) | Lots have to be somewhere. |
| 10 | Reason codes for write-ups, write-offs and holds, and utilization reasons for machine time | [Reasons](#reason-codes) (a starter set comes with `db/seed-reasons.sql`) | Every adjustment and hold needs a reason; machine profiles map stop codes to utilization reasons. |
| 11 | Machine profiles, and which one each machine uses | [Machine profiles](#19-machine-profiles), then [Equipment](#4-equipment) | So the machine's state values and stop codes mean something in Pulaski. |
| 11a | Users with a role at the site (optional) | [Users](#15-users-roles-and-signing-in) | So the people who plan, run and check the work can sign in. |
| 12 | Edge nodes, their connections, and tags binding machine signals to equipment (optional) | [Edge nodes](#20-edge-nodes-collecting-machine-data) | So Pulaski hears machine state and counts. |

The demo data that comes with Pulaski already contains steps 1 to 4, and 9 (plant PLT1 with EXT-01, EXT-02 and PACK1, the stores RM-STORE and staging FG-STAGE, 200 materials, and the standard units with conversions for the demo materials), so you can start at step 6.

### The Overview page

**Operate → Overview** is the home page: a live board for the plant. In a few seconds it answers *which lines are running, which aren't and why, and what needs a person*. It [updates by itself](#screens-that-update-themselves) within seconds of a change, and every 30 seconds; the pill at the top right says when it was last updated (and turns gray, with a warning, if Pulaski can't reach the server for over a minute). The refresh button updates it at once. With several sites whose Overview you may see, pick one at the top.

**Diagonal hatching means "no data".** A part of the board that has nothing to show yet (machine figures when no line reports its state, say) sits where it will be, hatched and grayed, with a plug symbol saying what will fill it, so the page won't move around when the data arrives. A dash or hatching means *not measured*; a 0 means measured and none.

**Needs attention** (the grid at the top) counts what needs a person, most urgent first. A count above zero is marked with a colored bar and icon; click it to go to the list behind it.

| Cell | Counts | Today |
| --- | --- | --- |
| Uncoded stops | Stops in each line's current shift, longer than a micro-stop, with no reason yet | Live → Machines (needs a line that reports its state) |
| Open SPC alerts | Control charts that signaled and wait for a cause | Live → Quality → SPC alerts |
| Orders behind | Open orders projected late or slipping past their baseline | Live → Work orders |
| Lots on hold | Lots with stock on hold or in quarantine | Live → Inventory → Lots |
| Schedule conflicts | Open orders with a schedule warning or an overlap | Live → Schedule |
| Lines with no signal | Lines that report their state but send nothing now, or whose edge node has gone quiet | Live → Machines (needs a line that reports its state) |
| Not on a line | Planned orders with quantity not yet placed | Live → Schedule |
| ERP changes to accept | Open orders the ERP changed after they left planning | Live → Work orders |
| Labor to approve | Shifts of the last 14 days that have ended with labor not yet approved | Live → Labor → Shift review |

**This shift** adds up each line's current shift so far (a line without shifts counts the site's day so far):

- **OEE** and its availability, performance and quality components, from the machine and production reports. Site OEE combines the active production lines from their underlying time and ideal-output components; it does not average line percentages. When there is no rated production to calculate OEE, it shows a dash.
- **Good output** from production reports, against what the schedule planned for the shift, with a bar. The plan shares each job's quantity over its time on the schedule and counts the part inside the shift. Lines making different units show one figure per unit; units are never added together. Machine counts aren't output until they are reported (see [Report production from the machine](#report-production-from-the-machine)).
- **Scrap** reported, with its share of all output in that unit.
- **Downtime** in minutes, with the biggest reason named. It counts stops, time not running and time with no signal; micro-stops and planned stops (setup, cleaning) don't count. *No signal* and *Not coded* are named like reasons. With no line reporting its state it shows a dash and *Needs machine data*.

**Labor to approve** counts the site's shifts from the last 14 days that have ended, have time recorded and aren't approved yet; underneath it says how many sessions they hold and the oldest day. Click it to open **Shift review** on that day, on the first shift waiting. It shows 0, *Nothing waiting*, when there is nothing to approve, including when no labor has been recorded. Approving a shift takes it off the count at the next refresh; correcting an approved shift doesn't put it back. Without permission to see labor at the site the cell shows a lock and *Needs permission to see labor*, and no count.

**Lines** has one row per line:

- **State:** what the line is doing now, as on **Operate → Machines** (Producing, Waiting, Held, Stopped, Ready, or No signal), with how long, why it stopped and how many of its stops this shift still need a reason. *Edge quiet* means the edge node it is read through has sent nothing for over 2 minutes. A line that doesn't report its state says **Not tracked**.
- **Now or next:** the job the schedule has on the line now, or the next one and when it starts, with how much of it is made, and what the line has reported this shift (good and scrap).
- **The strip** covers the current shift, with a black line at *now*; when lines are on different shifts it runs from the earliest one's start to the latest one's end, and when no line is on a shift it covers six hours back to eighteen ahead. The top lane shows what the line actually did up to now: green running, dark red down, blue a planned stop, gray not in production mode, hatched no signal (all hatched for a line that doesn't report its state). Point at a stretch to see it. The bottom lane shows the planned jobs: outlined for planned, solid for dispatched or firmed, hatched for changeovers. Hover over a job to see it; click it to open its order on the Schedule. If nothing is planned in the window, the lane says when the next work starts.
- **OEE** for the line's current shift, with small bars for availability, performance and quality. The value comes from the machine/OEE report and refreshes with the Overview. A dash means there is no rated production data for that shift.
- **Flags** at the end: a red mark if work overlaps on the line, an amber clock if a job has a schedule warning (hover for the messages).

At the bottom, **Work orders** counts orders at each stage, Planned → Dispatched → Firmed → In progress → Completed (click one to list them), and **Starting next** lists the next six jobs to start across the plant.

---

## 4. Equipment

**Configure → Equipment.** This is where you describe your plant as a tree.

### The screen

- **Left: the tree.** Each row shows the code, name, level, role and chips for its settings: **State** (tracks machine state), **Schedulable** or **Plans for children** (a department), **Runs jobs**, **Inventory**, **Mixes lots** (lots are indistinguishable), **One material**, and **Inactive** in gray.
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

1. **Machine profile**: how its controller reports state and stop codes. Choose one of its site's [machine profiles](#19-machine-profiles), or *None*.
2. **Micro-stop threshold**: stops shorter than this many minutes are micro-stops. They are logged but not queued for a reason, and they don't count against availability. Default 5 minutes; 0 to 60, in steps of half a minute. An extruder may use 2, a pack cell more.
3. **State from**: where its state comes from.
   - *Its own state tag* (default).
   - *Worst of its machines (series)*: for a line whose machines run one after another, so one stopped machine stops the line.
   - *Best of its machines (parallel)*: for machines running side by side, so the line runs while any of them does.
   - *Its constraint machine*: the line's state is one machine's. A **Constraint machine** row appears; Pulaski first picks the first machine below that tracks state, and you can choose another.

Each saves as soon as you change it. The profile reads the machine's data as it arrives through an [edge node](#20-edge-nodes-collecting-machine-data); the threshold decides which stops are micro-stops, and *State from* how a line shows on [Machines](#21-machines-live-status-and-stops).

Equipment with **Can run jobs** set also shows **Production counted at**: whose counters a [report from the machine](#report-production-from-the-machine) takes, usually the line's last machine (the saw, the packer). Choose the line itself or any equipment below it. Left at *Found from the counter tags*, Pulaski uses the line's own processed counter, or else the only machine below it that has one. If several machines below it count, choose one here.

### Production report defaults

For a line that runs jobs, the equipment's panel has a **Production reports** part (changing it needs *equipment: manage*):

1. Choose where **Good output goes to**: prefilled on the line's reports, at the Floor and at its stations. **Ask on each report** leaves it empty.
2. Choose a printer under **Print lot labels on** to print each lot's label by itself once the lot is closed (see
   [Report production](#report-production)); **Don't print them** turns it off.
3. Click **Save**.

In detail: the location must hold inventory at the line's site and the printer be one of the site's active printers (set up under
**Site settings → Labels**). On a line that fills lots, the lot being filled's location is offered first. A report can always choose another place.

### Copy a line

A new line much like one you have doesn't need setting up from nothing.

1. Select the line on **Equipment**, and under its properties, in **Copy this line**, type the **New line's code** and its **Name**, then click
   **Review the copy**.
2. Check what comes with it, and untick what you don't want:
   - **The equipment below it**: its machines, feed points and storage, with their flags, calendar and inventory settings.
   - **Capabilities and rates**: what it can run, ideal rates, batch and lot rules, from now on, each with its planning rate from the ideal rate
     (as when added by hand); and its changeover rules. Needs permission to maintain processes and capabilities.
   - **Machine settings**: machine profile, how its state is worked out (its counter and constraint machine become the copies'), output
     location, label printer and stop-code overrides.
   - **Qualifications** required, **OEE targets** of its own (from today), **maintenance plans** (counting from now) and **work instructions**
     aimed at the line.
3. Choose where it goes (**It goes under**: a site or an area, at this site or another where you manage equipment; the same as the
   original to start with), and check every copy's
   **Code** and **Name**: a child whose code starts with the line's gets the new line's code instead (EXT-01-SAW → EXT-03-SAW), any other the
   new code in front. A code already used at the site is flagged.
4. Click **Copy the line**. The tree shows the new line, selected.

In detail:

- **Not copied:** machine tags (the addresses an edge node reads; bind the new machines on **Edge nodes**), stops, history and anything that
  happened; processes keep naming the original line, so move or assign a job to the copy, or add it to a process, for orders to reach it.
- **Into another site**, what belongs to a site is found there by its code: machine profile, shift schedule, label printer, an output location
  outside the line, qualifications, the site's own stop-code reasons (by kind and code) and work instructions. The review lists what that site
  has nothing of; the copy goes without it (a shift schedule not found: the line follows the equipment above it). Copy the site's reference
  data first ([Start a new site from another](#start-a-new-site-from-another)) and most of it matches. Changeover rules, OEE targets and
  maintenance plans move to the new site.
- It is one change: everything is copied or nothing is, and each new piece of equipment and capability is in the audit trail, with the copy
  itself (`pulaski.equipment.line_copied.v1`).
- Copying needs permission to manage equipment where the copy goes (and to maintain processes and capabilities there, for capabilities).

### Start a new site from another

A new plant set up like one you have can start with that one's lists and settings.

1. Select the site on **Equipment**, and under **Start a new site from this one** click **Review what it would bring**.
2. Give the **New site's code**, **Name** and **Time zone**, and untick what you don't want:
   - **Shift schedules** and **calendar exceptions** still to come (holidays, shutdowns). A schedule's one-day changes aren't copied.
   - **The site's own reasons** (stops, scrap, holds, adjustments, SPC causes, with their groups) and **machine profiles** with their state and
     stop-code maps, which point at the new site's copies of the reasons.
   - **Crews**, **qualifications** and **andon call types**.
   - **Site settings** (changeover mode, reporting tolerances, planning-rate refresh, allocation and release approval), the site's **OEE
     targets** (from today) and **lot attributes**.
3. Click **Make the new site**. It goes under the same enterprise, and the tree shows it, selected.

In detail: areas and lines aren't copied: add areas, then copy each line into the new site with [Copy a line](#copy-a-line). Nor
are people, badges, terminals, printers, label settings (the SSCC serial numbers belong to one site), materials and processes (shared across
the enterprise anyway), or anything that happened. It needs permission to manage equipment everywhere (administrators). One change: all or
nothing, in the audit trail as `pulaski.equipment.site_copied.v1`.

### Deactivate equipment

Set **Active** to *false*. Inactive equipment stays in the tree and in history, grayed out, but can no longer be used for new work. Pulaski never deletes equipment.

### In detail

- **Required qualifications:** under the properties, a site, area or line lists the qualifications working on it requires, its own and those from above it (*from …*). **Require** adds one; **×** stops requiring it. They apply to everything below. Pulaski warns when someone without one is put on a line, but doesn't stop them. See [Qualifications](#qualifications).
- The levels, top to bottom: enterprise, site, area, work center, work unit, equipment module, control module. A child always sits below its parent's level.
- Roles allowed at each level: work center: production line, process cell, production unit, storage zone; work unit: work cell, unit, machine, storage unit; equipment module: machine, feed point, module; control module: module.
- Codes are unique within a site.
- The **Working time** group: **Shifts** chooses when it works for planning (*Inherit from parent*, one of its site's shift schedules, or *Around the clock*), and **Shifts in effect** shows what it ends up with and where from, such as *2X8 · Two shifts, from PLT1*. See [Shift schedules](#16-shift-schedules). Changing **Shifts** needs permission to maintain shift schedules, not equipment.
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

### Release with approval

Where the site asks for approval before release ([Approval before release](#approval-before-release)), a draft isn't released by whoever
edits it: someone else approves it.

1. Click **Submit version 1 for approval…**, optionally type what changed, and click **Submit**. The draft now **waits for approval** and
   can't be changed; it shows in the [Inbox](#your-inbox) of those who may approve releases at the site.
2. Someone who didn't edit or submit the draft opens it (from the Inbox), types a **Comment** and clicks **Approve and release**, which
   releases it, or **Send back**, which makes it a draft again for its editors.
3. Whoever submitted it may **Withdraw** it while it waits, to change it.

**Approvals** on the process's page lists every request: the version, what became of it, who submitted it and when, who decided and why.
Approving needs *Approve or send back processes, BOMs and specifications submitted for release* at the site (Quality and Process engineer
to start with).

### Change a released process

1. Open the process. If there is no draft, click **Change: start version 2** (the number follows on). This copies the released version into a new draft.
2. Edit the draft and release it. The previous version becomes **obsolete**.

Orders already made keep the version they were made from. If a draft already exists, click **Open the draft** instead.

### In detail

- Operations with no path of links between them run in parallel; links make them sequential. Links must flow one way; a loop is refused.
- A released process with more than one operation must be connected.
- Only lines and departments of the process's own site can be used; machines and other equipment that neither runs nor plans jobs are refused.
- The link's *after start/after end* and lag set the **earliest** time each operation's jobs may start, counted from the order's start. The schedule then also waits for the earlier operation's jobs as they are projected: see [Process links on the schedule](#process-links-on-the-schedule).

### Work instructions

**Configure → Work instructions** holds how to set up and run: the steps for a process's operation, a material or a line, written once and
released in versions, with drawings or photos attached. Everyone who works on the floor can read them; Quality and Process engineers write
and release them.

Make one:

1. Open **Configure → Work instructions**, choose the site if you have more than one.
2. Type a **Code** (such as *WI-EXT-001*) and a **Title**, then click **New instruction**. Its page opens with **Draft 1**.
3. Write the steps under **Text**. Start a line with `#` for a heading, `1.` for a numbered step and `-` for a point; put words between `**`
   to make them bold. **How it reads** shows the result as you type.
4. Click **Save the draft**.
5. To attach a drawing or photo, choose a file under **Files** (PDF, PNG or JPEG, at most 10 MB). It is added at once; **Remove** takes it out
   of the draft again.
6. Optionally say **Why this version**, then click **Release version 1**.
7. Under **Shown for**, choose **A process's operation** (the process and the operation's number), **A material** or **A line**, and click
   **Add**. Add as many as it applies to.

Change one: click **Start version 2** on its page. The new draft starts from the released text and files; edit it and release it, and the
version before becomes obsolete.

Add checklists: a version can hold a **Setup checklist**, answered before the job starts on a line, and a **First-article checklist**, answered
before its first production report there.

1. In the draft, under **Setup checklist** or **First-article checklist**, click **Add an item**.
2. Type the question, and choose its **Answer**: **Yes or no** (with **Passes when** *Yes*, *No* or *Either answer*), **A number** (with the
   lowest and highest allowed and a unit, any of them optional), **A choice** (one per line) or **Text**. Clear **Required** for a question that
   may be left blank. **↑** moves an item up; **Remove** takes it out.
3. To have someone else check the answers, tick **Needs a second sign-off**: a passed checklist then counts only once someone who may sign
   off checklists (Quality and Supervisor to start with) signs it off.
4. Click **Save the draft**, then release it. A checklist on its own is enough to release a version.

#### In detail

- **One released version at a time.** A released version never changes; only the draft does. **Versions** lists every version with who released
  it, when and why. While there is a draft, the released version stays in force.
- **Releasing needs something in it:** text, a file or a checklist. Release is off while the text or the checklists have unsaved changes (*Save
  the draft first.*).
- **Checklists** hold at most 50 items each. A new draft starts with the released version's checklists. Answers are kept against the version
  answered, so changing a checklist never changes what was answered.
- **The text holds no formatting of its own** beyond headings, numbered steps, points, bold and paragraphs. Anything else, such as HTML, is
  shown as typed. Up to 20,000 characters; put longer material in a file.
- **Files** are checked by their contents, not their names: a renamed text file is refused. A version holds at most 20. A new draft shares the
  released version's files; removing one from the draft leaves the released version as it was.
- **Shown for an operation** means that operation number of the process in whichever version, so it carries on when the process is revised.
  A line must be one of the site's that runs jobs. The same target can't be added twice; **Remove** ends it.
- **The code** is 1 to 30 letters, digits, `-`, `_` and `.`, unique at the site, and fixed once made. Clear **Shown at jobs** to switch an
  instruction off without deleting it.
- Permissions: *See work instructions* (Planner, Supervisor, Operator, Maintenance, Quality, Process engineer, Manager, Auditor) and *Maintain
  work instructions* (Quality, Process engineer). Giving checklists their second sign-off needs *Give setup and first-article checklists their
  second sign-off* (Quality, Supervisor).

---

## 6. Units and conversions

**Configure → Units.** Here you see every unit Pulaski knows, and say how a material converts between units that aren't of the same kind.

### Why it matters

People work in different units. A customer orders 10,000 ft of profile, the BOM is written per 1000 m, the line's rate is in meters per hour, and the blend arrives in 25 kg bags. Pulaski converts between all of these, as long as it knows how. When it doesn't, you'll see a message such as *ft doesn't convert to kg for this material; add a conversion for it*, and this page is where you fix it.

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
| [The Schedule](#10-the-schedule) | A job's length is worked out from the line's rate even when the job is in another unit (an order in feet on a line rated in meters per hour). The side panel shows the rate in the job's unit. |

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
| **Consumed by** | How it is taken from stock (see [Feed points](#feed-points)): **Backflush on count** (automatically as production is reported, in proportion to what was made, from the lots at the line's feed points), **Time window (loaded lot)** (shared among the lots loaded at the line's feed points while it was made, typical for resin and blend), **Declared by operator** or **Scanned lot** (both entered by hand). |
| **Operation** | Only on a BOM for a process: which operation consumes it. **First operation** if you leave it. |

3. **Approved alternates** (optional): under a line, click **Add an approved alternate** for a material that may be used in its place. Choose the material, how much of it **uses** per one of the component (both in their base units: `1.05` uses 5% more), and its **priority** (1 is offered first). Remove one with its red ×.
4. Remove a line with its red ×.
5. Click **Save draft**. A yellow box lists anything that stops the release, and the lines concerned are highlighted.

### Release it

When the draft is saved and has no problems, type an optional **Release note** and click **Release version N**. From then on, new orders for that material (with that process) plan their components from it.

Where approval is required ([Approval before release](#approval-before-release): any site that can make the material, or the BOM's
process's site, asks for it), **Release version N** gives way to **Submit version N for approval…**, as for
[processes](#release-with-approval): the draft waits, someone else approves it (which releases it) or sends it back, and **Approvals** lists
every request.

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
- **Alternates** belong to the line and the version: a new version can change them, and orders keep the ones of the version they were planned with. An alternate can't be the component itself or the BOM's own material, is listed once per line, needs a ratio and priority above zero, and must be active to release. Orders still plan only the component; alternates are offered when [allocating lots](#allocate-lots-to-a-job), and consuming one is recorded as an approved alternate rather than a substitution.
- To release, each component's unit must convert to that component's base unit (so its use can be taken from stock later), and the BOM's unit must convert to the material's base unit. The yellow box says which conversion is missing.
- Operation numbers must exist in the process's **released** version. A draft may name an operation that isn't released yet, but can't be released until it is.
- Each component's total is rounded to six decimal places. The jobs of an operation share it in proportion to their quantities, and the last job takes any rounding remainder, so the jobs always add up to the total.
- Planned components never change after the order is made, even if the BOM changes later.
- A job added to the order later (from the Schedule, or the replacement when a job is canceled and replaced) gets its components too: those of its operation, for its own quantity, from the BOM version the order was planned with.

### When the process changes

A released BOM names operations by number. If the process is later released with different operations (Pack renumbered from 30 to 40, say), the BOM still names 30. Pulaski doesn't stop the process release, but:

- the process editor shows which BOMs the release broke;
- the BOMs list and the BOM editor say which operation the BOM names that the process no longer has;
- the new-order form grays out the process for that material, and creating the order is refused with the same reason.

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
   - A process whose lines can't run this material is **grayed out** with *can't run this material*, and the reason is listed underneath, for example *Operation 10 (Extrude): EXT-02 is not set up to run FLM-HDPE-20-BLK; add a capability for it first.*
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
4. **The ERP cancels an order.** While planned, it is canceled with its jobs; after that the cancellation waits for you like any change.

**From a B2MML file.** When the ERP exports its orders as an ISA-95 production schedule in B2MML XML:

1. On **Operate → Work orders**, click **Import from the ERP**.
2. Type the **External system** the orders come from, such as `SAP-PRD`: Pulaski keeps each order under it, so the same file again doesn't make new ones.
3. If the file doesn't name its site, choose the **Site** for its orders.
4. Pick the **File**. Pulaski checks it at once and lists each order with what importing would do (create, update, cancel, unchanged, wait for a planner) or why it is refused. Nothing is saved yet.
5. Click **Import**. Each order is imported on its own: a refused one is left out with its reason, and the others go in.

**Download a template** in the same box gives a file to start from.

**In detail**

- **Sending once is enough.** Orders are matched on the ERP's system and key, so sending one again never makes a second; the same version again changes nothing. An order number already used by an order made in Pulaski is refused.
- **Which process.** The one the ERP names, else the material's default process, else the only active process at the site whose released version makes it. If none or several fit, the order is refused and the answer says why.
- **What the ERP owns:** product, quantity, start, due date and priority. Pulaski owns the schedule, dispatch and status. The ERP can't change an order's product; cancel it and send a new one.
- **Re-planning** changes each job's quantity in place, shared between the lines as when it was made (whole batches of each line's size), scales its components, and moves its hold-until by as much as the start moved. It is refused once a job has started, or when the order's jobs were changed by hand on the schedule; change them there instead.
- **Every step is recorded:** imported, changed, change pending, accepted (with the ERP's values) and rejected (with your reason).
- **What a B2MML file gives.** Each `ProductionRequest` is one order, wherever it is in the file (a `ProductionSchedule`, or one wrapped in `SyncProductionSchedule` or another verb). Its `ID` is the order number and the ERP's key; its `Description` the order's note; its `Priority` the priority. The material is what one of its `SegmentRequirement`s produces: a `MaterialProducedRequirement`, or a `MaterialRequirement` whose `MaterialUse` is `Produced`, with its `MaterialDefinitionID` and `Quantity` (`QuantityString` and `UnitOfMeasure`). One material per order. It runs from its `StartTime` (else the segment's `EarliestStartTime`) to its `EndTime` (else `LatestEndTime`); a time without an offset is UTC. The site is its `HierarchyScope`'s `EquipmentID`, else its schedule's, else the one you chose. `ProductProductionRuleID` (else a segment's `ProductSegmentID` or `ProcessSegmentID`) names the process by its code. `RequestState` `Canceled` cancels the order. A B2MML request has no version, so every import compares the order with what Pulaski has, and the same order again changes nothing. An `ID` twice in one file is imported once.
- **For the integration:** `POST /api/work-orders/import` with a list of orders (`externalSystem`, `externalKey`, `externalVersion`, `siteCode`, `orderNumber`, `materialCode`, `quantity`, `uomCode`, `plannedStart`, `dueAt`, `priority`, `processCode`, `notes`, `cancelled`). Each comes back `created`, `updated`, `unchanged`, `pending`, `cancelled` or `refused` with why, and each is saved on its own. `?dryRun=true` gives the same answers and saves nothing. A B2MML file goes to `POST /api/work-orders/import/b2mml?externalSystem=…&siteCode=…&dryRun=…` as the request body (at most 5 MB, 500 orders); `GET /api/work-orders/import/b2mml/template` is the template. The caller needs *work_order.manage* at the order's site.

#### Production performance for the ERP

Pulaski can give the ERP what a production job made and used, as an ISA-95 production performance in B2MML XML. Each document is the job's totals as they stand now, so a later one replaces an earlier one: a reversed or corrected report needs nothing special. To have them sent as they change, add a [production performance feed](#send-movements-to-the-erp-and-certificates-to-a-document-system) on **Configure → Integrations**.

1. Open the job (**Operate → Floor**, then the job).
2. Click **Production performance (B2MML)**. The file downloads, named after the job.

In detail:

- **What it holds.** `ProductionPerformance/ID` and the `ProductionResponse` and `SegmentResponse` IDs are the job number. `ProductionRequestID` is the ERP's key for the order (its `ID` when it came from B2MML); for an order made in Pulaski, the order number. The site is the `HierarchyScope`, the job's operation number the `ProcessSegmentID`, and each line it ran on an `EquipmentActual`.
- **Times and state.** `StartTime` is when its first run started; `EndTime` when it was completed, or when its last run ended if it isn't running. The state is `Ready` before it starts, `Running`, `Held` while paused, `Completed` (completed or closed) or `Aborted` (canceled). Times are UTC.
- **Quantities.** `SegmentData` `GoodQuantity` and `ScrapQuantity` are the job's totals in its unit; reversed reports are left out. Each lot it produced or consumed is a `MaterialActual` (`MaterialUse` `Produced` or `Consumed`) with what still stands of it in the material's base unit; a balance has no `MaterialLotID`, only its `StorageLocation`.
- **For scripts:** `GET /api/jobs/{id}/production-performance` (needs *floor.view* or *integrations.view* at the job's site). Only production jobs have one.

### Print a shop order

1. Find the work order and open its row's **Actions** menu.
2. Choose **Print shop order**. Pulaski downloads one PDF with a traveler sheet for each job.
3. Print the PDF for the floor. Check the schedule before running.

**In detail.** Each sheet identifies the work order, job and process operation; shows the material, planned quantity, line or waiting department, scheduled time, run rate, work-order due date, status and priority, special instructions, operation description and that job's planned BOM components; and carries a Code 128 barcode containing `order-number|OP-number` (for example, `WO-1001|OP-10`). A currently projected schedule is labeled **Projected schedule**; jobs without a live projection show their published baseline. Orders without jobs get a summary page. Under **Specifications**, each sheet lists the material's released [specification](#13-quality-specifications-and-results): its version, each characteristic with its limits and unit, and which ones hold the lot on failure. When the material has none, the sheet says so and asks for the approved specification to be checked elsewhere.

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
- **Bars** are jobs, colored by status (see the legend under the chart). Changeovers between products appear as their own bars, in the changeover style, before the job that needs them. Late and overlapping work is marked too.
- **Line labels** show the code, name and chips: the shift schedule the line follows, such as **2X8** (hover to see where it comes from), **24/7** (no shift schedule, or set to run around the clock) and **Plans for N lines** (a department).

**Resequence or move a job:** drag its bar. A marker shows the slot it will go into: before the job under the pointer, or at the end of the line. Drop it on another line to move it there. Times then recalculate.

**Place an order that isn't on a line yet:** planned orders with quantity still to place are in the **To place** tray on the left. Drag one onto a line. While you drag, each line shows a **drop lane** previewing where the new job would land and which jobs it would push later; a line that can't take it shows why. Dropping creates a job for the unplaced quantity.

**Departments:** a department's row shows each waiting job as a band over its window, with the fastest line's estimate. Each line that could run it shows a faint **ghost** ("~14h here"). Drag the band or a ghost onto a line to assign the job there. Dragging an order from the tray onto a department opens the planning form with a suggested window.

### Calendar view

A month of work by day; a job that spans several days appears on each. The number in each day counts the jobs running that day.

- **Hold a job until a later day:** drag a planned job to that day. It won't start before then.
- Dragging to an earlier day only clears a hold: to run something sooner, change its place in the line on the Gantt.

### Kanban view

Work orders as cards in columns by status: Planned, Dispatched, Firmed, In progress and Completed. Turn on **Closed and canceled** to show those columns too. Click a card to open the order.

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
- **Cancel:** click **Cancel job…**, give a reason, and for a production job optionally tick **Replace it with a job for the quantity not yet made**, then confirm. **Keep it** backs out. For a material made in batches, choose how the replacement is sized: **Round up to whole batches, to meet the order** (the default), **Round down to whole batches; the order ends short**, or **Exactly what is left, even a partial batch** (see [Materials made in batches](#materials-made-in-batches)). The message after canceling says how much the order is short when it rounds down.

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

**Operate → Floor.** Where operators start, pause and complete jobs and report what they make. The schedule says what should run; the floor records what does. Each line shows how many people are on it, and someone set up to clock themselves on sees **Clock on** and **Clock off** on each line (see [Labor](#17-labor-people-and-crews)).

### Ideas to know first

- **Runs.** A job runs in one or more **runs**: starting a job opens a run, pausing or completing it ends the run, and resuming opens the next. Good and scrap are reported into a run, and so are the lots the job uses and makes.
- **Only one run at a time on a line.** Pause or complete what is running before starting the next job.
- **When a job can start.** Its work order must be on the floor (dispatched or firmed). If its order was made from a process, the process links must be met: a Pack operation set to start *after Extrude ends* can't start until every Extrude job of the order is completed, plus any lag. Pulaski never starts a job by itself; it tells you when one can start.
- **The rate check.** A run can't hold more than the line could have made in it: its length × the line's **ideal rate** for the material × the site's tolerance (110% unless a manager changed it on [Site settings](#18-site-settings)). Reporting 1000 m after one hour on a line rated 100 m/h is refused. The check uses the ideal rate from the line's capability, not the planning rate, which is usually lower.
- **The order follows its jobs.** Starting an order's first job firms the order (if it was only dispatched) and puts it **in progress**; completing its last open job **completes** it; reopening a job takes it back to in progress.

### The board

Each line is a card. A running line shows its job with the run's length, good quantity against the plan (with a bar), scrap, and how much more the run can hold at the line's rate. Below it, **Next** lists the line's other open jobs in sequence, each with its status and, when it can't start yet, why (*Waiting for operation 10 Extrude to end*) and when (*Ready at 14:30*). Departments with jobs waiting on them have a card of their own. The board shows changes [within seconds](#screens-that-update-themselves), and refreshes every 15 seconds, while no form is open.

### Use an operator station

An operator station is a touch screen at one line: it shows the line's state, running job and next jobs, and the person working there signs on
with their badge. (Starting, pausing, reporting and coding stops at the station come next.)

1. On **Labor → Terminals**, register a terminal for the station and set **First line offered** to its line.
2. Click **Enrollment code**, open `/terminal/enrol` on the station and type the code, then open `/terminal/station` and leave it there.
3. To sign on, hold your badge to the reader (or type its number and press Enter), and your PIN if the terminal asks for one.
4. Click **Sign off** when you leave. Badging with someone else's badge switches to them.

In detail: the station always uses the [touch layout](#use-a-touch-screen) and shows changes [within seconds](#screens-that-update-themselves)
(and every 30 seconds), never while someone is typing in one of its forms. Someone signed on stays signed on
while they use it; after 15 minutes with nothing done there, it signs them off and waits for a badge. A terminal with no first line offered
asks which of its lines the station is for (`/terminal/station?line=EXT-01` chooses one). Switching the terminal off stops it at once.

**Read the job's instructions at a station.** Below the running job, the station shows its released work instructions, as the job page
does, to anyone there: no badge is needed. While nothing runs, it shows the next job's (*for the next job, J10*), so its setup can be done
before starting it. Tap a folded one to open it, and a file's name to open the file.

**Answer a checklist at a station.** Badge in (setup needs a role that runs jobs, first article one that reports), answer the questions under
the instruction and tap **Save the answers**. The station says whether it passed, or which items failed. A checklist waiting for its second
sign-off can be signed off (**Sign it off**) or rejected with a note at the station too, by someone else badged in who may sign off checklists.

**Scan at a station.** Signed on, the station has a **Scan** box: scan a lot, a container or the barcode on a shop order (a scanner that types
like a keyboard presses Enter itself). It offers what fits at the station's line, as the [Scan page does at a line](#scan-at-a-line-one-scan-one-confirm),
the likely action first: consume the lot into the running job (for what the job still needs; change the quantity if need be), load it at a feed
point or move it to the line, move a container there, or start the shop order's job here. Tap **Confirm**. Only what your role allows is offered;
if nothing fits, it says why. The station works the action out again when you confirm, so a lot that changed in the meantime is refused, not
guessed at: scan it again.

**Run the job at a station.** Signed on with a role that runs jobs:

1. **Start** (or **Resume**) is on the line's next job that is ready, while nothing runs.
2. **Pause** or **Complete** on the running job shows why it stops: tap a reason, or **Pause with no reason** / **Complete with no reason**.
   A reason marked * needs a comment typed first. **Back** returns without stopping.
3. The station says what happened (*Paused. Run 1 of WO-5001-10 ended after 2 h 05 min.*), as the Floor does.

**Report production at a station.** Signed on with a role that reports:

1. Click **Report** on the running job.
2. When the line's machine counts, the station shows what it counted since the last report. Choose a **Scrap reason** if it counted scrap,
   and where **Good output goes to**, then click **Report the machine's count**: exactly the figures on the button are reported, as a report
   from the machine.
3. Otherwise type **Good** and **Scrap** in the job's unit, with a scrap reason when there is scrap and the location, and click **Report these
   figures**.
4. The station says what was reported and which lot the good output went into. On a line that fills lots, the form says which lot it goes into
   at the location shown, and the location defaults to where that lot is.

Only the running job and the line's next jobs can be acted on at a station; a partial batch when stopping, starting a department's job on
a line, reporting a material made in batches, choosing a lot number or what output was made from, and back-dating are done on the Floor for now.

**Give a stop its reason at a station.** When a machine on the line has stopped and nobody has said why, the station asks, at the top of the
screen, even before anyone badges in (*A stop needs its reason · EXT-01-SAW · stopped 4 min ago, still stopped*).

1. Badge in, with a role that codes stops.
2. Click **Give the reason** on the stop, and tap its reason. **Confirm** takes the reason the machine's stop code points to, when there is
   one; a reason marked * needs a comment typed first.
3. The station says the stop is coded and drops it from the list.

In detail: it lists the stops of the line and its machines from the last 12 hours that have no reason, the one still going first, without
micro-stops. Splitting, merging or changing a stop's times is done on **Machines**.

**Call for help at a station.** When the line needs maintenance, quality, materials or a supervisor, call them from the station.

1. Badge in, with a role that calls for help (Operator and Supervisor do).
2. Tap **Call for help**, type a note if it helps (*belt slipping on the puller*), and tap the kind of help.
3. The call shows at the top of the station with how long it has waited, and in red once nobody has come within its type's time.

**Answer a call at a station.** Whoever comes badges in at the line's station (Supervisor and Quality may answer calls):

1. Tap **I'm here**: the station shows that you're on it, and the call stops waiting.
2. When it's done, tap **Resolve**, say what was done, and tap **Resolved**. The call leaves the station.

In detail: the station lists every call of its line that isn't resolved, even before anyone badges in, the longest waiting first. Calling again
for a kind of help that is still waiting doesn't make a second call; the station says it was called already. **Resolve** works on a call nobody
acknowledged too: it counts as answered then. The kinds of help and their times are kept on **Site settings** (see [Andon: calls for
help](#andon-calls-for-help)).

**Request maintenance at a station.** When something needs fixing but not someone right now (a squeal, a loose guard), ask maintenance:

1. Badge in, with a role that requests maintenance (Operator, Supervisor and Maintenance do).
2. Tap **Request maintenance**, under **Something to fix?**, or on one of the stops waiting for a reason, to ask about that stop's machine.
3. Say **What needs looking at** and tap **Request**, or **Request, urgent** for high priority. The station gives its number, such as MR-000042.

In detail: the station lists every request of its line and its machines that isn't closed, even before anyone badges in, with its state
(waiting, or in progress and who is on it). Asking again for the same stop while its request is open doesn't make a second one; the station
says it already has one. Starting and closing requests is done on the **Maintenance** tab of Machines (see [Maintenance requests](#maintenance-requests)).

**Who may do what at a station.** What someone may do there is what their Pulaski user may do at the station's site, as if they had signed
in: an operator's role lets them start, pause and complete jobs, report production and code stops; a planner's doesn't. Link a person to their
user on **People** (their user name). Someone with no user can still clock on and off at the badge terminal, and the station tells them what it
can't do for them. Everything done at a station is recorded under the person's user, with the station named as where it was done.

**When the network drops.** The station, the line board and the badge terminal need their connection to Pulaski: nothing is kept on the
device to send later.

1. If the connection goes, a red bar across the top says **No connection to Pulaski: nothing is saved until it is back**. The screen stays as
   it was (its figures stop moving) instead of turning into the browser's error page.
2. If you tap a button to send something (a report, a stop's reason, a clock-on) while it is down, it isn't sent: the bar says so, and what you
   typed stays in the form.
3. When the connection is back, the bar goes and the screen reloads by itself (a station waits until you have finished typing). Send again
   what wasn't sent.

In detail: the screen checks with Pulaski before every reload and every send, and every five seconds while the bar shows. Machine counts and
states don't depend on it: the edge node keeps them and forwards them once its own link is back, so **From the machine** still offers the
counts afterwards. If the link goes in the moment a send is on its way, the browser may show its own error page; go back and look at the
job, stop or clock before sending again, since it may have arrived.

### Show a line board on a TV

A TV on the floor can show each line's state, running job, output, down time and OEE for the shift, in large type, without anyone signing in.

1. On **Labor → Terminals**, register a terminal for the TV; **Offers the lines of** decides which lines it shows (an area, or one line).
2. Click **Enrollment code**, open `/terminal/enrol` in the TV's browser and type the code.
3. Open `/terminal/board` on the TV (add `?line=EXT-01` to show one line full screen) and leave it running.

In detail: the board reloads [within seconds](#screens-that-update-themselves) of a change at its site, and every 15 seconds, so it recovers by
itself after a network drop (see [When the network drops](#use-an-operator-station)). Its figures are the Overview's: a line that tracks
machine state but has no signal shows **No signal**, and down time counts no-signal time. Color is kept for what needs attention: down, no
signal, waiting, OEE below target, stops to code. Switching the terminal off stops the board at once. A line's calls for help that aren't
resolved show under its name: amber while waiting, quiet once someone is on it, and red, with the line's frame, once escalated. Its
maintenance requests that aren't closed, its machines' too, show under them with a dashed frame: up to three, the waiting ones first, with
**and … more** after that.

### Answer calls for help from the Andon page

**Operate → Andon** lists every call for help at the site that isn't resolved, for supervisors and responders away from the line.

1. Open **Operate → Andon**. Each call shows its line, what help is wanted and the caller's note, who called and from which station, and
   where it stands: *waiting 4 min*, *waiting 12 min: escalated* (red) or *Lee Ong is on it*.
2. Click **I'm on it** when you go to a call.
3. When it's done, click **Resolve**, say what was done, and click **Resolved**.

In detail: the page updates [by itself](#screens-that-update-themselves) as calls are raised and answered, and every 30 seconds (the live pill
says when it last did). Below, **Resolved in the last day** lists the calls
resolved in the last 24 hours, with who resolved them, how long they took to answer and to resolve, and what was done. Seeing the page needs
**floor: view**; answering needs **andon: respond**. Calls are raised at the line's [operator station](#use-an-operator-station).

**Response times.** At the foot of the Andon page, choose **Last 7 days**, **30 days** or **90 days** to see how fast calls were answered:

- one row per line and kind of help: how many calls, how many escalated, how long they took to answer on average and at the longest, how long to
  resolve on average, and how many aren't resolved yet; **All calls** totals them;
- **Longest waits**: the ten calls that waited longest for someone, a call still waiting counting until now.

In detail: a call belongs to the period it was raised in. *Answered* is from the call to **I'm here** / **I'm on it** (or to **Resolve**, when
nobody acknowledged it first); *resolved* is from the call to **Resolved**. A call is counted as escalated when it waited for someone at least
its type's time, whether it has been answered since or not.

### Hand over to the next shift

**Operate → Handover** keeps each line's story from shift to shift: notes on what happened, and open items the next shift must see to.

1. Open **Operate → Handover**. Each line shows its **open items**, then what was written **This shift** and in the **Last shift**.
2. Under **Hand on to the next shift**, write what happened and click **Add a note**, or write what someone must see to and click
   **Add an open item**.
3. When an open item is dealt with, click **Close**, optionally say what was done, and click **Close it**.

In detail:

- An entry belongs to the shift running on its line when it was written (the line's shift schedule); without shifts, to the site's day.
  **Last shift** is the one that ended before the current one; between shifts, the next one to start counts as **This shift**.
- Notes stay with their shift. Open items stay at the top of their line, shift after shift, until someone closes them; nothing is deleted.
- The **Overview** shows *1 open item* (or more) under a line's name, linking here, and the [line boards](#show-a-line-board-on-a-tv) list a
  line's first three open items.
- Writing and closing needs *handover: write* (Operator, Supervisor and Quality have it); anyone who sees the Floor can read it. The page
  updates [by itself](#screens-that-update-themselves) when a note is written or an item closed, and every 30 seconds.

### Use a touch screen

On a tablet or a touch panel at the line, the floor screens switch to a **touch layout**: larger buttons and fields, a number keypad for
quantities, and reasons as buttons instead of a list.

1. On a tablet the touch layout comes on by itself. On a touch panel that works like a mouse (many industrial screens do), click your name at
   the top right, then **Preferences…**, and under **Touch layout** choose **On**. It applies at once.
2. The choice is kept on that device, so a line's panel stays in touch layout and a desk computer stays as it was.
3. Choose **Automatic** (or **Off**) to switch back.

In detail:

- **The floor board.** A line's **Report**, **Pause** and **Complete** fill the card's width; a line with a form open takes the whole board's
  width.
- **Forms.** **Report production** and **Pause** or **Complete** keep their buttons at the foot of the screen while you scroll. Quantity
  boxes open a number keypad on tablets.
- **Reasons as buttons.** Why a job stops, why output was scrapped and why a machine stopped (**Machines**, stops to code) are large buttons,
  grouped as the reason tree groups them; the chosen one is highlighted. With a mouse they stay a dropdown.
- **Everywhere else** buttons, fields and table rows are larger too, and a page's main action comes first among its buttons.

### Start a job

1. Find the job under **Next** on its line's card.
2. Click **Start** (or **Resume** for a paused job). The button is active only when the job can start and the line is free.
3. For a job waiting on a department, choose the **Line** it runs on next to it, then click **Start**.

If the material changes on a line in automatic changeover mode, Pulaski starts a **changeover** job first (named after the job, such as *WO-5002-10-CO*) and says so. Complete the changeover when the line is ready, then start the job. If no changeover is needed, complete it straight away.

### Report production

1. On the running line's card, click **Report**.
2. The **Run** is the open run. Enter the **Good** quantity and the **Scrap** quantity (either can be zero), in the job's unit.
3. For good output, check where it **goes to** (already filled in when the line has a default, or with the location of the lot being
   filled) and, optionally, a **Lot number**; left blank, Pulaski numbers it after the job (*WO-5001-10-01*, *-02*…).
   If the job has used lots, you can also tick under **Made from** the ones this output came from. Leave them blank and Pulaski pairs them by time (see [Trace a lot](#trace-a-lot)).
4. For scrap, choose a **Scrap reason**.
5. Click **Report**. The card updates with the new totals.

If the report is more than the run can hold, Pulaski refuses it and says how much more fits.

**Labels printed by themselves.** When the line has a label printer (see [Production report defaults](#production-report-defaults)), each lot's
label prints there once the lot is closed, and the report says so (*Lot WO-5001-10-01's label printed on BT-1.*). A lot is closed:

- at once, on a line that makes a lot per report (or per batch);
- on a line that fills lots, when a report fills it to its size, when a report finds it past its age or from an earlier shift, when someone
  clicks **Start a new lot**, or when the job is completed (which now also ends the lot it was filling);
- for a partial batch reported as the line pauses or completes, when it is reported.

If the label can't print (the printer is off or unreachable, BarTender refuses it), the report is kept and its note says why; print the label
from the lot. Through BarTender the document is the printer's *Lot labels: produced* one, else its lot or default document.

**Lots filled by reports.** A line can fill one lot over many reports instead of making a lot per report, until the lot is full: at a size,
a number of hours after its first output, or at a change of shift.

1. To set it up, open the material on **Materials** and, under [Where it can run](#where-it-can-run-capabilities-and-rates), click **Change** on
   the line (or **Add line**).
2. Under **Production lots on this line**, fill in any of **Lot size** (in the material's base unit), **Lot age, hours** and **New lot each
   shift**, and click **Save**. The line's row then says *Lots of at most …*.
3. Report as usual, leaving **Lot number** blank: under it the form says which lot the output goes into. Each report adds to the job's lot being filled at that location; when it is full, the rest goes
   into new lots, each up to the size, and the report says which lots it filled and why a new one was started.
4. The job's page shows **Lots being filled**: the rule, each lot, how much it holds and when it started. To start a new lot sooner (a new pallet,
   a quality break), click **Start a new lot**; the next report starts one.

In detail:

- A lot that is full or past its age is no longer listed under **Lots being filled**; at a change of shift it stays listed until the next report starts a new one.
- A lot carries on only while nothing else has happened to it: once it is moved, held, split, packed or used, the next report starts a new one.
  A lot that was full stays full even if a report into it is reversed later.
- A shift is the one on the line's schedule (its production day and shift); with no schedule, or between shifts, it is the site's calendar day.
- A lot number typed by hand, a back-dated report, and a location where lots mix work as before: they don't fill a lot. One report starts at
  most 100 lots.
- Only for a lot-tracked material that isn't made in batches (each batch is already its own lot); the capability refuses it otherwise. The rule
  is part of the capability, so a capability starting later can change it.
- For scripts: `lotSize`, `lotHours` and `lotByShift` on `POST /api/materials/{id}/capabilities` and `PUT /api/capabilities/{id}`, and
  `POST /api/jobs/{id}/new-lot` (needs *production.report*) to start a new lot.

### Report production from the machine

When an [edge node](#20-edge-nodes-collecting-machine-data) collects the line's counters, a report can take its figures from them.

1. Click **Report** on the line's card, or **Report production** on the job's page, and choose the **Run**.
2. Click **From the machine**. Pulaski says what the line's counter counted since the run's last report from the machine (or since the run started), for example *EXT-01-SAW counted 348 good and 12 scrap m since the run started, to 14:40 (2.4 m a count).* Below it, the whole run as the machine counted it and as it has been reported.
3. Click **Use these figures**. Good and scrap are filled in, in the material's base unit, and marked *From the machine*. Check them and change them if the machine miscounted.
4. Choose the scrap reason and where the good output goes, as for any report, and click **Report**.

The job's reports mark it *machine*. The next report from the machine starts after the counts this one took. **Count again** reads the counter again; **Enter by hand instead** clears the figures.

**In detail**

- **What is counted:** the processed count is everything the counter saw, and the defective count the scrapped part of it, so good = processed − defective. Each count is the line's **… per machine count** for the material (one when blank), after the tag's own scale. The counter is the line's [**Production counted at**](#machine-data).
- **When nothing is offered**, Pulaski says why: nothing on the line has a processed counter; several machines count and none is chosen; the line had no capability for the material when the run started, so a count can't be turned into output; or nothing has been counted since the last report from the machine.
- **Never counted twice.** A report from the machine records the time of the last count it took. Pulaski refuses another report from the machine in the same run that doesn't reach past it, so pressing **Report** twice, or two people taking the same counts, reports them once. Reports entered by hand don't use counts up, so don't report the same output by hand as well.
- **Reconciling:** *Run so far* compares the machine's total for the run with everything reported into it (by hand or from the machine). They differ when something was reported by hand, figures were changed before reporting, or counts since the last report haven't been reported yet.
- **Corrections and reversals.** A [corrected report](#correct-a-production-report) keeps the counts the old one took. Reversing the run's latest report from the machine offers its counts again.
- **Materials made in batches** are reported by batch weight, so they don't offer machine figures.

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

`jobs/<job>` shows the job's status, planned, good and scrap quantities, its **runs** (line, start, end, length, good, scrap, the most the run can hold, and why it ended) and its **production reports** (when entered, which run, good, scrap, scrap reason, who and any comment; a reversed report is marked *reversed*). The buttons offer what the job's status allows: **Start** or **Resume**, **Pause**, **Complete**, **Reopen** for a completed job whose order isn't closed, and **Report production**. **Materials and lots** opens the job's inventory page. For a production job, **Production performance (B2MML)** downloads its totals as an ISA-95 production performance for the ERP (see [Production performance for the ERP](#production-performance-for-the-erp)).

**Work instructions at the job.** Under the figures, the job page shows every released [work instruction](#work-instructions) for it: those
shown for its process's operation, for its line (or one the line belongs to) and for its material, in that order. Each gives its code, title
and version, and what it is shown for (*version 2 · for its operation, material*). A lone instruction is open; with more than one, each
starts folded: click its title to open it. Its files open by name.
The job always shows the version released now: a new release shows at once, and a draft never does. Seeing them needs the Floor or *See work
instructions* at the job's site.

**Answer a checklist at the job.** An instruction with a checklist shows it under its text, with how it stands: *To do before the job starts
on this line*, *Passed … by …* (with the answers), or *Failed on item 2*.

1. Answer each question (those marked * are required) and click **Save the answers**.
2. If every answer is within what its question allows, the checklist **passed**. If one isn't (a *No* where *Yes* is expected, a number
   outside its limits), it **failed**: the answers are kept, and the job needs it answered again once the problem is put right.

In detail:

- **Start waits for setup.** Until every setup checklist for the job on its line has passed, **Start** is refused: *Complete the setup
  checklist of WI-EXT-001 for J10 on this line first.* A department's job learns its line only as it starts, so its setup checklist is asked
  for at its first report instead.
- **Reporting waits for both.** A production report is refused until the setup and first-article checklists have passed on the run's line.
  Correcting or reversing a report, and the partial batch of a stop, aren't held up.
- **Once per job and line.** Pausing and resuming on the same line needs neither again; moving the job to another line needs both there.
- **Kept with the run.** Answers given during a run belong to it; setup answered before the start belongs to the run that start opens.
- **Who answers:** setup, whoever may start jobs (Operator, Supervisor); first article, whoever may report production. The person answering
  signs it: Pulaski records their user and the time.

**Give a checklist its second sign-off.** A checklist that *needs a second sign-off* says, once passed, *Answered … by …; waiting for a second
sign-off*, and the job waits as if it weren't answered. It is in the [Inbox](#your-inbox) of everyone who may sign off checklists at the site.

1. Open the job's page (from the Inbox, or the Floor) and read the answers under the checklist.
2. Click **Sign it off**, or type a **Note** saying what is wrong and click **Reject**.
3. Signed off, the checklist counts. Rejected, it doesn't: the job needs it answered again, and the new answers wait for a sign-off in turn.

In detail: someone other than who answered it signs it off. Only a passed checklist waits for a sign-off; a failed one is simply answered
again. The **Checklists answered** table on the job's page keeps every checklist answered for the job, latest first: its run (or *Before a run*
until the run starts) and line, the instruction and version, the outcome (*Passed*, *Failed*, *Waiting for sign-off*, *Signed off*,
*Rejected*), who answered and when, and who signed or rejected it and why; **Answers** shows each answer, failed ones in red. Signing is a
record of who did what, not an electronic signature (Pulaski has no 21 CFR Part 11 features).

### Reverse a production report

A report entered on the wrong job, or with the wrong quantity, is reversed whole and then reported again correctly.

1. Open the job's page and find the report under **Production reports**.
2. Click **Reverse…** on its row.
3. Choose an adjustment **Reason**, write a **Comment** saying why, and click **Reverse report**.
4. The report is marked *reversed*. Its good and scrap come off the run, and every lot it made gives its quantity back (a lot that only held that is *canceled*).

**In detail**

- **Nothing is deleted.** Each of the report's counts is offset by a correction at the same time, so production by shift and OEE see it as never made; the lots get reversal movements, and the report stays in the list.
- **Its lots must still hold what it gave them.** If one has been consumed, split, shipped or written down since, Pulaski names what happened so that is reversed first. Output that went straight into a silo's balance is taken out of the balance, which must still hold it.
- **Partial batches.** A partial batch the report made no longer counts, so the run can report another. A rejected partial batch was written off: reverse that first.
- **Until the job is closed**, as with reporting. Needs *production.correct* (Supervisor and Auditor to start with).
- Reports entered before this version can't be reversed whole; they show no **Reverse…** button.

### Correct a production report

A report with the wrong good or scrap quantity, or the wrong scrap reason, is corrected in one step.

1. Open the job's page and find the report under **Production reports**.
2. Click **Correct…** on its row. The form shows its figures: change **Good** (for a material made in batches, type each good batch's weight, separated by commas), **Scrap** and its **Scrap reason**.
3. Choose an adjustment **Reason**, write a **Comment** saying why, and click **Correct report**.
4. The old report is marked *reversed* and a new one with the right figures takes its place, in the same run and at the same time. The page says how many lots gave their output back and how many new lots were made.

**In detail**

- **It is a reversal and a new report in one.** Everything either would check is checked, and if any of it fails nothing changes: the old report's lots must still hold what it gave them, and the new figures must fit the run (its rate cap, whole batches, the partial-batch rules). Automated consumption from the line's feed points is put back and taken again for the new figures.
- **New lots.** The good output goes into new lots with new numbers, where the old lots were; print their labels again. The old lots are *canceled*.
- **What can't change:** the run and the time it counts at. To move output to another job or run, reverse it and report it there. A report older than 31 days can't be corrected, as it can't be back-dated.
- **History** shows both reports, and one *production corrected* event names them both with the figures before and after. Needs *production.correct*.

### Production by shift

What each shift made, for a supervisor's shift report or the morning meeting.

1. On **Operate → Floor**, click **Production by shift**.
2. Choose **From** and **to** (production days, up to 93) and click **Show**. It opens on the last seven days.
3. Each shift is a block, headed by its day, name, code, schedule, times and crew, with its good and scrap per unit. Below it, one row per line and job: work order, material, good, scrap, unit and how many reports it adds up.
4. **Against the jobs' runs** lists each job's good and scrap in these days next to the totals on its job page. *Matches* means every report of the job is in these days; otherwise part of it was reported on other days.
5. **Export CSV** saves every row, with the shift's start and end.

**In detail**

- **Which shift a report counts in:** the shift running on its line when the report was made, from the [shift schedule](#16-shift-schedules) the line follows with its one-off changes. A report into a run that had already ended counts when the run ended.
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
- **How close a batch must be.** Under the **near** rule a batch may differ from the batch size by the tolerance (2% by default: 49 to 51 kg for 50 kg), so 49.3 kg is a batch and 40 kg isn't. A line can have a tolerance of its own (on its capability), which its reports use instead. Under the **exact** rule, the default for finished goods, every batch is exactly the batch size, on every line.
- **The order form** says the batch size under **Planned quantity**; when the chosen process puts the order on lines with sizes of their own, it lists each operation's lines with their size and share (*op 10: MIX-1 200 kg (60%), MIX-2 50 kg (40%)*), so you can pick a quantity that shares into them exactly.

**A partial batch (the line went down)**

A batch cut short is an exception, so it is reported only as the line stops:

1. Click **Pause** (or **Complete**, if the job ends there).
2. Choose **why** it stopped: a partial batch needs a reason.
3. Tick **A batch was in progress**, enter its **Quantity** and where it **Goes to**.
4. Click **Pause** or **Complete**.

The partial batch goes into a lot of its own. Under the near rule it is **accepted**: it will be used. Under the exact rule it goes **on hold until approved**, because a finished good can't be sold as a partial; the floor shows *A partial batch waits for approval*. Each run can have one partial batch. If you forgot it, open the job's page and report it into that run: the form offers **Partial batch** for a run that ended with a reason.

**A replacement planned with a partial batch.** When a job is canceled and replaced with exactly what is left (*Exactly what is left, even a partial batch* on the schedule), the replacement is whole batches and a partial last batch: 75 kg left of 50 kg batches is one batch and a partial batch of 25 kg. The floor reports its last batch under **Partial batch** on the report form as it is made, without stopping the line; it then follows the rule above (accepted, or on hold until approved). Its quantity needn't be whole batches on any line, so it can move between lines of different sizes. Planning one needs *schedule.plan_partial* (Planner to start with); rounding up or down needs only *schedule.plan*.

**A replacement rounded down.** *Round down to whole batches* plans only the whole batches that fit in what is left, of the line's size: 125 kg left of 50 kg batches is a replacement of 100 kg, and the order ends 25 kg short. With less than a batch left there is nothing to replace, and Pulaski says so: replace exactly what is left, round up, or cancel without a replacement.

**Approve or reject it** on the job's page, under **Partial batches**: **Approve** releases the lot; **Reject** asks for a scrap reason, writes the lot off and moves its quantity from good to scrap. Both record who decided and when. Only a Supervisor or Quality user at the site can decide, and never the person who reported it.

### In detail

- **Statuses.** A production job starts from *dispatched* or *firmed* (starting firms a dispatched order with all its jobs, since execution freezes the plan), and resumes from *paused*. Non-production jobs (cleaning, maintenance, trial, changeover) also start from *planned*. A completed job can be reopened to *paused* until its order is closed; closing the order closes its completed jobs.
- **The order's status** can't be set to in progress or completed by hand any more: it follows its jobs. Closing a completed order is still done on the Work orders screen.
- **The rate check** uses the capability in effect when the run started, converted into the job's unit, and counts good plus scrap across the whole run, including late reports. If the line had no capability for the material, or its rate unit doesn't convert, the report is recorded with a warning that it wasn't checked. The tolerance is set per site, between 100% and 1000%, through `PUT /api/execution/sites/<site>/policy`; there is no screen for it yet.
- **When a report counts.** A report into the open run counts at the moment it is entered; one into an ended run counts at the end of that run. Both keep the time they were entered.
- **Consumption** needs a run too: the job's open run, or the run chosen (see [Record what a job used and made](#record-what-a-job-used-and-made-the-jobs-tab)). Using more of a planned component than the plan allows is recorded with a warning. The plan's quantity is scaled up when the job makes more than planned, and gets the site's consumption tolerance (also 110% by default).
- **The changeover check** compares the job's material with the last job that ran on the line. If that was a production job of another material, a changeover job is started first. In *scheduler* mode, and on a line that hasn't run anything yet, no changeover is added.
- **Process links.** *After start* is met once any job of the upstream operation has started; *after end* once all of them are completed. Each is plus its lag. Canceled upstream jobs don't count.
- **Events:** `pulaski.job.started.v1`, `…paused.v1`, `…completed.v1`, `…reopened.v1` and `…production_reported.v1`; the counts are kept in `ts.production_count`.
- **Batches in detail.** Batch weights are in the material's base unit. A partial batch is more than 0 and less than a batch (under the near rule, less than the size minus the tolerance). The rate check counts it like any output. Under the exact rule a partial batch needs a location that keeps lots apart (not a silo or tank where lots mix). The lot is held with the reason *Partial batch awaiting approval*. When a job of a batch material is canceled and replaced, what is left is rounded **up** to whole batches. Partial batches are recorded as `pulaski.partial_batch.reported.v1`, `…approved.v1` and `…rejected.v1`.
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

**As it was.** Under **As it was**, choose a date and time and click **Show** to see the lot then: its quantity, where it was (and in which container), and its status. A lot that didn't exist yet says so. It is worked out from the history in the order movements count, so back-dated movements are in their place and it agrees with the journal.

### Reverse a movement or a declaration

Something recorded wrongly is put right by reversing it, then recording what really happened. A reversal is a new movement that undoes the old one; the old one stays in the history, marked as reversed, with who reversed it, the reason and the comment.

1. Open the lot's page and find the movement in its **History**.
2. Click **Reverse** on its row. Rows that can't be reversed (a reversal, or a movement reversed already) have no button.
3. Read what the reversal will do, choose an adjustment **Reason** and write a **Comment** saying why. Both are required.
4. Click **Reverse**. If something since depends on it, Pulaski says what (for example *Since then: consumed into WO-1001-10*) so you can reverse that first, or undo it all at once (below).
5. If an amount was wrong rather than the whole movement, record the right amount again as usual.

**Undo a movement with everything after it.** Tick **With everything after it that depends on it**, choose the reason and comment, and click **Check**: the drawer lists every movement it would reverse, latest first (this lot's later movements, and those of lots split off it since). Nothing is done yet. If one of them can't be reversed, it says which and why. Click **Undo N movements** to do them all in one go; if anything changed in between, it asks you to check again.

What can be reversed on a lot's page:

| Movement | What the reversal does | When it can't |
| --- | --- | --- |
| A receipt, write-up, write-off or shipment | Puts back the quantity at the time it counted; a reversed receipt of a whole lot makes it *canceled*, a reversed shipment of all of it makes it available again. | The lot's stock would go below zero at some time since, or a posted count came after it; a whole shipment, once the lot has changed status again. |
| A **move** | The lot goes back to where it was. | The lot has moved since (reverse that move first), or it moved with its container (move the container back). |
| A **hold or release** | The lot goes back to the status it had. | Its status has changed again since. |
| **Packing** or **unpacking** | The lot comes out of the container (back into any it was in before, and back where it was if packing moved it), or goes back into it. | The lot has moved or changed container since, or the container isn't where the lot is any more. |
| A **correction** | The lot number, supplier lot and expiry go back to what they were. | They have been corrected again since, the old number is taken by another lot, or the correction was made before Pulaski kept what it replaced. |
| **Consumption** into a job | The quantity goes back to the lot; it stops being a parent of what the job made unless more of it went into that job. | The job is closed or canceled, or the lot was shipped since. |
| A **split** | The new lot is canceled and its quantity goes back to the lot it came from. | Anything has happened to the new lot since. |
| A **merge** | The lot that was merged gets its quantity and status back; the lot it joined goes down by that much. A lot that joined a balance where lots mix comes back out as itself, and the balance gives its quantity back. | The lot it joined no longer holds that much at some time since. |
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
- **Nothing is deleted.** A certificate is withdrawn, never removed, and a stored file can't be changed afterwards (the database refuses it). Pulaski keeps a fingerprint (SHA-256) of each file. A file is recognized by its contents, so a renamed file of another kind is refused.
- **Split and merge.** A lot split off takes its parent's certificates and attribute values with it. A merged lot, or a balance a lot joins where lots mix, takes the certificates of every lot that went into it, marked *Carried from …*; its own attribute values stay as they are.
- **An attribute no longer offered** for the material (switched off, or its classes changed) still shows a value already recorded, which can only be cleared.
- **Concurrency.** If someone else changed the same value since you opened the page, you'll see *"changed by someone else"*: reload and try again.
- **Audit.** Each change is an event: `pulaski.lot.attribute_set.v1` (with the value before and after), `pulaski.lot_document.attached.v1` and `…withdrawn.v1`.

### Issue a certificate of analysis

When the lot's material has a released [specification](#write-a-specification), Pulaski writes the lot's certificate of analysis from the
results recorded.

1. On the lot's page, under **Certificate of analysis**, click **Prepare**. It shows one line per item of the specification: the requirement,
   the results (for a measured one, their mean and range; for an inspected one, defects in units inspected) and **Pass**, **Fail**, **No
   result** or **Not judged**. Anything to know first is listed above it: an item out of specification or with no result, a lot on hold.
2. Optionally type the **Customer order or shipment** to print on it. **Download a preview** gives the PDF as it would be.
3. Click **Issue and attach**. The PDF is attached under **Attributes and certificates** as a certificate of analysis, numbered
   *COA-{lot}-{date and time}*, and goes out through a [documents feed](#send-movements-to-the-erp-and-certificates-to-a-document-system)
   like any certificate.

When shipping a lot, tick **Issue its certificate of analysis** to issue it with the shipment's number in one step.

In detail:

- **Whose results:** the lot's own, not left out. For an item the lot has none of, those recorded against the job that made it with no lot
  named (marked *from its job*). Results of the lots it was split or merged from aren't used.
- **Nothing is hidden or refused:** a failed item says **FAIL** on the certificate, a missing one **NO RESULT**, and every note is printed
  above the signature line. Issuing needs permission to record lot attributes and certificates; looking at it, to see inventory or quality.
- A certificate is a snapshot: issuing it again after more results makes a new one; withdraw the old one if it was sent by mistake. It is
  printed in English.

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
3. The **suggested lots** cover what is still needed: the one that expires first first, then by the site's [preferences](#lot-allocations), then the oldest. Each says why (*Expires 3 Nov 2026, the earliest; received 12 Oct 2026; 40 kg free*). When the component's own lots aren't enough, lots of its **approved alternates** (from the BOM line) follow, marked *an approved alternate*, in their own unit.
4. Click **Allocate** on a suggestion, or **Allocate all suggested** for every component.
5. **Release** gives an allocation back. **Refresh** puts the job's allocations right after stock or the schedule has changed (see below).
6. On the **Shortages** tab, **Refresh allocations** does the same for every open job of the site, the job needed soonest first.
7. **Picking list** (on the job's panel, or on the **Shortages** tab for every open job, narrowed by the equipment chosen there) downloads a PDF: each lot to fetch, where it is and in which container, how much, for which job and line, and when it is needed, nearest location first, with a box to tick.

**In detail**

- **What is suggested.** Only available lots of the component at the job's site, with stock not set aside for other jobs. Under **lots not suggested**, each other lot says why: on hold or in quarantine, expires before the job needs it, all allocated to other jobs, or not needed because the lots before it cover the need. *When the job needs it* is its planned start, or now once that has passed or it is running.
- **What allocated means.** The stock stays where it is and can still be moved. Other jobs' suggestions and the **Shortages** tab treat it as taken: it covers its own job first. Consuming the lot into its job uses the allocation up. Consuming it into **another** job is allowed, with a warning naming the job it was allocated to; refresh that job afterwards.
- **When an allocation goes wrong.** If the lot is put on hold, would expire before the job needs it, or no longer holds what is allocated (written off, shipped, consumed elsewhere), the allocation shows why in amber. Nothing changes until someone clicks **Refresh**, or, at a site that [keeps allocations current](#lot-allocations), until the background refresh runs a minute or so after things settle. Refresh then releases what can't be used, trims what a lot can only partly give, and allocates what is still needed from the suggestions.
- **Approved alternates.** An alternate's lot is allocated in its own unit and covers the component at the BOM line's ratio: with a ratio of 1.25, 125 kg of it covers 100 kg of the component. The allocation says so under the lot. The **Shortages** tab counts it against the component's need, and takes it out of the alternate's own stock. A material that isn't the component or one of its approved alternates can't be allocated to the job.
- **Hard reservation.** At a site that has it on, consuming stock allocated to another job is refused, naming that job: release or move its allocation first. Without it, the consumption goes through with a warning.
- **Picking lists** leave out lots already at the job's line (or below it, such as its feed points), and list only what is still allocated, at most what the lot holds. The times are site time.
- One allocation per lot and job: allocating more of the same lot adds to it. Only a job's planned components and their approved alternates are allocated, in the lot's base unit, and not to a job that is completed, closed or canceled.
- **Who.** Allocating, releasing and refreshing need *lot.allocate* (Planner, Supervisor and Quality to start with). Anyone who can see inventory sees allocations and suggestions, and a lot's page lists the jobs it is allocated to. Each change is recorded as an event.

### Trace a lot

On a lot's page, **Genealogy** first shows the lots it was **made from** and the lots made from it (**used in**). Click **Trace back** to see everything it was made from, all the way up, or **Trace forward** to see everything made from it, all the way down. Each lot is indented under the one it was reached from; click any lot to open it.

**Lots a job used and made.** A job records every lot it used as a parent of every lot it made. A trace keeps only the pairs that fit by time. A lot the job used counts as in use from when its consumption was declared until the next lot of that material, so a made lot pairs with:

- the lot that was in use when the job's previous output was declared;
- every lot of that material declared since.

Each kept lot carries a chip (point at it for why):

- **Exact:** the job used only one lot of that material.
- **Estimated:** paired by time.
- **Declared:** someone picked it as what the lot was made from.
- **Possible:** it can't be paired by time, so its job link is kept anyway. Either it was declared after everything the job made, or the lot was made before any lot of that material was declared.

Pairs that don't fit are left out of traces. In the **Made from** and **Used in** lists they stay, marked **Not paired**.

**Pick what a lot was made from.** When you know better than the times, name the lots yourself:

1. Open a lot the job made. Under **Made from**, click **Pick what it was made from…** (you need permission to report production).
2. Tick the lots it was made from. The list holds the lots its job used.
3. Optionally add a comment, then click **Save**.

The ticked lots replace the pairing by time for that lot, so tick every material it came from. Untick them all and save to go back to pairing by time. You can also tick them when [reporting production](#report-production).

**Through a silo or tank.** Where lots mix, a trace keeps only the lots that, by how the location [flows](#how-a-silo-flows), went into the withdrawals that matter: back from a lot a job made, the arrivals that fed that job's withdrawals; forward from an arrival, what was made from withdrawals it fed. Each such lot carries a chip saying how sure that is (point at it for why):

- **Confirmed:** exact, and the silo's level backs it: it read empty before the arrival, or it agreed with the declared stock.
- **Exact:** only one arrival could have been in the silo then.
- **Estimated:** worked out from the declared quantities by the flow model; likely, not measured.
- **Possible:** the declared movements don't settle it. For example, more was taken than was put in (often after a count correction), material would have left before the minimum residence time, or the level disagreed with the declared stock.

A lot reached both through a silo and through a job's link takes the less certain of the two chips.

A balance's own page shows **Where what was taken came from**: every withdrawal with the lots it came from, their quantities and how sure each is.

#### How a silo flows

On **Inventory → Locations**, a location where lots mix shows how it flows; click **Change** (needs permission to maintain equipment):

- **Mixed** (the default): everything blends, as in a tank or a mixed silo. A withdrawal takes a share of every arrival still there, by quantity.
- **First in, first out:** plug flow, as in a mass-flow silo or bin. A withdrawal takes the oldest material first.
- **Minimum residence** (optional, in minutes): the least time material stays before it can leave. Material taken before then is only *possible*.
- **Empty at level** (optional): for a silo with a [level tag](#20-edge-nodes-collecting-machine-data), in the tag's unit. A reading at or below this means the silo was empty.
- **Level tolerance** (optional): in the tag's unit, how far the level may be from the declared stock and still agree. Leave it blank to not compare them.

Everything is worked out from the movements as recorded, by when they count, so [back-dated](#back-date-a-movement) ones fit in. Changing these settings changes every trace through the silo at once, because nothing is stored.

**Level readings.** With a level tag and these settings, the estimate is checked against what was measured:

- **Empty.** When the level reads empty, whatever the declared movements still had in the silo is set aside: it was really gone. The withdrawal after that says so. A withdrawal that can only have come from one arrival since then is *confirmed*. An arrival recorded after the last reading above empty isn't set aside, so a delivery the level hasn't caught up with yet isn't lost.
- **Stock.** With a tolerance, the last good level reading in the 15 minutes before each withdrawal is compared with the declared stock then. If they agree, an exact withdrawal becomes *confirmed*. If they don't, it becomes *possible*. This needs the tag's unit to convert to the material's unit (kg to kg, or with a [conversion](#6-units-and-conversions)). If it doesn't convert, only empty readings are used.

Readings are kept for 90 days by default, so older withdrawals rest on the declared movements alone. The level is taken as this balance's, so the comparison only makes sense when the silo holds one material.

### Check for shortages

The **Shortages** tab answers *"will we have the material for the jobs coming up?"*.

1. Open **Inventory → Shortages**. It lists every component the site's open jobs still need, short ones first.
2. Narrow it to a line or department (and everything under it) with the equipment picker, or to jobs needed soon: the next 24 hours, 3, 7 or 14 days.
3. Choose the timing: **Jobs as the schedule has them now** (the default, as the Schedule screen shows them) or **Jobs at their published times**.
4. Clear **Short only** to see covered components too.
5. Click a component to see each job that needs it, in the order they are needed: what it still needs, how much comes **from stock** and how much **incoming**, whether it is **covered** or how much is short, and when it is needed (*as scheduled now*, *published start*, or *hold-until or window*).
6. Click a job to open it, or **stock** to open the component.

Each row shows **Needed** (what the jobs still need: planned less consumed), **Available**, **Held** (on hold or quarantine, in amber: releasing it might cover the gap), **Incoming** (what open [expected deliveries](#incoming-deliveries) still have to bring, with the next one's time, in red when late), **Short** (in red), and the **first short** job with when it is needed. **Can't arrive in time** (amber) marks a component whose shortage is needed sooner than its lead time from now; the job's line says *within lead time*.

**In detail**

- **Order.** Jobs take supply in the order they are needed, then job number; jobs with no time come last. With live timing a job's time is where the schedule projects it now, so a job that has slipped or moved up is ordered by its new time (point at *as scheduled now* to see its published start). Jobs planned to a department, and jobs not on the schedule, keep their published start or hold-until.
- With a horizon, jobs needed after it, and jobs with no time at all, are left out; the horizon uses the same timing.
- **Allocations.** Stock [allocated](#allocate-lots-to-a-job) to a job covers that job first and isn't free for the others.
- **Supply.** Each job takes **available** stock first, then incoming deliveries due by the time it needs them. A delivery due after a job needs the material doesn't cover that job, however much arrives. A job already under way needs what it has left from now, and a late delivery counts as arriving now. Held stock isn't counted.
- **Lead times.** A material can carry a **Lead time** in days (on its page). A job short of it and needed before now plus that time is flagged: supply asked for today wouldn't arrive in time, so it needs something else (another source, a different plan). A job with no time isn't flagged.
- **Not purchasing.** Nothing is suggested, ordered or rescheduled: deliveries are only what the ERP or a person said is coming, and ordering stays in the ERP.
- A component planned in a unit that doesn't convert to its base unit is left out, with an amber note naming the job.

### Incoming deliveries

The **Incoming** tab lists purchases and transfers expected to arrive, for the shortage check. Pulaski doesn't order anything; the ERP sends them, or you enter them.

1. Open **Inventory → Incoming**. Open deliveries are listed by when they are expected; late ones in red.
2. **Expect a delivery:** choose the **Material**, enter the **Quantity** and **Unit**, when it is **Expected**, and optionally the **Reference** (PO, transfer or ASN number), **Supplier** and a **Note**. Click **Add delivery**.
3. **Change** an open one when the date or quantity changes, or **Cancel** it if it won't come.
4. When it arrives, **Receive** the lot as usual and choose it under **Against expected receipt** (the quantity still to come is filled in). What arrived counts toward it: once all of it has arrived it closes. Until then it stays open, *partly received*, showing what arrived and what is still to come, and the shortage check counts only the rest.
5. When nothing more of a partly received delivery is coming, click **Close as complete**. (**Cancel** is only for one where nothing arrived.)
6. Tick **Show received and canceled** to see closed ones.

**Receive lots from a B2MML file**

An ERP or a supplier's system can send the lots that arrived as B2MML material lots.

1. On **Inventory → Incoming**, under **Receive lots from a B2MML file**, click **Download a template** to see the format, or **Choose File** and pick the file.
2. Pulaski checks it and says how many lots it would receive, update or leave unchanged, and lists any errors.
3. When there are none, click **Import**. As with every import, it is all or nothing.

- **A lot the site doesn't have** (by lot number and material) is received: its `Quantity` and `UnitOfMeasure` at the location whose code is its `StorageLocation`, expiring at its `ExpirationTime` (a date alone is midnight UTC), with a `SupplierLot` property as its supplier lot. The receive rules apply as on screen. Where lots mix, it joins the location's balance, and its properties aren't recorded.
- **A lot the site has** keeps its quantity, location and expiry; the file only adds its properties.
- **Properties.** A `MaterialLotProperty` whose `ID` is a [lot attribute](#lot-attributes-and-certificates) code, in any case, sets that attribute, read as on the lot page; an empty value leaves it alone. Other properties are listed as left out.
- Needs permission to receive lots. For scripts: `POST /api/lots/import/b2mml?siteId=…&dryRun=true`, and `GET /api/lots/import/b2mml/template`. At most 5 MB and 5,000 lots.

**In detail**

- **Who:** anyone who can see inventory sees them; adding, changing and canceling needs permission to receive lots.
- **From the ERP:** `POST /api/inventory/expected-receipts/import` takes one delivery with the ERP's name and its key there, the site, the material and unit codes, the quantity and expected time, the reference and supplier, and whether it is canceled. Sending the same one again changes nothing; a change updates it while it is open; once received or canceled in Pulaski it stays so. It is listed with the ERP's name beside the reference.
- The unit must convert to the material's base unit, so the shortage check can count it. What arrives is counted in the delivery's own unit, so 400 kg against a delivery expected in grams counts 400 000 g.
- **Partly received.** Its unit can't change, and its quantity can only be changed to more than has arrived. From the ERP, a quantity no more than what arrived, or a cancellation, closes it as complete. More than expected closes it too. Each lot received against it is recorded; the row shows the latest and how many more.
- **Events:** `pulaski.expected_receipt.created.v1`, `…updated.v1`, `…canceled.v1` and `…received.v1`.

### The movement journal

The **Journal** tab lists every movement in the site, newest first: receipts, write-ups, write-offs, moves, holds, consumption and production.

1. Open **Inventory → Journal**. It shows the last 7 days.
2. Change the **From** and **to** dates (whole days at the site; at most a year), or filter by movement, material, location, lot or supplier lot number, and **Who** (part of the person's name).
3. Click **Search**. Click **Show older** at the bottom for the next 200.
4. Click a lot, a material or a job to open it.

A material's and a job's pages have a **Movements** button that opens the journal filtered to them.

A movement's reason shows the groups it is filed under after it (*Damaged · Handling › Forklift*); the export has them in a **Reason group** column.

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

When a movement is entered late, it can count from when it really happened: a receipt, consumption, production, write-up or write-off, and a move, hold, release, split, merge or shipment.

1. Open the movement as usual (**Receive**, **Consume**, **Write up**, **Write off**, **Move**, **Hold**, **Release**, **Split**, **Merge**, **Ship**, or production on the Jobs tab).
2. Fill in **Counts from** with the date and time it happened, in the site's time zone. The field shows only if you may back-date.
3. Write a **comment** saying why it is late (required), and save.

**In detail**

- **Who:** back-dating needs permission to back-date inventory movements (the Quality and Auditor roles have it), as well as permission for the movement itself.
- **Limits:** at most 31 days back, and never in the future. A movement can't count before the lot had any stock, before a posted stock count that counted the lot, or so that the lot would have been below zero at any time since. The message says when it would have gone short.
- **Runs.** Back-dated consumption and production go into the run of the job that was going on then (or the run you choose, which must have been going on then); if the job wasn't running then, it is refused. Back-dated production counts in the shift it happened in, for production by shift and OEE.
- **Expiry** is checked when the movement counts: a lot that was in date then can be consumed then, even if it has expired since. A back-dated receipt's or production's expiry runs from when it counts.
- **Both times are kept.** The lot's history lists a back-dated movement at the time it counts, with *recorded …* beneath it; the journal lists it when it was recorded, with *counts from …*, and its export has both. Genealogy links made by back-dated consumption or production show *since …* with *(back-dated)*. The event log records it now, with `EffectiveAt` in its data.
- **Reversing** a back-dated movement undoes it at the time it counted, so every past date comes out as if it had never happened.
- **Moves, holds and releases** can count from earlier only as the lot's latest movement: nothing else may count after them, since what happened later says where the lot was and what status it had (Pulaski names the movement in the way). A move to where lots mix can't be back-dated, because the lot joins the balance as it is now.
- **Splits and shipments** can count from earlier only if the lot's status and location haven't changed since, and within the stock it had then; shipping all of a lot ends it, so then it must be its latest movement too. A split's new lot exists from then.
- **Merges** can count from earlier only if each lot taken in has nothing counting after it, and the lot they go into hasn't changed status or location since.
- **Counts** aren't back-dated by hand: each difference a count posts counts from when that lot was counted, since it hasn't changed since (the count refuses a lot that has).
- **Corrections, packing and reversals** always count when they are recorded (a reversal at the time of what it undoes).

### Reports and exports

**Inventory → Reports** has two reports:

- **Stock on a date:** each material's on-hand quantity at the end of the day you choose, **where** it was then (by location) and how much of it was **on hold** or in quarantine then. Each lot is worked out as it was, from every movement that counts by then, including [back-dated](#back-date-a-movement) ones entered later, so a past date can change after a correction, gives the same answer whatever order movements were entered in, and agrees with the journal. Older dates take longer. The export has a row per material and location.
- **Aging:** on-hand stock by how long ago each lot was received or made (from when its first movement counts, so a back-dated receipt ages from its real day): up to 30 days, 31–90, 91–180 and over 180 (in amber). Balances of materials that aren't lot-tracked aren't aged, and a split lot counts from the day it was split.

**Export CSV** downloads a spreadsheet file from the **Stock** tab (what is on screen, with its filters), the **Lots** tab (every matching lot, up to 10,000, with a column per active lot attribute), the **Journal** (every matching movement, up to 10,000) and both reports. Lot search shows 200 at a time: click **Show more** for the next.

### Reason codes

**Configure → Reasons** lists the reasons people choose, one tab per kind: inventory adjustments, holds, scrap, run ends, **utilization** (what a machine's time counts as), and kinds later screens will use.

1. To add one, type a **code** (letters, digits, `-`, `_` or `.`), a **name**, choose **Every site** or one site, tick **Needs a comment** if people must explain, and click **Add**.
2. To change one, edit its name, comment setting, order or **Active** box, then click **Save** on its row (**Undo** throws the change away).
3. A code and its kind can't change once created, because history refers to them. To stop using a reason, untick **Active**.
4. A **utilization** reason also has a **Class**: *Runtime* (producing or running slow), *Downtime* (an unplanned stop, counted against availability) or *Planned (not in OEE)* (setup, teardown, cleaning, planned maintenance, breaks). Choose it when you add the reason; you can change it later on its row and **Save**.
5. To keep a long list tidy, file reasons under **groups**. Add a group by ticking **Group** before you click **Add**: it holds reasons (and other groups) but is never chosen itself. Choose **Under** to add a reason or group straight into a group.
6. To move a reason or group, choose another group in its row's **Under** list (or **At the top**) and click **Save**. Everything inside a group moves with it.
7. To turn a reason into a group, click **Make group** on its row. To turn an empty group into a reason, click **Make reason**; on the utilization tab, choose its **Class** first, then click **Make reason** again (**Cancel** leaves it a group).

**In detail**

- **Where people see it.** Wherever a reason is chosen (writing stock up or off, holds, scrap, ending a run, indirect labor, coding a machine stop), it is listed with its groups, such as *Mechanical › Jam*, in the order of the tree. Groups aren't offered.
- **History doesn't change.** Movements, stops, reports and labor keep pointing at the reason itself. Moving it to another group, or renaming a group, changes nothing they recorded; they go on showing the reason's code and name.
- **Reason or group.** Only a reason that has never been recorded anywhere (a movement, a stop, a profile's mapping, a labor session…) can become a group, so what history recorded keeps its meaning; Pulaski says where it was used. To file a used reason under a heading, add a new group and move the reason into it. A group becomes a reason only once it is empty: move what it holds out first. A reason that becomes a group stops needing a comment and loses its class, and is no longer offered.
- **The rules.** A reason goes only under a group of its own kind, never inside itself, and at most five levels deep. A reason for every site goes only under a group for every site; a site's reason goes under a group for every site or for that site. The **Under** list offers only groups that fit.
- **Switching off.** A group can be switched off only when everything in it is off. Nothing can be switched on, or moved, into a group that is off.
- **Order** sorts a reason among the others in the same group; the tree is shown group by group.
- A group has no class and never needs a comment: only the reasons in it are chosen.
- Each move is recorded as an event (`pulaski.reason_code.moved.v1`) with where it was and where it went.

### Locations

The **Locations** tab lists every piece of equipment that holds inventory, with how many lots and materials are there. **Stock** and **Lots** open those lists for that location, and **Label** prints a label to stick at the location, so moving stock there is one scan. To add a location, add equipment (for example a storage zone) on the [Equipment](#4-equipment) page and tick **Holds inventory**.

### Containers

Pallets, totes, boxes and bins that hold lots, and other containers (boxes on a pallet). Each lot inside keeps its own number, history and genealogy; moving the container moves everything in it.

1. Open **Inventory → Containers** and choose the **kind**, an optional **code** (blank makes one such as *PAL-1042*) and **where it is**. Tick **Lots mix in it** for a container whose contents physically blend, such as a gaylord partial bags are tipped into (see below). Click **Add container**.
2. **Pack a lot** into it from the lot's page (**Container → Pack…**, then choose the container) or by [scanning](#scan-labels). A lot elsewhere is moved to the container's location as it is packed. To pack part of a lot, split it first.
3. **Move…** a container to move it and everything in it: its lots, the containers inside it and their lots.
4. **Pack into…** puts a container inside another (a box on a pallet). If the other one is somewhere else, the container and everything in it move there. **Take out of …** takes it off again; it stays where it is.
5. Click a container to see the containers and lots inside it. **Label** prints its label.
6. **Unpack** a lot on its page to take it out; it stays where it is. **Retire** a container once it holds no lots and no containers.

**In detail**

- **Leaving a container.** A lot moved on its own, split off, or used up (consumed, shipped or written off to zero) leaves its container. A part split off is loose.
- **Where containers can be:** any location that holds inventory at the site, except one where lots mix into balances (a silo), since a container keeps its lots apart. A location that takes one material at a time only takes a container whose lots are that material.
- **Mixed contents** are allowed: a container can hold lots of several materials, and containers beside lots. Balances of materials that aren't lot-tracked can't be packed.
- **Containers where lots mix.** Whole lots keep their identity in containers until they are put into a container where lots mix (**Mixes lots** on the list). There they lose it: each lot packed in joins the container's one balance of its material, and the lot goes to zero, linked to the balance in genealogy, as at a silo.
  - **What goes in:** only available, unexpired lots. A lot elsewhere is moved to the container first.
  - **Genealogy:** traces through the balance are narrowed by the container's flow (**Mixing…**: mixed or first in, first out, and a minimum residence), as for a silo; see [How a silo flows](#how-a-silo-flows).
  - **What comes out:** the balance's stock leaves by consumption, a move of a quantity, or a write-off, never by unpacking. The balance moves with the container.
  - **Containers:** such a container holds no containers, but it can sit on a pallet and travel with it.
  - **Changing it:** **Mixing…** switches mixing on or off only while the container is empty, so mixed stock never turns back into whole lots. Its flow can change at any time.
  - **Recall scope:** a trace forward from a lot tipped in reaches everything taken from the container's balance afterwards.
- **Containers inside containers.** A container sits inside at most one other, at that one's location, and never inside itself or anything inside it. Nesting goes at most five deep (a pallet of cases of boxes is three). A container moved on its own leaves the one it was in, as a lot moved alone leaves its container. Each lot that moves along records its own movement, naming the container it is in.
- **History.** Packing, unpacking and moving with a container are ordinary movements in each lot's history (*Packed into …*, *Unpacked out of …*, *Moved with …*), and `pulaski.lot.packed.v1`, `…unpacked.v1` and `pulaski.container.created.v1` / `…moved.v1` / `…packed.v1` / `…unpacked.v1` / `…retired.v1` in the event log. Genealogy isn't touched: a container isn't a lot.
- **Who:** containers, packing and unpacking need permission to move lots.

### Feed points

A feed point is a hopper, feeder, doser or unwind where lots enter a line: equipment with the role **Feed point** below the line on the [Equipment](#4-equipment) page, with **Holds inventory** ticked. Pulaski knows which lot is loaded at each, and uses that to consume components without anyone declaring them.

**Load a lot**

1. Open the lot and click **Load** (you need permission to move lots).
2. Choose the **Feed point**. A lot elsewhere is moved there whole. A lot already there is loaded again: it feeds from now.
3. Click **Load**.

Receiving, moving or splitting a lot straight to a feed point loads it too. A lot stays loaded until one of three things happens: the next lot of its material is loaded there, it leaves, or it runs out.

**What is consumed automatically.** It depends on the job's components, as their BOM lines say (**Consumed by**):

- **Backflush on count:** each production report takes the component's planned quantity per unit made, times what it reports (good and scrap). It comes from the lots of that material at the line's feed points: the loaded lot first, then the others there, first expiry first.
- **Time window (loaded lot):** each production report takes the same quantity, shared among the lots of that material loaded at the line's feed points since the run's previous report (or its start), by how long each was loaded.
- **A consumed counter:** a feed point with a consumed counter tag (see [Bind tags](#bind-tags)) takes each counted amount out of the lot loaded there at that moment. Time-window components at that feed point are left to the counter, so they aren't taken twice.

Each is an ordinary consumption into the job and its run. It shows in the lot's history with a **Backflush**, **Time window** or **Counter** chip, links genealogy, and is sent to the ERP like any other.

**What it couldn't cover.** Automation never takes a lot below zero, never uses a lot on hold or expired, and never refuses a report. Whatever it can't cover is listed on **Inventory → Feed points** under **What automation couldn't consume**, with where, the job, the material, how much and why. A report that left something uncovered says so in its confirmation.

1. To **resolve** one, click **Resolve…**, choose the lot it really came from and click **Consume it**. That consumes the quantity from the lot into the job and run, now. You need permission to report production.
2. To **dismiss** one, click **Dismiss…**, say why and click **Dismiss**. Nothing is consumed. You need permission to correct inventory.

**In detail**

- **Loads are worked out from the lots' movements**, so a back-dated move or a reversal falls into place. The Feed points page shows what is loaded at each now.
- **Repeated signals.** Each report and each machine count is posted once. The edge's resent signals are skipped, and a count already consumed or listed is never taken again. Counts are posted just after each batch from the edge is saved. One that wasn't posted (after an error) is taken up with the next batch, for up to 31 days.
- **Counters.** A counter counts in its tag's unit, or in the material's base unit when the tag has none. It needs one material loaded at its feed point: with lots of two materials loaded, a count is listed rather than guessed. A count with nothing running on the line, or nothing loaded, is listed too.
- **Reversing a report** puts back everything it consumed automatically, and dismisses what it left open. A shortfall already resolved stays resolved: reverse that consumption on its lot if it was wrong.
- **Allocations.** Automated consumption uses up the job's own [allocation](#allocate-lots-to-a-job) of a lot. Under the site's **hard reservation**, it takes only what no other job holds; the rest is listed as a shortfall (*…was reserved for another job…*), and resolving it from that lot is refused until the other job's allocation is released or moved.
- **Where it is recorded:** the consumption rows carry `auto_source` and `source_ref` (the report or the machine count). Shortfalls are `pulaski.feed_shortfall.recorded.v1`, `…resolved.v1` and `…dismissed.v1`, and loading again is `pulaski.lot.loaded.v1`.

### Lot, container and location labels

**Print label** on a lot's page, **Label** on a container or location, or on the Scan page, gives a PDF label 4 × 6 in (about A6) to print on any printer: the lot or container number, a barcode, the same code in text beneath it (to type if the barcode won't scan), and the material, quantity, supplier lot and expiry for a lot.

When the site has [label printers](#labels-and-printers), **Print to…** beside it sends the label straight to one: choose the printer (the site's default is chosen first) and the number of copies, then click **Print**.

- The barcode is Code 128 and holds a short Pulaski code: `PUL1L` and a number for a lot, `PUL1C` and a number for a container, `PUL1E:` and the code for a location. Each lot and container keeps its number for good, so a scan always names exactly that one.
- **GS1 labels.** When the site's label format is GS1-128 or GS1 DataMatrix, a lot whose material has a GTIN gets a GS1 barcode first: (01) GTIN, (17) expiry, the net weight for a lot kept in kg or lb, and (10) lot number. The elements are printed in brackets beneath it. A container with an SSCC gets (00) SSCC. The Pulaski code stays, smaller, at the foot. A lot without a GTIN (or whose number has characters GS1 doesn't allow, or is longer than 20), a container without an SSCC, and every location get Pulaski's label.
- A label shows the lot as it was when printed; print a new one after a change you want on it.

### Scan labels

The **Scan** tab is for a barcode scanner that types like a keyboard (most USB and Bluetooth scanners do), or for typing a code.

1. Open **Inventory → Scan**; the box is ready for a scan. Scan a lot, container or location label, or type a lot, supplier lot or container number and press **Enter**.
2. If more than one thing matches typed text (say a lot and a container with the same number), choose the one you mean. A Pulaski label always matches exactly one.
3. Choose what to do:
   - **A lot:** **Move** (then scan where it goes), **Pack** (then scan the container), **Unpack**, **Consume…** (into a job), **Load** (then scan the [feed point](#feed-points)), or **Open lot**.
   - **A container:** **Move with what it holds** (then scan where), **Pack into another** (then scan that container), **Take out of …**, or **Open container**.
   - **A location:** **Receive here…** (the receive form opens with the location filled in), or **What is here**.
4. A green message confirms each movement; scan the next thing, or **Start again**.

**In detail**

- A scan that names nothing at this site says so: a Pulaski label from another site, or a lot used up, isn't found. Scanned typed text matches lots on hand by lot or supplier lot number, active containers by code, and locations by code, ignoring case.
- **GS1 and supplier barcodes.** A GS1 barcode (GS1-128, GS1 DataMatrix or a GS1 QR code, as the scanner sends it, or typed in brackets like `(01)…(10)…`) is read for its parts. So is a supplier barcode in one of the site's [supplier formats](#labels-and-printers).
  - An SSCC finds its container.
  - A lot number finds the lots on hand with it as their lot or supplier lot number, of the material its GTIN or material code names.
  - When nothing is here yet but the material is known, the Scan page shows what the barcode said and offers **Receive it…**, with the material, lot, supplier lot, quantity and expiry filled in. A GTIN no material has says so: set it on the material's page.
- When a second scan is asked for (where to move, or which container), only that kind counts; scanning something else says so.
- Every movement made by scanning is the same as one made from the lot's or container's page, with the same rules, history and permissions.

#### Scan at a line: one scan, one confirm

At a line, the Scan page offers the likely action straight away.

1. Choose the line under **Working at**, once: this browser keeps it for the site. (An [operator station](#use-an-operator-station) always
   scans at its own line.)
2. Scan. Above the lot, Pulaski shows what fits at that line, the likely action first, ready to **Confirm**:
   - **A lot** of a material the line's running job uses: **Consume … into …**, for what the job still needs of it (its planned quantity less
     what it has used, at most what the lot holds). Change the quantity if you used another amount, then **Confirm**.
   - **A lot** going to the line: **Load … at …** for each of the line's feed points it isn't at, or **Move … to …** when the line itself holds
     stock and has no feed points.
   - **A container**: **Move … and what it holds to …** the line or one of its feed points.
   - **The barcode on a shop order** (order and operation): **Start … on …**, for its job planned on this line, or to a department or area
     above it.
3. Other offered actions are under **Or:**; clicking one makes it the one to confirm. The usual buttons stay below.

In detail:

- **Consuming** goes into the open run of the job running on the line. A lot of a material the job doesn't plan to use is still offered, whole,
  with *"… doesn't plan to use …"*; when the job has used what it planned, the whole lot is offered with *"… has already used what it planned …"*.
  Held lots aren't offered for consuming. A planned quantity in another unit is converted to the lot's.
- **Starting** is offered for a dispatched, firmed or paused job. If another job is running on the line it says so, and the start refuses as it
  would anywhere: stop that one first.
- Only actions you may take are offered (consuming needs permission to report production; loading and moving, to move lots; starting, to
  run jobs). **No line: inventory only** scans as before.

### Send movements to the ERP and certificates to a document system

Pulaski can send every inventory movement (received, moved, consumed, produced, written up or off, shipped, held and so on) to your ERP as it happens, so the ERP's stock follows the floor's, and also production reports, orders and jobs, labor, machine stops and quality results if you choose. A second feed sends certificates and other lot documents, as they are attached and withdrawn, to a document system, with their files. **Configure → Integrations** is where each feed is set up, shows how it is doing, and is where failures are put right.

**Set a feed up** (you need permission to manage integrations):

1. Open **Configure → Integrations** and choose the feed: **ERP** or **Document system**.
2. Under **Settings**, type the **Receiver's address** (an `https://` address) and tick **Send to this receiver**.
3. Under **What it sends**, tick the kinds of events: *Inventory movements*, *Certificates and lot documents*, *Work orders and jobs*, *Production reports and partial batches*, *Labor sessions, breaks and approvals*, *Machine stops coded and adjusted*, *Quality results*, *Maintenance requests*.
4. Tick **Send a certificate's file with its event** if the receiver should get the file itself.
5. Leave **Attempts**, **Timeout** and **Check every** as they are unless the receiver needs otherwise, then click **Save settings**.

The feed uses the new settings within seconds; nothing needs restarting. The shared secret that signs every message stays in the API's configuration, where whoever runs Pulaski sets it: until it is set, the panel says so and nothing is sent.

**Add another feed** (another system, or a message broker):

1. Click **Add a feed**.
2. Type a **Code** (2 to 30 lower-case letters, digits, `-` or `_`, such as `wms`) and a **Name**, and choose what it **Sends to**: *A webhook (HTTPS)* or *An MQTT broker*, and its **Format**: *Events (CloudEvents JSON)* or *Production performance (B2MML)* (see below). None of these can change later.
3. Click **Add**. The new feed gets its own tab, switched off.
4. Under **Settings**, give the address (`https://…` for a webhook, `mqtt://host:1883` or `mqtts://host:8883` for a broker) and, for a broker, the **Topic**, then choose what it sends (an events feed only) and switch it on.

Whoever runs Pulaski puts the feed's secret (and a broker's username and password) in the API's configuration under `Integrations:Feeds:<code>`. Each feed keeps its own queue and place in the event log, so feeds never hold each other up.

**A production performance feed (B2MML)** gives the ERP each production job's totals, as [Production performance for the ERP](#production-performance-for-the-erp) describes, every time they change: when the job starts, pauses, completes, is reopened, closed or canceled; when a report is entered, corrected or reversed; when a lot is produced or consumed for it, or such a movement is reversed; and when one of its partial batches is reported or decided. Its deliveries are listed by job (**Job** *number*, which opens the job), and a search by job number finds them. Planning changes, and jobs other than production, send nothing.

- **The message** is the job's `SyncProductionPerformance` as it stands when it is sent (not when the change happened), `application/xml`, with the change's event id as both the `Idempotency-Key` and the document's `BODID`, signed as an event is. Its `Sync` action is `Replace`: each document replaces the job's last one, so a document sent twice, or two in a row that say the same, do no harm.
- **In order per job.** A job's documents wait behind its undelivered one, as a lot's events do; retry or skip it to let them go. The answer is treated as for any webhook or broker, acknowledgments included.

1. Open **Configure → Integrations** and choose the feed: **ERP** or **Document system**. The top panel says whether the ERP webhook is **On** and where it sends, how many movements are waiting, how many have **failed**, and how many were delivered in the last 24 hours.
2. The list starts on **Failed**. Each row shows the movement, the lot, how many attempts were made and the ERP's last answer (its status code and message).
3. Put the cause right (the ERP rejected an unknown material, say, or a lot it doesn't know), then choose **Retry** on the row, or **Retry all failed** in the panel. It goes again within a few seconds.
4. If it shouldn't be sent at all (you have booked it in the ERP by hand, for example), choose **Skip…**, give the reason and choose **Skip it**. The ERP won't get that movement.
5. Choose **Refresh** to see the latest; switch the list to **Queued, retrying or held**, **Delivered**, **Skipped** or **All** to see the rest.
6. To find a particular delivery, type a **lot number**, a **job number** (for a production performance feed) or a subject's id, part of the **event type** (such as `consumed`), and dates, then click **Search**.

**In detail**

- **Nothing is lost in an outage.** Movements are queued in Pulaski's database in the order they were committed. While the ERP can't be reached, or answers with a server error, a timeout (408), *too early* (425) or *too many requests* (429), Pulaski keeps trying, waiting 10 s, then 20 s, 40 s and so on up to an hour between tries. After 12 attempts it stops and marks the delivery **failed**.
- **A refusal fails at once.** Any other 4xx answer (400, 401, 404, 422 …) means trying again won't help until something is fixed, so the delivery is marked failed straight away.
- **A lot's movements arrive in order.** While one of a lot's movements is waiting or failed, that lot's later movements are **held** behind it (shown as *Held*); other lots carry on. Retrying or skipping the failed one lets the held ones go.
- **Sent twice is safe.** Pulaski may send a movement again if it didn't hear the ERP's answer (at least once). Every message carries the movement's event id as its `Idempotency-Key`; the receiver should ignore an id it has already booked.
- **Acknowledgments.** A receiver can say what it did with each event, and Pulaski records it on the delivery (*Accepted* or *Rejected*, with its reference) in the **Last answer** column:
  - in its answer: a 2xx response whose body is `{"status":"accepted"}` or `{"status":"rejected"}`, optionally with `"reference"` (its own id for what it booked) and `"message"`;
  - or later: by posting `{"eventId": "…", "status": "…", "reference": "…", "message": "…"}` to `/api/integrations/erp/acknowledgements` (or `…/documents/…`), signed like Pulaski's messages with the same secret (`X-Pulaski-Timestamp` and `X-Pulaski-Signature`, within five minutes). A bad or old signature is refused.

  A **rejection** turns the delivery into **failed**, with the receiver's message, and holds back that lot's (or order's, job's …) later events, just as a refused delivery does: put it right, then **Retry** or **Skip…**. An acceptance posted later releases a failed delivery as delivered.
- **The message** is a CloudEvents 1.0 JSON document (`application/cloudevents+json`) holding the event as Pulaski recorded it: its type (`pulaski.lot.received.v1` …), the lot as its subject, the time, who did it, the reason and the movement's details. It is signed: `X-Pulaski-Signature` is `sha256=` and the hex HMAC-SHA256, with the shared secret, of the `X-Pulaski-Timestamp` value, a full stop, and the body. A receiver should check the signature and refuse old timestamps.
- **The secret, and the configuration.** In the API's settings (or environment), section `Integrations:Erp` (or `Integrations:Documents`): `Secret`, shared with the receiver; keep it in a secret store or environment variable, not in a file in source control. Until **Save settings** is first used, the feed also takes the rest from there: `Enabled`, `Url`, `EventTypes` (type prefixes, default `pulaski.lot.`), `IncludeFiles`, `MaxAttempts` (12), `TimeoutSeconds` (15), `PollSeconds` (5) and `StartFromNow` (true: the first time it is switched on, only events from then on are sent; false sends the whole history). After that the screen's settings are used and only the secret comes from the configuration. Changing the secret needs an API restart.
- **Settings are recorded.** Each save is an event (`pulaski.delivery_feed.settings_saved.v1`) with the settings before and after and who saved them. The full address is shown only to those who may change it; everyone else sees the receiver's host.
- **The document feed.** Section `Integrations:Documents`, with the same settings as the ERP's. It sends `pulaski.lot_document.attached.v1` and `…withdrawn.v1` (default `EventTypes` `pulaski.lot_document.`), each holding the document's kind, title, number, issuer, issue date, URL, file name, type, size and SHA-256 and the lot it was attached to; a withdrawal has the reason. With `IncludeFiles` (on for this feed, off for the ERP's unless set), the attached event also carries the stored file in `data.file`: `fileName`, `contentType`, `sizeBytes`, `sha256` and `contentBase64`. The ERP feed can take certificates too: add `pulaski.lot_document.` to its `EventTypes`. A certificate's later events wait behind an undelivered earlier one, as a lot's do. Documents carried to other lots by a split or merge send nothing of their own.
- **Maintenance requests to a CMMS** (#127). Tick *Maintenance requests* to send each request as it is raised (`pulaski.maintenance.request_raised.v1`, with its number such as MR-000042, the machine's code, priority, what needs looking at and the stop it came from), started and closed. The CMMS answers by posting to `/api/integrations/{feed}/maintenance-requests`, signed as acknowledgments are: `{"number": "MR-000042", "action": "accepted", "reference": "WO-1234"}` records its own work order number for the request (shown on the Maintenance tab as *CMMS WO-1234*); `"action": "started"` starts it and `"action": "closed"` with `"resolution": "…"` closes it, as the receiver (`receiver:{feed}`). Sending an answer again changes nothing, so a CMMS can repeat it safely. Requests started and closed by the CMMS are sent back to it like any others; a CMMS ignores what it did itself.
- **An MQTT feed** publishes each event as the same CloudEvents JSON a webhook gets, with quality of service 1, on the topic from its template: `{type}` becomes the event type with slashes for dots (`pulaski/lot/received/v1`), and `{subjectType}` and `{subject}` are filled in too. It uses MQTT 5: the event id goes as the `Idempotency-Key` user property, and with a secret the timestamp and signature as `X-Pulaski-Timestamp` and `X-Pulaski-Signature`. An event counts as delivered when the broker accepts it; a broker that refuses it (not allowed, bad topic) fails it at once, and one that is busy or unreachable is retried. Whatever reads from the broker can post an acknowledgment back to the feed's address, as a webhook receiver can.
- **Delivered rows are cleared** after 90 days (`Retention:DeliveredDays` in the API's configuration, at least 7). Failed, waiting and skipped deliveries stay until someone deals with them, and the events themselves are always kept.
- **Several API servers** share the work safely: only one sends each feed at a time.
- **Who.** Anyone who can see Integrations (Administrator, Manager and Auditor to start with) sees the status and the deliveries; retrying and skipping need *integrations.manage* (Administrator to start with). Each retry and skip is recorded as an event with who did it, and a skip keeps its reason on the delivery.

### In detail

- **One site per lot.** Moves stay within the lot's site; consumption needs a job in the same site.
- **Concurrency.** Write up, write off, move, hold and release check that the lot hasn't changed since you opened it; if it has you'll see *"changed by someone else"*: reload and try again. Consumption and production don't check, so several people can declare against a busy line; the no-negative-stock rule still protects them.
- **Substitutions.** Consuming a material the job has no planned component of is allowed, and recorded as a **substitution** (amber on the job page and in the confirmation). When the material is an approved alternate on the BOM line, the note says so instead (*used in place of …, as the BOM allows*).
- **Genealogy per lot.** Every lot consumed into a job is recorded as a parent of every lot the job made. Traces then narrow that to the lots that pair by time, or to the lots someone picked (see [Trace a lot](#trace-a-lot)). Pairing goes by when movements were declared, not by machine data, so declaring consumption when a lot is loaded gives the best pairs.
- **Times** are the moment you click, unless the movement is [back-dated](#back-date-a-movement). A movement recorded into a run that has ended keeps the time you entered it and names the run it belongs to.
- **What the ERP sees.** Each movement is an event in Pulaski's event log (`pulaski.lot.received.v1`, `…consumed.v1` and so on), sent to the ERP's webhook when that is switched on (see [Send movements to the ERP](#send-movements-to-the-erp-and-certificates-to-a-document-system)).

---

## 13. Quality: specifications and results

**Configure → Specifications** keeps what each material must meet. **Operate → Quality** records what was measured or inspected, checks it
against the material's released specification, and charts it against control limits. You can hold a lot whose result is out of limits, and reject it, which takes it off the good
output of the run that made it.

### Ideas to know first

- **A characteristic** is something measured, such as a wall thickness in mm or a melt flow index, or something inspected, such as surface defects
  counted on a sample. Characteristics are shared by every site and material.
- **A specification** belongs to one material and gives some characteristics their limits:
  - a measured one has a lower limit, an upper limit or both, and an optional target;
  - an inspected one has the most defects a sample may have.
- **Versions, as BOMs.** A specification is edited as a draft, then released. Results are checked against the released version. Releasing a newer
  one makes the old one obsolete. To change a released specification, start a new draft.
- **Holds the lot.** For each characteristic, the specification says whether a failing result for a lot puts the lot on hold. Otherwise the result
  is only flagged.
- **A result** belongs to a lot, a job or both, at a site, and keeps who recorded it, when the sample was taken, the values or counts, where it
  came from (at the line or the laboratory), the limits it was checked against and whether it was in specification.

### Add characteristics

1. Open **Configure → Specifications** and click **Characteristics**.
2. Under **Add a characteristic**, enter a **Code** (upper-case letters, digits, `-` and `_`) and a **Name**.
3. Choose its **Kind**: **Measured** or **Inspected (defects)**.
4. For a measured one, choose its **Unit** and how many **Decimals** to show. Add a **Description** if it helps.
5. Click **Add**.

To change one, click **Change**: the name, description, unit and decimals can change, and **On** switches it off or on. The code and kind
never change, since results and specifications depend on them.

**Its control chart.** When you change a characteristic you can also choose the chart its results go on, for statistical process control (SPC):
- measured: **Individuals and moving range** (one value a sample, the starting choice), **X-bar and R** or **X-bar and S** (several values a
  sample: set **Values a subgroup**, 2 to 25; X-bar and S suits larger subgroups);
- inspected: **p** (proportion defective, the starting choice, for samples of any size), **np** (number defective; set **Items inspected a
  sample**), **c** (defects a sample) or **u** (defects per unit).

**Run rules its chart checks** picks the patterns that signal a change, numbered as Lloyd Nelson numbered them. Rule 1 (beyond the control
limits) is always checked; 1, 2, 5 and 6, the Western Electric set, are ticked to start with:
1. one point beyond the control limits (3 sigma);
2. 9 in a row on one side of the center line;
3. 6 in a row rising or falling;
4. 14 in a row alternating up and down;
5. 2 of 3 beyond 2 sigma on one side;
6. 4 of 5 beyond 1 sigma on one side;
7. 15 in a row within 1 sigma (hugging the center line);
8. 8 in a row beyond 1 sigma, on either side.

More rules find smaller changes sooner, and give more false alarms. Rules can change at any time.

The **Control chart** column shows each one's chart. The chart can't change while the characteristic has control limits in force: they were
worked out for that chart, so end them first.

### Set control limits

Control limits say how much a process varies when nothing unusual is going on. They come from the results themselves, not from the specification:
a process can be in specification and still out of control, or the other way round. Limits are kept per characteristic and material, and a line
can have its own, which it uses before the material's.

1. On **Characteristics**, click **Control limits** under the characteristic's chart.
2. Under **Work out limits from results**, choose the **Material**, a **Line** if it should have its own limits (else **Every line**), and the
   period (**From**, **To**) when the process ran normally. Click **Work it out**.
3. Pulaski shows the limits the results give, the limits in force now (if any) and every result in the period: its plotted value (and range,
   standard deviation or moving range), its size, and whether it is **Beyond the limits** or **Can't be used** (and why).
4. Results beyond the limits, or completing a run rule (*Rule 2*…), are worth looking into. If one had a known cause, such as a startup or a wrong
   setting, tick **Leave out** and click **Work out again**.
5. When the limits look right, optionally set when they **Start** (now if blank) and a **Note**, and click **Approve these limits**.

To enter limits by hand (agreed with a customer, or carried over from another system), use **Enter limits by hand**. It takes the material and
line chosen above, the **Lower**, **Center** and **Upper** limits (and the dispersion chart's center and upper limit for measured charts), and
**Why**. To stop using limits, click **End** on their row.

**In detail**

- **How they are worked out.** Shewhart 3-sigma limits, with sigma estimated within subgroups (from the mean range, standard deviation or moving
  range), as the charts are meant to be. Limits need at least 20 usable results; under 25, Pulaski warns that they are rough. A result whose size
  doesn't fit the chart (for X-bar, np and c charts) is left out with why. Excluded results never count. Count charts' lower limits stop at
  zero, and p and u limits follow each sample's size.
- **Which results.** The characteristic's results for the material in the period, at most 1,000. For one line: results recorded against a job
  on that line (a result for a lot alone has no line, so it counts only towards limits for every line).
- **Fixed once approved.** Approving works the limits out again from the same results. If results were recorded or excluded since you looked
  and the limits changed, Pulaski says so and shows them again. Limits start now or later, never in the past. New limits for the same material
  and line end the ones in force where they start, and any other overlap is refused. Limits are ended, never changed or deleted, and the list
  keeps every period with how it was set, the results left out and who approved it.
- **Who.** Anyone who can see quality sees the limits; working them out, approving, entering and ending them needs permission to maintain
  specifications. Each approval is recorded as `pulaski.control_limit.approved.v1`, each end as `…ended.v1`.
- **Charts and alerts.** See [Control charts](#control-charts) and [Respond to SPC alerts](#respond-to-spc-alerts).

### Sample from process values

A measured characteristic that a machine already reports, such as a melt temperature or a line speed, can have its results made by Pulaski
from the [process values](#process-values) its tag sends, instead of by hand.

1. On **Characteristics**, click **Control limits** under the characteristic.
2. Under **Sampled from process values**, choose the **Process-value tag** (listed by site, line, machine and address) and how often (**Every,
   minutes**; 15 to start with). Click **Sample from it**.
3. From then on, at the end of each interval, Pulaski records a result for the job running on the tag's line. It shows on Quality results as
   *From process values*, recorded by *Automatic*, and goes on the control chart like any other.

**Change** sets another interval; **Switch off** stops it, and **Switch on** starts again from then.

**In detail**

- **What a result holds.** The value at the end of the interval, or for an X-bar chart, the values at that many consecutive minutes ending
  then (a subgroup of 5 is the last 5 minutes). A value is the last reading at or before each minute; the edge sends one at least every minute
  while the signal is good.
- **When.** Intervals end on the clock (every 15 minutes means :00, :15, :30, :45 UTC). Pulaski waits two minutes after each for late readings,
  then records it, normally within a minute after that.
- **Skipped intervals.** No result is made when no job is running on the line (changeovers, idle time), or when a reading in the subgroup is bad
  or more than two minutes old. If Pulaski was stopped, it makes up to the last 24 hours when it starts again; older intervals are skipped.
- **One line, one tag.** A characteristic is sampled from one tag on each line, and the tag must be on a line that runs jobs (or a machine below
  one). Only measured characteristics can be sampled. Deleting the tag on Edge nodes switches its sampling off.
- **Everything else is as usual.** The result is checked against the material's specification, goes on its chart, and can raise an
  [SPC alert](#respond-to-spc-alerts). A wrong one is left out like any other. It doesn't hold a lot: it belongs to the job, not to a lot.
- **Who:** setting up sampling needs permission to maintain specifications. The background work runs as *system*
  (`Quality:AutoSampling:Enabled`, `Quality:AutoSampling:PollSeconds`, 60).

### Control charts

1. On **Operate → Quality**, click **Control charts** (or **Chart** on a period of control limits).
2. Choose the **Characteristic**, the **Material**, a **Line** if you want one line's results only (else **Every line**), and the period (the last
   30 days to start with). Click **Show**.
3. The chart shows each result in time order: the value (or a subgroup's mean, proportion or count, as the chart takes it), with the control
   limits in force for it as dashed lines and the center line solid. A result beyond its limits is a red diamond; one that completes another run
   rule is an amber square labeled with the rules' numbers. The rules checked are listed above the chart.
4. Measured characteristics have views above the chart. Subgroups (X-bar charts): **X̄ chart (means)**, the default, **R chart (ranges)** and
   **S chart (standard deviations)**. Single values: **Individuals** and **Moving range**. Click one to see it; Pulaski keeps it as you move
   between charts.
5. Hover over a point to see when it was sampled, its value against its limits, and its line, job and lot. Below the chart, **The data
   plotted** lists every result, newest first: when, its measurements, the size, the view's value (mean, range, standard deviation or moving
   range), the center line and limits in force for it, its line, job and lot, and whether it is beyond the limits or breaks a run rule.
6. Under the chart, **Capability** says how well the results fit the material's specification.
7. The chart refreshes itself every 30 seconds while open; the pill at the top right says when it was last updated, and the refresh button
   updates it at once.

**In detail**

- **Whose results.** The characteristic's results for the material at the selected site in the period, excluded results left out; the latest 500
  at most. For a line, only results recorded against a job on it.
- **Which limits.** Each result is drawn against the limits in force at the time it was sampled: its line's own if it has some, else the
  material's. Limits worked out for another chart (before the chart changed) don't count. Results from before any limits were approved, such
  as the baseline itself, are drawn without limits, and the summary above the chart says how many.
- **Can't go on the chart.** A result whose size doesn't fit the chart (an X-bar chart's subgroup size, say) is left off, and the summary says how
  many.
- **Run rules** are marked on the point that completes the pattern: the ninth of nine in a row, say. Each point is measured against its own
  limits, so a run carries on across new limits, and a result without limits breaks it. Rising, falling and alternating look at the values alone.
  Rules apply to the means (or individuals); the range, standard deviation and moving range views mark only results beyond their limits. A
  newly recorded result that signals raises an [SPC alert](#respond-to-spc-alerts): for the view the characteristic is set up with (its chart
  type), not the other.
- **R and S from one set of limits.** A characteristic is set up as X̄-R or X̄-S, and its limits come from that. The other dispersion view uses
  limits from the same estimate of sigma (sigma = R̄ ÷ d₂, or S̄ ÷ c₄), so both describe the same process; the view says so. A subgroup's range
  is its largest less its smallest measurement; its standard deviation is the sample standard deviation (n − 1).
- **Capability** is worked out from the results the chart shows (the period and line you chose), against the material's released
  specification now:
  - **Cp · Cpk (within)** use the spread within subgroups (the mean range, standard deviation or moving range, as the chart estimates sigma):
    what the process could do. **Pp · Ppk (overall)** use the standard deviation of every value: what it did, drifts included.
  - Cp and Pp compare the specification's width with six sigma, so they need both limits; Cpk and Ppk also count how far the mean is from the
    nearer limit, so a one-sided specification has them only.
  - **Capable** at 1.33 or more, **marginal** from 1.00, **not capable** below, judged by Cpk (and by Ppk for the overall pair).
  - At least 20 subgroups are needed. If the chart signals (beyond the limits, or a run rule), Pulaski says the process isn't stable, since
    capability describes an unstable process poorly.
  - Inspected characteristics have no indices: Capability gives their rate (defective, or defects a unit for c and u charts) as a percentage
    and in parts per million, beside the most defects the specification allows a sample.
- **Live.** Each refresh redraws the chart you clicked **Show** for, not filters you've changed since. If its period reached the present, new
  results appear as they're recorded (its **To** moves on with the time); a period ending earlier stays as it is. If Pulaski can't reach the
  server, the chart stays as it was and the pill turns gray: *Stale since* the last update.
- **Who:** anyone who can see quality at the site.

### Write a specification

1. On **Configure → Specifications**, click **New specification**, choose the **Material** and click **Create and edit**.
2. Click **Add characteristic** for each line, and choose the characteristic.
3. For a measured one, fill in **Lower limit**, **Upper limit** (one or both) and, if you like, **Target**. For an inspected one, fill in
   **Most defects** (0 for none).
4. Tick **Holds the lot** where a failure should put the lot on hold.
5. To have it sampled while the material runs, fill in the line under it, **Sample:** tick **at the start of each run**, and/or give **and
   every** so many **hours** or **units of output** (in the job's unit), and the minutes after which a due sample is **overdue** (15 to start
   with). Leave both empty for no plan.
6. Click **Save draft**. A yellow box lists anything that stops the release, and the lines concerned are highlighted.
7. Type an optional **Release note** and click **Release version N**. Where approval is required (any site that can make the material asks
   for it, under [Approval before release](#approval-before-release)), click **Submit version N for approval…** instead; someone else approves
   it, which releases it, or sends it back, as for [processes](#release-with-approval).

To change it later, click **New draft**: it starts as a copy of the released version.

### Samples a running job owes

When the material of a running job has a released specification with sampling plans, the job's page lists them under **Samples**, and the
line's card on the **Floor** shows **1 sample due** (amber) or **2 samples overdue** (red); click it to open the job.

1. On the job's page, each characteristic says **Due now**, **Overdue**, **Taken** or **Not due yet**, its plan (*At the start of the run, then
   every 2 h*) and when it fell due, how many earlier ones were missed, and when the next one is (*Next at 16:00*, or *Next after 40 m more*).
2. Click **Record a result** and record it as usual ([Record a result](#record-a-result)), against the job. The list updates by itself.

In detail:

- **When samples fall due:** at the run's start if asked; every so many hours from the start; or each time the run's reported output (good
  and scrap, with corrections) reaches another multiple of the units. Each run starts over: pausing and resuming starts a new run.
- **What counts:** a result recorded against the job in that run, not left out, taken from the grace before a sample falls due until the
  grace before the next. A due sample is **overdue** once its grace has passed with none. If the next falls due first, the earlier one counts
  as **missed**.
- Only running jobs are listed; nothing is stored, so a plan changed by a new release applies at once.

**At the line.** The line's [operator station](#use-an-operator-station) lists the running job's samples under **Samples**, with how each stands,
to anyone there. Someone badged in who may record quality results (Operator, Supervisor, Quality) types the value (or each value of a
subgroup, or how many were inspected and defective) under a sample that is due or overdue and taps **Record the sample**. The station says
whether it is in specification and updates the list. It is recorded against the job, at the line, as that person. The line's
[board](#show-a-line-board-on-a-tv) shows the samples due and overdue (*Sample WALL-01 · Overdue · 14:00*), overdue ones in red.

### Record a result

1. Open **Operate → Quality**, and choose the site if you have more than one.
2. Under **Record a result**, find the **Lot**: scan or type its number and click **Find**. To record against a job, open the job's page and click
   **Quality results** instead: the job is filled in.
3. Choose the **Characteristic**. Those in the material's released specification come first, with their limits.
4. For a measured one, type the **Values**: one, or several from one sample, separated by spaces. For an inspected one, type how many were
   **Inspected** and how many were **Defective**.
5. Choose the **Source** (**At the line** or **Laboratory**), check **Sampled at** and add a comment if needed.
6. Click **Record**. Pulaski says whether the result is in specification. If it isn't and the characteristic holds the lot, it says the lot
   was put on hold. If the result makes its [control chart](#control-charts) signal (beyond the limits, or completing a run rule), it says so and
   that an [SPC alert](#respond-to-spc-alerts) was raised, or added to the one already open.

From a lot's page, **Record a result** under **Quality** opens this form with the lot filled in.

### Respond to SPC alerts

An SPC alert means a control chart signaled: a result beyond its control limits, or completing one of its characteristic's run rules. The
Overview's **Open SPC alerts** counts those waiting at the site.

1. Open **Operate → Quality** and click **SPC alerts** (or click the Overview's count).
2. Open alerts come first: the chart (characteristic, material and line), the rules broken, how many results broke them and when.
3. Click **Respond**. The alert lists each result that broke a rule, with its value, job and lot; **See the chart** opens its control chart.
4. Find the cause at the line. Choose the **Cause**, say **What was done**, and click **Close alert**.

The list refreshes itself every 30 seconds, like the Overview, so new alerts and breaks appear while it's open; the alert you opened follows
too, keeping the cause and action you're typing. The pill at the top right says when it was last updated.

**In detail**

- **One alert per chart.** While a chart's alert is open, further results that break a rule are added to it (its count and rules grow), rather
  than raising new ones. Once it is closed, the next break opens a new alert.
- **When it is raised.** When a result is recorded (by hand, from the laboratory, or through a lot attribute), it is checked against the results
  before it on the same line and the limits in force for it. A result recorded with an earlier sample time is judged by the results before it.
  Nothing is raised while the chart has no control limits.
- **Which chart.** Results on one line are a chart of their own; results for a lot alone (no line) are another. Limits are the line's own if it
  has some, else the material's.
- **Lots aren't held.** Out of control isn't out of specification; whether a result holds its lot is the specification's choice. Hold or reject
  the lot yourself if the cause calls for it.
- **Causes** are reasons of the kind *SPC cause* (Configure → Reasons). Starters: material or lot change, machine setting changed, die or
  tooling wear, startup or changeover, measurement or gauge error, and no cause found (which needs a fuller note).
- **Excluding** a result later doesn't change an alert it raised; close it with the cause *Measurement or gauge error* if that's what it was.
- **Who:** anyone who can see quality sees alerts; closing one needs permission to record quality results at the site. Raising, adding to and
  closing are events (`pulaski.spc_alert.raised.v1`, `…signaled.v1`, `…closed.v1`) that [delivery feeds](#send-movements-to-the-erp-and-certificates-to-a-document-system) can send on.
- Closed alerts stay listed for 30 days, and on record after that.
- An alert's details offer **Raise an NCR**, when the cause needs following up (see below).

### Nonconformance reports (NCRs)

An NCR records something found wrong, through to closure: what was found and how bad, its root cause, what is done about it (corrective and
preventive actions, each with an owner and a due day) and what becomes of the lots.

1. **Raise it** where the problem shows: **Raise an NCR** on a lot's page (under **Nonconformances**), on a result out of specification
   (Quality results), on an SPC alert's details, on a run or a failed or rejected checklist on the job's page; or on **Operate → Quality →
   Nonconformances**, on its own or from a customer complaint.
   Say what was found, how severe it is (**Minor**, **Major**, **Critical**), and optionally who owns it, then click **Raise NCR**. It opens
   on its own page, numbered *NCR-000001*.
2. **Investigate.** Under **Investigation**, ask why up to five times, write the **Root cause**, and click **Save the investigation** as you
   go. When the cause is found, click **Root cause found: act on it**.
3. **Act.** Under **Corrective and preventive actions**, add what is to be done, by whom and by when. Whoever owns an action, or anyone who
   manages NCRs, clicks **Finish** and says what was done, then **Mark done** (or **Cancel the action**, saying why).
4. **Close.** Once every action is done or canceled, choose **What becomes of the lots** (*Use as is*, *Rework*, *Scrap*, *Return to
   supplier*, *Not applicable*), add a **Closing note** and click **Close NCR**.
5. **Carry out the disposition.** Once closed, the NCR lists its lots still in stock with what to do to each: **Release it** (used as is,
   held), **Hold it** (scrap, rework or return, not held yet: choose a hold reason), **Write it off** (scrap, held: choose a scrap reason). Each
   is noted on the lot with the NCR's number.

In detail:

- **What it is linked to.** Raised from a lot, it lists that lot and the runs that made it; from a result or an SPC alert, the lot and run of
  the result (an alert's latest); from a run, the run and the lots it made; from a checklist, its run. A lot's page lists every NCR about it.
  Raised from a held lot, its source is the hold. Until it is closed, **Add a lot by its number** links another lot of the site (every
  material's lot of that number).
- **Statuses:** *Open* when raised, *Investigating* once the investigation is saved, *Acting* once the root cause is found (it can still be
  edited), *Closed*. An action is **Overdue** once its due day has passed; the list shows how many each NCR has.
- **Who:** raising needs permission to raise NCRs (Quality, Supervisor and Operator roles); editing, investigating, adding actions and closing
  need permission to work them (Quality). Owners are users with a role at the NCR's site. Anyone who can see quality or inventory sees NCRs.
- If someone else changed the NCR since you opened it, you'll see *"changed by someone else"*: reload and try again.
- **Audit.** Each step is an event: `pulaski.ncr.raised.v1`, `…changed.v1`, `…investigated.v1`, `…action_added.v1`, `…action_done.v1`,
  `…action_cancelled.v1`, `…closed.v1`.
- **Carrying out** uses the lots' own steps, so it needs their permissions: holding and releasing lots, or rejecting them on quality (writing
  off moves the quantity good → scrap in the run that made it, like rejecting a lot). Rework and returns are done as usual once the lot is held.
  Overdue actions reach the [inbox](#your-inbox) and an [e-mail alert](#get-e-mails-when-something-needs-you-my-alerts) you can subscribe to.

### Leave out a wrong result

1. Find the result: on **Operate → Quality**, or under **Quality** on the lot's page.
2. Click **Leave out…**, choose why (*Entered in error*, *Sample not representative*) and add a comment.
3. Click **Leave it out**.

The result stays, marked **Left out**, with who did it and why. It is no longer counted as out of specification. A result is never edited.

### Reject a lot on quality

1. Open the lot's page. It must be on hold (or in quarantine).
2. Under **Quality**, click **Reject…**.
3. Choose the **Scrap reason** (*Rejected on quality* is offered first) and type a comment.
4. Click **Reject the lot**.

Everything left of the lot is written off as scrap. If the lot was made in a run, its quantity moves from good to scrap on that run, at the time
the lot was made. OEE quality, the job's totals and production by shift all show it.

**In detail**

- **What is checked.** Every value must be within the limits, which count as in specification. An inspected sample may have at most the defects
  allowed. A characteristic that isn't in the material's released specification is recorded without limits, and Pulaski says so.
- **Whose specification.** The lot's material, or for a job alone, the material the job makes. The released version at the time of recording is
  stamped on the result, so a later version never changes what an earlier result was checked against.
- **Holding.** Only a lot that is available and has stock is put on hold, with the reason *Quality result out of specification* (`OUT-OF-SPEC`)
  when it exists, and the failure as the comment. A lot already held, used up or gone is left as it is. Release it as usual (see
  [A lot's page](#a-lots-page)).
- **Lot attributes.** A number lot attribute can stand for a measured characteristic (**Stands for characteristic** on
  [Lot attributes](#lot-attributes)). Setting it on a lot records a result, source *lot attribute*, checked and held like any other.
- **Time.** **Sampled at** can be up to 31 days back, never ahead. A job's result is tied to the run that was open on its line then.
- **The run's good output** changes only when a lot is rejected. Results alone never change OEE.
- **The shop order sheet** prints the material's released specification. See [Print a shop order](#print-a-shop-order).
- **Who.**
  - Seeing specifications and results needs permission to see quality (Planner, Supervisor, Operator, Quality, Manager, Auditor, Process
    engineer to start with).
  - Recording results: Supervisor, Operator and Quality.
  - Leaving results out and rejecting lots: Quality.
  - Maintaining characteristics and specifications: Quality and Process engineer.
- **Where it is recorded:** results in `ts.quality_sample`, with the events `pulaski.quality_result.recorded.v1` and `…excluded.v1`.
  Specification changes are `pulaski.material_spec.*` and characteristic changes `pulaski.quality_characteristic.*`. A rejection is the lot's
  `pulaski.lot.written_off.v1`, marked as rejected on quality.

---

## 14. Materials

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

The left side lists the material's properties, grouped into *Identity*, *Classification*, *Stock* and *Status*. Edit one the same way as equipment: text saves when you press **Enter** or leave the field, and a choice saves as soon as you pick it. Click a property to read what it means and, when it is grayed out, why it can't change.

The rest of the page shows what the material is connected to:

- **What uses it:** stock on hand (with the number of lots), open work orders, open jobs, released BOMs that use it as a component, and the lines that can run it. Click the stock tile, or **Stock** at the top, for its inventory.
- **Where it can run:** each line with a capability for it, and its planning rate; see [below](#where-it-can-run-capabilities-and-rates).
- **How it is made:** the processes that make it (**default** marks the one order creation picks first) and its BOMs.
- **Where it is used:** the BOMs that list it as a component, and how many open jobs plan to use it.
- **Its own conversions,** such as *1 m = 1.2 kg*. Click **Conversions** at the top to add one on the Units page.

### Where it can run: capabilities and rates

The **Where it can run** table lists each line that can run the material: its ideal rate, the **planning rate** Pulaski schedules with and where that comes from (*calculated* or *modified*; point at the word to see who set a modified rate and why), the standard changeover and crew, and the days it applies. ★ marks a preferred line. A capability that has ended is hidden; tick **Show ended** to see it in gray.

**Add a line**

1. Click **Add line**.
2. Choose the **Line**. Only active lines that run jobs are offered; a department gets its lines' capabilities.
3. Enter the **Ideal rate, per hour**, the best the line can do, and its **Rate unit**. The unit must convert to the material's base unit; for meters of profile on a line rated in kilograms, add a [conversion](#6-units-and-conversions) first.
4. Optionally enter the **Standard changeover** in minutes (used when no changeover rule matches), the **Crew size**, and tick **Preferred line**. For a material made in batches, enter the **Batch size on this line** when the line's equipment makes a different amount at a time (a 200 kg mixer next to the 50 kg ones), and the **Batch tolerance on this line, %** when its batches may vary more or less than the material's; leave either blank for the material's.
   **… per machine count** is how much of the material one count of the line's counter makes, in the material's base unit: *2.4* when each cut of the saw is 2.4 m of profile. Leave it blank for one. [Reports from the machine](#report-production-from-the-machine) use it; the counter tag's own scale applies first. The rate column shows it as *2.4 m a count*.
   For a lot-tracked material not made in batches, **Production lots on this line** (**Lot size**, **Lot age, hours**, **New lot each shift**) makes reports fill a lot until it is full instead of making one each; see [Lots filled by reports](#report-production). The line's row shows it as *Lots of at most 1000 m, 8 h*.
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

1. Click **Rate** on the line's row. Under **From run history**, choose how far back to look (**Runs that ended in the last** 30 to 365 days; 90 to start with).
2. Tick **Take out stops** to divide by running time rather than run time; it starts as the site's choice (see [Planning rates from run history](#planning-rates-from-run-history)). Click **Work it out**.
3. Pulaski shows the rate the runs give, how many runs and hours it used (with stops taken out: the running hours and how many hours of stops came off), and how it compares with the rate in force. Below it, every run it looked at: its job, when it ended, its hours, its stopped hours when they are taken out, good output and rate, and whether it was **Used** or **Left out**, with why.
4. If the rate looks right, click **Use … as the calculated rate**. Beside it Pulaski says what it is recorded as: *from run history* or *from run history, stops taken out*. Nothing changes until you click it.
5. To go back to the ideal rate later, click **Rate**, then **Back to the ideal rate**.

If the scheduled recalculation has proposed a rate, the review shows it at the top with **Accept proposal** and **Reject**. The line's row on the material shows *proposed …* until it is decided. All of a site's proposals are on [Planning rates](#review-proposed-planning-rates).

**In detail**

- **The rate** is the good output of the used runs ÷ their hours, so a long run counts for more than a short one. Scrap doesn't count: the planning rate is for good output.
- **Taking out stops.** Each run's stopped time comes off its hours: down time (stops, time without a signal), planned stops and time in a unit mode that isn't production, as the [OEE report](#21-machines-live-status-and-stops) classifies the line's time. A line deriving its state from its machines (series, parallel, constraint machine) takes theirs. Micro-stops stay in: they are lost speed, which the rate should show. A run that ran less than 30 minutes once its stops come out is left out. A line that doesn't record machine state can't have its stops taken out: Pulaski says so and uses run time.
- **The basis is recorded:** the calculated rate says *from run history* or *from run history, stops taken out*, and so does its `…recalculated.v1` event, with the stopped hours.
- **Which runs:** ended runs of production jobs of the material on that line, ended in the chosen days. Runs under 30 minutes, runs with no good output, and runs whose unit doesn't convert to the planning rate's unit are left out.
- **Unusual runs:** with four or more runs, a run much slower or faster than the rest (outside 1.5 times the spread of the middle half of the runs, and never closer than 10% either side of the middle run) is left out, so a breakdown or a report put into the wrong run doesn't move the rate. The limits are shown under the list.
- **Enough history:** a rate needs at least 5 used runs and 8 hours. With less, Pulaski says how much it has and offers no rate.
- **A modified rate stays in force.** Using a history rate changes the calculated rate only; if a modified rate is set, planning keeps using it, and the comparison shows how far it has drifted. Clear the modified rate to plan with the history rate.
- **Checked again when you accept.** If a run ended or was reported since you looked and the rate is different, Pulaski says so and shows the new figures; review them and accept again.
- **Updating it.** A rate from run history stays until it is worked out again (by you, or a [scheduled recalculation](#planning-rates-from-run-history) you or the site accepts) or you go back to the ideal rate. A new ideal rate on the capability no longer changes it.
- **Who:** permission to set the planning rate (Planner, to start with).

- **Periods.** A capability applies from the start of its first day to the start of its end day, in the line's site time zone. One that starts today starts the moment you add it, so it can follow straight on from one you ended earlier today. Two periods for the same material on the same line can't overlap: end or change the other one first.
- **Nothing is deleted.** Ending sets the end to now. A capability that hasn't started yet can't be ended; change its dates instead.
- **The planning rate** is one per line and material, whatever the capability periods. Pulaski calculates it from the ideal rate until you use a rate from run history (above); from then on the ideal rate no longer changes it, until you go back to it. You can't change a capability's rate unit while the planning rate has a modified rate in the old unit: clear the modified rate first.
- **Events:** `pulaski.capability.added.v1`, `…changed.v1`, `…ended.v1`, `pulaski.run_standard.created.v1` and `…recalculated.v1`, besides the existing `…modified.v1` and `…modification_cleared.v1`.

### Import materials from a file

Load many materials at once, for example when setting up a new site, from a spreadsheet saved as CSV or from an ERP's ISA-95 export in B2MML XML.

1. On the Materials tab, click **Import file**.
2. Leave **Import** on **Materials** and pick the **Format**: **CSV** or **B2MML** (see [B2MML files](#b2mml-files) below). For B2MML, type the **External system** the items come from, such as `ERP1`, if they are the ERP's items.
3. Tick **Create missing classes** if the file may name classes that aren't on the Classes tab yet. Left unticked, such a row is an error, so a misspelled class can't slip in.
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
- **A CSV file:** comma or semicolon separators, quotes around a value that contains them (or line breaks), column names in any order and case (`Base unit` and `base-unit` both work, as do `item_code` and `uom`). An unknown column stops the import, so a misspelled name isn't ignored. At most 5 MB and 5,000 rows per file.
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
- **Other properties are skipped.** Properties Pulaski has no field for (density, color…) are ignored, unlike an unknown CSV column. Property IDs are compared ignoring case, spaces and underscores, so `Shelf life days` and `shelf_life_days` work too. Giving a field twice, such as a class by `MaterialClassID` and by a property, is an error on that material.
- **Left out or blank** works as in CSV: a property that isn't there keeps what the material has, and one with an empty value clears an optional field.
- Any B2MML namespace version (or none) works, inside `MaterialInformation`, a wrapper such as `SyncMaterialInformation`, or as a bare `MaterialDefinition`. The *Line* is where the material's `MaterialDefinition` starts. Further class IDs and descriptions (other languages) are ignored. Files with a `DOCTYPE` are refused. At most 5 MB and 5,000 materials per file.
- **Classes.** Each `MaterialClass` in the file (its `ID` the code, its `Description` the name) is added if Pulaski doesn't have it and renamed if its name differs, before the materials, whether or not **Create missing classes** is ticked: the file states the class itself. The result says which classes it creates or renames. Whether a class is switched on stays as Pulaski has it. A file can hold only classes.

#### Export materials as B2MML

1. On the Materials tab, click **Export B2MML**.
2. Pulaski downloads `materials.xml`: every class as a `MaterialClass`, then every material (switched off ones too) as a `MaterialDefinition` with its class and every field the import reads as a property.

The file is in the format the B2MML import reads, so it can go to another Pulaski or back in unchanged. Anyone who can see materials can export. For scripts: `GET /api/materials/export/b2mml`.

### Import materials from a connection

Instead of a file, Pulaski can read items straight from an **OData v4**, **REST** (JSON) or **SOAP** service, which many ERPs and item masters offer, or from a **query of a PostgreSQL database**. Someone who manages integrations sets the connection up once; anyone who maintains materials can then check and import it whenever the item master changes.

1. Open **Materials**, click **Import file**, and find **Material connections** under the file import (or open **Configure → Integrations**).
2. Click **Check** on the connection's row. Pulaski reads every item and says what an import would do: how many to create, update or leave unchanged, and any items with errors, with why.
3. When the check finds no errors, click **Import**. As with a file, it is all or nothing.
4. Tick **Create missing classes** before checking if the items name classes Pulaski doesn't have yet.

**Set up a connection** (needs *integrations.manage*, Administrator to start with)

1. On **Configure → Integrations**, under **Material connections**, click **Add connection**.
2. Give it a **Code** (fixed once saved: it names the credentials in the configuration), a **Name**, and the **External system** the items belong to, such as *ERP1*. Items are matched on that system and each item's key, so the same item is never made twice.
3. Choose its **Kind** (fixed once saved) and fill in what that kind needs:
   - **OData v4:** the **Service root** (an https address), the **Entity set** holding the items, such as *Products*, and optionally a **Filter** in OData syntax to take only some of them.
   - **REST (JSON):** the **Address** that answers with the items (a query such as `?plant=1000` is fine), where the items are in the answer under **Items at** (such as `data.items`; blank when the answer is the list itself), and optionally where the answer gives the next page under **Next page at** (such as `links.next`).
   - **SOAP:** the **Address** the request is posted to, the **Request envelope** to post, the **SOAPAction** if the service wants one, and the **Item element** that holds one item in the answer, such as *Material*.
   - **PostgreSQL query:** one **Query** (`SELECT …` or `WITH … SELECT …`). The database's connection string goes in the API's configuration, not here.
4. Under **Fields**, type, for each Pulaski field, the name of the item's property (or the query's column) that holds it: at least **code** and **external_key**, and for new items **name**, **type** and **base_unit**. Use `/` for a property of a property, such as `Unit/Code`. A field left blank isn't read: materials keep what they have.
5. Click **Save**.

**In detail**

- **Credentials** never go in the database. Put them in the API's settings or environment under `MaterialConnections:<code>`: `BearerToken`, or `Username` and `Password` for basic authentication; for a database, `ConnectionString`. The list says whether credentials are configured.
- **What is read.** Pulaski asks for only the properties the fields name (`$select`), applies the filter, and follows the service's next-page links, up to 5,000 items (an import's limit). A property the item doesn't have leaves that field as the material has it; a null clears an optional field. Values are read as from a file: yes/no and true/false, whole days.
- **The rules are the import's.** For an ERP item the ERP owns its code, name, base unit and type; Pulaski owns the rest. Each change is recorded with the reason *<connection name> import*, and the list shows the last import, who ran it and what it did. A check is not recorded.
- **REST** follows the next-page link (relative addresses are read against the page) up to 200 pages. **SOAP** reads every element with the item element's name, anywhere in the answer, as an item of its child elements (nested ones as properties of properties; of repeated names, the first). A SOAP fault is reported with its message. Files and answers with a `DOCTYPE` are refused.
- **A database query** runs read-only, with a 30-second limit, and stops at 5,000 rows. Each column is a field by its name. Give the connection a login that can only read.
- **When it runs.** Under **Runs**, choose **When you click Import**, **Every so many hours** (1 to 168), or **Daily at set times** (up to four, in UTC, such as `02:00, 14:00`). A scheduled run imports straight away, all or nothing, as *system*, with the reason *<name> import (scheduled)*; a saved **Create missing classes** applies to it. The list shows the schedule and the next run. A run that comes late is followed by the next one due, not by those it missed. Switching a connection off stops its schedule.
- **History.** Every import, on demand or scheduled, is recorded; checks aren't. The list marks a last run that **Failed** (the source couldn't be read) or was **Not imported** because some items had errors. Click **History** for the latest 50 runs: when, by whom or the schedule, and what each did or why it failed. For scripts: `GET /api/material-connections/{id}/runs` (or `/api/people-connections/{id}/runs`).

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
   | `batch_tolerance` | The line's own batch tolerance in percent, 0 to 50, for a material made in batches; blank for the material's. |
   | `lot_size`, `lot_hours`, `lot_by_shift` | When the line's production lots are full ([lots filled by reports](#report-production)): a size in the material's base unit, an age in hours (up to 720), and `yes` for a new lot each shift. Blank size or age: none. |
   | `active` | `no` ends the capability now. |
   | `effective_from` | The day the capability starts, at the line's site (`yyyy-MM-dd`). Blank: the capability in effect now. |
   | `effective_to` | The day after its last day (`yyyy-MM-dd`); blank on a dated row: no end. |

3. Click **Choose File** and pick the file. Each row shows **Create** (a new capability from now), **Update** (with what changes), **Unchanged** or **Error**, with what happens to the line's planning rate.
4. When every row is fine, click **Import N capabilities**.

**In detail**

- **Which capability a row is.** Without `effective_from`, the one for that material and line in effect now. If there is one, its values change in place and its period stays. If there is none, a new one starts now with no end. `active` = `no` ends the one in effect now. Nothing is ever deleted.
- **Periods.** With `effective_from`, the row is the capability that starts that day: if there is one, it changes in place (and `effective_to` can end it); if not, one is added from that day. Adding it ends a capability with no end that started earlier on the day the new one starts (the row says so). Any other overlap is an error on the row, so the whole file waits until it is fixed. A future capability doesn't change the planning rate until it is in effect.
- **The planning rate follows** as on the material page: a new capability sets it from the ideal rate when the line has none, and a new ideal rate recalculates it while it is still based on the ideal rate. A modified rate stays in force.
- **Left out or blank.** A column the file doesn't have keeps what the capability has. A blank `changeover_minutes`, `crew_size`, `batch_size`, `batch_tolerance`, `lot_size` or `lot_hours` clears it; a blank `lot_by_shift` keeps it.
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

**A line with its own batch size.** Equipment of different sizes can make the same material: give a line its own batch size on its row under [Where it can run](#where-it-can-run-capabilities-and-rates). A line can also have its own **batch tolerance**, for equipment that weighs more or less precisely; it counts under the near rule. The batch rule stays the material's, because it describes the product. A line can have its own size or tolerance only once the material has a batch size.

### What can change, and when

| Field | Pulaski material | ERP material |
| --- | --- | --- |
| Code | Until it is in use | In the ERP only |
| Name, type | Always | In the ERP only |
| Base unit | Until it is in use | In the ERP, until it is in use |
| Lot-tracked | While none of it is on hand | Same |
| Description, class, shelf life, active | Always | Same |
| Batch size, rule, tolerance | Always; open orders keep their quantity | Same |
| GTIN, lead time | Always | Same |

- **Base unit.** Quantities are stored in the base unit, so it can't change once the material is in use, not even from the ERP: an import that tries is refused.
- **Lot-tracked.** Switching between lots and balances needs no stock on hand: use, move out or write off what is there first.
- **Shelf life** applies to lots that arrive from now on; lots already on hand keep their expiry.
- **GTIN** (GS1 trade item number): 8, 12, 13 or 14 digits with a right check digit, kept as 14; one material per GTIN. It goes on GS1 lot labels and lets a scanned GS1 or supplier barcode find the material (see [Scan labels](#scan-labels)). Leave it blank to clear it.
- **Lead time:** how many days supply of it takes to come, 1 to 3650, or blank. The [shortage check](#check-for-shortages) flags shortages needed sooner than that; nothing is ordered.
- **Switching a material off** (setting **Active** to *false*) is always allowed. Afterwards it can't be received, put on new orders or added to BOMs, but what already uses it carries on. A yellow notice lists what still uses it, for example *a capability on 3 lines*. Pulaski never deletes a material.

### Classes

1. Open the **Classes** tab. Each class shows its code, name, how many materials are in it, and whether it is active.
2. **Add a class:** type a **CODE** (up to 30 characters, unique) and a name, then click **Add class**.
3. **Rename or switch off a class:** change its name or untick **Active**. **Save** and **Undo** appear on the row; click **Save**.
4. Click a class's material count to see its materials (inactive ones included).

A class's code can't change and a class is never deleted: switch it off instead. An inactive class isn't offered for new materials, and a material can't be moved into one, but materials already in it stay there.

### Lot attributes

The properties a site records on its lots, such as melt index, moisture or color grade, recorded on each [lot's page](#lot-attributes-and-certificates).

1. Open the **Lot attributes** tab and, with more than one site, choose the site.
2. Type a **Code** (up to 30 characters, unique in the site; it can't change later) and a **Name**.
3. Pick its **Type**: *Text*, *Number* (with an optional **Unit**, for display), *Date*, *Yes / no* or *Choice* (type the choices, one per line). The type can't change later. A *Number* can also be linked to a measured quality characteristic under **Stands for characteristic**: setting it on a lot then records a [quality result](#13-quality-specifications-and-results), checked against the material's specification.
4. Leave **For every material** ticked, or untick it and tick the material classes it is for.
5. Set the **Order** it is listed in, and click **Add attribute**.
6. To change one, click **Change**: its name, unit, characteristic, choices, classes, order and whether it is **Active** can change. Click **Save attribute**.

An attribute is never deleted: untick **Active** to stop it being recorded; values already recorded stay and can be searched. The list shows how many lots have a value of each. Removing a choice doesn't change values already recorded. Maintaining lot attributes needs permission to maintain lot attribute definitions (the Quality and Manager roles have it).

**Values in bulk.** Under the list, **Values in bulk** moves many lots' values at once, for example from a supplier's certificate data:

1. Click **Export values** for a file of every recorded value of the site's numbered lots: `lot_number`, `material_code`, then a column per attribute code.
2. Edit it in a spreadsheet, or make your own file with those columns. Only `lot_number` and the attribute columns you want are needed; add
   `material_code` when two materials share a lot number.
3. Pick the file. Pulaski checks it and says what it would change, row by row, or which rows are wrong.
4. If every row is valid, click **Import**.

A blank cell leaves the value as it is; type `(clear)` to clear one. Values are read as on the lot page: numbers with a decimal point, dates as
`yyyy-MM-dd`, yes/no, and choices in any case. A lot number is matched in any case. Nothing is saved unless every row is valid, so fix the rows
it lists and pick the file again. Balances have no lot number, so they aren't in the file. Each value set is recorded as the lot's
`pulaski.lot.attribute_set.v1`, with the reason *CSV import*, and an attribute standing for a quality characteristic records its result too.
Importing needs permission to record lot attributes and certificates.

### In detail

- **The ERP import** is `POST /api/materials/import`, one item at a time, with the ERP's name, the item's key in the ERP, an optional version, and the code, name, base unit code and type. The first import of a key creates the material; later ones update the code, name, base unit and type, and change nothing (no new version, no event) when nothing differs. The ERP may rename an item's code even while it is in use, since the ERP's key, not the code, identifies it. The base unit must be a unit Pulaski has. To load many at once, [import a CSV or B2MML file](#import-materials-from-a-file).
- **Codes** are unique across all materials, whatever their origin; an import whose code is already used by another material is refused.
- **Events.** Each change is recorded in the event log as `pulaski.material.created.v1`, `…updated.v1` or `…imported.v1` (and `pulaski.material_class.created.v1` / `…updated.v1`), with the material before and after.
- **Concurrency.** As elsewhere, if someone else changed the material or class since you opened it, you'll see *"changed by someone else"*: reload and try again.

---

## 15. Users, roles and signing in

Everyone signs in to Pulaski. Your **roles** decide what you **see** and what you can **change**: you see only the screens, and only the sites, your roles allow, and the menu shows nothing else. A role is a set of **permissions**, such as *See inventory*, *Receive lots* or *Plan jobs*. An administrator decides what each role may do and gives each user their roles, each at a **site** (only Administrator is everywhere).

### Sign in

1. Open Pulaski. It shows **Sign in**.
2. If your organization set up its own sign-in, click **Sign in with …** and sign in there; Pulaski opens when you come back. Otherwise type your **User name** and **Password** and click **Sign in**.
   The first time you use your organization's sign-in, Pulaski says you have no role yet: an administrator gives you one, and then you sign in again.
3. The first time, or after an administrator reset your password, Pulaski asks you to **Change your password**: type the temporary password, then your new one twice, and click **Change password**.
4. To sign out, click the door at the top right.

Your session lasts up to 12 hours. When it ends, or an administrator switches you off, Pulaski shows **Your session ended. Sign in again to carry on.**

### Change your password

1. Click the padlock at the top right.
2. Type your **Current password**, then the **New password** twice.
3. Click **Change password**.

A password needs at least 10 characters, with two of: lower case, upper case, digits, symbols, and must not contain your user name. Five wrong passwords in a row lock you out for 15 minutes; an administrator can unlock you sooner by resetting your password.

### Forgotten your password?

Where whoever runs Pulaski has switched it on, the sign-in page shows **Forgotten your password?**:

1. Click **Forgotten your password?**, type your **User name or e-mail**, and click **Send the link**. Pulaski says a link is on its way if that matches an account; it says the same either way, so nobody can find out who has an account.
2. Open the e-mail and its link within 30 minutes (or the time your organization set). The link works once.
3. Type the **New password** twice and click **Change password**, then sign in with it. Pulaski e-mails you that the password was changed.

**In detail**

- The link goes to the e-mail address on your user in Pulaski. Typing an address finds you only if it is yours alone; a user name always works.
- Asking again sends a new link and the old one stops working. Pulaski sends at most one link a minute and three an hour per user; asking more often sends nothing more.
- Resetting this way also unlocks you. A link stops working when an administrator resets your password, when you change it, or when you are switched off.
- Machines' accounts (service accounts) never get links. When only administrators use Pulaski passwords, only administrators can reset theirs this way. Without the link on the sign-in page, ask an administrator.

### Get e-mails when something needs you (My alerts)

Pulaski can e-mail you when a machine has been stopped too long, when a stop is still without a reason, when an andon call is waiting past
its time, when a control chart signals, when a lot is put on hold, or when a job will be short of a component, so you don't have to keep looking.

1. Click the warning-triangle button at the top right (**My alerts**).
2. Under **Add an alert**, choose what to be told about (**Tell me when**), the **Site**, and **Where**: the whole site, or one area, line
   or stock location.
3. Where it asks for **Minutes**, set them: how long a stop must last, how long after it ended it may stay without a reason, or how long
   before a job needs a component you want to know it will be short (480, a shift, unless you change it). Click **Add**.
4. **Change** sets other minutes; **Switch off** stops an alert without losing it.

**In detail**

- You get one e-mail for each stop, call, SPC alert, hold or shortage, with a link to it (**Machines**, **Andon**, **SPC alerts**, the lot,
  **Shortages**) when your organization has told Pulaski its web address. Times in the e-mail are the site's.
- *A machine stopped longer than*: a stop of a machine or line at or below **Where**, still going or not, once it has lasted the minutes.
  *A stop still without a reason*: that many minutes after it ended. Micro-stops don't count, and pieces of a stop someone split, merged or
  retimed don't alert again. *An andon call escalated*: once a call is still unanswered past its kind of help's time.
- *A control chart signaled*: when an SPC alert opens for a line at or below **Where** (an alert for results without a line only for the
  whole site); further breaks while it stays open don't e-mail again. *A lot put on hold*: when someone holds a lot that is at or below
  **Where** now, with the reason and comment. *A job will be short of a component*: what the **Shortages** check shows as not covered by
  stock or supply due in time, for jobs needing it within the minutes; checked every 10 minutes, and once per job and component.
  *An NCR action is overdue*: once an open action of an NCR at the site is past its due day (at the site's midnight), with what it is and its
  owner; for the whole site only.
- You can only add alerts about what you can see at the site (machines for stops, the floor for andon, quality for SPC alerts and NCR actions, inventory for
  holds and shortages), and they are only sent while you still can; the list says when one isn't.
- E-mails go to the address on your user. Without a mail server, or without an address, nothing is sent; the page says so. An alert added
  now is about what happens from now on, not before.

### What each role can change

A new Pulaski starts with these roles. Your administrator may have changed them or added others: the **Roles** and **Permissions** tabs of **Configure → Users** show what each role may do today.

| Role | Can change |
| --- | --- |
| **Administrator** | Everything, at every site, including users and roles. |
| **Planner** | The schedule (place, sequence, pin, assign and cancel jobs, and replace one with a partial batch); work orders (create, dispatch, un-dispatch, firm, cancel, close); planning rates; changeover rules; materials; allocating lots to jobs. |
| **Supervisor** | Start, pause, complete and reopen jobs; report production and reverse reports; record and approve labor; code machine stops, and enter, split and merge them; receive, move, ship, hold and release lots; allocate lots to jobs; stock counts; approve or reject partial batches; record quality results; request maintenance and start and close requests; give checklists their second sign-off. |
| **Operator** | Start, pause and complete jobs; report production; code machine stops; receive and move lots; clock themselves on and off; record quality results; request maintenance. |
| **Quality** | Hold and release lots; correct inventory (write up, write off, correct, reverse, split, merge); allocate lots to jobs; approve or reject partial batches; record lot attributes and certificates; maintain lot attribute definitions; back-date inventory movements; maintain characteristics and specifications; record quality results, leave them out and reject lots on quality; write and release work instructions; give checklists their second sign-off; approve releases. |
| **Manager** | Reopen jobs; cancel and close work orders; approve labor; reason codes; shift schedules; people and crews; shop-floor terminals; lot attribute definitions; the site's settings. |
| **Auditor** | Correct inventory, including reversing consumption, splits, merges and transfers; reverse production reports; stock counts; back-date inventory movements. Can't plan or run jobs. |
| **Process engineer** | Processes, BOMs, which lines can run each material, changeover rules, characteristics and specifications, and work instructions; approve releases. |
| **Maintenance** | Start and close maintenance requests, and raise them; code machine stops; answer andon calls; write handover notes. |
| **Edge node** | Nothing on screen: it is for the user a Pulaski Edge node signs in as, to read its configuration and forward machine data. |

Equipment (with machine profiles and edge nodes), units and retrying or skipping deliveries to the ERP (**Integrations**) belong to the Administrator. Someone with two roles can do what either allows.

When you can't change something, Pulaski leaves out the button, or switches the fields off and says *You can view this, but changing it needs permission to …*. If you should be able to, ask an administrator. A switched-off button is grayed out the same way everywhere. When it is off because of the state of something, such as a lot on hold, a shift that hasn't ended or a file not checked yet, it says why: point at it, or move to it with the Tab key, and a small note appears; screen readers read the note with the button. A button that is off only while Pulaski saves says nothing and comes back by itself. Permissions held at one site count only there: on the **Schedule**, at a site where you can't plan, jobs don't drag and a job's panel shows a lock note instead of *Move*, *Pins* and *Cancel*; where you can't manage work orders, cards don't drag between Kanban columns and an order's panel has no status buttons.

### What each role can see

You see a screen, and its entry in the menu, only if one of your roles lets you see it at your site. A permission to change something includes seeing it: someone who may start jobs sees the Floor. A new Pulaski starts with these:

| Role | Sees |
| --- | --- |
| **Administrator**, **Auditor**, **Manager** | Every screen (Users only for Administrator). |
| **Planner** | Overview, Work orders, Schedule, Floor, Machines, Inventory, Quality, Materials, Processes and BOMs, Specifications, Work instructions, Changeovers, Site settings (to look at). |
| **Supervisor** | Overview, Work orders (to look at), Floor, Machines, Inventory, Quality, Specifications and Work instructions (to look at), Labor, Site settings (to look at). |
| **Operator** | Overview, Work orders (to look at), Floor, Machines, Inventory, Quality, Specifications and Work instructions (to look at). |
| **Quality** | Overview, Floor, Machines, Inventory, Quality, Materials, Specifications, Work instructions. |
| **Process engineer** | Equipment, Materials, Processes and BOMs, Specifications, Work instructions, Units, Schedule, Changeovers. |
| **Maintenance** | Overview, Floor, Machines, Equipment and Work instructions (to look at). |

- **Start page.** After signing in you land on the Overview, or, if you can't see it, the first screen in the menu you can.
- **A screen you can't see** isn't in the menu, links to it show as plain text and buttons for it are left out. Typing its address shows **Page not found**.
- **No screens at all.** Someone whose roles let them see nothing lands on **Nothing to see here yet**, with a button to sign out. Ask an administrator for a role.
- **Sites.** A role held at one site shows that site's data only: other sites' work orders, lots, lines and schedules aren't listed.

### Let in someone from your organization's sign-in (administrators)

1. Ask them to sign in once with **Sign in with …**. Pulaski adds them and tells them they have no role yet.
2. On **Configure → Users**, they show **Awaiting a role**. Click **Change**, choose a role and site under **Add a role…**, and click **Add**.
3. Ask them to sign in again.

Their name and e-mail come from your organization's sign-in and are kept up to date at every sign-in; they have no Pulaski password to reset. Setting up the sign-in is a job for whoever runs Pulaski: see `deploy/identity/README.md`.

### Add a user (administrators)

1. Open **Configure → Users** (the **Users** tab) and click **New user**.
2. Fill in **User name** (letters, digits and `. - _ @`), **Name**, an optional **E-mail** and a **Temporary password**.
3. Choose a **Role** and, for every role but Administrator, the **site** they hold it at.
4. Click **Create user**, and tell them the user name and temporary password yourself: Pulaski sends nothing.

### Change a user's roles, details or password (administrators)

1. On **Configure → Users**, **Users** tab, click **Change** on the user's row.
2. **Details:** change the name or e-mail, or untick **Can sign in** to switch them off; for a Pulaski account, tick **Service account** if a machine (such as the edge) uses it. Click **Save details**.
3. **Roles:** choose a role (and site) under **Add a role…** and click **Add**; click **Take away** to end one. For a role allowed at every site, the site list also offers **Every site** (if you may grant roles everywhere).
4. **Password:** type a **Temporary password** and click **Reset password**. They must change it when they next sign in; this also unlocks them.

### Languages and translations (administrators)

**Configure → Languages** holds the languages the screens can show and their translations. English is what the screens are written in;
every other language is a translation of its terms (each piece of English text).

1. To add a language, type its **Code** (such as *fr*, or *pt-BR*) and its **Name** as its speakers write it (*Français*), and click
   **Add a language**.
2. Click **Translations** on a language. **Untranslated** lists the terms it doesn't have yet; **All** lists every term. **Search** finds a
   term in either language.
3. Type translations and click **Save changes**. A term without one shows in English.
4. To work in a spreadsheet, click **Export CSV**, fill in the *translation* column, and choose the file under **Translations file**: Pulaski
   checks it and says how many would change, then **Import** applies it, all or nothing.
5. **Switch off** stops offering a language; whoever chose it sees English. Its translations are kept.

In detail:

- Keep a term's placeholders, `{0}`, `{1}` and so on, in the translation: they stand for the values the screen fills in. A translation that
  loses or adds one is refused.
- Pulaski ships Spanish. At each start it adds the terms Spanish doesn't have yet and updates its own texts, but never one you changed or
  cleared here; *Pulaski's translation* marks those still as shipped. Clearing a translation brings back the English.
- Changing translations needs *languages: manage*, which only administrators have until a role is given it. Changes reach the screens
  within a minute.
- The terms are the screens' text and the server's messages. A message such as *Job {0} not found.* is one term; the screen fills in the
  values. A status inside a message (*Job J-1 is {1}…*) shows as that status's own translation, starting lower case, so in some languages
  it may not agree with the words around it; word the translation so it reads well. Some messages carry a smaller term of their own, such
  as a qualification's *not held* or a terminal action's *report production*: translate those on their own too.

### Add or change a role (administrators)

1. Open **Configure → Users** and the **Roles** tab.
2. Click **New role**, or **Change** on a role's row.
3. For a new role, type a **Code** (lower-case letters, digits and `_`, such as `line_lead`; it can't change later) and a **Name**. Add what it's for if you like.
4. Under **May change**, tick each permission the role should have. They are grouped by area: Planning, Floor, Inventory, Configuration, Administration.
5. Click **Create role** or **Save role**. Then give the role to people on the **Users** tab.

Tick **May be held at every site** for a role that some people need at all sites at once, such as a regional quality lead. Leave it off for roles that belong to one plant. It can't be switched off while someone holds the role at every site.

To stop using a role, untick **Active** and save. A role someone holds can't be switched off: give them another role and take this one away first. **Administrator** has every permission and can't be changed.

### Give roles permissions, one at a time (administrators)

1. Open **Configure → Users** and the **Permissions** tab. Each row is a permission, grouped by area; each column is a role.
2. Tick a box to give the role that permission; untick it to take it away. It is saved straight away and applies to everyone who holds the role.

### In detail

- **Viewing permissions** are listed first on the Permissions tab, under *Viewing*: one per screen (Overview, Work orders, Schedule, Floor, Inventory, Equipment, Materials, Processes and BOMs, Units, Reasons, Shifts). A view a role gets with a change permission shows ticked and locked, with what it comes with when you point at it.
- **Permissions belong to Pulaski; roles belong to you.** The list of permissions is fixed by the version of Pulaski you run, and grows when new screens need one. Roles, their names and which permissions they hold are yours to change. Administrator always holds every permission, including new ones.
- **Site pickers follow the screen.** A screen's site picker lists only the sites where you may see that screen, so a role held at one site doesn't offer the others. Pulaski remembers the site you chose: a screen that can't show it opens on its own first site, and the next screen that can show it goes back to it. The site lists for a new work order or a new process offer only the sites where you may create one. Choosing where a role applies, on **Users**, still lists every site.
- **Changes to a role apply at once** to everyone who holds it, on their next click.
- **Every user needs a role.** A new user comes with one, and a user's last role can't be taken away: add another first, or switch them off.
- **Someone stays administrator.** The last active administrator can't lose the role or be switched off, and nobody can switch themselves off.
- **Roles are held at a site.** A Supervisor at PLT1 can start jobs at PLT1 only.
- **Or at every site.** A role allowed it can be given for every site at once: it then allows its own permissions at every site, now and any site added later, and nothing more (only Administrator holds every permission). Such grants show as *Role · every site*, highlighted on the Users tab. Giving or taking one away needs the **Give or take away a role held at every site** permission, held at every site itself, so someone who manages one site's users can't reach the others. Only Administrator holds it to start with. Something that belongs to no site, such as a reason for every site or an enterprise-wide process, needs the permission at any site.
- **Partial batches:** whoever reported a partial batch can't approve or reject it; the job page says *You reported it: someone else decides*.
- **History** names who did what by user name, such as *oli.operator* on a lot's history.
- **Changes apply at once.** Switching someone off or taking a role away takes effect on their next click, without waiting for their session to end.
- **Ended, not deleted.** Taking a role away from someone ends it, and a role or permission is switched off rather than deleted; Pulaski keeps who held which role when, and who changed a role.
- **Your organization's sign-in.** People who use it are known by who it says they are, not by name or e-mail, so a renamed person keeps their history. Switching them off in Pulaski keeps them out even when the sign-in lets them in. Roles always come from Pulaski.
- **Passwords only for administrators.** Whoever runs Pulaski can set it so that, besides your organization's sign-in, only administrators and service accounts use a Pulaski password. Anyone else trying one is told to use the organization's sign-in. An administrator's password always works, so you can get in if the organization's sign-in is down.
- **The first administrator** is created when Pulaski starts with no users, from the `Security:BootstrapAdmin` settings, with a temporary password to change at the first sign-in.
- **For integrators:** the API takes a bearer token from `POST /api/auth/sign-in`; every other call needs one, and a call without the permission is refused with 403 and the permission in words.


### System health (administrators)

**Configure → System health** shows, on one page, what is unhealthy in the parts of Pulaski that run by themselves, and where to put it
right.

1. Open **Configure → System health**. The line at the top says whether everything checked is healthy, or how many items fail or need a look.
2. Sections with a problem come first: **Database**, **Background services**, **Edge nodes**, **Delivery feeds**, **Scheduled connections**,
   **OEE rollups** and **E-mail**. Each item is **Failing**, **Needs a look**, **Healthy** or **Off**, with what was found.
3. Click an item's name (an edge node, a connection), or **Open the page** beside a section, to go where it is fixed.

In detail:

- **Database:** *failing* when the database is older than this version needs (run `db/migrate.sh`), or a history table has no partition for
  next month yet (writes there would fail once it starts; the API normally creates them 3 months ahead).
- **Background services** (partition upkeep, the delivery relay and its clean-up, OEE rollups, allocation and planning rate refreshes,
  preventive maintenance, process value sampling, the connection scheduler, qualification notices, e-mail alerts, live updates): *failing* when
  the last round failed (with its error), *needs a look* when no round has finished for three of its intervals, *off* when switched off in
  configuration. They are known since the API last started, which the page says.
- **Edge nodes:** healthy when heard from within 2 minutes, *needs a look* when quiet for longer or never connected, *failing* after an hour.
- **Delivery feeds:** *failing* while a delivery has failed (it holds back the subject's later events until it is retried or skipped on
  Integrations), *needs a look* when the oldest waiting delivery is over 15 minutes old.
- **Scheduled connections** (material and people imports): *failing* when the last run failed, *needs a look* when it found problems and
  changed nothing, or a scheduled run is an hour late.
- **OEE rollups:** sites with production days changed more than a day ago and still not rolled up. OEE reports stay right meanwhile; long
  ones work those days out from the history.
- **E-mail:** *off* without a mail server; *failing* when the last e-mail couldn't be sent.
- The page refreshes every 30 seconds. Seeing it needs *See system health*: the administrator has it, and no other role does until it is
  given on the Roles tab.

---

### Audit trail: who changed what (administrators)

Every change in Pulaski is recorded, with who made it and when. **Configure → Integrations → Audit trail** shows them.

1. **One thing's history:** on its page, click **History**. It's on a material (and on each of its capabilities), a piece of equipment
   (below its properties), a process, a lot, a work order (in the actions of one selected order) and a user. The audit trail opens with
   every recorded change to it, newest first, all time.
2. **Search:** choose an **Area** (such as *Equipment capability*), type **Who** (a name or user name) or **Text in the change** (a code, number
   or name, such as `EXT-01` or a lot number), choose **From** and **To** days, and click **Search**. **Older changes** shows the next page.
3. Click **Details** on a change. A change to something that already existed shows only the fields that changed, **Before** and **After**;
   something added shows its fields. **As recorded** shows the change exactly as it was recorded.

In detail:

- **Who** is the user's name with their user name, or what made the change when no person did: a terminal (`terminal:…`), the edge's user,
  or a background job (`system`).
- **Times** are when it happened, in the site's time; the details also give when it was recorded.
- A search looks back 30 days unless you choose dates, and at most 92 days at a time; a thing's history has no limit.
- **Who may see it:** permission to browse the audit trail, held at every site, since a change isn't recorded against one site (the
  Administrator role has it; it can be given to another role held everywhere). Others don't see the **History** links or the menu entry.
- Numbers compare by value, so a rate saved as `500.000` and shown as `500` isn't reported as changed. Field names are as recorded, in English.
- Nothing here changes anything: the record is append-only, and a correction is a new change.

### Reports and dashboards in other tools

Pulaski's screens have their fixed reports (OEE, production by shift, labor, inventory). For your own dashboards and reports, use a business
intelligence tool such as Power BI, Excel or Tableau, or copy the data into a warehouse such as Snowflake. Pulaski gives them a **read-only
reporting layer**: views in the database's `report` schema that keep their meaning as Pulaski changes, with figures worked out the way the
screens work them out (OEE, good and scrap).

1. Ask whoever runs Pulaski's database for a reporting login. They create it as a member of the `pulaski_report` role, which can read the
   reporting views and nothing else.
2. In your tool, connect to the PostgreSQL database `pulaski` with that login, and pick tables from the `report` schema.
3. Follow the analysts' guide, `design/reporting.md`: what each view holds, how they join, how OEE adds up (add the hours, never average
   the percentages), and sample queries.

In detail: the login can't see users, passwords or the event log, and can't change anything. OEE by shift and day is there for days that are
final (up to two days ago); today's figures are on **Machines → Utilization and OEE**. A report's times are sent in UTC; each site's time zone
is in `report.site`.

## 16. Shift schedules

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
6. Under each shift, **Planned breaks** lists its breaks, such as lunch: **+ Break** adds one; give it a name, the time it starts, its minutes and whether it is **paid**. A break must fall inside its shift and not overlap another; up to six.
7. Check **The next 7 days** below the grid: it lists the shifts as they will fall, and the hours per cycle. Problems, such as two shifts overlapping, show there instead.
8. Click **Create schedule**.

Planned breaks are a plan, not a record: at that time terminals offer them first and **Operate → Labor** shows them, and **Shift review** says who was on all through one without taking a break. A break is recorded only when someone starts it.

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

## 17. Labor, people and crews

Who worked on which line, for how long, and against the standard crew. Supervisors check their crew onto a line at the start of a shift, operators can clock themselves on, and at the end a supervisor approves the shift's labor. Pulaski doesn't do payroll or attendance: export the hours for that.

### Ideas to know first

- **People and crews.** Everyone whose time is recorded is a **person**, with an employee number and usually a **crew**. A person needn't sign in to Pulaski; link one to a Pulaski user to let them clock themselves on.
- **The crew on shift.** Each shift in a [shift schedule](#16-shift-schedules) can name its crew, so Pulaski knows who should be on. A rotation is several shifts at the same time on different days, each with its crew.
- **Kinds of time.** *Direct* is running the line, *setup* is getting a job ready (it names the job), *indirect* is time off production such as cleaning (it needs a reason), *training* is training.
- **Jobs change, people don't re-clock.** Direct time on a line counts towards whatever ran on it while the person was there. Time on a line with nothing running is shown as *idle*.
- **A share of their time.** Someone tending two lines is on each at 50%. A person's time adds up to at most 100%.
- **Shifts and approval.** Time belongs to the shift running when it started (or starting within two hours). Once a shift has ended, a supervisor approves it: open time ends when the shift ended, and the shift is locked. After that, a change is a **correction** with a reason.
- **Breaks** are recorded on their own, paid or unpaid. A break's time comes out of the hours of every line the person is on, and is shown apart in the reports; the time they were clocked on stays as it was.
- **Terminals and badges.** A shared tablet or panel PC can be a **terminal** where people badge to clock on and off and take breaks, without signing in to Pulaski. Each person has one badge; a terminal can also ask for a PIN.
- **Qualifications** are the skills and certificates a line needs, such as a forklift license. Each person holds them for a set of days. Someone checked onto a line without a qualification it needs is still checked on, and Pulaski warns.

### Set up people and crews

1. Open **Configure → People**. With more than one site, choose the site at the top.
2. On **Crews**, type a **Code** (such as `RED`; it can't change later) and a **Name**, and click **Add crew**.
3. On **People**, fill in **Employee no.**, **Name**, the **Crew** and, if they will clock themselves on, the Pulaski **User name** they sign in as. Click **Add person**.
4. In **Configure → Shifts**, set the **Crew** of each shift.
5. Someone who also works at another site: on **People**, click **Change** under **Also works at**, tick the other sites and click **Save sites**. There they show *From* their own site in the lists, can be checked in, clock on and badge at the terminals; their time there belongs to that site and is approved there. Their details stay at their own site. A site they are clocked on at can't be taken off.

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
   | `qualifications` | The qualifications they hold, by code, separated by `;`, such as `FORKLIFT; FIRST-AID`. Each may give its grant's days as `CODE:valid-from:expires-on` (`yyyy-MM-dd`, either may be blank). |

   For SCIM, each user's `employeeNumber` (from the enterprise extension) is the employee number, `displayName` (or the parts of `name`) the name, `active` whether they work here, and `externalId` (or `id`) their key.
5. Click **Choose File** and pick the file. Pulaski checks it straight away and saves nothing yet: each row shows **Create**, **Update** (with what changes), **Unchanged** or **Error** with the reason.
6. Fix any errors and pick the file again, then click **Import N people**. The list updates, and imported people show their system's name next to their employee number.

Import the same file, or next week's, as often as you like: people are matched on their key, so nobody is added twice.

**In detail**

- **Leavers.** Tick **The file is all of the system's people** (with an external system named) and the site's people from that system who aren't in the file are switched off; each is listed. Someone still clocked on stays on, with a note, and a later full import switches them off. People kept by hand in Pulaski are never touched, and a file with no people switches no one off.
- **Qualifications** are added, never taken away. A listed one is granted unless the person already holds it: with no dates, a grant valid today is enough, and a new one runs from today for the qualification's usual validity; with a later expiry than the one they hold, it is renewed with a new grant. Importing the same file again grants nothing. Leaving a qualification out of the file doesn't revoke it; revoke it on the person's page. A code the site doesn't have is an error on the row. SCIM users carry no qualifications.

### Import people from a connection

Instead of a file, Pulaski can read people straight from your HR or identity system: its **SCIM 2.0** users, an OData, REST or SOAP service, or a query of a PostgreSQL database. The connection belongs to the site.

1. On **Configure → People**, choose the site and click **Import file**. **People connections** is under the file import.
2. Click **Check** on a connection: Pulaski reads everyone and says what an import would do, saving nothing. Then click **Import**.
3. **Set one up** (needs *integrations.manage* at the site): click **Add connection**, give it a **Code**, **Name** and **External system**, pick the **Kind**, fill in its address (for SCIM, the base address before `/Users`, and optionally a filter such as `active eq true`), and for other kinds the fields as for [material connections](#import-materials-from-a-connection), with `employee_number`, `name` and `external_key` required and `qualifications` optional. Tick **Switch off people the source no longer has** to treat it as a full list. Choose when it **Runs** (see below) and click **Save**.

Running it needs permission to maintain people at the site. Credentials go in the API's configuration under `MaterialConnections:<code>`, as for materials.

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
2. The terminal greets you and shows what you are on. Press **Clock on** and a line (the highlighted one is this terminal's usual line), **Clock off**, **Start break**, **Start paid break** or **End break**. When a break is planned now in your shift, **Start lunch** (or its name) comes first.
   For time that isn't production, first press **Setup**, **Indirect** or **Training**, then the line. Setup is for the job on the line (the one running, else the next one planned); **Indirect** first asks what for, from the site's indirect reasons.
3. It says what it did, and any warning (such as a missing qualification), then waits for the next person. **Not me / cancel** goes straight back.

### Set up a terminal (managers)

1. Open **Configure → Terminals**. Type a **Code** (it can't change later) and a **Name** such as where it hangs. Under **Offers the lines of**, choose the site, an area or a line, then the **First line offered**, and whether people identify with **Badge** or **Badge and PIN**. Choose its **Language** and, if you like, its **Region** (such as *es-MX*) for the people at that line: its screens follow them, not whoever badges in. Click **Register terminal**.
2. Click **Enrollment code** on its row. A ten-character code shows, once, with the time it runs out (15 minutes).
3. On the device, open the address the page shows (ending `/terminal/enrol`), type the code and press **Enroll**. The device now shows the badge screen; leave it there.
4. To stop a terminal working (lost, stolen, moved), click **Change**, untick **Active** and save. To move it to another device, issue a new code and enroll the new device: the old one stops working.

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
6. **Notice days** on a qualification (30 when left empty) sets how long before a grant expires Pulaski e-mails about it: once, to the person (if their Pulaski user has an e-mail address) and in one list per site to whoever maintains people there. It needs the e-mail server set up by whoever runs Pulaski; without it nothing is sent. A grant already renewed by a longer one isn't mentioned.

### Review and approve a shift

1. Open **Operate → Labor → Shift review** (or click **Labor to approve** on the Overview) and choose the **Day**. Each shift that has labor is a button; one with *approved* is locked. The first shift that has ended and isn't approved opens by itself.
2. Check the people, times and hours. Click **Change** to fix a start, end or share, the kind of time (direct, setup, indirect, training), its job or its indirect reason, or **End** for someone still on. Setup needs a job and indirect a reason. Above the table, the shift's planned breaks are listed, with anyone who was on all through one and recorded no break; add the break if they took it.
3. Once the shift has ended, add a note if you like and click **Approve shift**. Anyone still on is clocked off at the end of the shift.
4. To fix an approved shift, click **Correct** (change the times, share, kind, job or reason) or **Remove**, say why, and click **Save**. The original row stays, struck through; the hours count the correction.

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
- **Terminals:** a terminal records only the badged person's own time: direct, setup, indirect or training time on a line it offers, ending their own sessions, and their breaks, under the same rules as clocking yourself on. People of another site listed as also working here badge here too. Shift review shows *Badged at* and the terminal for that time. A badge that is unknown, revoked or from another site is just *not recognized*. Five wrong PINs in a row lock the badge for 5 minutes. A badge scan is good for 2 minutes; after that, badge again. Pulaski keeps only a scrambled form of badge numbers and PINs, so they can't be read back: to change a PIN, issue the badge again.
- **Who:** registering terminals needs permission to register and enroll terminals (Manager); issuing badges, permission to maintain people and crews. People badging at a terminal need no Pulaski permission of their own: the terminal is trusted for that.
- **Who:** qualifications and grants need permission to maintain people and crews; requiring them on equipment needs permission to maintain equipment. Recording breaks for others needs permission to record labor; taking your own, permission to clock yourself on.

---

## 18. Site settings

**Configure → Site settings.** What applies to a whole site: who places changeovers on the schedule, how far production reports may go, the OEE it aims for, its labels, how it allocates lots, how its planning rates follow run history, and the holidays and shutdowns when the site doesn't work. Each part has its own **Save** button.

### Ideas to know first

- **Changeovers** are the time a line needs between two materials. *Automatic* (the default) lets Pulaski add them to the schedule; *Placed by planners* leaves it to the planners, and Pulaski warns where one is missing. See [Changeovers](#changeovers).
- **Reporting tolerances** keep production reports believable. The output tolerance is a **limit**: a report that would take a run past its length × the line's ideal rate × the tolerance is refused. The consumption tolerance is a **warning**: using more of a component than planned × the tolerance is recorded, with a warning to check it. Both are 110% to start with.
- **OEE targets** say what availability, performance, quality and OEE should reach. The site's targets apply to all its equipment; a machine or line can have targets of its own, which replace the site's. Each set of targets applies from a day on, so earlier days keep the targets they had. The OEE report shows each figure against its target.
- **Calendar exceptions** take time out of the calendar: a holiday, a planned shutdown, a week with no demand. One set on the site covers everything in it; one on an area or line covers that and everything below it, whichever [shift schedule](#16-shift-schedules) it follows. Planning places no work in it.

### Lot allocations

Under **Lot allocations** on Site settings (you need permission to change site settings to save):

1. **Keep allocations current:** tick it for every open job's allocations to be refreshed on their own, a minute or so after lots, jobs or orders change. Below it, *Last refreshed* says when that last ran.
2. **Hard reservation:** tick it to refuse consuming stock allocated to another job, instead of only warning.
3. **Preferences, in order:** tick those that should decide among lots expiring the same day, and put them in order with ↑ and ↓:
   - *Already at the job's line*;
   - *In a container* (one move fetches it);
   - *A lot that covers what is needed alone*;
   - *Part lots first*, so they get used up.
4. Click **Save allocation settings**.

**In detail**

- **Expiry always comes first.** Preferences only decide among lots expiring on the same day, or never. The oldest receipt decides after them. *A whole lot* and *part lots first* pull opposite ways; the earlier one in the list wins.
- **The background refresh** does what **Refresh allocations** on the Shortages tab does, as *system*. It waits until changes have been quiet for a minute, or refreshes anyway when the last refresh is 15 minutes old and changes keep coming. Everything is off to start with.
- **Hard reservation** has no override: whoever needs the stock releases or moves the other job's allocation first. Automated consumption at feed points takes only the free stock, and lists the rest as a shortfall.
- Each save is recorded as `pulaski.site.allocation_settings_set.v1`.

### Planning rates from run history

Under **Planning rates from run history** on Site settings (you need permission to change site settings to save):

1. **Take out stops:** tick it for reviews to start with stops taken out, and for the scheduled recalculation to take them out.
2. **Recalculate on a schedule:** tick it for Pulaski to work out every line's planning rates from run history on its own, **Every** so many days (7 to start with), over the runs that ended in the last so many days (90). Below it, *Last recalculated* says when that last ran.
3. **Propose changes over, %:** a rate is proposed only when it moves the calculated rate by more than this (5% to start with), or would change its basis.
4. **Accept small changes without review:** tick it, and set **Up to, %** (10 to start with), for proposals within that change to rates already from run history to be accepted at once.
5. Click **Save planning rate settings**.

**In detail**

- **Nothing is accepted without review unless you tick it.** Even then, a line still planning with its ideal rate has its first rate from run history reviewed, and a bigger change waits. A modified rate always stays in force; accepting changes only the calculated rate.
- **What it does** is what **Work it out** does, as *system*, for each line's planning rates at the site: with too little history it proposes nothing. A newer proposal replaces the one waiting (*Replaced*). Proposals are on [Planning rates](#review-proposed-planning-rates).
- **When:** the API checks every 15 minutes which sites are due (`RunStandards:Refresh:PollMinutes`; `RunStandards:Refresh:Enabled` false switches it off everywhere). Everything is off to start with.
- Each save is recorded as `pulaski.site.rate_refresh_settings_set.v1`; proposals as `pulaski.run_standard.proposed.v1`, rejections as `…proposal_rejected.v1`, and accepting as `…recalculated.v1`.

### Review proposed planning rates

**Configure → Planning rates** lists the site's proposals: those waiting first, then those decided in the last 30 days.

1. Each row shows the line and material, the **Proposed** rate and how far it is from the calculated rate when proposed, the **Calculated now** rate with its basis (and a modified rate in force), what it was worked out from (basis, runs, hours, stops taken out, days) and, for one waiting, why it waits for review.
2. Click **Accept** to make it the calculated planning rate, or **Reject…**, optionally say why, and click **Reject**. The rate stays as it was.

**In detail**

- **Checked again when you accept.** The rate is worked out again over the same days and basis. If runs ended since and it changed, Pulaski says so and nothing changes: review it on the material, or wait for the next proposal.
- **Who:** anyone who can see materials or the schedule sees the list; accepting and rejecting needs permission to set the planning rate (Planner, to start with).

### Labels and printers

Under **Labels** on Site settings (you need permission to change site settings):

1. **Label format:** *Pulaski* (Code 128 only), *GS1-128* or *GS1 DataMatrix*. See [Lot, container and location labels](#lot-container-and-location-labels).
2. **GS1 company prefix** (6 to 12 digits, as GS1 issued it) and **SSCC extension digit**: once set, every new container gets an SSCC. The line beneath says which serial the next one takes.
3. **Default printer**, chosen first when someone prints. Click **Save**.

**Printers.** Click **Add a printer**: a code and name, then how it **Sends**:

- **Raw TCP:** the label goes straight to the printer's **Host** and **Port** (label printers usually listen on 9100). The API server must be able to reach it.
- **Drop folder:** the label is written as a file into a folder the API is configured with, for label software (BarTender and the like) that watches it. The choice lists the folders in the API's configuration; the path never comes from the screen.
- **BarTender (web service):** each label is sent to BarTender as a BTXML script, and BarTender prints it with one of its own documents (see below).

and, for raw TCP and drop folders, the **Templates** it uses: a set of template files on the API server (*zpl* for 4 × 6 in labels at 203 dpi, *csv* for label software, or your own). **Change** a printer to edit it or switch it off.

**Print through BarTender.** In BarTender's Integration Builder, make an integration with a web service trigger and a **Print BTXML Script** action that runs the script it receives, and deploy it. Then add a printer in Pulaski:

1. Choose **Sends: BarTender (web service)**.
2. Type the integration's **BarTender web service address**, such as `https://bartender.example.com:8080/Integration/Print/Execute`.
3. Optionally type the **Printer in BarTender** (its Windows name); left empty, each document prints to its own printer.
4. Under **BarTender document (.btw) for each kind of label**, give the path BarTender opens for each: **Default (any label)**, and, where they differ, **Lot labels**, **Container labels** and **Location labels**. A kind without its own document uses the default.
5. Keep the **BTXML template** as it is, or adapt it (**Use the standard template** puts it back). Click **Save printer**.

In detail:

- Pulaski sends one script per label, and the copies as BarTender's own copy count. It reads BarTender's answer: an error message in it, or a refused request, is shown to whoever printed, saying how many labels went before it.
- The standard template fills the document's **named data sources** from the label: `Code` (what the barcode carries), `Title`, `Line1` to `Line10`, `Kind`, `Format`, `Gs1Hri` (GS1 data with its AIs in brackets, for a GS1 barcode in BarTender) and every value of the label's kind, such as `LotNumber`, `MaterialCode`, `MaterialName`, `Quantity`, `Uom`, `Expiry`, `SupplierLot`, `Gtin`, `ContainerCode`, `Sscc` or `LocationCode`. Name the document's data sources the same way.
- In your own template, `{{BtwPath}}`, `{{NamedSubStrings}}`, `{{PrintSetup}}`, `{{Printer}}`, `{{Copies}}`, `{{JobName}}` and any value by name (`{{LotNumber}}`) are filled in, escaped for XML. Saving checks that the template is a BTXML print script and that each document ends in `.btw`.
- If the web service asks for a user name and password, or a key in a header, whoever runs Pulaski sets them in the API's configuration (`Labels:BarTender:<printer code>`); they are never kept in the database or shown here.

**Supplier barcodes.** GS1 barcodes are always read. For a supplier's own, click **Add a supplier format**:

1. Give a code and name.
2. Write the **Pattern**: a regular expression whose named parts say what each piece is: `lot`, `supplier_lot`, `material` (a material code), `gtin`, `qty` and `expiry`. For example, `(?<material>[A-Z0-9-]+)\|(?<supplier_lot>\w+)\|(?<qty>\d+)` reads `PVC-K67|B4471|500`.
3. Give the **Expiry format** when it has an expiry, such as `yyMMdd`.
4. Scan or type a sample under **Try it on a scan** and click **Try** to see what it reads. Click **Save format**.

Formats are tried in their **Order**, after GS1; the whole scan must match.

### Approval before release

**Approval before release** asks for a second person's approval before a draft is released, for each kind of document: **Processes**,
**BOMs**, **Specifications**. Tick the kinds and click **Save approval settings**.

- A process follows its own site's setting. A BOM or specification needs approval when any site that can make its material asks for it: a
  site with a line that has a capability for the material, or (for a BOM made for a process) the process's site. An approver may decide it
  if they may approve releases at any one of those sites.
- Turning it on doesn't affect what is released already. A draft that waits when it is turned off still waits until it is decided or
  withdrawn; after that, drafts are released directly again.
- Changing it needs permission to manage the site's settings.

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

### Set OEE targets

1. Under **OEE targets**, the table shows the targets in force today; **Show earlier and ended targets** shows all of them.
2. To set targets, choose what they are **For** (**Site (default)** or a piece of equipment), the day they start (**From**, today to begin with), and type the **Availability**, **Performance**, **Quality** and **OEE** percentages you want. Leave a box blank for no target.
3. Click **Save targets**. **Change** on a row fills the form with its targets, from today.
4. To stop a target, click **End today**: from today, equipment follows the site's targets again (or, for the site's, there are none).

**In detail:** a new start day ends the targets before it the day before; starting on the same day as the latest targets changes them instead. Targets can't start before the latest ones already set, and they are never deleted, so the report always measures a day against the targets it had. Each figure is above 0 and at most 100, with one decimal at most. A machine's own targets replace the site's whole: a figure left blank there has no target, even if the site has one.

### Changeover rules

**Configure → Changeovers** holds the times the schedule uses when a line changes from one material to another. Everyone who sees the schedule or the materials sees it; changing it needs permission to maintain changeover rules (Planners and Process engineers start with it).

1. Open **Configure → Changeovers**. With more than one site, choose the site at the top. The list shows the rules switched on; tick **Switched off too** for the rest, choose a line, or type part of a material or class code to narrow it.
2. To add a rule, under **Add a rule** choose the **Line** (or **All lines**), what it changes **From** and **To** (*Anything*, a *Material* by its code, or a *Class*), the **Minutes** it takes and a **Note** if you like, then click **Add rule**.
3. To change a rule's time or note, click **Change**, edit them and click **Save**. To stop using it, click **Switch off**; **Switch on** brings it back.
4. To load many rules, click **Import from a file**, pick a CSV file and check what it would do, then click **Import**. **Download a template** gives the columns. **Export CSV** saves the site's rules in the same columns, so you can edit them in a spreadsheet and import them back.

**In detail**

- **Which rule applies.** For each change on a line, the most specific rule that fits wins: a material beats a class, which beats *Anything*, counted on both sides; then the side being changed *to*; then a rule for this line over one for all lines; and at a tie, the longer time. With no rule, the line's **Standard changeover** for the incoming material (on the material's capability) applies; with neither, the change takes no time.
- **One rule per pattern.** Only one rule may be switched on for the same line, from and to; Pulaski refuses another and names the one there. What a rule matches can't change: switch it off and add another. Rules are never deleted, and every change is recorded as an event.
- **The checks.** Each side is a material or a class, not both; a material to itself is refused (the line just keeps running); the time is whole minutes from 0 to 7 days; the line must be an active line of the site that runs jobs.
- **The file.** One rule per row: `line` (blank for every line), `from_material` or `from_class`, `to_material` or `to_class` (blank for anything), `minutes` (required), `note`, `active` (*no* switches the rule off). A row changes the site's rule with the same line and sides, or adds one. A blank `note` cell clears the note; leave the column out to keep notes as they are. Nothing is saved until every row is valid.
- **The schedule** works changeovers out every time it is shown, so a change appears on its next refresh.

### Andon: calls for help

Andon lets an operator call for help to their line: maintenance, quality, materials, a supervisor. Here you choose which kinds of help a line can
call for at the site, and how long a call may wait for someone before it is escalated.

1. On **Configure → Site settings**, find **Andon: calls for help**.
2. Type a **Code** (such as MAINT), a **Name** (such as Maintenance), the minutes after which an unanswered call **Escalates**, and its
   **Order** in the list, then click **Add call type**.
3. **Change** a type to rename it or change its time or order; **Switch off** stops offering it.

In detail: a code is up to 20 letters, digits, `-` and `_`, kept in capitals and fixed once made; escalation is 1 to 480 minutes. A switched-off
type keeps its calls in the history and can be switched on again. Changing these needs **site settings: manage**. A line has at most one waiting
call of each type: calling again while it waits is the same call. A call is open until someone acknowledges it (they have come), then resolved
with what was done; it is escalated while it is still open past its type's time. Calling needs **andon: call** (Operator, Supervisor), answering
**andon: respond** (Supervisor, Quality).

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

## 19. Machine profiles

**Configure → Machine profiles.** A machine profile describes how one kind of machine reports itself: the values its state tag takes and the PackML state each one means, and its own stop, alarm and warning codes with the utilization reason each one counts as. Identical machines share a profile; machines from different vendors or of different models each get their own. You give each machine its profile on the [Equipment](#machine-data) screen.

Machine data arrives through [edge nodes](#20-edge-nodes-collecting-machine-data): the profile turns what each machine sends into PackML states.

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

**Each kind in the file replaces that list:** its rows are added or changed, and entries of that kind the file leaves out are switched off. A kind the file doesn't mention stays as it is, so a file of stop reasons only keeps the states. An example file is `design/samples/machine-profile-example.csv`. **Export CSV** saves the profile's active entries in the same format, to edit and import again.

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

## 20. Edge nodes: collecting machine data

**Configure → Edge nodes.** A Pulaski Edge node is a small program on plant hardware, near the machines. It reads them through an MQTT broker they publish to in **Sparkplug B**, an **OPC UA** server, or straight from an **EtherNet/IP** PLC (Allen-Bradley Logix, Micro800, Omron NJ/NX), keeps what it reads in a buffer on its own disk, and forwards it to Pulaski. If the network or Pulaski goes down, it keeps reading and forwards everything once it can, so nothing is lost.

### Ideas to know first

- **Connections** are where an edge node reads from: an MQTT broker (Pulaski listens as a Sparkplug B host application), an OPC UA server, or a PLC over EtherNet/IP.
- **Tags** bind a signal to equipment: *this* metric is PACK1's state, *that* one its processed count. The address depends on the connection: Sparkplug B `group/node/device:metric` (such as `Plant1/Packing/PACK-01:Status/StateCurrent`), an OPC UA node id (such as `ns=2;s=Line1.State`), or an EtherNet/IP tag name with its type after `|` (such as `Program:Main.State|dint`; `dint` when left out; also `bool`, `sint`, `int`, `lint`, `usint`, `uint`, `udint`, `ulint`, `real`, `lreal`).
- **What Pulaski does with them.** A state value goes through the equipment's [machine profile](#19-machine-profiles) to its PackML state, and every change of state is kept, with the value the machine sent. The machine's first-out stop code is kept until it runs again. Counter readings become counts, in the job running on the equipment. Losing the signal is recorded as *Undefined*.
- **Alarms and warnings.** An alarm or warning tag carries the number of the one active now, as PackML's `Admin.Alarm[].ID` does, and 0 for none. A new number clears the one before and raises it, with its message from the machine profile; 0 clears it. A machine may have several alarm and warning tags. They are kept apart from its state and stops.
- **Process values** (a temperature, a pressure) and **levels** are kept as readings, multiplied by the tag's scale, for looking back over on [Machines](#21-machines-live-status-and-stops).

### Add an edge node

1. Open **Configure → Edge nodes**. With more than one site, choose the site.
2. Type a **Code** (such as `GW-PACK`; it can't change later) and a **Name**, and click **Add edge node**. Its page opens.
3. On **Connections**, give the connection a **Code** and **Name**, choose its **Protocol**, fill in its settings and click **Add connection**:
   - **Sparkplug B (MQTT):** the **Broker host** and **Port**, **TLS** if it uses it, and optionally the Sparkplug **Group id** (to hear only one group), a **Host id** (Pulaski then announces itself, so nodes know it is listening), a **Client id** and a **User name**.
   - **OPC UA:** the server's **Endpoint** (`opc.tcp://plc1:4840`). It signs and encrypts unless **Security** says otherwise, with the best **Policy** the server offers. The edge trusts the server only if its certificate is in the edge's trusted store or matches the **Server thumbprint** you give; **Trust any server** is for a test server only. Optionally a **User name** (anonymous without), how often the server sends changes (**Publishing ms**, 1000) and samples (**Sampling ms**), and where and how deep to look for variables to offer on Seen (**Browse from**, the Objects folder; **Browse depth**, 3).
   - **EtherNet/IP:** the PLC's address (**PLC address**), its **CIP path** (`1,0` for a ControlLogix CPU in slot 0; CompactLogix and Micro800 need none), the **PLC** family, and how often to read (**Poll ms**, 1000) and to wait (**Timeout ms**, 5000).
4. On **Configure → Users**, add a user for the edge (such as `edge-gw-pack`) with the **Edge node** role at the site. Sign in as that user once and set its password, then sign out.
5. On the node's **Setup** tab, copy the configuration into the edge's `appsettings.json` on the edge machine, fill in the user's password (and the broker's or OPC UA server's, if the connection has a user name), and start Pulaski Edge. It runs as a console program, a container, a Windows service or a systemd service: `deploy/edge/README.md` explains installing it as a service.
6. To remove a node from use, click **Delete** on its row or page and confirm. Its connections and tags are switched off with it.

### Bind tags

1. Once the edge runs, open its **Seen** tab: every metric its brokers have announced and every variable its OPC UA servers offer, with the last value. EtherNet/IP tags appear once they have been read, so type those on the **Tags** tab. Type in **Filter addresses** to find one.
2. Click **Bind** next to a metric. Pulaski guesses the role from the name; choose the **Equipment** (equipment of the site that tracks state is offered; for a consumed counter or a level, equipment that holds inventory too, such as a hopper or a silo) and check the **Role**.
3. For a count, set the **Scale** (how many units one count is) and, if the counter wraps, its **Rollover** value. For a process value, set the **Scale** (a temperature in tenths: 0.1) and, if you like, a **Deadband**: a change no bigger than that, in scaled units, isn't recorded straight away. Click **Bind tag**.
4. To type an address instead, use the same form on the **Tags** tab. To change a tag, edit its address, scale, rollover, deadband or **Active** checkbox and click **Save**. To delete a tag completely, whether active or inactive, click **Delete** on its row and confirm.

The edge reads its tags every minute, so new and changed ones take effect without restarting it.

### Watch it work

- The node's heading shows **Forwarding** while it is heard from, **Quiet** after 2 minutes without anything (offline, or nothing changed), and when it was last heard from and its version.
- **Live** shows each bound machine's PackML state (and the value it sent, where different), since when, its unit mode, latched stop code and speed. Click **Refresh** to update. Its stops are on [Machines](#21-machines-live-status-and-stops).
- **Tags** shows each tag's last value.

### In detail

- **Who:** seeing edge nodes needs permission to see equipment; adding and changing them, their connections and tags needs permission to maintain equipment at the site. The edge's own user needs only the **Edge node** role and sees no screens.
- **Sending once.** The edge numbers everything it buffers and Pulaski applies each number once, so forwarding again after a failure changes nothing. A new buffer file starts a new numbering, which Pulaski recognizes.
- **What the edge forwards:** a tag's value when it changes, when it is lost, and the last value seen when the tag is newly bound; a speed at most every 5 seconds; a process value when it moves past its deadband, and at least once a minute while it holds steady or stays within it (`Edge:ProcessValueHeartbeat`). A connection it hears from without a birth certificate is asked for one (a Sparkplug *rebirth*), as is every node its tags name when it connects, so an idle machine's state is known.
- **Stop codes** are latched: a machine that clears its code once acknowledged keeps it in Pulaski until it runs (Execute) again.
- **Counts** are taken across the rollover value; a counter going back without one is taken as reset to zero. Machine counts don't change the job's reported quantities by themselves: a [report from the machine](#report-production-from-the-machine) takes them, once the operator has checked them.
- **Switching a node off** stops Pulaski taking its data and the edge collecting. Switching a tag or connection off stops it being read.
- **Delete behavior.** Deleting a node deactivates it and all its connections and tags. Deleting a tag physically removes its binding, so it disappears from the Tags tab; this works whether the tag is active or inactive. A quality characteristic [sampled from it](#sample-from-process-values) is kept, and its sampling from that tag switched off. The deletion event and historical machine data are retained. Add a new binding if the signal is needed again. Reactivating a node later does not reactivate its connections or tags; switch those back on individually if needed.
- **Without Pulaski**, the edge starts with the last configuration it kept.
- **OPC UA in detail.** One subscription carries the bound nodes; a reading takes the time the server stamped it with. A status that isn't good is recorded as a lost signal. If the server stops answering, the edge records every bound node as lost and reconnects, waiting a second and then longer each time, up to a minute. The edge keeps its own certificate and the servers it trusts in a `pki` folder beside it (`Edge:PkiPath`). A server it refuses leaves its certificate in `pki/rejected/certs`: check it and move it to `pki/trusted/certs`, or put its thumbprint on the connection. The server must trust the edge's certificate (`pki/own/certs`) too.
- **EtherNet/IP in detail.** The edge reads each bound tag every poll interval and records the time it read it (the PLC sends no time). A tag it can't read is recorded as lost once and tried again on every poll. Only single values are read, not whole arrays or structures.
- **Alarm messages** come from the machine profile's **Alarms** and **Warnings** lists, by number, as they were when the alarm was raised. An alarm stays active while the signal is lost: it clears only when the machine says so.
- **Process values are kept 90 days** (`Retention:ProcessValueDays` in the API's settings), dropped a month at a time once a whole month is older. Only numbers are kept.
- **The demo simulators.** The [demo](#try-the-demo) starts them; on their own, `docker compose -f deploy/simulator/compose.yaml up -d` starts an OPC UA server on `opc.tcp://localhost:50000` (Microsoft's OPC PLC; add an OPC UA connection with **Trust any server**, or **Security** none, and bind such as `ns=3;s=StepUp`), a local-only MQTT broker and three PackML simulators: group `Plant1`, nodes `Extruder`, `Saw` and `Packer`, and devices `EXT-01-EXT`, `EXT-01-SAW` and `PACK1`. The Spruiktec simulators number their states their own way (Idle is 1, Clearing 4), not as PackML does, so a machine needs a profile with their values: `deploy/simulator/packml-simulator-profile.csv` is one, which the demo loads as **PACKML-SIM**. A process simulator, node `ExtruderProcess` for device `EXT-01-EXT`, publishes `Process/MeltTemperature` (°C) and `Process/ProfileWidth` (mm): bind them as process values of the extruder. The broker is anonymous and has no TLS: keep it on your development machine.
- **Connect the edge.** Add a Sparkplug B connection on the edge node with broker host `localhost`, port `1883` (or the value of `PULASKI_MQTT_PORT` if 1883 is occupied), and group id `Plant1`. If Pulaski Edge runs in the same Docker Compose network as the broker, use host `broker` and port `1883` instead. Bind the three simulator addresses from the Seen tab, for example `Plant1/Extruder/EXT-01-EXT:Status/StateCurrent`, `Plant1/Saw/EXT-01-SAW:Status/StateCurrent` and `Plant1/Packer/PACK1:Status/StateCurrent`. Useful companion metrics include `Status/UnitModeCurrent`, `Status/CurMachSpeed`, and the `Admin/Prod*Count/0/AccCount` counters.
- **Start a simulated machine.** After a fresh start, wait for its initial `Clearing` transition to finish at `Stopped`. Then Reset takes 1–10 seconds to reach `Idle`; the simulator command tool's `--delay-seconds 12` allows for that before it sends Start. For example, run `dotnet run --project tools/Pulaski.SparkplugCommand -- --host localhost --port 1883 --delay-seconds 12 spBv1.0/Plant1/DCMD/Extruder/EXT-01-EXT Command/Reset=true Command/Start=true`. Change `Extruder/EXT-01-EXT` to `Saw/EXT-01-SAW` or `Packer/PACK1` for the other devices; use the configured broker port if it is not 1883. Start takes another 1–10 seconds to reach `Execute`.

## 21. Machines: live status and stops

**Operate → Machines.** What every machine is doing now, as its [edge node](#20-edge-nodes-collecting-machine-data) reports it, its stops to code, its alarms and warnings, its process values, and its utilization and OEE over past days.

### Ideas to know first

- **A stop** is time a machine isn't executing: it starts when the machine leaves Execute (or loses its signal) and ends when it executes again. The states it passes through on the way (Stopping, Stopped, Resetting…) are one stop.
- **Its reason.** A stop takes the reason its first-out stop code maps to in the machine's [profile](#19-machine-profiles); with no code, the reason of the first state on its way that has one (Suspended = *Starved*, say). An operator confirms that reason or gives another, and both are kept.
- **Micro-stops** are stops shorter than the machine's micro-stop threshold (5 minutes unless set otherwise on the Equipment screen). They are listed but not coded, and don't count against availability.

### The status board

1. Open **Operate → Machines**. With more than one site, choose the site.
2. Each tile is a piece of equipment that tracks state, colored by what it is doing: **Producing** (green), **Waiting** (starved or blocked), **Held**, **Stopped**, **Ready** (idle, starting, resetting…), and **No signal** or **Not reporting** (hatched).
3. A tile shows the PackML state and for how long, the unit mode when it isn't Production, the stop in progress with its reason (or **uncoded**) and stop code, the job running with its good count so far, and the speed.
4. A line sits first in its row, with the machines below it. A line set to take its state from its machines says so: **Worst of its machines** (series), **Best of its machines** (parallel) or **Its constraint machine**.
5. **… stops to code** on a tile opens that machine's stops. **… quiet** means its edge node hasn't been heard from for 2 minutes.

The board refreshes every 5 seconds, and [within seconds](#screens-that-update-themselves) of a machine's change.

### Code stops

1. Open the **Stops** tab. The heading shows how many are waiting. **Only stops to code** is ticked: stops with no reason, longer than the micro-stop threshold, including one still going on.
2. Choose the **Equipment** and **Period** (8 hours, 24 hours or 7 days) to look at. Untick **Only stops to code** to see every stop, with its reason (and the groups it is filed under, beneath it); tick **Show micro-stops** to see those too. The newest 500 show first; when there are more, the list says so under it: click **Load more** for the next 500. Stops already loaded stay as the list refreshes.
3. Click **Code** on a stop. Choose the **Reason**, add a **Comment** if you like (some reasons need one), and click **Save**. Where the stop code mapped a reason, **Confirm …** accepts it in one click; until then the list shows it as *to confirm*.
4. **Change** on a coded stop gives it another reason. The list shows who coded it, their comment, and the reason the code gave when the two differ.

### Put stops right: enter, split and merge

When the machine's record is wrong or missing, a supervisor can correct it. Nothing is deleted: a stop that is split or merged is kept as **replaced**, and the new stops take its place in the list and in utilization and OEE. Every change needs a reason in words.

**Enter a stop the machine didn't report** (a power cut with no signal, a line with no tags yet):

1. On the **Stops** tab, click **Enter a stop**.
2. Choose the **Machine**, when it **Started** and **Ended** (site time), and the **Reason**.
3. Say **Why** it is entered, and click **Save**.

It may not overlap another stop of that machine, must have ended already, and can be at most 31 days back.

**Split a stop in two** (a jam, then waiting for cartons):

1. Untick **Only stops to code** if the stop has a reason already.
2. Click **Split** on the stop. It offers the middle of the stop; change **Split at** to when the second part began.
3. Say why, and click **Split**. Both parts keep the stop's reason; **Code** or **Change** each one afterwards.

**Merge stops into one** (one jam where the machine flickered back to running for a few seconds):

1. Tick the stops to merge, at least two, all of one machine and next to each other: no other stop of that machine between them. Tick **Show micro-stops** to merge those too.
2. Say why in the bar above the list, and click **Merge into one stop**. The merged stop runs from the first one's start to the last one's end and keeps the first one's reason.

**Change a stop's times** (the machine's clock was off, or the stop really ended later):

1. Click **Change times** on the stop, set **From** and **to** (site time), say why, and click **Change times**.

It keeps its reason and coding, and may not overlap another stop of the machine.

**Remove a stop entered in error:** click **Remove** on it (only stops entered by hand have it), say why, and click **Remove stop**. It stays in the list as *removed*, with why.

**Undo a split, merge or change of times:** click **Undo …** on a stop it made, say why, and click **Undo**. The stops it replaced come back, with their reasons and coding. It works only while none of its stops has been adjusted since (undo that first), and only if the stops coming back wouldn't overlap another stop.

Only stops that have ended can be split, merged or retimed. **Show replaced stops** lists the stops that were replaced or removed too, with who did it, when and, for a removed or undone one, why.

**Machine data that arrives late.** If the edge was offline and you entered the outage by hand, the machine's own stop for that time may arrive later. Pulaski keeps your stop: the machine's stop is replaced by only the parts of it your stop doesn't cover (shown as **Trimmed**), so stops never overlap. This happens when the machine's stop ends.

### Alarms and warnings

1. Open the **Alarms** tab. It lists the alarms and warnings active in the period, the active ones first, then the newest.
2. Choose the **Equipment** (it and everything below it) and **Period** (8 hours, 24 hours or 7 days). Tick **Only active ones** to see what is still going on.
3. Each row shows when it was raised and cleared (**active** while it isn't), how long, the equipment, **Alarm** or **Warning**, its number and message, and the tag that raised it. Click **Refresh** to update.

### Process values

1. Open the **Process values** tab and choose the **Equipment**.
2. Choose the **Period** (last hour, 8 hours, 24 hours or 7 days). Each process value and level tag shows its latest reading and when, the lowest, the highest and the mean, and how many readings there were (and how many without a value).
3. **Export CSV** saves every reading in the period, for a spreadsheet or a chart.

The mean weighs each reading by how long it held, since readings are recorded when the value moves, not at a steady rate.

### Utilization and OEE reports

1. Open the **Utilization and OEE** tab.
2. Choose the production days (**From** and **to**, at most 366 days) and, if you like, one piece of **Equipment** (it and everything below it) and one **Shift**; click **Show**. It opens on the last 7 days, all equipment, all shifts.
3. **By equipment** lists each piece of equipment that tracks state, machines indented under their line, with its scheduled, planned, run and down hours, stops, and **availability**, **performance**, **quality** and **OEE**. Down time not coded yet shows as *uncoded* under the hours.
4. **By shift** shows the same for each shift worked, for the line (the top of each group); choose one machine to see its own shifts. For a period longer than 31 days it is **By day**: one row per day, all shifts together (or the shift you chose).
5. **By product** shows each material made: where it ran, how many runs, good and scrap as reported, and its OEE over the time inside its runs.
6. **Stop Pareto** ranks the down time by reason, the biggest first, across the lines and machines shown: each reason's hours and stops, its share, the running total, and which equipment it happened on. **Uncoded** and **No signal** are listed like reasons. Tick **Planned stops** or **Micro-stops** to rank those too, and choose **Rank by number of stops** to rank by how often rather than how long. The reasons in bold make up the first 80%.
7. **Where the time went** breaks one piece of equipment's scheduled time into run, micro-stops, down (by reason, **Uncoded** or **No signal**), planned stops (by reason) and non-production modes, longest first. Choose the equipment in the section's list.
   When reasons are filed under [groups](#reason-codes), both the Pareto and this section have a list to total them: **Each reason**, **Top groups**, or **Groups, level 2** and deeper.
8. **Trend** charts one figure (**OEE**, **Availability**, **Performance** or **Quality**) for one piece of equipment, shift by shift or day by day, with its target as a dashed line. Points below their target are red; hover a point for its value and target. Under the chart: the figure over the whole period and how many shifts or days fell below target.
9. **Export CSV** on each section saves it as a file; the equipment and shift files have the targets too.

A dash means there is nothing to measure that figure against, such as performance on a machine that doesn't run jobs. With [OEE targets](#set-oee-targets) set, each percentage shows its target beneath it (*of 65%*), in red as *below 65%* when it falls short: equipment against the targets of the period's last day (or today), shifts and days against their own day's.

### In detail

- **Who:** seeing Machines (with its alarms and process values) needs permission to see machines; coding stops needs permission to code machine stops (Operators and Supervisors have both); entering, splitting and merging stops needs permission to adjust them (Supervisors).
- **Which reasons are offered:** with reasons ticked on the machine's profile (**Reasons offered**), those and the ones its codes and states map to; with none ticked, or no profile, every utilization reason of the site.
- **Stop code:** the first one the machine reports during the stop is the stop's, even if the machine reports another later. A machine's own override (on the profile's **Machines** tab) beats the profile.
- **Unit mode:** a stop keeps the unit mode it began in; one in Maintenance or Manual is shown with that mode.
- **Counts on the board** are the machine's processed less defective counts in the run open on it.
- **Each coding is recorded** as an event, with the reason before and after and whether it confirmed the mapped one.
- **Adjusted stops** (D-069) say where they came from: **Entered by hand**, **Split** or **Merged**, with who did it and why. A replaced stop can't be coded; code the stops that replaced it. A stop entered by hand has no PackML state (it shows *Undefined*), takes the run on the machine at its start, and is coded with the reason given. None of them is a micro-stop, however short: someone said it was a stop. Time between merged stops counts as stop time, even where the machine ran. Each adjustment is recorded as an event with the stops before and after. Splits and merges can be repeated on the stops they made, within 31 days of the stop's start.
- **Scheduled time** is what the equipment's [shift schedule](#16-shift-schedules) gives it: its shifts, counted towards the day they start on; equipment without shifts (around the clock) is scheduled the whole site-local day. Time outside its shifts isn't counted, and neither is time still to come.
- **How each moment counts:** in a unit mode that isn't production time (Maintenance, Manual) it is *non-production*; in a stop it counts by its reason's class: **planned** reasons (setup, changeover, cleaning) are planned stops, **runtime** reasons are run time, **downtime** reasons and uncoded stops are down. A micro-stop is run time. Otherwise executing is run time and anything else is down. Before a machine has sent anything, or while its signal is lost, it is down with *no signal*. Time in a job that counts as planned downtime (planned maintenance) is a planned stop.
- **Planned time** is scheduled time less non-production modes and planned stops. **Availability** = run time ÷ planned time.
- **Performance** = output at the ideal rate ÷ run time in jobs. The ideal rate is the line's capability for the material when the run started. Run time with no job running isn't in it (its output can't be reported). **Quality** = good ÷ good and scrap. **OEE** = availability × performance × quality. Output comes from production reports, including reports taken from the machine; machine counts nobody has reported aren't used.
- **Several products** add up by their time at the ideal rate (quantity ÷ ideal rate), so different units don't matter. A run without an ideal rate (no capability, or a rate whose unit doesn't convert) is listed in a note above the tables; its output shows in the product's good and scrap but not in performance or quality.
- **Lines:** a line set to take its state from its machines takes its time from theirs: in series, at each moment the worst (down, then planned, then non-production, then run); in parallel, the best; or its constraint machine's. Its jobs and reports are its own, so the line has full OEE; a machine below it that runs no jobs has availability only.
- **Stops** count the stops that started in the period (a stop already going on when it began isn't counted again); micro-stops are counted separately.
- **One shift** keeps only that shift's working time, its stops and the output reported in it; output reported between shifts goes with the shift before, if that shift started the same day or the day before. Output reported on a day with no shift before it counts in that day's totals but in no shift. Equipment without shifts shows nothing for a shift. The shifts offered are those worked in the period.
- **The stop Pareto** uses the same time as **Where the time went**, for the top equipment shown: a line's time already comes from its machines, so the machines below a line aren't added again. Choose one machine as the **Equipment** to rank its own stops.
- **Totals by group** add up each reason under the group it is filed in at the level chosen; a reason filed less deep than that, or not in a group, stays itself. Groups are as they are now, so moving a reason to another group moves its time with it. The export of **Where the time went** has a **Groups** column.
- **Longer periods** (more than 31 days, up to 366) come from **daily rollups**: Pulaski works each finished day out once, in the background, and keeps it. The report says so above the tables. Today and yesterday are always worked out when you ask, and so is any day being worked out again; if more than 31 days are waiting, the oldest are left out and a note names them (try again a little later).
- **Rollups stay right.** A stop entered, adjusted or coded late, a late machine state, or output reported into an old run marks its day to be worked out again. Changing a shift schedule, equipment settings, a utilization reason's class, an ideal rate or a unit conversion works out every day of the site again (it takes a few minutes; meanwhile long reports leave out what isn't ready). A rolled day gives exactly the figures the history gives.
- **The trend** uses the rows of the **By shift** (or **By day**) table. By day adds a day's shifts up from their hours, as the totals do, never averaging percentages. Output reported on a day outside every shift is in the totals but not in the trend.
- **Worked out when you ask:** up to 31 days nothing is stored, so coding a stop, changing a reason's class or correcting a shift schedule changes the report at once.

### Maintenance requests

A **maintenance request** asks maintenance to look at a line or machine, and keeps track of it until it is closed with what was done.
An andon call brings someone to the line now; a request is work to do and to follow up.

**Request maintenance for a stop:**

1. On the **Stops** tab, click **Request maintenance** on the stop.
2. Choose the **Priority** (normal, high or low), say **What needs looking at**, and click **Request**.
3. Pulaski gives the request a number, such as **MR-000042**, and says it is waiting. **See the Maintenance tab** takes you to it.

**Request maintenance on its own** (a noise, a leak, a guard that is loose):

1. Open the **Maintenance** tab and click **Request maintenance**.
2. Choose the **Line or machine**, the **Priority**, say what needs looking at, and click **Request**.

**Work a request** (Maintenance and Supervisor roles):

1. On the **Maintenance** tab, the requests not closed are listed, the ones waiting first and high priority first among them.
2. Click **Start** when you begin: the request shows **In progress** and who is on it.
3. Click **Close**, say **What was done** (or why nothing was), and click **Close**. It moves to **Closed in the last 7 days**, with who
   closed it and how long it took.

In detail:

- **One request per stop.** While a stop's request isn't closed, asking again from the same stop gives the same request, and nothing new is
  recorded. Once it is closed, the stop can have a new one. A stop that is later split or merged keeps its request, which still names it.
- **Where.** A request is for equipment below an area: a line, a machine, a unit or a feed point that is switched on. Raised from a stop, it
  is for the stop's machine.
- **Closing one nobody started** starts it at the same moment, by the same person. A closed request stays closed; nothing is deleted. Each
  step is recorded in the event log.
- **Waiting** counts from when it was raised until someone starts it; **Took** counts until it was closed.
- **Who:** anyone who can see machines sees requests. Raising one needs *Request maintenance* (Operator, Supervisor, Maintenance); starting
  and closing needs *Work maintenance requests* (Supervisor, Maintenance). The Maintenance role comes with a new Pulaski and is added to
  an existing one by its upgrade.
- The tab refreshes itself, as the Andon page does. **Equipment** narrows it to a line or machine; a line includes its machines' requests.
**Plan maintenance by run hours or counts** (Maintenance and Supervisor roles):

1. On the **Maintenance** tab, under **Preventive plans**, click **Add a plan**.
2. Choose the **Line or machine**, say **What is done** (such as *Grease the gearbox*), and set **Every** so many **run hours** or
   **machine counts**, and the **Priority**.
3. Click **Save**. **So far** shows how far its meter has got, and its share of the interval.

When the meter reaches the interval, Pulaski raises a request by itself, with the plan's words, marked **Preventive** and raised by **Its plan**.

- **Run hours** are the time the line or machine spent executing (PackML Execute), from its machine state; **machine counts** are what its
  counter tag counted. Equipment that reports neither never reaches its interval.
- **One at a time.** While a plan's request is open, its meter waits (**Waiting for its request**). Once the request is closed, the meter
  starts again from then, so time or counts before the close don't count towards the next one.
- **Change** a plan's words, interval or priority, or switch it **Off**. Its meter (run hours or counts) is fixed once made; make another
  plan for the other. Switched on again, its meter starts from then. Plans are never deleted, since their requests name them.
- Pulaski checks the plans every 10 minutes (`Maintenance:Plans:PollMinutes` in the API's settings; `Maintenance:Plans:Enabled` = false stops
  it), so a request comes up to 10 minutes after its interval is reached.

- **A CMMS** can take requests on: send them through a delivery feed with *Maintenance requests* ticked, and it answers with its own number,
  and can start and close them (see [Send movements to the ERP and certificates to a document system](#send-movements-to-the-erp-and-certificates-to-a-document-system)). Its number shows as **CMMS …** under the request's.
- **Elsewhere:** a line's open requests show on its [operator station](#use-an-operator-station), where they can be raised too, and on its
  [line board](#show-a-line-board-on-a-tv); requests nobody has started are in the [Inbox](#your-inbox) of whoever may work them.

---

## 22. Reference

### Work order statuses

| Status | Meaning | Can go to |
| --- | --- | --- |
| **Planned** | Being arranged; jobs move freely. | Dispatched, Canceled |
| **Dispatched** | Published to the floor with a baseline. | Planned (un-dispatch), Firmed, Canceled |
| **Firmed** | Plan frozen; jobs anchored at their start. | In progress (when its first job starts), Canceled |
| **In progress** | Being made: its first job has started. | Completed (when its last open job completes) |
| **Completed** | All its jobs are done; production can still be reported into their runs. | In progress (a job is reopened), Closed |
| **Closed** | Done and closed: its jobs are closed and take no more reports. | — |
| **Canceled** | Not going ahead. Cancel its open jobs first. | — |

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

Between two production jobs on different materials, a line needs a changeover. Pulaski sizes it from the plant's [changeover rules](#changeover-rules) (for example *white to gray: 30 minutes*, *gray to white: 2 hours*), or from the line's standard changeover for the incoming material. By default Pulaski inserts these automatically as placeholders and recalculates them as you resequence. A plant can instead choose to have planners place changeover jobs themselves; Pulaski then warns where one is missing. A manager chooses which on [Site settings](#18-site-settings).

---

## 23. What Pulaski doesn't do yet

Pulaski is pre-alpha. These are known gaps, so you don't spend time looking for them:

- **Working offline:** the station, line boards and badge terminals say when the connection is gone and keep what was typed, but don't queue
  reports, stop reasons or clock-ons to send later; they are sent again by hand once it is back. The signed-in screens show Blazor's own
  reconnecting notice.

- **Help** is the guide in English, whatever your language; line boards (TV screens) have no help link.
- **Your own reports and dashboards** aren't built in Pulaski: analysts build them in Power BI, Excel or a warehouse from the read-only
  reporting views (see [Reports and dashboards in other tools](#reports-and-dashboards-in-other-tools)). The views cover sites, equipment,
  materials, reasons, production, runs, stops, OEE by shift and day, work orders and jobs, lots, stock and movements, genealogy, labor,
  quality results, SPC alerts and NCRs. Production by shift and today's OEE aren't in them; the screens have those.
- **Accessibility:** the main flows (signing in, the Overview and menu, the Floor and job pages, Inventory and Scan, the station and line boards)
  pass automated WCAG 2.2 AA checks, but haven't yet been tested with a screen reader by a person, and the other screens have only the checks
  every build runs on all markup. Text-only zoom to 200% isn't checked. See `design/accessibility.md`.
- **Labor:** people from HR or the ERP come from a file or a connection; schedules are in UTC, not the site's time, and a failed scheduled run waits for the next one. Qualifications are only added by an import, never revoked, and SCIM users carry none. Terminals don't take production or downtime reports and can't sign anyone in as a user. Planned breaks are never recorded on their own. A grant that expired before its notice went out (say, while e-mail was off) gets no notice. At a terminal people record only direct time, their own clock-off and breaks: setup, indirect and training time, and production reports, are recorded on the signed-in screens. A terminal can't be used to sign in as a user. Breaks aren't planned on shift schedules (a fixed lunch is recorded like any other break), and a missing qualification only warns: it never stops someone being put on a line. Pulaski sends no notice before a grant expires; use *Expiring in 30 days*. A person works only at their own site. On **Shift review** only a session's times and share can be changed, not its job or kind (end it and add a new one instead). The Overview's *Labor to approve* looks back only 14 days: an older shift left unapproved isn't counted (open its day on **Shift review**).
- **Materials import** from CSV or B2MML files, from an OData, REST, SOAP or PostgreSQL connection on demand or on a schedule, or one item at a time through the API. Connections can't sign in with OAuth, and there are no adapters for label printing. Capabilities import from CSV only. Pulaski exports materials and classes as B2MML, but not lots; B2MML sublots, assemblies and other languages aren't read.
- **Planning rates from run history:** the scheduled recalculation looks at every line of a site at once, on whole days; it can't be limited to some lines or materials, or run at a set time of day. Stops can be taken out only on lines that record machine state, and a proposal accepted without review is worked out once, not again when it is accepted.
- **Machine data:** only stops can be adjusted, not a machine's state history; a machine stop still going on over a stop entered by hand is trimmed only once it ends. Machine counts reach OEE only through production reports. The OEE report covers at most 366 days (beyond 31 it shows days, not shifts); its only chart is the trend of one figure for one piece of equipment, and the stop Pareto is a table. OPC UA's own alarms and events aren't read (alarms come from alarm-number tags), Pulaski can't write to machines, EtherNet/IP tags can't be browsed (type their names) and arrays aren't read, process values are numbers only, with no charts of their own on screen (a characteristic sampled from one has a [control chart](#control-charts)).
- **Reports from the machine are confirmed by an operator.** Nothing reports machine counts by itself, materials made in batches can't take them, and the capabilities CSV has no units-per-count column. Reversing a report from the machine that isn't the run's latest doesn't offer its counts again; report them by hand. A corrected report's good output goes into new lots, not the old ones, and its run and time can't change.
- **Languages:** alert e-mails are in English, and so is what Pulaski keeps as history: a connection run's summary, why a planning rate was
  put up for review, a delivery's error. Dates and quantities inside a message keep Pulaski's usual form (*2026-11-24*, *1,234.5*), and the
  screens that haven't moved to regional formats yet still write them that way.
- **Maintenance requests** are started and closed on the Machines page only, not at the operator station, and no e-mail alert says one is
  waiting. A CMMS link is a feed and a signed answer, not an adapter for a particular CMMS's own API. Preventive plans count only run hours
  and machine counts, not days on the calendar.
- **Approval before release:** nothing is e-mailed about a draft waiting for approval (it is in the Inbox). A BOM's or specification's need for
  approval follows the sites that can make its material now; a capability added later doesn't affect a request already waiting. Work
  instructions are released directly.
- **Sampling plans** show on the job page, the Floor, operator stations and line boards; nothing is e-mailed about an overdue sample, and a
  sample recorded at a station can't be tied to a lot there (use the Quality page). A unit-based plan counts reported output, not machine counts.
- **Work instructions** show on the job page and at the station, not on the Floor's board or line boards. At a station only the running job's
  show, or the next job's while nothing runs. A failed or rejected checklist only holds up the job; it doesn't put lots on hold or raise a quality
  alert, and nothing e-mails anyone about a checklist waiting for its sign-off (it is in the Inbox). Who may give the second sign-off is one
  permission for every checklist, not chosen per checklist. Text has headings, steps, points and bold only: no tables, links or pictures inside it (attach them as files).
- **Live updates** reach the Overview, Floor, job pages, Machines, Andon, Handover, the Inbox, line boards and operator stations; the Schedule,
  Inventory and Quality pages show what was there when they were opened (charts and alerts every 30 seconds). Boards and stations reload
  for any change at their site, not only their own lines'.
- **System health** is a page to look at: it sends no alerts. Background services are known only since the API last started, and only the API's
  own; an edge node's own buffer and connections show on its page, not here.
- **The Inbox** words its items (partial batches, SPC alerts, labor, ERP changes, planning rates) in English whatever your language.
- **Shift handover** shows this shift and the last one; older notes aren't listed on screen, and entries can't be edited once written (add
  another note, or close an item with what was done).
- **The inbox** gathers seven kinds of decision; failed deliveries to the ERP, stops to code and andon calls stay on their own pages. It
  links to the page where an item is decided rather than deciding it in place.
- **Alerts** come by e-mail only. Each person keeps their own; nobody can subscribe others. A lot made on hold (rather than put on hold
  later) doesn't alert, and a shortage that clears and comes back for the same job isn't e-mailed again.
- **Report defaults** are kept per line, not per line and material, and the capability CSV doesn't set them. A lot filled past its age or
  into a new shift is closed (and its label printed) only by the next report, **Start a new lot** or completing the job, not by the clock.
- **BarTender** printing sends one script per label and stops at the first one BarTender refuses; it doesn't use BarTender's print queue or
  report when the printer itself has finished. Documents are named per kind of label only, until printing from other places (#122) adds more.
- **Andon** calls are raised only at an operator station. Response times cover at most 90 days on screen and can't be exported. Escalated calls are e-mailed only to those who added that alert under **My alerts**.
- **Batches:** a rejected partial batch shows in the job's reports as a correction (good down, scrap up). The order form lists each line's batch size but doesn't work out the quantities that share into them exactly; the refusal names the nearest ones.
- **Automated consumption is at feed points only.** Backflush, time-window and counter consumption work from the line's [feed points](#feed-points). Production isn't counted into lots from machines. Lots are paired by when they were declared, not from machine data (see [Trace a lot](#trace-a-lot)).
- **Not in inventory yet:** GTINs are set on the material page only; the material import doesn't fill them. A container made before its site had a GS1 company prefix keeps no SSCC. Printing to a label printer reports a failure to reach it, but not whether the printer then printed. A back-dated move, hold or release must be the lot's latest movement, and back-dating is limited to 31 days. Only a number attribute standing for a quality characteristic is checked against specification limits. Balances' attribute values aren't in the values file.
- **Reversing:** a chain undo follows a lot and the lots split off it, not merges into other lots, consumption into jobs or the output made from it; corrections made before this version can't be reversed.
- **Genealogy through a silo is an estimate** from the declared movements. A level tag's readings confirm when it was empty and check the declared stock (see [How a silo flows](#how-a-silo-flows)), but they don't change how much each arrival gave. A reversed movement and its reversal are left out of the estimate, as if neither happened.
- **Production performance** carries no labor or machine time, and a job's lots are listed but not which lot each was made from. A production performance feed sends one document per change, even when several changes come together; the built-in ERP feed sends only events (add a feed for B2MML).
- **Sending to other systems** is by HTTPS webhook or MQTT; there is no Kafka or other message queue yet, and retention is the same for every feed. Receivers' acknowledgments are recorded on the delivery, but nothing compares what the ERP booked with what Pulaski sent; there is no adapter for a particular ERP's or document system's own API. A certificate's file goes inline in the event, up to 10 MB; there is no download link for a receiver to fetch it later. The delivery list shows at most 200 at a time, with no paging beyond that.
- **Allocations:** the background refresh is all or nothing for a site, and any change anywhere makes it run. Preferences are per site, not per material. A picking list is printed, not confirmed: fetching the lot is still an ordinary move. Approved alternates show only in allocations and on consumption, not in an order's planned components, and only a BOM's approved alternates can be allocated.
- **Shortages don't plan.** Incoming deliveries are counted only as entered or sent by the ERP, and a lead time only flags what can't arrive in time; nothing is suggested or ordered. Lead times are per material, not per supplier or site. The live timing is the schedule as projected now, not a forecast of slips.
- **The Overview** has no quality figures yet, and no targets per line. Its shift figures don't take calendar exceptions out (as in the OEE report), and good output is only what is reported, never the machine's counter.
- **Notes** can only be written when the order is created.
- **The demo's history** stops where it was recorded: EXT-02 shows no signal after it (its edge node isn't live), and nothing more is reported on its running orders unless you do it. Labor of the current shift is left open, as if the crew were still on.
- **The demo's** OEE on EXT-01 has no output until you report production on its running job (machine counts only fill in a report's *From the machine*), and its counts are pieces, counted as one unit of the material each. Its control limits are set by hand from the simulator's design, not from a baseline. The OPC UA simulator isn't connected.
- **Units** have no temperature dimension, so a temperature characteristic has no unit (name it, as the demo's *Melt temperature (°C)*).
- **Nonconformances:** a disposition is carried out lot by lot by hand (there is no one-click run over every lot); rework and returns to the
  supplier have no steps of their own; runs can't be added to an NCR after it is raised; NCRs aren't sent on a delivery feed.
- **Quality:** alerts aren't e-mailed, and there are no sampling plans or reminders of samples due. Process values make results only at fixed intervals from one tag per line, never for an inspected characteristic or a lot. A specification is per material, not per site or customer, so a certificate of analysis has one layout and no customer-specific
requirements; it takes no results from the lots a lot was split or merged from, and is printed in English. A job's results are recorded from its page; the Quality page itself has no job picker. Results only change OEE through a rejected lot.
- **Signing in through your organization's identity provider** works for OpenID Connect providers set up in configuration; its groups don't give Pulaski roles yet, signing out of Pulaski doesn't sign you out of the provider, and a session still ends after 12 hours. Forgotten Pulaski passwords are reset by e-mail only where it is switched on; Pulaski sends no other e-mail.
- **Some switched-off controls give no reason yet:** a form's Save waiting on a field next to it, an Export with nothing to export, and the locked boxes on the Permissions tab (point at those with the mouse to see why).

---

## 24. Troubleshooting and questions

**A component wasn't consumed when I reported production.**
Check its **Consumed by** in the BOM: only backflush and time window are automatic. Then check what is loaded on **Inventory → Feed points**: the lot must be at a feed point below the job's line, available and not expired. Anything it couldn't cover is listed there to resolve or dismiss.

**"You may not do this…" or a button I expected isn't there.**
Your roles don't allow that change, or not at this site. The message names the permission; ask an administrator (see [What each role can change](#what-each-role-can-change)).

**I can't sign in.**
Check the user name and password (the password is case-sensitive). After five wrong tries you are locked out for 15 minutes; an administrator can reset your password, which unlocks you. A user who is switched off or has no role can't sign in.

**"Your session ended".**
Sessions last up to 12 hours, and end straight away if an administrator switches you off. Sign in again.

**I can't find a process when creating an order.**
The process must be at the order's site, be active, have a released version, and make the material (all materials, its class, or the material itself). If it is listed but grayed out, read the reason under the field: usually a missing capability.

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

**"… has moved since" or "… status has changed since".**
A move is reversed only while it is the lot's latest move, and a hold or release while it is its latest status change. Reverse the later one first, or tick **With everything after it that depends on it** to undo them together.

**I can't post a count.**
Every lot must have a count (enter 0 if it isn't there), the counts must be saved, and a lot marked *changed since counted* must be counted again.

**I can't merge two lots.**
They must be the same material, at the same location, and in the same status. Move or release one first.

**A BOM won't release.**
Read the yellow box: a component that doesn't exist or is inactive, an operation number the process's released version doesn't have, a unit with no conversion, or no components at all. Save the draft after fixing.

---

## 25. Glossary

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
| **Role** | A set of permissions, such as Planner or Quality, that an administrator maintains and gives to users at a site (Administrator everywhere). See [Users, roles and signing in](#15-users-roles-and-signing-in). |
| **Run** | One stretch of a job running on a line, from a start to a pause or completion. Production and consumption go into a run. |
| **Labor session** | A stretch of time one person spent on a line: direct, setup, indirect or training. See [Labor](#17-labor-people-and-crews). |
| **Shift schedule** | When a site works: shifts repeating over a cycle of days. Equipment follows one, inherits its parent's or runs around the clock. See [Shift schedules](#16-shift-schedules). |
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
