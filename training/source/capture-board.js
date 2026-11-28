// Takes the screenshots of the line board deck (screens/tv-*.png): registering a TV, enrolling it and the board it shows, from a demo
// you can throw away (it registers terminal TV-FLOOR and enrolls a browser as it).
//
//   BASE=http://localhost:5090 PASSWORD=<admin's password> npm run screens:board
const { chromium } = require("playwright");
const sharp = require("sharp");
const path = require("path");

const base = process.env.BASE ?? "http://localhost:5090";
const user = process.env.USER_NAME ?? "admin";
const password = process.env.PASSWORD;
const OUT = path.join(__dirname, "screens");
const TV = { width: 1920, height: 1080 }; // a 1080p TV

if (!password) { console.error("Set PASSWORD (admin's password on the demo)."); process.exit(1); }

async function settle(page, ms = 1500) {
    await page.waitForLoadState("networkidle").catch(() => {});
    await page.waitForTimeout(ms);
}

async function save(page, name, { around, margin = 40 } = {}) {
    let clip;
    if (around) {
        const b = await page.locator(around).first().boundingBox();
        const m = margin;
        clip = { x: Math.max(0, b.x - m), y: Math.max(0, b.y - m), width: b.width + m * 2, height: b.height + m * 2 };
    }
    let png;
    for (let tries = 0; ; tries++) {
        try { png = await page.screenshot({ clip }); break; } catch (e) { // a board can reload itself mid-shot
            if (tries === 3) throw e;
            await settle(page);
        }
    }
    await sharp(png).png({ palette: true, quality: 90, effort: 8 }).toFile(path.join(OUT, `${name}.png`));
    console.log("  " + name);
}

(async () => {
    const browser = await chromium.launch();

    // The office side: a manager registers the TV and issues its code.
    const office = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1.5, locale: "en-US" });
    await office.addInitScript(() => { try { localStorage.setItem("pulaski.theme", "light"); } catch {} });
    const p = await office.newPage();
    await p.goto(base + "/account/login");
    await settle(p);
    await p.fill("#username", user);
    await p.fill("#password", password);
    await save(p, "tv-sign-in", { around: "xpath=//form/.." });
    await p.click("button[type=submit]");
    await p.waitForURL((u) => !u.toString().includes("/account/login"), { timeout: 20000 });

    await p.goto(base + "/terminals");
    await settle(p);
    if (!(await p.locator("td", { hasText: "TV-FLOOR" }).count())) {
        await p.fill("#term-code", "TV-FLOOR");
        await p.fill("#term-name", "Plant floor TV");
        await settle(p, 500);
        await save(p, "tv-register");
        await p.locator("button", { hasText: "Register terminal" }).click();
        await settle(p);
    }
    await p.locator("tr", { hasText: "TV-FLOOR" }).locator("button", { hasText: "Enrollment code" }).click();
    await settle(p);
    await save(p, "tv-code");
    const code = (await p.locator(".alert-info .font-monospace").first().textContent()).trim();
    await office.close();

    // The TV: a browser that has never been enrolled.
    const tv = await browser.newContext({ viewport: TV, deviceScaleFactor: 1, locale: "en-US" });
    const t = await tv.newPage();
    await t.goto(base + "/terminal/board");
    await settle(t);
    await save(t, "tv-not-enrolled", { around: ".board-message", margin: 160 });
    await t.goto(base + "/terminal/enrol");
    await settle(t);
    await t.locator("#code").fill(code);
    await save(t, "tv-enroll", { around: "xpath=//form/.." });
    await t.locator("button[type=submit]").first().click();
    await settle(t);
    await t.goto(base + "/terminal/board");
    await settle(t, 2500);
    await save(t, "tv-board");
    await t.goto(base + "/terminal/board?line=EXT-01");
    await settle(t, 2500);
    await save(t, "tv-board-line");
    await tv.close();
    await browser.close();
})().catch((e) => { console.error(e); process.exit(1); });
