import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError, type Attendance, type Employee, type Receipt } from "../api";
import { announceChange, clearPending, loadPending, onRemoteChange, savePending, type PendingPunch } from "../pendingPunch";
import { formatTimer, splitZurich, zurichClock, zurichLongDate, zurichToday } from "../time";
import { ShiftTable } from "./ShiftTable";
import { ZurichStamp } from "./ZurichTime";

type Notice = { kind: "success" | "error" | "info"; text: string } | null;

function receiptText(r: Receipt) {
  const { date, time } = splitZurich(r.atZurich);
  return `${r.action === "in" ? "הכניסה" : "היציאה"} נרשמה ב-${time} (${date}, שעון ציריך)`;
}

export function EmployeeClock({ employee, onBusyChange }: { employee: Employee; onBusyChange: (busy: boolean) => void }) {
  const [state, setState] = useState<Attendance | null>(null);
  const [loadError, setLoadError] = useState("");
  const [notice, setNotice] = useState<Notice>(null);
  const [busy, setBusy] = useState(false);
  const [now, setNow] = useState(() => new Date());
  // The difference between the server clock and the browser clock, so the timer is right even if the computer's clock is wrong.
  const skew = useRef(0);
  const sending = useRef(false);
  const pending = useRef<PendingPunch | null>(null);

  const reload = useCallback(async (signal?: AbortSignal) => {
    try {
      const data = await api.attendance(employee.id, signal);
      skew.current = Date.parse(data.serverNowUtc) - Date.now();
      setState(data);
      setLoadError("");
    } catch (err) {
      if (signal?.aborted) return;
      setLoadError(err instanceof Error ? err.message : "שגיאה בטעינה");
    }
  }, [employee.id]);

  // First load + recovery of a punch that was in flight before a refresh.
  useEffect(() => {
    const controller = new AbortController();
    (async () => {
      const stored = loadPending(employee.id);
      if (stored) {
        try {
          const receipt = await api.findPunch(employee.id, stored.requestId);
          setNotice({ kind: "success", text: "ההחתמה מלפני הרענון נשמרה: " + receiptText(receipt) });
          clearPending(employee.id);
        } catch (err) {
          if (err instanceof ApiError && err.status === 404) {
            setNotice({ kind: "info", text: "ההחתמה הקודמת לא הושלמה לפני שהדף נטען מחדש. אפשר להחתים שוב." });
            clearPending(employee.id);
          } else {
            pending.current = stored; // the server is unreachable: keep the id so the retry is idempotent
          }
        }
      }
      await reload(controller.signal);
    })();
    return () => controller.abort();
  }, [employee.id, reload]);

  // Clock tick: the current time and the open-shift timer.
  useEffect(() => {
    const t = setInterval(() => setNow(new Date(Date.now() + skew.current)), 1000);
    return () => clearInterval(t);
  }, []);

  // Refresh when returning to the window or when another tab punched.
  useEffect(() => {
    const refreshIfIdle = () => { if (!sending.current && document.visibilityState === "visible") void reload(); };
    document.addEventListener("visibilitychange", refreshIfIdle);
    window.addEventListener("focus", refreshIfIdle);
    const off = onRemoteChange(id => { if (id === employee.id) refreshIfIdle(); });
    return () => {
      document.removeEventListener("visibilitychange", refreshIfIdle);
      window.removeEventListener("focus", refreshIfIdle);
      off();
    };
  }, [employee.id, reload]);

  async function punch() {
    if (sending.current || !state) return; // protects against a double click even before React re-renders
    sending.current = true;
    setBusy(true);
    onBusyChange(true);
    setNotice(null);

    const shiftId = state.openShift?.id ?? null;
    const action = shiftId ? "out" : "in";
    // The same requestId is kept for a retry of the same operation (after a timeout/503), so the server will not record it twice.
    if (!pending.current || pending.current.action !== action || pending.current.shiftId !== shiftId) {
      pending.current = { requestId: crypto.randomUUID(), action, shiftId, createdAt: Date.now() };
    }
    const attempt = pending.current;
    savePending(employee.id, attempt);

    try {
      const receipt = action === "in"
        ? await api.clockIn(employee.id, attempt.requestId)
        : await api.clockOut(employee.id, attempt.requestId, shiftId!);
      pending.current = null;
      clearPending(employee.id);
      setNotice({ kind: "success", text: receiptText(receipt) + (receipt.replayed ? " · כבר נשמרה קודם" : "") });
      announceChange(employee.id);
      await reload();
    } catch (err) {
      const e = err instanceof ApiError ? err : new ApiError(0, "unknown", "שגיאה לא צפויה");
      setNotice({ kind: "error", text: e.message });
      if (e.isDefinitive) {
        // The server refused (409 and so on). This operation is finished. Refresh the state to show the truth.
        pending.current = null;
        clearPending(employee.id);
        await reload();
      }
      // Otherwise (503/network) the requestId is kept, and another click retries the same operation.
    } finally {
      sending.current = false;
      setBusy(false);
      onBusyChange(false);
    }
  }

  const open = state?.openShift ?? null;
  const elapsed = open ? (now.getTime() - Date.parse(open.startUtc)) / 1000 : 0;
  const retrying = pending.current !== null && !busy;

  return (
    <>
      <section className="heading">
        <p>{zurichLongDate(now)} · <bdi>{zurichClock(now)}</bdi> בציריך</p>
        <h1>שלום, {employee.name.split(" ")[0]}</h1>
      </section>

      <section className={"clock-card" + (open ? " is-open" : "")}>
        <span className={"tag " + (open ? "live" : "muted")}>{state ? (open ? "במשמרת" : "לא במשמרת") : "טוען…"}</span>
        {open ? (
          <>
            <div className="timer" aria-live="off"><bdi>{formatTimer(elapsed)}</bdi></div>
            <p className="clock-sub">נכנסת ב-<ZurichStamp iso={open.startZurich} showDate={open.startZurich.slice(0, 10) !== zurichToday(now)} /></p>
          </>
        ) : (
          <>
            <div className="timer idle">--:--:--</div>
            <p className="clock-sub">השעה תיקבע לפי שירות זמן חיצוני (ציריך) ברגע ההחתמה</p>
          </>
        )}
        <button className={"punch " + (open ? "out" : "in")} disabled={busy || !state} onClick={punch}>
          {busy ? "מקבל שעה ושומר…" : retrying ? "נסו שוב" : open ? "החתמת יציאה" : "החתמת כניסה"}
        </button>
      </section>

      {notice && <p className={"notice " + notice.kind} role={notice.kind === "error" ? "alert" : "status"}>{notice.text}</p>}
      {loadError && <p className="notice error" role="alert">{loadError}</p>}

      <section className="card">
        <h2>המשמרות שלי</h2>
        {state
          ? <ShiftTable shifts={state.shifts} empty="עדיין אין משמרות. לחצו על 'החתמת כניסה' כדי להתחיל." />
          : <p className="empty">{loadError ? "לא ניתן לטעון את המשמרות." : "טוען משמרות…"}</p>}
        {state && state.shifts.length >= 30 && <p className="hint">מוצגות 30 המשמרות האחרונות.</p>}
      </section>
    </>
  );
}
