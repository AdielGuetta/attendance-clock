// The demo flow in a real (headless) browser against a running system (docker compose up):
// pick an employee → clock in → refresh the page → the timer continues → clock out → the shift appears in the manager report.
// Runs in CI. Locally: npm i playwright && npx playwright install chromium && node scripts/browser-demo.mjs
//   node browser-demo.mjs [base-url] [employee-id] [screenshots-dir]
import { chromium } from "playwright";
import { mkdirSync } from "node:fs";

const [base = "http://localhost:8080", employeeId = "2", shots = "browser-shots"] = process.argv.slice(2);
mkdirSync(shots, { recursive: true });

const browser = await chromium.launch();
const page = await browser.newPage({ locale: "he-IL", viewport: { width: 1100, height: 900 } });
const problems = [];
page.on("console", m => { if (m.type() === "error") problems.push(`console: ${m.text()}`); });
page.on("pageerror", e => problems.push(`page error: ${e.message}`));
page.on("response", r => { if (r.url().includes("/api/") && r.status() >= 500) problems.push(`HTTP ${r.status()} ${r.url()}`); });

// GitHub shows ::notice / ::error lines as public annotations on the run page (logs need sign-in).
const esc = s => String(s).replace(/%/g, "%25").replace(/\r/g, "").replace(/\n/g, "%0A");
const note = (title, text) => console.log(`::notice title=browser: ${title}::${esc(text)}`);

let n = 0;
const step = async (title, fn) => {
  process.stdout.write(`== ${title}\n`);
  await fn();
  await page.screenshot({ path: `${shots}/${String(++n).padStart(2, "0")}-${title.replace(/\W+/g, "-")}.png`, fullPage: true });
};
const punchButton = () => page.locator("button.punch");
const waitForButton = text => page.locator("button.punch", { hasText: text }).and(page.locator(":enabled")).waitFor({ timeout: 40_000 });
const selectEmployee = async () => {
  await page.locator("header select").selectOption(employeeId);
  await punchButton().and(page.locator(":enabled")).waitFor({ timeout: 20_000 });
};

try {
  await step("open the employee screen", async () => {
    await page.goto(base);
    await selectEmployee();
    // A shift left open by an earlier run is closed first, so the flow always starts from "not on shift".
    if ((await punchButton().innerText()).includes("יציאה")) {
      await punchButton().click();
      await waitForButton("החתמת כניסה");
    }
  });

  await step("clock in", async () => {
    await punchButton().click();
    await waitForButton("החתמת יציאה");
    note("clock-in", await page.locator(".notice").first().innerText());
  });

  await step("refresh during the open shift", async () => {
    await page.reload();
    await waitForButton("החתמת יציאה");
    if (await page.locator("header select").inputValue() !== employeeId) throw new Error("the employee was not remembered after refresh");
    const t1 = await page.locator(".timer").innerText();
    await page.waitForTimeout(2500);
    const t2 = await page.locator(".timer").innerText();
    note("refresh", `timer ${t1} → ${t2}`);
    if (t1 === t2) throw new Error("the timer did not continue after refresh");
  });

  await step("clock out", async () => {
    await punchButton().click();
    await waitForButton("החתמת כניסה");
    note("clock-out", await page.locator(".notice").first().innerText());
  });

  await step("manager report", async () => {
    const name = await page.locator(`header select option[value="${employeeId}"]`).innerText();
    await page.getByRole("button", { name: "מנהל" }).click();
    const shifts = page.locator("h2", { hasText: "משמרות בתקופה" }).locator("xpath=following-sibling::*[1]");
    await shifts.getByText(name).first().waitFor({ timeout: 20_000 });
    const summary = page.locator("table.summary tr", { hasText: name });
    note("report", (await summary.innerText()).replace(/\s+/g, " "));
  });

  if (problems.length) throw new Error(`browser problems:\n${problems.join("\n")}`);
  console.log("== All browser steps passed");
} catch (err) {
  await page.screenshot({ path: `${shots}/failure.png`, fullPage: true }).catch(() => {});
  console.log(`::error title=browser demo failed::${esc(err?.stack ?? err)}${problems.length ? esc("\n" + problems.join("\n")) : ""}`);
  console.error(err);
  process.exitCode = 1;
} finally {
  await browser.close();
}
