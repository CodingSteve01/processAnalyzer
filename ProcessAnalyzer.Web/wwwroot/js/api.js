// Single fetch chokepoint. Nothing else in this app calls fetch() directly, so aborts, error shapes
// and JSON parsing exist in exactly one place.

// One controller per route: the 5 s refresh must be able to cancel its own previous, still-pending
// request without touching an unrelated one (a manual trigger must not kill the status poll).
const _controllers = new Map();

function nextSignal(route) {
  _controllers.get(route)?.abort();
  const controller = new AbortController();
  _controllers.set(route, controller);
  return controller.signal;
}

// Only the routes the polling loop owns. abortAll() used to cancel everything in flight, which meant switching
// away from the tab during load killed the analysis requests too — and since an aborted request resolves to null,
// the page then rendered empty and said nothing about why. A background tab should stop polling, not discard work
// somebody is waiting for.
const PollingRoutes = ['/health', '/api/sync/status'];

export function abortPolling() {
  for (const route of PollingRoutes) {
    _controllers.get(route)?.abort();
    _controllers.delete(route);
  }
}

// Whether anything is in flight, in one place, because this is the only place that knows.
//
// A page that waits without saying so looks broken. One query used to take two minutes and the screen showed the
// previous answer the whole time, which is worse than a spinner: the reader believes what they are looking at. The
// polling routes are excluded — a heartbeat every five seconds would leave the bar permanently on and it would stop
// meaning anything.
let inFlight = 0;

function busy(delta) {
  inFlight = Math.max(0, inFlight + delta);
  document.documentElement.classList.toggle('loading', inFlight > 0);
}

export async function request(route, init = {}) {
  const counts = !PollingRoutes.some((polling) => route.startsWith(polling));
  if (counts) busy(1);
  try {
    const res = await fetch(route, { ...init, signal: nextSignal(route) });
    const body = await res.json().catch(() => null);
    if (!res.ok) {
      // A 503 from /health is a normal answer, not a transport failure — the caller decides what it means.
      const err = new Error(body?.message || body?.error || `HTTP ${res.status}`);
      err.status = res.status;
      err.body = body;
      throw err;
    }
    return body;
  } catch (err) {
    // AbortError means we cancelled this request ourselves; returning null lets callers exit quietly
    // instead of painting an error the user never caused.
    if (err.name === 'AbortError') return null;
    throw err;
  } finally {
    if (counts) busy(-1);
  }
}

export function getSyncStatus() {
  return request('/api/sync/status');
}

export function getVersion() {
  return request('/api/version');
}

// /health answers 503 for "not healthy" — that is data, so it is unwrapped here instead of thrown.
export async function getHealth() {
  try {
    return await request('/health');
  } catch (err) {
    if (err.status === 503 && err.body) return err.body;
    throw err;
  }
}

export async function triggerRun(kind) {
  const route = `/api/sync/run?kind=${encodeURIComponent(kind)}`;
  try {
    await request(route, { method: 'POST' });
    return { started: true };
  } catch (err) {
    if (err.status === 409) return { started: false, message: err.message };
    throw err;
  }
}
