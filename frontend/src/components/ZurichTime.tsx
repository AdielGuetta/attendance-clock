import { offsetLabel, splitZurich } from "../time";

/** Shows Zurich local time. On a DST-change day the offset is shown too, so the repeated hour can be told apart. */
export function ZurichStamp({ iso, showDate = true, showOffset = false }: { iso: string; showDate?: boolean; showOffset?: boolean }) {
  const { date, time, offset } = splitZurich(iso);
  return (
    <bdi className="stamp" title={`${date} ${time} (UTC${offset}, ${offsetLabel(offset)})`}>
      {showDate && <span className="stamp-date">{date}</span>} <span className="stamp-time">{time}</span>
      {showOffset && <span className="stamp-offset"> UTC{offset}</span>}
    </bdi>
  );
}
