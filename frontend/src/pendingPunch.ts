// Keeps the punch currently being sent in localStorage.
// Purpose: if the page refreshes or closes mid-send, we can ask the server on the next load
// whether that punch was saved (by requestId), without sending it again blindly.

export interface PendingPunch {
  requestId: string;
  action: "in" | "out";
  shiftId: string | null;
  createdAt: number;
}

const key = (employeeId: number) => `attendance.pending.${employeeId}`;

export function loadPending(employeeId: number): PendingPunch | null {
  try {
    const raw = localStorage.getItem(key(employeeId));
    return raw ? (JSON.parse(raw) as PendingPunch) : null;
  } catch {
    return null;
  }
}

export function savePending(employeeId: number, punch: PendingPunch) {
  try { localStorage.setItem(key(employeeId), JSON.stringify(punch)); } catch { /* private mode: in-memory only */ }
}

export function clearPending(employeeId: number) {
  try { localStorage.removeItem(key(employeeId)); } catch { /* ignore */ }
}

// Sync between tabs: after a punch, other tabs refresh their state.
const channel = typeof BroadcastChannel !== "undefined" ? new BroadcastChannel("attendance") : null;

export function announceChange(employeeId: number) {
  channel?.postMessage({ type: "changed", employeeId });
}

export function onRemoteChange(handler: (employeeId: number) => void) {
  if (!channel) return () => {};
  const listener = (e: MessageEvent) => { if (e.data?.type === "changed") handler(e.data.employeeId); };
  channel.addEventListener("message", listener);
  return () => channel.removeEventListener("message", listener);
}
