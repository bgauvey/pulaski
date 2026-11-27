# Pulaski training decks

One PowerPoint course per default role. Each deck covers what that role can see and change, the role's tasks with numbered steps and "Good
to know" notes (speaker notes carry more detail), a knowledge check with answers, a quick-reference table and where to get help.

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

Pulaski has no separate scheduler role: scheduling is part of the Planner's course. The Edge node role is a service account and has no course.

The decks follow `design/user_guide.md` and the default roles in `Roles.Defaults`. An administrator may have changed what a role can do on
Configure → Users; the decks describe the roles as a new Pulaski starts with them.

## Rebuilding

The decks are generated, so change the words in `source/content.js` (slide types and layout in `source/build-decks.js`), never in the
.pptx files:

```sh
cd training/source
npm install
npm run build        # all decks; `node build-decks.js planner` for one
```

The build warns when text might not fit its box. Rebuild when the user guide changes a screen a course describes.
