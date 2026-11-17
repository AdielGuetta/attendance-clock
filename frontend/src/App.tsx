import { useEffect, useState } from "react";
import { api, type Employee } from "./api";
import { EmployeeClock } from "./components/EmployeeClock";
import { ManagerReport } from "./components/ManagerReport";

type View = "employee" | "manager";

const readHash = (): View => (location.hash === "#manager" ? "manager" : "employee");

export function App() {
  const [employees, setEmployees] = useState<Employee[]>([]);
  const [employeeId, setEmployeeId] = useState<number | null>(() => {
    const saved = Number(localStorage.getItem("attendance.employee"));
    return Number.isInteger(saved) && saved > 0 ? saved : null;
  });
  const [view, setView] = useState<View>(readHash);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    api.employees(controller.signal)
      .then(list => {
        setEmployees(list);
        setEmployeeId(id => (list.some(e => e.id === id) ? id : list[0]?.id ?? null));
      })
      .catch(err => { if (!controller.signal.aborted) setError(err.message); });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    const onHash = () => setView(readHash());
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);

  const go = (v: View) => { location.hash = v === "manager" ? "manager" : ""; setView(v); };
  const employee = employees.find(e => e.id === employeeId);

  return (
    <>
      <header>
        <div className="header-inner">
          <div className="brand"><span aria-hidden="true">◷</span> שעון נוכחות</div>
          <nav className="segmented" aria-label="מסכים">
            <button disabled={busy} aria-pressed={view === "employee"} onClick={() => go("employee")}>עובד</button>
            <button disabled={busy} aria-pressed={view === "manager"} onClick={() => go("manager")}>מנהל</button>
          </nav>
          {view === "employee" && (
            <label className="who">
              עובד:
              <select disabled={busy || employees.length === 0} value={employeeId ?? ""}
                onChange={e => { const id = Number(e.target.value); setEmployeeId(id); localStorage.setItem("attendance.employee", String(id)); }}>
                {employees.map(e => <option key={e.id} value={e.id}>{e.name}</option>)}
              </select>
            </label>
          )}
        </div>
      </header>
      <main>
        {error && <p className="notice error" role="alert">{error}</p>}
        {view === "manager"
          ? <ManagerReport />
          : employee
            ? <EmployeeClock key={employee.id} employee={employee} onBusyChange={setBusy} />
            : !error && <p className="empty">טוען…</p>}
        <footer>הדגמה מקומית · בחירת עובד ומסך מנהל ללא התחברות · שעות לפי Europe/Zurich משירות זמן חיצוני</footer>
      </main>
    </>
  );
}
