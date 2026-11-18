// A Node stand-in for the ASP.NET Core API, for end-to-end testing of the React UI only.
// Why it exists: Smart App Control on the development machine blocks running locally built .NET DLLs.
// It implements the same API contract (paths, fields, error codes) in memory, but takes the time from the
// real external service (timezone.io → timeapi.io), just like the real server.
// It does NOT replace the .NET tests (those run in GitHub Actions against SQL Server).
//
//   node scripts/mock-api.mjs            → http://localhost:5080
//   POST /__mock/config {"timeFailure":true,"delayMs":3000}   inject an outage/delay
//   GET  /__mock/log                                        request log (to check that requestId repeats)
import { createServer } from "node:http";
import { randomUUID } from "node:crypto";

const PORT = Number(process.env.PORT ?? 5080);
const ZONE = "Europe/Zurich";
const employees = [{ id: 1, name: "נועה כהן" }, { id: 2, name: "יוסי לוי" }, { id: 3, name: "מאיה פרץ" }];
const shifts = [];             // { id, employeeId, startUtc, endUtc, startSource, endSource }
const punches = new Map();     // `${employeeId}:${requestId}` → receipt
const config = { timeFailure: false, delayMs: 0 };
const log = [];
let chain = Promise.resolve(); // serializes punches (stands in for the row lock)

// ---------- Zurich time ----------
const offsetFmt = new Intl.DateTimeFormat("en-US", { timeZone: ZONE, timeZoneName: "longOffset" });
function offsetMinutes(date) {
  const m = offsetFmt.formatToParts(date).find(p => p.type === "timeZoneName").value.match(/GMT([+-])(\d\d):(\d\d)/);
  return m ? (m[1] === "-" ? -1 : 1) * (Number(m[2]) * 60 + Number(m[3])) : 0;
}
function toZurichIso(utcIso) {
  const d = new Date(utcIso);
  const off = offsetMinutes(d);
  const local = new Date(d.getTime() + off * 60000).toISOString().slice(0, 19);
  const sign = off >= 0 ? "+" : "-";
  const a = Math.abs(off);
  return `${local}${sign}${String(Math.floor(a / 60)).padStart(2, "0")}:${String(a % 60).padStart(2, "0")}`;
}
function midnightUtc(isoDate) {
  const [y, m, d] = isoDate.split("-").map(Number);
  const guess = Date.UTC(y, m - 1, d);
  let t = guess - offsetMinutes(new Date(guess)) * 60000;
  t = guess - offsetMinutes(new Date(t)) * 60000;
  return t;
}
const zurichDate = utcIso => toZurichIso(utcIso).slice(0, 10);
const addDays = (iso, n) => { const d = new Date(iso + "T12:00:00Z"); d.setUTCDate(d.getUTCDate() + n); return d.toISOString().slice(0, 10); };

// ---------- External time (the same providers as the server) ----------
async function externalTime() {
  if (config.delayMs) await new Promise(r => setTimeout(r, config.delayMs));
  if (config.timeFailure) throw Object.assign(new Error("time"), { status: 503, code: "time_unavailable",
    msg: "שירות השעה החיצוני אינו זמין כרגע. ההחתמה לא נשמרה. אפשר לנסות שוב בעוד רגע." });
  try {
    const r = await fetch("https://worldtime.timezone.io/api/timezone/Europe/Zurich", { signal: AbortSignal.timeout(4000) });
    const j = await r.json();
    if (j.timezone !== ZONE || !/(Z|\+00:00)$/.test(j.utc_datetime)) throw new Error("invalid");
    return { utc: new Date(j.utc_datetime).toISOString(), source: "worldtimeapi" };
  } catch {
    const r = await fetch("https://timeapi.io/api/time/current/zone?timeZone=Europe%2FZurich", { signal: AbortSignal.timeout(4000) });
    const j = await r.json();
    const localMs = Date.parse(j.dateTime.slice(0, 23) + "Z");
    const off = j.dstActive ? 120 : 60;
    return { utc: new Date(localMs - off * 60000).toISOString(), source: "timeapi.io" };
  }
}

// ---------- Domain ----------
const fail = (status, code, msg) => Object.assign(new Error(code), { status, code, msg });
const nameOf = id => employees.find(e => e.id === id)?.name;
function dto(s) {
  const startZurich = toZurichIso(s.startUtc);
  const endZurich = s.endUtc ? toZurichIso(s.endUtc) : null;
  return {
    id: s.id, employeeId: s.employeeId, employeeName: nameOf(s.employeeId),
    startUtc: s.startUtc, startZurich, endUtc: s.endUtc, endZurich,
    durationSeconds: s.endUtc ? Math.floor((Date.parse(s.endUtc) - Date.parse(s.startUtc)) / 1000) : null,
    crossesMidnight: !!endZurich && endZurich.slice(0, 10) !== startZurich.slice(0, 10),
    spansDstChange: !!endZurich && endZurich.slice(-6) !== startZurich.slice(-6),
    startSource: s.startSource, endSource: s.endSource,
  };
}
const receipt = (requestId, shiftId, action, utc, source, replayed = false) =>
  ({ requestId, shiftId, action, atUtc: utc, atZurich: toZurichIso(utc), timeSource: source, replayed });

async function punch(employeeId, action, body) {
  if (!nameOf(employeeId)) throw fail(404, "employee_not_found", "העובד לא נמצא.");
  const { requestId, shiftId } = body ?? {};
  if (!requestId || (action === "out" && !shiftId)) throw fail(400, "invalid_punch", "פרטי ההחתמה אינם תקינים.");
  const key = `${employeeId}:${requestId}`;
  const replay = () => {
    const r = punches.get(key);
    if (r.action !== action || (action === "out" && r.shiftId !== shiftId)) throw fail(409, "request_reused", "מזהה ההחתמה כבר שימש לפעולה אחרת.");
    return { ...r, replayed: true };
  };
  if (punches.has(key)) return replay();
  const check = at => {
    const open = shifts.find(s => s.employeeId === employeeId && !s.endUtc);
    if (action === "in" && open) throw fail(409, "shift_already_open", "כבר יש משמרת פתוחה. רעננו את המסך כדי לראות אותה.");
    if (action === "out" && !open) throw fail(409, "no_open_shift", "אין משמרת פתוחה לסגירה. ייתכן שהיא נסגרה בחלון אחר.");
    if (action === "out" && open.id !== shiftId) throw fail(409, "shift_changed", "המשמרת השתנתה מאז שהמסך נטען. רעננו ונסו שוב.");
    if (at && open && at < open.startUtc) throw fail(409, "time_before_start", "השעה שהתקבלה מוקדמת משעת הכניסה.");
    const lastEnd = shifts.filter(s => s.employeeId === employeeId && s.endUtc).map(s => s.endUtc).sort().at(-1);
    if (at && action === "in" && lastEnd && at < lastEnd) throw fail(409, "time_before_previous_end", "השעה שהתקבלה מוקדמת מסיום המשמרת הקודמת.");
    return open;
  };
  check(null);
  const now = await externalTime();      // before the "lock", like the server
  const run = chain.then(() => {
    if (punches.has(key)) return replay();
    const open = check(now.utc);
    let id;
    if (action === "in") { id = randomUUID(); shifts.push({ id, employeeId, startUtc: now.utc, endUtc: null, startSource: now.source, endSource: null }); }
    else { id = open.id; open.endUtc = now.utc; open.endSource = now.source; }
    const r = receipt(requestId, id, action, now.utc, now.source);
    punches.set(key, r);
    return r;
  });
  chain = run.catch(() => {});
  return run;
}

function report(from, to) {
  const days = [];
  for (let d = from; d <= to; d = addDays(d, 1)) days.push(d);
  const start = midnightUtc(from), end = midnightUtc(addDays(to, 1)), now = Date.now();
  const inRange = shifts.filter(s => Date.parse(s.startUtc) < end && (!s.endUtc || Date.parse(s.endUtc) > start))
    .sort((a, b) => a.startUtc.localeCompare(b.startUtc));
  return {
    from, to, generatedAtUtc: new Date().toISOString(),
    employees: employees.map(e => {
      const own = inRange.filter(s => s.employeeId === e.id);
      const perDay = days.map(date => {
        const ds = midnightUtc(date), de = midnightUtc(addDays(date, 1));
        const seconds = own.reduce((sum, s) => {
          const se = s.endUtc ? Date.parse(s.endUtc) : Math.max(now, Date.parse(s.startUtc));
          return sum + Math.max(0, Math.floor((Math.min(se, de) - Math.max(Date.parse(s.startUtc), ds)) / 1000));
        }, 0);
        return { date, seconds };
      });
      return { employeeId: e.id, employeeName: e.name, totalSeconds: perDay.reduce((a, d) => a + d.seconds, 0),
        daysWorked: perDay.filter(d => d.seconds > 0).length, shiftCount: own.length, hasOpenShift: own.some(s => !s.endUtc), days: perDay };
    }),
    shifts: inRange.map(dto),
  };
}

// ---------- HTTP ----------
const send = (res, status, body) => {
  res.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
  res.end(JSON.stringify(body));
};
async function readBody(req) {
  let raw = "";
  for await (const chunk of req) raw += chunk;
  try { return raw ? JSON.parse(raw) : null; } catch { throw fail(400, "bad_request", "הבקשה אינה תקינה."); }
}

createServer(async (req, res) => {
  const url = new URL(req.url, "http://localhost");
  const p = url.pathname;
  try {
    let m;
    if (p === "/__mock/config" && req.method === "POST") { Object.assign(config, await readBody(req)); return send(res, 200, config); }
    if (p === "/__mock/log") return send(res, 200, log);
    if (p === "/__mock/reset" && req.method === "POST") { shifts.length = 0; punches.clear(); log.length = 0; return send(res, 200, {}); }
    if (p === "/api/health") return send(res, 200, { status: "ok" });
    if (p === "/api/employees") return send(res, 200, employees);
    if ((m = p.match(/^\/api\/employees\/(\d+)\/attendance$/))) {
      const id = Number(m[1]);
      if (!nameOf(id)) throw fail(404, "employee_not_found", "העובד לא נמצא.");
      const own = shifts.filter(s => s.employeeId === id).sort((a, b) => b.startUtc.localeCompare(a.startUtc));
      const open = own.find(s => !s.endUtc);
      return send(res, 200, { employee: employees.find(e => e.id === id), openShift: open ? dto(open) : null,
        shifts: own.slice(0, 30).map(dto), serverNowUtc: new Date().toISOString() });
    }
    if ((m = p.match(/^\/api\/employees\/(\d+)\/punches\/([0-9a-f-]{36})$/))) {
      const r = punches.get(`${m[1]}:${m[2]}`);
      return r ? send(res, 200, r) : send(res, 404, { code: "punch_not_found", error: "ההחתמה לא נשמרה." });
    }
    if ((m = p.match(/^\/api\/employees\/(\d+)\/clock-(in|out)$/)) && req.method === "POST") {
      const body = await readBody(req);
      log.push({ at: new Date().toISOString(), employeeId: Number(m[1]), action: m[2], requestId: body?.requestId, shiftId: body?.shiftId ?? null });
      return send(res, 200, await punch(Number(m[1]), m[2], body));
    }
    if (p === "/api/report") {
      const today = zurichDate(new Date().toISOString());
      const from = url.searchParams.get("from") ?? today, to = url.searchParams.get("to") ?? from;
      if (!/^\d{4}-\d\d-\d\d$/.test(from) || !/^\d{4}-\d\d-\d\d$/.test(to)) throw fail(400, "invalid_date", "יש לבחור תאריך תקין בפורמט YYYY-MM-DD.");
      if (to < from) throw fail(400, "invalid_range", "תאריך הסיום קודם לתאריך ההתחלה.");
      return send(res, 200, report(from, to));
    }
    send(res, 404, { code: "not_found", error: "הכתובת לא נמצאה." });
  } catch (err) {
    if (err.status) return send(res, err.status, { code: err.code, error: err.msg });
    console.error(err);
    send(res, 503, { code: "time_unavailable", error: "שירות השעה החיצוני אינו זמין כרגע. ההחתמה לא נשמרה." });
  }
}).listen(PORT, "localhost", () => console.log(`mock API listening on http://localhost:${PORT}`));
