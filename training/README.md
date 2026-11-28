# Pulaski training decks

One PowerPoint course per default role, and one on line boards. Each role deck covers what that role can see and change, the role's tasks with numbered steps and "Good
to know" notes (speaker notes carry more detail), screenshots of the screens it uses with what to notice on each, a knowledge check with
answers, a quick-reference table and where to get help.

| Deck | For |
| --- | --- |
| Pulaski training - Planner.pptx | Planning and scheduling: work orders, the schedule, rates, changeovers, materials, allocations |
| Pulaski training - Operator.pptx | Running jobs at the line: the Floor, the operator station, stops, lots, quality results, help |
| Pulaski training - Supervisor.pptx | Running the floor: everything an operator does, plus corrections, labor, stops, andon, maintenance, counts, OEE |
| Pulaski training - Quality.pptx | Results, SPC, holds and rejections, inventory corrections, NCRs, specifications, certificates, instructions, approvals |
| Pulaski training - Manager.pptx | Performance, labor approval, shift schedules, people and terminals, site settings, reason codes |
| Pulaski training - Auditor.pptx | The audit trail, tracing, inventory corrections and reversals, counts, reports in BI tools |
| Pulaski training - Process engineer.pptx | Processes, BOMs, capabilities, changeover rules, specifications, work instructions, approvals |
| Pulaski training - Maintenance.pptx | The machine board, stops, maintenance requests and preventive plans, andon calls, handover |
| Pulaski training - Administrator.pptx | Users and roles, equipment, site settings, machine data, system health, integrations, audit |
| Pulaski training - Line boards.pptx | A TV on the floor: what it needs, setting it up from Pulaski's address and your user name to the board, reading it, looking after it |

The line board course is for whoever puts a TV up (a manager, an administrator, IT) and for anyone who reads one. It is a *topic* in
`content.js` (`topics`), built like a role's course without the role slides.

Pulaski has no separate scheduler role: scheduling is part of the Planner's course. The Edge node role is a service account and has no course.

The decks follow `design/user_guide.md` and the default roles in `Roles.Defaults`. An administrator may have changed what a role can do on
Configure → Users; the decks describe the roles as a new Pulaski starts with them.

## Rebuilding

The decks are generated, so change the words in `source/content.js` (slide types and layout in `source/build-decks.js`), never in the
.pptx files:

```sh
cd training/source
npm install
npm run build        # all decks; `node build-decks.js planner` (or `boards`) for one
```

The build warns when text might not fit its box. Rebuild when the user guide changes a screen a course describes.

## Screenshots

The screenshots are `source/screens/*.png`, taken from the demo (D-101) with its demo history by `source/capture-screens.js` (Playwright,
Chromium). A screen slide names its picture and the points beside it in the *Screenshots* part of `content.js`. Take them from a demo you can
throw away, not one you use: they are taken as `admin`, and the station and board shots enroll a terminal. The line board course's shots
(`tv-*.png`) come from `source/capture-board.js`, which registers terminal TV-FLOOR and enrolls a 1920 × 1080 browser as it. To run a second demo beside your
own, on other ports:

```sh
PULASKI_API_PORT=5180 PULASKI_WEB_PORT=5190 PULASKI_MAIL_PORT=8125 PULASKI_MQTT_PORT=1884 PULASKI_OPCUA_PORT=50100 \
  docker compose -p pulaski-shots up -d --build
# once demo-history has finished: a terminal, a person and a badge; prints an enrollment code
API=http://localhost:5180/api WORKING=<a new admin password> python3 tools/a11y-audit/setup-terminal.py
cd training/source
npx playwright install chromium   # once
BASE=http://localhost:5190 PASSWORD=<that password> ENROLL_CODE=<the code> BADGE=A11Y-BADGE-1 npm run screens
BASE=http://localhost:5190 PASSWORD=<that password> npm run screens:board
npm run build
```

`ONLY=<name>` retakes the screenshots whose names start with it (a terminal shot needs a new code: `setup-terminal.py code`).
`docker compose -p pulaski-shots down -v` removes that demo.
