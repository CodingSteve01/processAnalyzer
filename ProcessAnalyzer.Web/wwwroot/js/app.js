// Entry point for the pull-status page. One page, one poll loop, no framework.

import { getSyncStatus, getHealth, getVersion, triggerRun, abortPolling } from './api.js';
import { initInsights, renderInsights } from './insights.js';
import { initViews } from './views.js';
import { initTheme } from './theme.js';

const REFRESH_MS = 5000;

const $ = (id) => document.getElementById(id);
const nf = new Intl.NumberFormat('de-DE');
const dtf = new Intl.DateTimeFormat('de-DE', { dateStyle: 'short', timeStyle: 'medium' });

let timer = null;
let busy = false;

// ===== FORMATTING =====
// Missing values stay missing. A counter the server did not report is rendered as "—", never as 0,
// because a zero here reads as "measured, nothing happened" and would hide a broken pull.
function setMetric(id, value, attention = false) {
  const el = $(id);
  if (!el) return;
  const known = typeof value === 'number' && Number.isFinite(value);
  el.textContent = known ? nf.format(value) : '—';
  el.classList.toggle('unknown', !known);
  el.classList.toggle('attention', known && attention && value > 0);
}

function setText(id, value, extraClass = '') {
  const el = $(id);
  if (!el) return;
  const known = value !== null && value !== undefined && value !== '';
  el.textContent = known ? value : '—';
  el.className = known ? extraClass : 'unknown';
}

function formatMoment(iso) {
  if (!iso) return null;
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return null;
  return `${dtf.format(date)} (${formatAge(date)})`;
}

function formatAge(date) {
  const seconds = Math.max(0, Math.round((Date.now() - date.getTime()) / 1000));
  if (seconds < 60) return `vor ${seconds} s`;
  if (seconds < 3600) return `vor ${Math.round(seconds / 60)} min`;
  if (seconds < 86400) return `vor ${Math.round(seconds / 3600)} h`;
  return `vor ${Math.round(seconds / 86400)} Tagen`;
}

// ===== RENDERING =====
function renderBanner(status) {
  const banner = $('banner');
  if (!status.sourceConfigured) {
    banner.hidden = false;
    banner.className = 'banner warning';
    banner.textContent =
      'Keine Quelle konfiguriert. Es werden keine Ereignisse gespiegelt — die Zahlen unten sind kein Messergebnis.';
    return;
  }

  const error = status.sync?.lastError;
  if (error) {
    banner.hidden = false;
    banner.className = 'banner critical';
    banner.textContent = `Der letzte Lauf ist fehlgeschlagen: ${error}`;
    return;
  }

  banner.hidden = true;
}

function renderMetrics(status) {
  const sync = status.sync ?? {};
  const known = status.sourceConfigured;
  // Held-back and gap counts live on run rows, not on the mirror: they describe one run, not a running total.
  // The newest run is the only one that answers "is the pull healthy right now".
  const lastRun = sync.recentRuns?.[0] ?? {};

  // Without a source there is nothing to report; passing undefined keeps every tile on "—".
  const value = (v) => (known ? v : undefined);

  setMetric('mWatermark', value(sync.watermark));
  setMetric('mEvents', value(sync.eventCount));
  setMetric('mObjects', value(sync.objectCount));
  setMetric('mHeldBack', value(lastRun.heldBack), true);
  setMetric('mGaps', value(lastRun.gapsFound), true);

  const maxId = known && Number.isFinite(sync.maxSourceId) ? nf.format(sync.maxSourceId) : '—';
  $('mMaxId').textContent = maxId;
}

function renderLastRun(status) {
  const sync = status.sync ?? {};
  const lastRun = sync.recentRuns?.[0] ?? {};
  setText('dKind', lastRun.kind);
  setText('dStarted', formatMoment(lastRun.startedAt));
  setText('dFinished', formatMoment(lastRun.finishedAt));
  setText('dSuccess', formatMoment(sync.lastSuccessfulRunAt));
  setText('dError', sync.lastError, 'error-text');

  const interval = status.pullIntervalSeconds;
  const lag = status.lagSeconds;
  setText('dInterval', Number.isFinite(interval) ? `alle ${nf.format(interval)} s` : null);
  setText('dLag', Number.isFinite(lag) ? `${nf.format(lag)} s Rückstand` : null);
}

function renderHealth(health) {
  const pill = $('healthPill');
  if (!health) return;
  const healthy = health.status === 'healthy';
  pill.className = `status-pill ${healthy ? 'ok' : 'critical'}`;
  pill.textContent = healthy ? 'Spiegel aktuell' : `Nicht aktuell: ${health.reason ?? 'unbekannt'}`;
  pill.title = health.reason ?? '';
}

function renderButtons(status) {
  const disabled = !status.sourceConfigured || status.manualRunActive === true;
  $('runBtn').disabled = disabled;
  $('sweepBtn').disabled = disabled;

  if (!status.sourceConfigured) {
    $('runHint').textContent = 'Ohne Quelle nicht möglich.';
    return;
  }
  $('runHint').textContent = status.manualRunActive ? 'Ein Lauf ist bereits aktiv …' : '';
}

function renderTransportError(err) {
  const banner = $('banner');
  banner.hidden = false;
  banner.className = 'banner critical';
  banner.textContent = `Status konnte nicht geladen werden: ${err.message}`;
  $('healthPill').className = 'status-pill critical';
  $('healthPill').textContent = 'Keine Verbindung';
}

// ===== POLLING =====
async function refresh() {
  if (busy) return;
  busy = true;
  try {
    const [status, health] = await Promise.all([getSyncStatus(), getHealth()]);
    if (!status) return; // aborted — a newer refresh is already in flight
    renderBanner(status);
    renderMetrics(status);
    renderLastRun(status);
    renderButtons(status);
    renderHealth(health);
  } catch (err) {
    renderTransportError(err);
  } finally {
    busy = false;
  }
}

async function run(kind) {
  const button = kind === 'sweep' ? $('sweepBtn') : $('runBtn');
  button.disabled = true;
  $('runHint').textContent = 'Lauf wird gestartet …';
  try {
    const result = await triggerRun(kind);
    $('runHint').textContent = result.started ? 'Lauf gestartet.' : 'Ein Lauf ist bereits aktiv.';
  } catch (err) {
    $('runHint').textContent = `Start fehlgeschlagen: ${err.message}`;
  } finally {
    await refresh();
  }
}

function startPolling() {
  stopPolling();
  timer = setInterval(refresh, REFRESH_MS);
}

function stopPolling() {
  if (timer) clearInterval(timer);
  timer = null;
}

async function init() {
  initTheme();
  $('refreshBtn').addEventListener('click', refresh);
  $('runBtn').addEventListener('click', () => run('pull'));
  $('sweepBtn').addEventListener('click', () => run('sweep'));

  // A hidden tab must not keep polling: it produces load nobody looks at and its in-flight requests
  // would be aborted by the next visible refresh anyway.
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
      stopPolling();
      abortPolling();
      return;
    }
    refresh();
    startPolling();
  });

  const version = await getVersion().catch(() => null);
  $('versionInfo').textContent = `Version ${version?.version ?? 'unbekannt'}`;

  await refresh();
  startPolling();
}

init();

// The analysis section owns its own data and refresh; it is initialised once the shell is up.
initViews();
initInsights();

// The analysis is a dozen parallel requests fired the moment the page appears. Right after the login redirect the
// first of them sometimes dies as "Failed to fetch" — the browser tearing down the previous page's connections —
// and the panels then stay blank with nothing on screen to explain it. One retry covers that; a second failure is
// real and gets said out loud instead of leaving empty tables that look like "no data".
(async function loadAnalysis() {
  for (let attempt = 1; attempt <= 2; attempt++) {
    try {
      await renderInsights();
      return;
    } catch (error) {
      console.error(`insights (Versuch ${attempt})`, error);
      if (attempt === 2) showAnalysisFailure(error);
      else await new Promise((resolve) => setTimeout(resolve, 400));
    }
  }
})();

function showAnalysisFailure(error) {
  const banner = document.getElementById('banner');
  banner.hidden = false;
  banner.className = 'banner critical';
  banner.textContent = `Die Auswertung konnte nicht geladen werden: ${error.message}. Seite neu laden.`;
}
