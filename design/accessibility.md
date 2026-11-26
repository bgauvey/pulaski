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
| Design tokens' contrast, both themes: text 4.5:1, control borders, state colours and focus ring 3:1 | 1.4.3, 1.4.11 | `ContrastTests.cs`, from `tokens.css` | CI |
| Every CSS rule setting both a text colour and a background, both themes (`var()` and `color-mix` resolved) | 1.4.3 | same | CI |
| The running app, page by page, with axe-core | all automatable AA rules | piece (b), below | Once, recorded here |
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
| A5 | Station status badge *down* (dark): white on danger 3.34:1 | 1.4.3 | Text in the surface colour (5.14:1 dark, 6.54:1 light) |
| A6 | Station status badge *waiting* (light): dark text on warning 3.46:1 | 1.4.3 | Surface colour (5.33:1 light, 7.70:1 dark) |
| A7 | Station status badge *no signal* (dark): white on purple 3.09:1 | 1.4.3 | Surface colour (5.57:1 dark, 6.39:1 light) |
| A8 | No way past the header and menu to the page with the keyboard | 2.4.1 | **Skip to content** first on every page, to the main region |

Checked and fine: the design tokens themselves (every pair above passes in both themes); focus is visible everywhere (`:focus-visible`
outline in the accent colour; the equipment tree draws its own); `<html lang>` follows the user's language; icons are hidden from screen
readers; no positive `tabindex`; every page has a title.

### Piece (b): axe audit of the running app

To come.

## Screen reader test script

To come with piece (b).
