import type { Shift } from "../api";
import { formatDuration } from "../time";
import { ZurichStamp } from "./ZurichTime";

export function ShiftTable({ shifts, showEmployee = false, empty }: { shifts: Shift[]; showEmployee?: boolean; empty: string }) {
  if (shifts.length === 0) return <p className="empty">{empty}</p>;
  return (
    <div className="table-scroll">
      <table>
        <thead>
          <tr>
            {showEmployee && <th>עובד</th>}
            <th>כניסה</th>
            <th>יציאה</th>
            <th>משך</th>
            <th aria-label="הערות"></th>
          </tr>
        </thead>
        <tbody>
          {shifts.map(s => {
            const dst = s.spansDstChange;
            return (
              <tr key={s.id}>
                {showEmployee && <td>{s.employeeName}</td>}
                <td><ZurichStamp iso={s.startZurich} showOffset={dst} /></td>
                <td>{s.endZurich ? <ZurichStamp iso={s.endZurich} showOffset={dst} /> : <span className="tag live">פתוחה</span>}</td>
                <td className="num">{s.durationSeconds !== null ? formatDuration(s.durationSeconds) : "—"}</td>
                <td className="notes">
                  {s.crossesMidnight && <span className="tag" title="המשמרת עברה את חצות. בסיכום היא מתחלקת בין שני הימים.">חצתה חצות</span>}
                  {dst && <span className="tag warn" title="שעון קיץ/חורף התחלף במהלך המשמרת. המשך מחושב לפי זמן אמיתי.">מעבר שעון</span>}
                  {s.startSource !== "worldtimeapi" && <span className="tag muted" title="השעה התקבלה מספק הגיבוי">{s.startSource}</span>}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
