// Time display. The server sends Zurich local time with an explicit offset (e.g. "2026-10-25T02:30:00+01:00"),
// so the display does not depend on the browser's time zone, and the repeated autumn hour can be told apart by its offset.

export const ZONE = "Europe/Zurich";

/** "2026-11-17T08:05:00.123+01:00" → { date: "17.11.2026", time: "08:05", offset: "+01:00" } */
export function splitZurich(iso: string) {
  const [y, m, d] = iso.slice(0, 10).split("-");
  return { date: `${d}.${m}.${y}`, time: iso.slice(11, 16), offset: iso.slice(-6) };
}

export function offsetLabel(offset: string) {
  return offset === "+02:00" ? "שעון קיץ" : offset === "+01:00" ? "שעון חורף" : offset;
}

export function formatDuration(totalSeconds: number) {
  const s = Math.max(0, Math.floor(totalSeconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  return `${h}:${String(m).padStart(2, "0")}`;
}

export function formatTimer(totalSeconds: number) {
  const s = Math.max(0, Math.floor(totalSeconds));
  const parts = [Math.floor(s / 3600), Math.floor((s % 3600) / 60), s % 60];
  return parts.map(p => String(p).padStart(2, "0")).join(":");
}

const dateParts = new Intl.DateTimeFormat("en-CA", { timeZone: ZONE, year: "numeric", month: "2-digit", day: "2-digit" });
const clockFormat = new Intl.DateTimeFormat("he-IL", { timeZone: ZONE, hour: "2-digit", minute: "2-digit", second: "2-digit" });
const longDate = new Intl.DateTimeFormat("he-IL", { timeZone: ZONE, dateStyle: "full" });

/** Today's date in Zurich, YYYY-MM-DD (for display and default report choices only). */
export const zurichToday = (now = new Date()) => dateParts.format(now);
export const zurichClock = (now: Date) => clockFormat.format(now);
export const zurichLongDate = (now: Date) => longDate.format(now);

export function addDays(isoDate: string, days: number) {
  const d = new Date(isoDate + "T12:00:00Z");
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}

export function weekdayShort(isoDate: string) {
  return new Intl.DateTimeFormat("he-IL", { weekday: "short", timeZone: "UTC" }).format(new Date(isoDate + "T12:00:00Z"));
}

export function monthBounds(month: string) {
  const [y, m] = month.split("-").map(Number);
  const last = new Date(Date.UTC(y, m, 0)).getUTCDate();
  return { from: `${month}-01`, to: `${month}-${String(last).padStart(2, "0")}` };
}

/** Sunday to Saturday (the Israeli work week) containing the given date. */
export function weekBounds(isoDate: string) {
  const day = new Date(isoDate + "T12:00:00Z").getUTCDay();
  const from = addDays(isoDate, -day);
  return { from, to: addDays(from, 6) };
}
