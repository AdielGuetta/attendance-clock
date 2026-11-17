// Server calls. Every error becomes an ApiError with an HTTP status and a stable code.

export interface Employee { id: number; name: string }

export interface Shift {
  id: string;
  employeeId: number;
  employeeName: string;
  startUtc: string;
  startZurich: string;      // ISO with the Zurich offset, e.g. 2026-10-25T02:30:00+01:00
  endUtc: string | null;
  endZurich: string | null;
  durationSeconds: number | null;
  crossesMidnight: boolean;
  spansDstChange: boolean;
  startSource: string;
  endSource: string | null;
}

export interface Attendance {
  employee: Employee;
  openShift: Shift | null;
  shifts: Shift[];
  serverNowUtc: string;
}

export interface Receipt {
  requestId: string;
  shiftId: string;
  action: "in" | "out";
  atUtc: string;
  atZurich: string;
  timeSource: string;
  replayed: boolean;
}

export interface DayTotal { date: string; seconds: number }

export interface EmployeeSummary {
  employeeId: number;
  employeeName: string;
  totalSeconds: number;
  daysWorked: number;
  shiftCount: number;
  hasOpenShift: boolean;
  days: DayTotal[];
}

export interface Report {
  from: string;
  to: string;
  generatedAtUtc: string;
  employees: EmployeeSummary[];
  shifts: Shift[];
}

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string) {
    super(message);
  }
  /** A definitive rejection by the server (the request was handled and refused). Repeating the same request will not help. */
  get isDefinitive() {
    return this.status >= 400 && this.status < 500 && this.status !== 408 && this.status !== 429;
  }
}

// Longer than the worst case on the server (2 providers × 2 attempts × 4 s).
const TIMEOUT_MS = 25_000;

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  let response: Response;
  try {
    response = await fetch("/api" + path, {
      cache: "no-store",
      ...init,
      headers: init.body ? { "Content-Type": "application/json" } : undefined,
      signal: init.signal ?? AbortSignal.timeout(TIMEOUT_MS),
    });
  } catch (err) {
    if (err instanceof DOMException && err.name === "AbortError" && init.signal?.aborted) throw err;
    throw new ApiError(0, "network", "אין תשובה מהשרת. בדקו את החיבור ונסו שוב.");
  }
  const body = await response.json().catch(() => null);
  if (!response.ok) {
    throw new ApiError(response.status, body?.code ?? "http_" + response.status,
      body?.error ?? "הבקשה לא הושלמה. אפשר לנסות שוב.");
  }
  if (body === null) throw new ApiError(502, "invalid_response", "התקבלה תשובה לא תקינה מהשרת.");
  return body as T;
}

export const api = {
  employees: (signal?: AbortSignal) => request<Employee[]>("/employees", { signal }),
  attendance: (id: number, signal?: AbortSignal) => request<Attendance>(`/employees/${id}/attendance`, { signal }),
  findPunch: (id: number, requestId: string) => request<Receipt>(`/employees/${id}/punches/${requestId}`),
  clockIn: (id: number, requestId: string) =>
    request<Receipt>(`/employees/${id}/clock-in`, { method: "POST", body: JSON.stringify({ requestId }) }),
  clockOut: (id: number, requestId: string, shiftId: string) =>
    request<Receipt>(`/employees/${id}/clock-out`, { method: "POST", body: JSON.stringify({ requestId, shiftId }) }),
  report: (from: string, to: string, signal?: AbortSignal) =>
    request<Report>(`/report?${new URLSearchParams({ from, to })}`, { signal }),
};
