# Pulaski — Plant Overview Dashboard (design brief)

Original design for Pulaski. Framework-agnostic (Blazor vs React is still open in the charter).
Do not copy layouts, styling, iconography or branding from any other MES product.

## Purpose

The first screen a supervisor or plant manager sees. In five seconds it should answer:
**Which lines are running right now, which are not and why, and what needs a person?**
It is a live operations board, not a report. History and drill-down live on other screens.

## Users and context

- Supervisors, plant managers, quality techs. Desktop and 10–12" tablets on the floor.
- Glanceable from 2 m away on a wall display (kiosk mode, no nav, larger type).
- Always scoped to a site, and optionally an area; always shown "for the current shift."

## Page structure (top to bottom)

### 1. Top bar (sticky, 56 px)
- Left: Pulaski wordmark (placeholder text logo), then a **scope breadcrumb**:
  `Site ▸ Area ▸ (all work centers)`. Each segment is a dropdown fed by `core.equipment`.
- Center: **shift selector** showing the current shift instance, e.g. `Shift B · Crew 2 ·
  14:00–22:00`, with "previous shift" / "today" options.
- Right: live indicator (pulsing dot + "Live" or "Stale 2m" when the feed lags),
  site-local clock, alerts bell with count, user menu (name, role, sign out).

### 2. Left navigation (collapsible, 240 px → 64 px icon rail)
Grouped by how plants work, not by table:
- **Operate:** Overview (this page), Dispatch list, Downtime coding, Labor
- **Material:** Lots & inventory, Genealogy trace
- **Quality:** SPC charts, Alerts, Holds
- **Analyze:** OEE, Downtime Pareto, Production history
- **Configure:** Equipment, Materials & BOMs, Shifts & calendar, Reason codes, Users & roles, Connectors
Hidden entirely in kiosk mode.

### 3. "Needs attention" strip (the most important row)
A single horizontal row of **action cards**, ordered by urgency. Each is clickable into the
filtered work queue. Only cards with a non-zero count are emphasized; zero counts are shown
muted, not hidden, so the layout never jumps.

| Card | Count source | Click goes to |
| --- | --- | --- |
| Uncoded stops | `ts.downtime` where no effective reason and not a micro-stop, current shift | Downtime coding |
| Open SPC alerts | `core.spc_alert` status `open` (critical count in a badge) | Quality alerts |
| Lots on hold | `core.lot` status `on_hold` or `quarantine` | Holds |
| ERP changes to accept | work orders with `erp_change_pending` | Dispatch list |
| Lines with no signal | work centers whose current state is 0 / Undefined | Equipment status |
| Labor to approve | shifts of the last 14 days that have ended, with counting `core.labor_session` rows and no `core.labor_approval` | Labor → Shift review, oldest day |

Card anatomy: icon, large number, one-line label, small secondary text ("3 over 30 min").

### 4. Shift summary band
Four compact figures for the selected scope and shift, in one row:
- **Shift OEE**, with A / P / Q as three small labeled values beneath it
- **Good output** vs planned for the shift (e.g. `18,420 / 22,000 m`, with a thin progress bar)
- **Scrap** quantity and percentage
- **Downtime** total minutes, with the top reason named ("Top: Die change, 42 min")

### 5. Line status board (main area)
One row per work center (production line) in scope, sorted: stopped first, then held,
waiting, running. This is a **table-like list of rows, not a grid of gauges**: rows compare
better, scan faster and fit more lines on screen. Use cards below 900 px width.

Columns per row:
1. **Line**: code + name (`L3 · Profile Extrusion 3`).
2. **State chip**: current OEE category with its color, an icon and text (never color
   alone), plus time in state (`Stopped · 12m`). Hover/tap shows the raw PackML state and the
   first-out stop reason.
3. **Current job**: job number, product code and name, run progress
   (`7,850 / 10,000 m`, 78%).
4. **Shift timeline**: a horizontal strip spanning the shift, colored by state category
   over time (a state band, like a one-row Gantt), with a "now" marker. This is the signature
   visual of the page; it shows patterns (frequent short stops vs one long stop) that a single
   percentage hides.
5. **OEE**: shift OEE as a number, with three short horizontal bars for A, P and Q against the
   site target marks. Value text is neutral; only the bars shift color relative to target,
   so a page of below-target lines does not turn into a solid wall of red.
6. **Flags**: small icons for open SPC alert, uncoded stop, lot on hold at this line.

Row click opens the line detail screen. A density toggle switches between comfortable and
compact (compact fits ~25 lines at 1080p).

## Visual direction

- Calm, industrial, high-contrast. Neutral slate/graphite surfaces; one accent
  (deep teal) for interactive elements only. Status colors are reserved for status.
- Light and dark themes; dark is the default for kiosk mode.
- Typography: one clean sans for UI; tabular (fixed-width) numerals for all figures so
  counts don't jitter as they update. No all-caps label styling.
- Generous whitespace on desktop; tighter in compact density.
- Subtle elevation (1 px borders, no heavy shadows). 8 px spacing grid, 8 px radius.

### State category colors (tokens, configurable)
| Category | Token | Suggested |
| --- | --- | --- |
| Producing | `--state-producing` | green |
| Waiting (external) | `--state-waiting` | amber |
| Held (internal) | `--state-held` | violet |
| Stopped / faulted | `--state-stopped` | red |
| Ready / transitioning | `--state-ready` | blue-grey |
| No signal | `--state-nosignal` | grey with diagonal hatch |
| Not scheduled | `--state-unscheduled` | very light neutral |

All pairs must pass WCAG AA against their background in both themes, and each state also
carries an icon and text label.

## Behaviour

- Live updates pushed from the server (SignalR / server-sent events), not polling. Numbers
  that change briefly highlight, then settle; no constant animation.
- If the feed is more than 60 s stale, show a banner and grey the live indicator; never show
  stale numbers as if current.
- Loading: skeleton rows matching final layout. Empty scope: friendly message and a link to
  equipment setup.
- Everything respects the user's permission scope (`sec.v_user_permission`); lines outside it
  are not shown.
- Tablet: nav collapses to the icon rail; the board switches to cards with the timeline on
  its own row.

## Data contract (for the API)

`GET /api/v1/overview?scope={equipmentId}&shift={shiftInstanceId}` returns:
- `attention`: counts for each card above
- `summary`: shift OEE components (ms and quantities, not percentages), good, scrap, planned,
  downtime minutes, top reason
- `lines[]`: id, code, name, state (category, PackML code, since), current job/run summary,
  timeline segments `[from, to, category]`, OEE components, flags

The UI computes percentages from components (availability = producing / planned production
time, etc.) so the math lives in one shared helper.

Live deltas arrive on a hub/stream keyed by work center id: `stateChanged`, `countsUpdated`,
`attentionChanged`.

## Out of scope for this screen
Historical OEE trends, Pareto charts, SPC charts, scheduling edits. Those are separate pages.
