// 1.4.10 reflow (#142b): at 320 CSS px wide, the page itself mustn't scroll sideways (tables and charts may scroll in their own box).
import { chromium } from "playwright";
const base = "http://localhost:5090";
const browser = await chromium.launch();
const context = await browser.newContext({ viewport: { width: 320, height: 720 } });
const page = await context.newPage();
await page.goto(base + "/account/login");
await page.fill("#username", "admin");
await page.fill("#password", process.env.PASSWORD);
await page.click("button[type=submit]");
await page.waitForURL(u => !u.toString().includes("/account/login"), { timeout: 20000 }).catch(() => {});
for (const path of ["/", "/inbox", "/setup", "/floor", "/inventory", "/inventory/lots", "/inventory/scan"]) {
  await page.goto(base + path);
  await page.waitForLoadState("networkidle").catch(() => {});
  await page.waitForTimeout(1200);
  const r = await page.evaluate(() => {
    const doc = document.documentElement;
    const wide = [...document.querySelectorAll("body *")].filter(e => {
      const b = e.getBoundingClientRect();
      if (b.right <= doc.clientWidth + 1 || b.width === 0) return false;
      for (let p = e.parentElement; p; p = p.parentElement) {
        const o = getComputedStyle(p).overflowX;
        if (o === "auto" || o === "scroll" || o === "hidden") return false;
      }
      return true;
    }).slice(0, 4).map(e => e.tagName.toLowerCase() + (e.className ? "." + String(e.className).split(" ").slice(0, 2).join(".") : ""));
    return { scroll: doc.scrollWidth, client: doc.clientWidth, wide };
  });
  console.log(`${path.padEnd(18)} page ${r.scroll}/${r.client}${r.scroll > r.client + 1 ? "  SCROLLS: " + r.wide.join(", ") : "  ok"}`);
}
await browser.close();
