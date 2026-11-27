// Takes the screenshots the training decks show (screens/*.png), from a running demo (D-101) with its demo history.
//
//   BASE=http://localhost:5090 PASSWORD=<admin's password> ENROLL_CODE=<a terminal's enrollment code> BADGE=<a badge> npm run screens
//
// Use a demo you can throw away: the shots are taken as admin, and the terminal shots enroll a terminal. ONLY=<name prefix> retakes some.
// tools/a11y-audit/setup-terminal.py sets up a terminal, a person and a badge on a fresh demo and prints the enrollment code.
const { chromium } = require("playwright");
const sharp = require("sharp");
const fs = require("fs");
const path = require("path");

const base = process.env.BASE ?? "http://localhost:5090";
const user = process.env.USER_NAME ?? "admin";
const password = process.env.PASSWORD;
const only = process.env.ONLY;
const OUT = path.join(__dirname, "screens");
const VIEW = { width: 1440, height: 900 }; // 16:10, the frame the screen slides leave for a picture

if (!password) { console.error("Set PASSWORD (admin's password on the demo)."); process.exit(1); }

async function settle(page, ms = 1500) {
    await page.waitForLoadState("networkidle").catch(() => {});
    await page.waitForTimeout(ms);
}

async function clickText(page, selector, text) {
    const l = page.locator(selector, { hasText: text }).first();
    await l.waitFor({ state: "visible", timeout: 8000 });
    await l.click();
    await settle(page);
}

async function firstHref(page, prefix) {
    return page.locator(`a[href^='${prefix}']`).first().getAttribute("href", { timeout: 8000 });
}

// name → [path, action after loading]. Paths starting with "@" follow the first link with that prefix on the page before.
const office = [
    ["sign-in", "/account/login", null, { signedOut: true }],
    ["overview", "/"],
    ["inbox", "/inbox"],
    ["my-alerts", "/my-alerts"],
    ["preferences", "/preferences"],
    ["help-panel", "/floor", (p) => p.locator("header button[aria-label*='elp'], header a[aria-label*='elp']").first().click().then(() => settle(p))],
    ["work-orders", "/work-orders"],
    ["new-work-order", "/schedule/work-orders/new"],
    ["schedule", "/schedule"],
    ["floor", "/floor"],
    ["floor-report", "/floor", (p) => clickText(p, "button", "Report")],
    ["floor-production", "/floor/production"],
    ["job", "/floor", async (p) => { await p.goto(base + "/" + (await firstHref(p, "jobs/"))); await settle(p); }],
    ["handover", "/handover"],
    ["andon", "/andon"],
    ["machines", "/machines"],
    ["machines-oee", "/machines?tab=reports"],
    ["machines-maintenance", "/machines?tab=maintenance"],
    ["labor", "/labor"],
    ["labor-review", "/labor/review"],
    ["inventory", "/inventory"],
    ["lots", "/inventory/lots"],
    ["lot", "/inventory/lots", async (p) => { await p.goto(base + "/" + (await firstHref(p, "inventory/lots/0"))); await settle(p); }],
    ["scan", "/inventory/scan"],
    ["shortages", "/inventory/shortages"],
    ["allocations", "/inventory/jobs"],
    ["counts", "/inventory/counts"],
    ["journal", "/inventory/journal"],
    ["incoming", "/inventory/incoming"],
    ["quality-results", "/quality/results"],
    ["quality-charts", "/quality/charts", async (p) => {
        const melt = await p.locator("#cc-characteristic option", { hasText: "MELT-TEMP" }).first().getAttribute("value");
        await p.selectOption("#cc-characteristic", melt);
        await settle(p, 500);
        const wh = await p.locator("datalist option[value*='PRF-4410-WH']").first().getAttribute("value");
        await p.fill("#cc-material", wh);
        await p.locator("#cc-material").press("Tab");
        await settle(p, 500);
        await clickText(p, "button", "Show");
        await p.locator(".control-chart, svg").first().scrollIntoViewIfNeeded().catch(() => {});
    }],
    ["quality-alerts", "/quality/alerts"],
    ["ncrs", "/quality/ncrs"],
    ["specs", "/quality/specs"],
    ["materials", "/materials"],
    ["material", "/materials", async (p) => {
        await p.fill("input[placeholder*='code']", "PRF-4410-WH");
        await p.keyboard.press("Enter");
        await settle(p);
        const href = await p.locator("a[href^='materials/0']", { hasText: "PRF-4410-WH" }).first().getAttribute("href");
        await p.goto(base + "/" + href);
        await settle(p);
    }],
    ["processes", "/processes"],
    ["process", "/processes", async (p) => { await p.goto(base + "/" + (await firstHref(p, "processes/0"))); await settle(p); }],
    ["boms", "/boms"],
    ["changeovers", "/changeovers"],
    ["instructions", "/instructions"],
    ["shifts", "/shifts"],
    ["people", "/people"],
    ["terminals", "/terminals"],
    ["site-settings", "/site-settings"],
    ["setup", "/setup"],
    ["reasons", "/reasons"],
    ["audit", "/audit"],
    ["users", "/users"],
    ["roles", "/users/roles"],
    ["equipment", "/equipment"],
    ["machine-profiles", "/machine-profiles"],
    ["edge-nodes", "/edge-nodes"],
    ["integrations", "/integrations"],
    ["system-health", "/system-health"],
    ["languages", "/languages"],
];

// fullPage takes the whole page; around = a selector to crop to, with a margin (a small form on an empty page).
async function save(page, name, { fullPage = false, around } = {}) {
    let clip;
    if (around) {
        const b = await page.locator(around).first().boundingBox();
        const m = 60;
        clip = { x: Math.max(0, b.x - m * 2), y: Math.max(0, b.y - m), width: b.width + m * 4, height: b.height + m * 2 };
    }
    let png;
    for (let tries = 0; ; tries++) {
        try { png = await page.screenshot({ fullPage, clip }); break; } catch (e) { // a board can reload itself mid-shot
            if (tries === 3) throw e;
            await settle(page);
        }
    }
    await sharp(png).png({ palette: true, quality: 90, effort: 8 }).toFile(path.join(OUT, `${name}.png`));
    console.log("  " + name);
}

(async () => {
    fs.mkdirSync(OUT, { recursive: true });
    const browser = await chromium.launch();
    const context = await browser.newContext({ viewport: VIEW, deviceScaleFactor: 1.5, locale: "en-US" });
    await context.addInitScript(() => { try { localStorage.setItem("pulaski.theme", "light"); } catch {} });
    const page = await context.newPage();
    const want = (name) => !only || name.startsWith(only);

    await page.goto(base + "/account/login");
    await settle(page);
    if (want("sign-in")) await save(page, "sign-in", { around: "xpath=//form/.." });
    await page.fill("#username", user);
    await page.fill("#password", password);
    await page.click("button[type=submit]");
    await page.waitForURL((u) => !u.toString().includes("/account/login"), { timeout: 20000 });

    for (const [name, where, then, opts] of office) {
        if (opts?.signedOut || !want(name)) continue;
        try {
            await page.goto(base + where);
            await settle(page);
            if (then) await then(page);
            await save(page, name);
        } catch (e) {
            console.log(`  ${name}: skipped (${e.message.split("\n")[0]})`);
        }
    }

    // The operator station, as an enrolled terminal. The line board's shots are capture-board.js's.
    if (process.env.ENROLL_CODE && want("station")) {
        const t = await browser.newContext({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 1.5, locale: "en-US" });
        const tp = await t.newPage();
        await tp.goto(base + "/terminal/enrol");
        await settle(tp);
        await tp.locator("#code").fill(process.env.ENROLL_CODE);
        await tp.locator("button[type=submit]").first().click();
        await settle(tp);
        await tp.goto(base + "/terminal/station");
        await settle(tp);
        await save(tp, "station-signed-out");
        if (process.env.BADGE) {
            await tp.locator("input[name='Input.Badge']").fill(process.env.BADGE);
            await tp.locator("input[name='Input.Badge']").press("Enter");
            await settle(tp);
            await save(tp, "station", { fullPage: true });
        }
        await t.close();
    }
    await browser.close();
})().catch((e) => { console.error(e); process.exit(1); });
