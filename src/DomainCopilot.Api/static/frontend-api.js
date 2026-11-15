export class ApiError extends Error {
  constructor(message, status, payload = null, correlationId = null) {
    super(message); this.name = "ApiError"; this.status = status; this.payload = payload; this.correlationId = correlationId;
  }
}
export function responseErrorMessage(payload, status) {
  if (typeof payload?.detail === "string" && payload.detail.trim()) return payload.detail;
  if (Array.isArray(payload?.detail)) return payload.detail.map(item => {
    const location = Array.isArray(item?.loc) ? item.loc.join(".") : "";
    return [location, item?.msg].filter(Boolean).join(": ");
  }).filter(Boolean).join("; ") || `Request failed (${status})`;
  if (typeof payload?.error === "string" && payload.error.trim()) return payload.error;
  return `Request failed (${status})`;
}
export class ApiClient {
  constructor({ baseUrl = "", fetchImpl, onUnauthorized = () => {} } = {}) {
    this.baseUrl = baseUrl.replace(/\/$/, ""); this.fetchImpl = fetchImpl ?? globalThis.fetch.bind(globalThis); this.onUnauthorized = onUnauthorized; this.token = null;
  }
  async request(path, { method = "GET", body, headers = {}, signal, authenticated = true } = {}) {
    const requestHeaders = new Headers(headers);
    if (body !== undefined && !requestHeaders.has("Content-Type")) requestHeaders.set("Content-Type", "application/json");
    if (authenticated && this.token) requestHeaders.set("Authorization", `Bearer ${this.token}`);
    const response = await this.fetchImpl(`${this.baseUrl}${path}`, {
      method, headers: requestHeaders, body: body === undefined ? undefined : JSON.stringify(body), signal
    });
    if (path === "/auth/login") console.info("[LOGIN-DIAG TEMP] fetch response received", { path, method, status: response.status });
    if (response.status === 401 && authenticated) this.onUnauthorized();
    if (!response.ok) {
      let payload = null;
      try { payload = await response.json(); } catch { /* non-JSON error body */ }
      throw new ApiError(responseErrorMessage(payload, response.status), response.status, payload,
        response.headers?.get?.("X-Correlation-ID") ?? payload?.correlation_id ?? null);
    }
    return response;
  }
  async json(path, options = {}) {
    const response = await this.request(path, options);
    return response.status === 204 ? null : response.json();
  }
  async stream(path, options = {}) {
    const response = await this.request(path, options);
    if (!response.body) throw new Error("The server returned no response stream.");
    return response.body;
  }
}
export async function* readSse(stream, { signal } = {}) {
  const reader = stream.getReader(), decoder = new TextDecoder();
  let buffer = "", frame = { data: [], event: "", id: "" };
  const reset = () => { frame = { data: [], event: "", id: "" }; };
  const dispatch = () => {
    if (!frame.data.length) { reset(); return null; }
    const value = { data: frame.data.join("\n"), event: frame.event || "message", id: frame.id };
    reset(); return value;
  };
  const consume = raw => {
    const line = raw.endsWith("\r") ? raw.slice(0, -1) : raw;
    if (!line) return dispatch();
    if (line.startsWith(":")) return null;
    const colon = line.indexOf(":"), field = colon < 0 ? line : line.slice(0, colon);
    let value = colon < 0 ? "" : line.slice(colon + 1);
    if (value.startsWith(" ")) value = value.slice(1);
    if (field === "data") frame.data.push(value);
    else if (field === "event") frame.event = value;
    else if (field === "id" && !value.includes("\0")) frame.id = value;
    return null;
  };
  const abort = () => { void reader.cancel().catch(() => {}); };
  signal?.addEventListener("abort", abort, { once: true });
  try {
    while (true) {
      if (signal?.aborted) throw new DOMException("The operation was aborted.", "AbortError");
      const { value, done } = await reader.read();
      if (signal?.aborted) throw new DOMException("The operation was aborted.", "AbortError");
      buffer += decoder.decode(value || new Uint8Array(), { stream: !done });
      const lines = buffer.split("\n"); buffer = lines.pop() ?? "";
      for (const line of lines) { const value = consume(line); if (value) yield value; }
      if (done) break;
    }
    if (buffer) { const value = consume(buffer); if (value) yield value; }
    const last = dispatch(); if (last) yield last;
  } finally {
    signal?.removeEventListener("abort", abort);
    try { reader.releaseLock(); } catch { /* already released */ }
  }
}
export function parseJsonEvent(event) { try { return JSON.parse(event.data); } catch { return null; } }
