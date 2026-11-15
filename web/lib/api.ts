// The only place the web app talks to the Acme HR API.
// "" = same origin: on Render the API serves this web app at /app (see src/Api/Dockerfile).
const BASE = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5080";
const BASE_PATH = process.env.NEXT_PUBLIC_BASE_PATH ?? "";

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const token = typeof window === "undefined" ? null : window.localStorage.getItem(TOKEN_KEY);
  const res = await fetch(`${BASE}${path}`, {
    ...init,
    headers: {
      ...(init?.body instanceof FormData ? {} : { "Content-Type": "application/json" }), // FormData sets its own
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...(init?.headers ?? {}),
    },
    cache: "no-store",
  });
  if (res.status === 401 && token && typeof window !== "undefined") {
    signOut(); // token expired: back to the sign-in page
    throw new Error("Please sign in again");
  }
  if (!res.ok) {
    let msg = `${res.status} ${res.statusText}`;
    try { const p = await res.json(); msg = p.detail ?? p.title ?? msg; } catch {}
    throw new Error(msg);
  }
  return res.status === 204 ? (undefined as T) : res.json();
}

export type Employee = { id: number; name: string; email: string; department: string; managerId: number | null; annualEntitlement: number; roles?: string[] };
export type LeaveRequest = { id: number; employeeId: number; startDate: string; endDate: string; halfDay: "None" | "AM" | "PM"; days: number; status: "Pending" | "Approved" | "Rejected"; reason: string };
export type Balance = { employeeId: number; entitlement: number; used: number; pending: number; available: number };
export type LeaveClash = { date: string; peopleOff: string[] };

/** Days on which two or more teammates are already off (only the request's manager or HR may ask). */
export async function getClashes(id: number): Promise<LeaveClash[]> {
  return (await api<{ requestId: number; clashes: LeaveClash[] }>(`/leave/${id}/clashes`)).clashes;
}

/** The text of the warning shown before approving, or "" when nobody clashes. */
export function clashWarning(clashes: LeaveClash[]): string {
  if (clashes.length === 0) return "";
  const lines = clashes.map(c => `${c.date}: ${c.peopleOff.join(", ")}`);
  return `Two or more people in the team are already off:\n${lines.join("\n")}\n\nApprove anyway?`;
}

// Sign-in: email + password against the API's built-in ASP.NET Core Identity endpoints.
// The bearer token and the signed-in employee are kept in this browser only.
const TOKEN_KEY = "acme.token";
const ME_KEY = "acme.me";

export async function signIn(email: string, password: string): Promise<Employee> {
  const res = await fetch(`${BASE}/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, password }),
  });
  if (!res.ok) throw new Error(res.status === 401 ? "Wrong email or password" : `${res.status} ${res.statusText}`);
  const { accessToken } = await res.json();
  window.localStorage.setItem(TOKEN_KEY, accessToken);
  const me = await api<Employee>("/me");
  window.localStorage.setItem(ME_KEY, JSON.stringify(me));
  return me;
}

export function signOut() {
  window.localStorage.removeItem(TOKEN_KEY);
  window.localStorage.removeItem(ME_KEY);
  window.location.href = `${BASE_PATH}/login/`;
}

export function currentEmployee(): Employee | null {
  if (typeof window === "undefined") return null;
  const raw = window.localStorage.getItem(ME_KEY);
  return raw ? (JSON.parse(raw) as Employee) : null;
}

export function currentEmployeeId(): number {
  return currentEmployee()?.id ?? 0;
}

export type StoredDocument = { id: number; employeeId: number; employeeName?: string; fileName: string; kind: string; extractedJson: string; status: "Valid" | "NeedsReview"; problems: string; createdAt: string };

/** Opens a stored document's PDF in a new tab (signed in, so it cannot be a plain link). */
export async function openDocumentFile(id: number) {
  const token = window.localStorage.getItem(TOKEN_KEY);
  const res = await fetch(`${BASE}/documents/${id}/file`, { headers: token ? { Authorization: `Bearer ${token}` } : {}, cache: "no-store" });
  if (!res.ok) throw new Error(res.status === 403 ? "Only the sender and HR may open this document" : `${res.status} ${res.statusText}`);
  window.open(URL.createObjectURL(await res.blob()), "_blank");
}
