# Accessibility audit (#142)

Pulaski aims at **WCAG 2.2 level AA** for its main flows. This page records how that is checked, what was found and what was done. Decision:
D-127 in `domain-model-decisions.md`.

## Main flows

The flows audited, as agreed:

1. **Sign in, Overview, menu**: signing in, the Overview, the sidebar and the header (user menu, Inbox).
2. **Floor and job page**: the Floor board, starting and pausing a job, reporting production, the job page.
3. **Inventory and Scan**: the stock and lot lists, a lot's page, the Scan page.
4. **Station and boards**: the operator station kiosk and line boards (touch screens, static pages).

## How it is checked

| Check | WCAG 2.2 | Where | When |
| --- | --- | --- | --- |
| Every form control has a label (a `<label for>`, an enclosing label, `aria-label` / `aria-labelledby` or `title`) | 1.3.1, 3.3.2, 4.1.2 | `Tests/Pulaski.Web.Tests/Accessibility/MarkupAccessibilityTests.cs`, over every `.razor` file | Every build (CI) |
| Every label points at a control that is there | 1.3.1 | same | CI |
| Every button and link has a name (text, or `aria-label` / `title` when it shows only an icon) | 2.4.4, 4.1.2 | same | CI |
| Every image has a text alternative; icons are decorative (`aria-hidden`, not focusable) | 1.1.1 | same | CI |
| Every page has a title | 2.4.2 | same | CI |
| No positive `tabindex` | 2.4.3 | same | CI |
| The page says its language; a skip link comes first | 3.1.1, 2.4.1 | same | CI |
| Design tokens' contrast, both themes: text 4.5:1, control borders, state colors and focus ring 3:1 | 1.4.3, 1.4.11 | `ContrastTests.cs`, from `tokens.css` | CI |
| Every CSS rule setting both a text color and a background, both themes (`var()` and `color-mix` resolved) | 1.4.3 | same | CI |
| The running app, page by page, with axe-core; keyboard walk; reflow at 320 px | all automatable AA rules, 2.1.1, 2.1.2, 2.4.3, 2.4.7, 1.4.10 | `tools/a11y-audit`, against the demo | Once (2026-11-26), recorded below; rerun before a release |
| A screen reader through the main flows | 1.3.1, 2.4.3, 4.1.2, 4.1.3 and the rest automation can't judge | the script below | By hand |

The markup checks read Razor with a small tolerant scanner (`RazorMarkup`), not a browser: they catch what is missing in the markup, and axe
checks what the browser builds from it.

## Findings and fixes

### Piece (a): markup and contrast

| # | Finding | WCAG | Fix |
| --- | --- | --- | --- |
| A1 | BOM editor rows: line, component, quantity, unit, scrap, consumed-by and operation cells had no labels (only the column header, which isn't one) | 1.3.1, 4.1.2 | Each control names its column (`aria-label`) |
| A2 | Specification editor rows: the characteristic select had no label | 1.3.1, 4.1.2 | `aria-label` |
| A3 | Warning chips (light theme): warning text on its tint was 4.29:1 | 1.4.3 | Tint 16% → 10% (4.66:1) |
| A4 | Danger chips (dark theme): danger text on its tint was 4.37:1 | 1.4.3 | Tint 14% → 8% (4.72:1) |
| A5 | Station status badge *down* (dark): white on danger 3.34:1 | 1.4.3 | Text in the surface color (5.14:1 dark, 6.54:1 light) |
| A6 | Station status badge *waiting* (light): dark text on warning 3.46:1 | 1.4.3 | Surface color (5.33:1 light, 7.70:1 dark) |
| A7 | Station status badge *no signal* (dark): white on purple 3.09:1 | 1.4.3 | Surface color (5.57:1 dark, 6.39:1 light) |
| A8 | No way past the header and menu to the page with the keyboard | 2.4.1 | **Skip to content** first on every page, to the main region |

Checked and fine: the design tokens themselves (every pair above passes in both themes); focus is visible everywhere (`:focus-visible`
outline in the accent color; the equipment tree draws its own); `<html lang>` follows the user's language; icons are hidden from screen
readers; no positive `tabindex`; every page has a title.

### Piece (b): the running app

Audited on 2026-11-26 against a freshly reset demo (`docker compose down -v && docker compose up --build`), as `admin`, with a terminal
enrolled for the station and boards, in the light and the dark theme, with the scripts in `tools/a11y-audit` (run them again the same way):

- **axe-core 4** with the `wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa` and `wcag22aa` rules on every page of the main flows: sign-in; Overview,
  with the user menu open; Inbox; Setup checklist; Floor, with the pause and report forms open; a job page; Stock; Lots; a lot page, with the
  certificate panel prepared; Scan, empty and with a lot scanned at a line (its actions offered); terminal enrollment; the station signed out
  and signed on; a line board. 37 checks.
- **Keyboard** (2.1.1, 2.1.2, 2.4.3, 2.4.7): the first Tab reaches **Skip to content** and Enter moves focus to the page; a Tab walk of up to
  80 stops on the Overview, Floor, Scan and the station, failing when focus sticks (a trap), lands on something invisible or isn't shown; the user
  menu opens with Enter, closes with Esc and gives focus back to its button.
- **Reflow** (1.4.10): every page above at 320 CSS px wide; the page itself must not scroll sideways (a data table may, in its own box).

| # | Finding | WCAG | Fix |
| --- | --- | --- | --- |
| B1 | Overview: each line's OEE bars carried an `aria-label` on a plain `span`, which screen readers ignore | 4.1.2 | `role="img"`, so the label is read |
| B2 | The user menu button's name ("Account menu for …") didn't contain the words it shows (name and roles), so a voice user saying what they see doesn't reach it | 2.5.3 | Named by its content, with "Account menu" added for screen readers |
| B3 | Floor at 320 px: hidden "why this is unavailable" tips (D-094) still took room past the right edge, so the page scrolled sideways | 1.4.10 | Tips take no room until shown, and never more than the screen's width |
| B4 | Overview at 320 px: the *Next up* table widened the page | 1.4.10 | The table scrolls in its own box (`.table-scroll`) |

After the fixes: no axe rule fails on any of the 37 checks in either theme, the keyboard checks find nothing, and no page scrolls sideways at
320 px.

**Not covered by automation**, so in the script below: what a screen reader says (order, names, live announcements), visible focus order
making sense, error messages being announced, the touch station with a real scanner, and zoom to 200% text only (1.4.4).

## Screen reader test script

Run with **VoiceOver** (macOS: ⌘F5; Safari or Chrome) or **NVDA** (Windows; Firefox or Chrome), on the demo signed in as `admin`, once in
each theme. For each step, note what is announced and anything wrong in the table at the end. "Hear" means the screen reader says it.

**Everywhere**
1. Load any page and press Tab once: hear "Skip to content, link". Press Enter, then read on (VO-→ / ↓): the page's heading comes next, not
   the menu.
2. Open the rotor (VO-U) or elements list (NVDA+F7): landmarks *banner*, *navigation "Main"* and *main* are listed; headings follow the page's
   outline (one level-1 heading, its sections below).
3. In the menu, a group's arrow says "Fold … / Unfold …, button, expanded / collapsed"; pressing it changes the state you hear.
4. Tab to your name at the top right: hear your name, your roles, "Account menu", "button", "menu pop-up". Enter opens it, arrows or Tab move
   through Preferences, My alerts, Change your password, Sign out; Esc closes it and you're back on the button.

**1. Sign in, Overview, menu**
5. Sign out and sign in: each field says its label ("User name, edit text"); a wrong password is announced as an error without moving away.
6. Overview: each line's row reads its code, state and OEE ("No current-shift OEE data" or the figure); the live indicator says when it was
   updated; a change arriving (wait for one) doesn't steal focus.
7. Inbox: each group's heading, then each item as a link naming what it is and where.

**2. Floor and job page**
8. Floor: each line's card heading, its running job, and the buttons. A button that can't be used now says "dimmed" / "unavailable" and its
   reason (the tip) as its description.
9. Pause a job: the reasons are a list or buttons with names; choosing one and pausing announces the result ("… is paused").
10. Report production: every field has its label and unit; a missing quantity is announced as an error tied to its field.
11. Job page: sections are headings; the components and runs tables read their column headers with each cell.

**3. Inventory and Scan**
12. Lots: the filters are labeled; the result count changes are announced or reachable; each lot is a link.
13. A lot's page: its facts read as name, value; **Certificate of analysis** → **Prepare** announces the result table and any warnings.
14. Scan: choose **Working at**, scan or type a lot number and Enter: the offered action is announced ("Consume … into …"), its warning read,
    the quantity labeled with its unit; **Confirm** announces what was done.

**4. Station and boards** (enrolled terminal; ideally a touch screen with a screen reader, or a keyboard)
15. Station signed out: the badge field is labeled and focused; badging in announces the person's name.
16. Signed on: the running job's heading, the **Scan** box ("Scan a lot, container or shop order"), and every action's button name; a stop to
    code lists its reasons as buttons; confirming announces the note at the top.
17. Line board: the line's state and job are read in order; it refreshes without the screen reader jumping back to the top mid-sentence.

| Step | Screen reader + browser | Theme | What was wrong | Fixed in |
| --- | --- | --- | --- | --- |
| | | | | |
