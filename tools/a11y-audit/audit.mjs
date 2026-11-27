// axe-core audit of the running demo (#142b): every page of the main flows, light and dark, WCAG 2.0-2.2 A and AA rules.
import { chromium } from "playwright";
import { AxeBuilder } from "@axe-core/playwright";
import fs from "node:fs";

const base = process.env.BASE ?? "http://localhost:5090";
const user = process.env.USER_NAME ?? "admin";
const password = process.env.PASSWORD ?? "Pulaski-demo-1";
const only = process.env.ONLY;
const tags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];
const results = [];
let terminalCookies = null;

async function settle(page) {
  await page.waitForLoadState("networkidle").catch(() => {});
  await page.waitForTimeout(1200);
}

async function check(page, name, theme) {
  await settle(page);
  const r = await new AxeBuilder({ page }).withTags(tags).analyze();
  for (const v of r.violations) {
    results.push({ page: name, theme, id: v.id, impact: v.impact, help: v.help, url: page.url(),
      nodes: v.nodes.slice(0, 6).map(n => ({ target: n.target.join(" "), summary: n.failureSummary?.split("\n").slice(1, 3).join(" ") })) , count: v.nodes.length });
  }
  console.log(`${theme.padEnd(5)} ${name.padEnd(28)} ${r.violations.length} rule(s) failing, ${r.passes.length} passing`);
}

// Keyboard (2.1.1, 2.1.2, 2.4.3, 2.4.7): Tab through the page; focus must keep moving, land only on visible things, and show.
const keyboard = [];
async function tabWalk(page, name, theme, steps = 80) {
  await settle(page);
  await page.locator("body").click({ position: { x: 1, y: 1 } }).catch(() => {});
  const seen = [];
  let repeats = 0, hidden = [], unoutlined = [];
  for (let i = 0; i < steps; i++) {
    await page.keyboard.press("Tab");
    const f = await page.evaluate(() => {
      const e = document.activeElement;
      if (!e || e === document.body) return null;
      const r = e.getBoundingClientRect(), cs = getComputedStyle(e);
      const label = (e.getAttribute("aria-label") || e.innerText || e.getAttribute("name") || e.id || e.tagName).trim().slice(0, 40);
      return { key: e.tagName + "#" + (e.id || "") + "." + label, visible: r.width > 0 && r.height > 0 && cs.visibility !== "hidden",
        outlined: cs.outlineStyle !== "none" && parseFloat(cs.outlineWidth) > 0 || cs.boxShadow !== "none" };
    }).catch(() => "reloaded");
    if (f === "reloaded") { await settle(page); continue; }
    if (!f) continue;
    if (seen.length && seen[seen.length - 1] === f.key) repeats++; else repeats = 0;
    if (repeats >= 3) { keyboard.push(`${theme}/${name}: focus stuck on ${f.key}`); break; }
    if (!f.visible) hidden.push(f.key);
    if (!f.outlined) unoutlined.push(f.key);
    seen.push(f.key);
  }
  if (hidden.length) keyboard.push(`${theme}/${name}: focus on invisible ${[...new Set(hidden)].slice(0, 5).join(" | ")}`);
  if (unoutlined.length) keyboard.push(`${theme}/${name}: focus not shown on ${[...new Set(unoutlined)].slice(0, 5).join(" | ")}`);
  console.log(`${theme.padEnd(5)} ${name.padEnd(28)} tabbed ${seen.length} stops`);
}

async function clickIf(page, locator) {
  const l = page.locator(locator).first();
  if (await l.count() > 0 && await l.isVisible()) { await l.click(); await settle(page); return true; }
  return false;
}

for (const theme of ["light", "dark"]) {
  const browser = await chromium.launch();
  const context = await browser.newContext({ viewport: { width: 1366, height: 900 } });
  await context.addInitScript(t => { try { localStorage.setItem("pulaski.theme", t); } catch {} }, theme);
  const page = await context.newPage();
  const go = async (path, name, then) => {
    if (only && !name.startsWith(only)) return;
    await page.goto(base + path);
    await settle(page);
    if (then) await then();
    await check(page, name, theme);
  };

  // 1. Sign in, Overview, menu
  await page.goto(base + "/account/login");
  await check(page, "1 sign-in", theme);
  await page.fill("#username", user);
  await page.fill("#password", password);
  await page.click("button[type=submit]");
  await page.waitForURL(u => !u.toString().includes("/account/login"), { timeout: 20000 }).catch(() => {});
  await go("/", "1 overview");
  // 2.4.1: the first Tab is the skip link, and Enter moves focus to the page.
  await page.goto(base + "/"); await settle(page);
  await page.keyboard.press("Tab");
  const first = await page.evaluate(() => document.activeElement?.className ?? "");
  await page.keyboard.press("Enter");
  const after = await page.evaluate(() => document.activeElement?.id ?? "");
  if (!String(first).includes("skip-link") || after !== "main-content") keyboard.push(`${theme}: skip link first=${first} then=${after}`);
  await tabWalk(page, "1 overview keys", theme);
  // The user menu opens and closes from the keyboard, and gives focus back.
  await page.goto(base + "/"); await settle(page);
  await page.locator(".user-button").focus();
  await page.keyboard.press("Enter"); await page.waitForTimeout(300);
  const inMenu = await page.evaluate(() => document.querySelector("#user-menu")?.matches(":popover-open") ?? false);
  await page.keyboard.press("Escape"); await page.waitForTimeout(300);
  const back = await page.evaluate(() => document.activeElement?.className ?? "");
  if (!inMenu || !String(back).includes("user-button")) keyboard.push(`${theme}: user menu open=${inMenu}, focus after Esc on ${back}`);
  await go("/", "1 user menu open", () => clickIf(page, ".user-menu button, button.user-button, header .header-end button"));
  await go("/inbox", "1 inbox");
  await go("/setup", "1 setup checklist");

  // 2. Floor and job page
  await go("/floor", "2 floor");
  await tabWalk(page, "2 floor keys", theme);
  let jobHref = await page.locator("a[href^='jobs/']").first().getAttribute("href").catch(() => null);
  await go("/floor", "2 floor pause form", () => clickIf(page, "button:has-text('Pause')"));
  await go("/floor", "2 floor report form", () => clickIf(page, "button:has-text('Report')"));
  if (jobHref) {
    await go("/" + jobHref, "2 job page");
  }

  // 3. Inventory and Scan
  await go("/inventory", "3 stock");
  await go("/inventory/lots", "3 lots");
  const lotHref = await page.locator("a[href^='inventory/lots/']").first().getAttribute("href").catch(() => null);
  if (lotHref) {
    await go("/" + lotHref, "3 lot page");
    await go("/" + lotHref, "3 lot certificate panel", () => clickIf(page, "button:has-text('Prepare')"));
  }
  await go("/inventory/scan", "3 scan");
  // Scanning at a line: the actions offered for a lot.
  await go("/inventory/scan", "3 scan with actions", async () => {
    await page.selectOption("#scan-line", { index: 1 }).catch(() => {});
    await settle(page);
    await page.fill("#scan-box", process.env.SCAN_CODE ?? "");
    await page.press("#scan-box", "Enter");
    await settle(page);
    console.log("      scan offered:", (await page.locator("section[aria-label]").allInnerTexts()).join(" / ").replace(/\s+/g, " ").slice(0, 160));
  });
  await tabWalk(page, "3 scan keys", theme, 40);

  // 4. Station and boards (only with a terminal cookie, set by ENROLL_CODE)
  if (process.env.ENROLL_CODE) {
    if (terminalCookies === null) {
      await page.goto(base + "/terminal/enrol");
      await settle(page);
      await check(page, "4 terminal enroll", theme);
      await page.locator("#code").fill(process.env.ENROLL_CODE);
      await page.locator("button[type=submit]").first().click();
      await settle(page);
      terminalCookies = (await context.cookies()).filter(c => c.name.toLowerCase().includes("terminal"));
      console.log("terminal cookies:", terminalCookies.map(c => c.name).join(", "));
    } else {
      await context.addCookies(terminalCookies);
    }
    await go("/terminal/station", "4 station signed out");
    if (process.env.BADGE) {
      await page.locator("input[name='Input.Badge']").fill(process.env.BADGE);
      await page.locator("input[name='Input.Badge']").press("Enter");
      await settle(page);
      await check(page, "4 station signed on", theme);
      await tabWalk(page, "4 station keys", theme);
    }
    await go("/terminal/board", "4 line board");
  }

  await browser.close();
}

fs.writeFileSync("results.json", JSON.stringify(results, null, 2));
console.log("\n== Keyboard ==");
console.log(keyboard.length ? keyboard.join("\n") : "no problems found");
const byRule = {};
for (const r of results) {
  const k = `${r.id} (${r.impact})`;
  (byRule[k] ??= []).push(`${r.theme}/${r.page} x${r.count}`);
}
console.log("\n== Failing rules ==");
for (const [k, v] of Object.entries(byRule)) console.log(`${k}: ${v.join(", ")}`);
