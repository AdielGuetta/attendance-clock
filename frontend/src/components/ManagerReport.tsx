import { Fragment, useEffect, useState } from "react";
import { api, type Report } from "../api";
import { addDays, formatDuration, monthBounds, splitZurich, weekBounds, weekdayShort, zurichToday } from "../time";
import { ShiftTable } from "./ShiftTable";

type Period = "today" | "day" | "week" | "month" | "custom";

const periods: [Period, string][] = [
  ["today", "היום"], ["day", "יום"], ["week", "שבוע"], ["month", "חודש"], ["custom", "טווח"],
];

function rangeFor(period: Period, day: string, month: string, custom: { from: string; to: string }) {
  switch (period) {
    case "today": { const t = zurichToday(); return { from: t, to: t }; }
    case "day": return { from: day, to: day };
    case "week": return weekBounds(day);
    case "month": return monthBounds(month);
    case "custom": return custom;
  }
}

function toCsv(report: Report) {
  const rows: string[][] = [["עובד", "כניסה (ציריך)", "יציאה (ציריך)", "משך (שעות:דקות)", "מקור שעת כניסה", "מקור שעת יציאה"]];
  for (const s of report.shifts) {
    const start = splitZurich(s.startZurich);
    const end = s.endZurich ? splitZurich(s.endZurich) : null;
    rows.push([
      s.employeeName,
      `${start.date} ${start.time} UTC${start.offset}`,
      end ? `${end.date} ${end.time} UTC${end.offset}` : "פתוחה",
      s.durationSeconds !== null ? formatDuration(s.durationSeconds) : "",
      s.startSource, s.endSource ?? "",
    ]);
  }
  rows.push([]);
  rows.push(["סיכום לפי עובד", "סה״כ שעות", "ימי עבודה", "משמרות", "משמרת פתוחה"]);
  for (const e of report.employees)
    rows.push([e.employeeName, formatDuration(e.totalSeconds), String(e.daysWorked), String(e.shiftCount), e.hasOpenShift ? "כן" : ""]);
  const escape = (v: string) => /[",\n]/.test(v) ? `"${v.replaceAll('"', '""')}"` : v;
  return "﻿" + rows.map(r => r.map(escape).join(",")).join("\r\n"); // BOM so Excel shows Hebrew correctly
}

function download(report: Report) {
  const url = URL.createObjectURL(new Blob([toCsv(report)], { type: "text/csv;charset=utf-8" }));
  const a = Object.assign(document.createElement("a"), { href: url, download: `attendance_${report.from}_${report.to}.csv` });
  a.click();
  URL.revokeObjectURL(url);
}

export function ManagerReport() {
  const today = zurichToday();
  const [period, setPeriod] = useState<Period>("today");
  const [day, setDay] = useState(today);
  const [month, setMonth] = useState(today.slice(0, 7));
  const [custom, setCustom] = useState({ from: addDays(today, -6), to: today });
  const [result, setResult] = useState<{ key: string; report?: Report; error?: string } | null>(null);
  const [expanded, setExpanded] = useState<number | null>(null);
  const [refreshTick, setRefreshTick] = useState(0);

  const range = rangeFor(period, day, month, custom);
  const key = `${range.from}|${range.to}|${refreshTick}`;

  useEffect(() => {
    const controller = new AbortController();
    api.report(range.from, range.to, controller.signal)
      .then(report => setResult({ key, report }))
      .catch(err => { if (!controller.signal.aborted) setResult({ key, error: err.message }); });
    return () => controller.abort();
  }, [key]);

  const current = result?.key === key ? result : null;
  const report = current?.report;
  const multiDay = range.from !== range.to;
  const totals = report?.employees.reduce((sum, e) => sum + e.totalSeconds, 0) ?? 0;

  return (
    <>
      <section className="heading">
        <p>דוח נוכחות · כל השעות לפי שעון ציריך</p>
        <h1>ניהול צוות</h1>
      </section>

      <section className="card">
        <div className="report-options">
          <div className="segmented" role="group" aria-label="תקופת הדוח">
            {periods.map(([value, label]) => (
              <button key={value} aria-pressed={period === value} onClick={() => setPeriod(value)}>{label}</button>
            ))}
          </div>
          {(period === "day" || period === "week") && (
            <label>תאריך <input type="date" value={day} onChange={e => e.target.value && setDay(e.target.value)} /></label>
          )}
          {period === "month" && (
            <label>חודש <input type="month" value={month} onChange={e => e.target.value && setMonth(e.target.value)} /></label>
          )}
          {period === "custom" && (
            <>
              <label>מ־<input type="date" value={custom.from} max={custom.to} onChange={e => e.target.value && setCustom(c => ({ ...c, from: e.target.value }))} /></label>
              <label>עד <input type="date" value={custom.to} min={custom.from} onChange={e => e.target.value && setCustom(c => ({ ...c, to: e.target.value }))} /></label>
            </>
          )}
          <span className="spacer" />
          <button className="secondary" onClick={() => setRefreshTick(t => t + 1)}>רענון</button>
          <button className="secondary" disabled={!report} onClick={() => report && download(report)}>ייצוא CSV</button>
        </div>
        <p className="range-label">
          {splitZurich(range.from + "T00:00:00+00:00").date}{multiDay && <> – {splitZurich(range.to + "T00:00:00+00:00").date}</>}
        </p>

        {current?.error && <p className="notice error" role="alert">{current.error}</p>}
        {!current && <p className="empty">טוען דוח…</p>}

        {report && (
          <>
            <h2>סיכום לפי עובד</h2>
            <div className="table-scroll">
              <table className="summary">
                <thead>
                  <tr><th>עובד</th><th>סה״כ שעות</th><th>ימי עבודה</th><th>משמרות</th><th>מצב</th>{multiDay && <th aria-label="פירוט"></th>}</tr>
                </thead>
                <tbody>
                  {report.employees.map(e => (
                    <Fragment key={e.employeeId}>
                      <tr>
                        <td>{e.employeeName}</td>
                        <td className="num">{formatDuration(e.totalSeconds)}</td>
                        <td className="num">{e.daysWorked}</td>
                        <td className="num">{e.shiftCount}</td>
                        <td>{e.hasOpenShift ? <span className="tag live" title="המשמרת עדיין פתוחה. השעות נספרות עד עכשיו.">במשמרת</span> : ""}</td>
                        {multiDay && (
                          <td>
                            <button className="link" aria-expanded={expanded === e.employeeId}
                              onClick={() => setExpanded(x => x === e.employeeId ? null : e.employeeId)}>
                              {expanded === e.employeeId ? "הסתר ימים" : "פירוט ימים"}
                            </button>
                          </td>
                        )}
                      </tr>
                      {multiDay && expanded === e.employeeId && (
                        <tr className="days-row">
                          <td colSpan={6}>
                            <div className="days-grid">
                              {e.days.map(d => (
                                <div key={d.date} className={"day" + (d.seconds ? " worked" : "")}>
                                  <span>{weekdayShort(d.date)} {d.date.slice(8, 10)}/{d.date.slice(5, 7)}</span>
                                  <strong>{d.seconds ? formatDuration(d.seconds) : "—"}</strong>
                                </div>
                              ))}
                            </div>
                          </td>
                        </tr>
                      )}
                    </Fragment>
                  ))}
                </tbody>
                <tfoot>
                  <tr><td>סה״כ</td><td className="num">{formatDuration(totals)}</td><td colSpan={multiDay ? 4 : 3}></td></tr>
                </tfoot>
              </table>
            </div>
            <p className="hint">
              משמרת שעוברת את חצות מתחלקת בין הימים לפי חצות בציריך, כך שהסיכום נכון גם לטווח ימים.
              משך מחושב לפי זמן אמיתי, כולל ימי מעבר שעון (23 או 25 שעות).
            </p>

            <h2>משמרות בתקופה</h2>
            <ShiftTable shifts={report.shifts} showEmployee empty="אין משמרות בתקופה שנבחרה." />
          </>
        )}
      </section>
    </>
  );
}
