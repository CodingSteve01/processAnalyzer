// The analysis page. Everything it shows is scoped to one object type, because an object-centric log must not be
// aggregated across types: an event that touches five objects would count five times.

import { request } from './api.js';
import { periodQuery, periodLabel, initPeriod, scopeQuery } from './period.js';
import { $, escape } from './utils.js';
import * as readings from './readings.js';
import { renderNaming, initNaming } from './naming.js';
import { renderDrill, initDrill, drillTo } from './drill.js';
import { renderReport, initReport } from './report.js';
import { renderNow } from './now.js';
import { whenOpened } from './views.js';
import { attachViewer } from './imageview.js';

const nf = new Intl.NumberFormat('de-DE');
const pf = new Intl.NumberFormat('de-DE', { style: 'percent', maximumFractionDigits: 1 });

let objectType = null;

/** The viewer of the diagram page, attached on first render. */
let viewer = null;

/** Hours, because "132840 Sekunden" is not a number anybody can act on. */
function hours(seconds) {
  if (seconds === null || seconds === undefined) return '—';
  const value = Number(seconds) / 3600;
  return value >= 10 ? `${nf.format(Math.round(value))} h` : `${value.toFixed(1)} h`;
}

/**
 * A share, or "—" when there was nothing to divide by.
 *
 * A process without a single closed case has no automation rate, and printing "0 %" for it claims a measurement
 * nobody made — the reader cannot tell it apart from a process that really runs entirely by hand.
 */
function share(value) {
  if (value === null || value === undefined) return '—';
  return pf.format(Number(value));
}

/** A count or average, or "—" when absent. Same reason as {@link share}. */
function amount(value, digits = 1) {
  if (value === null || value === undefined) return '—';
  return Number(value).toFixed(digits);
}

// Labels come from the database (ocel.label), where they can be corrected without a deployment. The frontend
// renders what it is given and never derives a name from a type key — a name invented in two places drifts.

async function scoped(route) {
  const response = await request(
    `/api/${route}?objectType=${encodeURIComponent(objectType)}${periodQuery()}`
  );
  // A superseded request answers with null, which is a normal event when somebody switches tabs mid-load. Reading
  // .rows off it killed the whole render.
  return response?.rows ?? [];
}

/** Puts a computed sentence above a panel. Empty text removes the element rather than leaving a blank line. */
function reading(id, text) {
  const element = document.getElementById(id);
  if (!element) return;
  element.textContent = text ?? '';
  element.hidden = !text;
}

/**
 * A table. With `link`, every row carries what the drill-down needs and becomes clickable.
 *
 * The tables used to be the end of the road: they name a process, a step, a case, and then the reader is on their own.
 * One optional function per table is enough to hand the row over instead.
 */
function table(rows, columns, link = null) {
  if (!rows.length) return '<p class="empty">Keine Daten für diesen Objekttyp.</p>';
  const head = columns.map((c) => `<th${c.numeric ? ' class="num"' : ''}>${escape(c.label)}</th>`).join('');
  const body = rows
    .map((row) => {
      const cells = columns
        .map((c) => `<td${c.numeric ? ' class="num"' : ''}>${c.render(row)}</td>`)
        .join('');
      const target = link?.(row);
      const attrs = target
        ? ` class="clickable" data-drill="${escape(JSON.stringify(target))}"`
        : '';
      return `<tr${attrs}>${cells}</tr>`;
    })
    .join('');
  return `<table class="data"><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>`;
}

/** Wires every row a table marked as drillable. One call per container, after it was written. */
function wireDrill(containerId) {
  for (const tr of $(containerId).querySelectorAll('tr[data-drill]')) {
    tr.addEventListener('click', () => drillTo(JSON.parse(tr.dataset.drill)));
  }
}

async function renderInventory() {
  const rows = await request('/api/inventory');
  const selector = $('objectTypeSelect');
  if (!selector.options.length) {
    rows.forEach((row) => {
      const option = document.createElement('option');
      option.value = row.object_type;
      option.textContent = `${row.bezeichnung} (${nf.format(row.objects)})`;
      selector.append(option);
    });
    objectType = rows[0]?.object_type ?? null;
    selector.value = objectType;
  }

  $('inventory').innerHTML = table(rows, [
    { label: 'Prozess', render: (r) => escape(r.bezeichnung) },
    { label: 'Objekte', numeric: true, render: (r) => nf.format(r.objects) },
    { label: 'Ereignisse', numeric: true, render: (r) => nf.format(r.events) },
    { label: 'Aktivitäten', numeric: true, render: (r) => nf.format(r.activities) },
  ], (row) => ({ process: row.object_type }));
  wireDrill('inventory');
}

async function renderThroughput() {
  const [row] = await scoped('throughput');
  if (!row) return;
  $('tCases').textContent = nf.format(row.cases);
  $('tP50').textContent = hours(row.p50_seconds);
  $('tP80').textContent = hours(row.p80_seconds);
  $('tP95').textContent = hours(row.p95_seconds);
  $('tSteps').textContent = amount(row.avg_steps);
  $('tOutside').textContent = share(row.outside_hours_share);
  reading('rThroughput', readings.throughput(row));
}

async function renderTransitions() {
  const rows = await scoped('transitions');
  reading('rTransitions', readings.transitions(rows));
  $('transitions').innerHTML = table(rows.slice(0, 12), [
    {
      label: 'Übergang',
      render: (r) =>
        `<span title="${escape(r.from_activity)} → ${escape(r.to_activity)}">` +
        `${escape(r.from_activity)} → ${escape(r.to_activity)}</span>`,
    },
    { label: 'Fälle', numeric: true, render: (r) => nf.format(r.n) },
    { label: 'Median', numeric: true, render: (r) => hours(r.median_seconds) },
    { label: 'Summe', numeric: true, render: (r) => hours(r.total_seconds) },
  ]);
}

async function renderRework() {
  const [repeats, negatives] = await Promise.all([scoped('rework'), scoped('negative-outcomes')]);
  reading('rRework', readings.rework(repeats, negatives));

  $('rework').innerHTML = table(
    repeats,
    [
      { label: 'Aktivität', render: (r) => escape(r.event_type) },
      { label: 'Fälle', numeric: true, render: (r) => nf.format(r.rework_cases) },
      { label: 'Quote', numeric: true, render: (r) => pf.format(Number(r.rework_rate)) },
      { label: 'Zusätzlich', numeric: true, render: (r) => nf.format(r.extra_executions) },
    ],
    (row) => (row.event_type_key ? { process: objectType, step: row.event_type_key } : null)
  );
  wireDrill('rework');

  $('negatives').innerHTML = table(negatives, [
    { label: 'Rückläufer-Schritt', render: (r) => escape(r.event_type) },
    { label: 'Fälle', numeric: true, render: (r) => nf.format(r.cases) },
    { label: 'Anteil', numeric: true, render: (r) => pf.format(Number(r.case_share)) },
    { label: 'Median mit', numeric: true, render: (r) => hours(r.median_with) },
    { label: 'Median ohne', numeric: true, render: (r) => hours(r.median_without) },
  ]);
}

async function renderVariants() {
  const rows = await scoped('variants');
  reading('rVariants', readings.variants(rows));
  $('variants').innerHTML = table(rows.slice(0, 10), [
    {
      label: 'Variante',
      render: (r) =>
        `<span class="variant" title="${escape(r.variant)}">` +
        escape(r.variant) +
        '</span>',
    },
    { label: 'Fälle', numeric: true, render: (r) => nf.format(r.n) },
    { label: 'Anteil', numeric: true, render: (r) => pf.format(Number(r.share)) },
    { label: 'Kumuliert', numeric: true, render: (r) => pf.format(Number(r.cum_share)) },
    { label: 'Ø Dauer', numeric: true, render: (r) => hours(r.avg_seconds) },
  ]);
}

async function renderAutomation() {
  const [[summary], candidates] = await Promise.all([scoped('automation'), scoped('automation-candidates')]);
  reading('rAutomation', readings.automation(summary, candidates));
  if (summary) {
    $('aStp').textContent = share(summary.straight_through_share);
    $('aManual').textContent = share(summary.manual_event_share);
  }

  $('candidates').innerHTML = table(
    candidates.slice(0, 10),
    [
    { label: 'Schritt', render: (r) => escape(r.event_type) },
    { label: 'Häufigkeit', numeric: true, render: (r) => nf.format(r.freq) },
    { label: 'Manuell', numeric: true, render: (r) => pf.format(Number(r.manual)) },
    // Low entropy means the next step is always the same, which is what makes a step mechanical rather than a
    // judgement call. Automating a high-entropy step produces a support queue instead of a saving.
      { label: 'Vorhersagbarkeit', numeric: true, render: (r) => pf.format(1 - Number(r.outcome_entropy)) },
    ],
    (row) => (row.event_type_key ? { process: objectType, step: row.event_type_key } : null)
  );
  wireDrill('candidates');
}

async function renderEndpointsAndHandovers() {
  const [ends, handovers] = await Promise.all([scoped('endpoints'), scoped('handovers')]);
  reading('rEndings', readings.endings(ends));

  $('endpoints').innerHTML = table(ends.slice(0, 8), [
    { label: 'Letzter Schritt', render: (r) => escape(r.last_activity) },
    { label: 'Fälle', numeric: true, render: (r) => nf.format(r.cases) },
    { label: 'Anteil', numeric: true, render: (r) => pf.format(Number(r.share)) },
    { label: 'Median bis dahin', numeric: true, render: (r) => hours(r.median_seconds) },
  ]);

  $('handovers').innerHTML = handovers.length
    ? table(handovers.slice(0, 12), [
        { label: 'von', render: (r) => escape(r.from_actor) },
        { label: 'an', render: (r) => escape(r.to_actor) },
        { label: 'Fälle', numeric: true, render: (r) => nf.format(r.cases) },
        { label: 'Übergaben', numeric: true, render: (r) => nf.format(r.handovers) },
      ])
    : '<p class="empty">Keine Paarung erreicht die Mindestzahl von 5 Fällen. Das ist die Sperre, nicht ein leerer Datenstand.</p>';
}

/** The discovery screens: what exists, who does it, what crosses the company boundary. No object type needed. */
async function renderOverview() {
  const responses = await Promise.all([
    request(`/api/discovery/processes${scopeQuery()}`),
    request(`/api/discovery/roles${scopeQuery()}`),
    request(`/api/discovery/who-does-what${scopeQuery()}`),
    request(`/api/discovery/handovers${scopeQuery()}`),
    request(`/api/discovery/role-handovers${scopeQuery()}`),
    request('/api/discovery/coverage'),
    request('/api/discovery/calendar'),
    request(`/api/discovery/decisions${scopeQuery()}`),
    request(`/api/discovery/collaboration${scopeQuery()}`),
  ]);

  // A cancelled request resolves to null. Painting that as an empty table would tell the reader there is no data
  // when the truth is that nobody asked yet.
  if (responses.some((response) => response === null)) {
    // Says which one, and says it out loud. A silent return here is indistinguishable from "the data is empty",
    // and that ambiguity cost an hour once already.
    const routes = ['processes','roles','who-does-what','handovers','role-handovers','coverage','calendar','decisions','collaboration'];
    console.warn('Überblick abgebrochen, keine Antwort von:', routes.filter((_, i) => responses[i] === null).join(', '));
    return;
  }
  const [processes, roles, whoDoesWhat, handovers, roleHandovers, coverage, calendar, decisions, collaboration] =
    responses;

  reading('rProcesses', readings.processes(processes));
  reading('rCalendar', readings.calendar(calendar[0]));
  reading('rRoles', readings.roles(roles));
  reading('rWhoDoesWhat', readings.whoDoesWhat(whoDoesWhat));
  reading('rHandovers', readings.handovers(handovers));
  reading('rRoleHandovers', readings.roleHandovers(roleHandovers));
  reading('rCoverage', readings.coverage(coverage));
  reading('rDecisions', readings.decisions(decisions));

  const named = decisions.some((row) => row.entschieden_von && !row.entschieden_von.startsWith('a:'));
  $('identityHint').textContent = named
    ? 'Namen sichtbar (SHOW_ACTOR_IDENTITY=true)'
    : 'Pseudonyme — Namen über SHOW_ACTOR_IDENTITY=true';

  // The person who decided leads to what that person does. The row names two people, and the decider is the one the
  // reader is asking about — the other is one click further, in the collaboration table below.
  $('decisions').innerHTML = table(
    decisions.slice(0, 25),
    [
      { label: 'Worum', render: (r) => escape(r.worum_geht_es) },
      { label: 'eingereicht von', render: (r) => escape(r.eingereicht_von) },
      { label: 'entschieden von', render: (r) => escape(r.entschieden_von) },
      { label: 'Entscheidung', render: (r) => escape(r.entscheidung) },
      { label: 'wie oft', numeric: true, render: (r) => nf.format(r.wie_oft) },
      { label: 'Wartezeit', numeric: true, render: (r) => `${nf.format(r.wartezeit_stunden ?? 0)} h` },
    ],
    (row) => (row.entschieden_von_key ? { actor: row.entschieden_von_key } : null)
  );
  wireDrill('decisions');

  $('collaboration').innerHTML = table(
    collaboration.slice(0, 20),
    [
      { label: 'Worum', render: (r) => escape(r.worum_geht_es) },
      { label: 'Person', render: (r) => escape(r.person) },
      { label: 'zusammen mit', render: (r) => escape(r.zusammen_mit) },
      { label: 'gemeinsame Fälle', numeric: true, render: (r) => nf.format(r.gemeinsame_faelle) },
    ],
    (row) => (row.person_key ? { actor: row.person_key } : null)
  );
  wireDrill('collaboration');

  $('processes').innerHTML = table(processes, [
    { label: 'Prozess', render: (r) => `<strong>${escape(r.prozess)}</strong>` },
    { label: 'Art', render: (r) => escape(r.art) },
    { label: 'Fälle', numeric: true, render: (r) => nf.format(r.faelle) },
    { label: 'Dauer', numeric: true, render: (r) => `${nf.format(r.dauer_stunden ?? 0)} h` },
    { label: 'Schritte', numeric: true, render: (r) => Number(r.schritte).toFixed(1) },
    { label: 'Automatisch', numeric: true, render: (r) => `${nf.format(r.automatisch_prozent ?? 0)} %` },
    { label: 'Beginnt mit', render: (r) => escape(r.beginnt_mit ?? '—') },
    { label: 'Endet mit', render: (r) => escape(r.endet_mit ?? '—') },
    { label: 'Beteiligt', render: (r) => escape(r.beteiligte ?? '—') },
  ], (row) => (row.technischer_typ ? { process: row.technischer_typ } : null));
  wireDrill('processes');

  $('roles').innerHTML = table(roles, [
    { label: 'Rolle', render: (r) => escape(r.rolle) },
    { label: 'Personen', numeric: true, render: (r) => nf.format(r.personen) },
    { label: 'ausgeschieden', numeric: true, render: (r) => nf.format(r.ausgeschieden) },
    { label: 'davon aktiv', numeric: true, render: (r) => nf.format(r.davon_aktiv) },
    { label: 'Schritte', numeric: true, render: (r) => nf.format(r.schritte) },
    { label: 'Anteil', numeric: true, render: (r) => `${Number(r.anteil_prozent).toFixed(1)} %` },
  ]);

  $('whoDoesWhat').innerHTML = table(whoDoesWhat.slice(0, 20), [
    { label: 'Schritt', render: (r) => escape(r.schritt) },
    { label: 'wird ausgeführt von', render: (r) => escape(r.wer) },
    { label: 'wie oft', numeric: true, render: (r) => nf.format(r.wie_oft) },
    { label: 'Anteil am Schritt', numeric: true, render: (r) => `${nf.format(r.anteil_am_schritt)} %` },
    // More than one role on the same step is either shared responsibility or an unclear boundary; both are
    // worth seeing, and neither is visible from the count alone.
    { label: 'Rollen', numeric: true, render: (r) => nf.format(r.rollen_am_schritt) },
  ]);

  $('handoverList').innerHTML = table(handovers, [
    { label: 'Richtung', render: (r) => escape(r.richtung) },
    { label: 'Vorgang', render: (r) => escape(r.vorgang) },
    { label: 'Anzahl', numeric: true, render: (r) => nf.format(r.anzahl) },
    { label: 'an Tagen', numeric: true, render: (r) => nf.format(r.an_tagen) },
    { label: 'ausgelöst von', render: (r) => escape(r.ausgeloest_von) },
  ]);

  $('roleHandovers').innerHTML = table(roleHandovers, [
    { label: 'von', render: (r) => escape(r.von) },
    { label: 'an', render: (r) => escape(r.an) },
    { label: 'Fälle', numeric: true, render: (r) => nf.format(r.faelle) },
    { label: 'Übergaben', numeric: true, render: (r) => nf.format(r.uebergaben) },
  ]);

  $('coverage').innerHTML = table(coverage, [
    { label: 'Bezeichnung', render: (r) => escape(r.bezeichnung) },
    { label: 'technischer Typ', render: (r) => `<code>${escape(r.technischer_typ)}</code>` },
    { label: 'beobachtet', numeric: true, render: (r) => nf.format(r.beobachtet) },
  ]);
}

/**
 * The mined diagrams: one process at a time, or all of them together.
 *
 * The combined graph answers one question — where do processes meet — and answers nothing else, because twenty object
 * types in one picture is a wall of crossing edges. So the process comes first here and the rendering second: pick
 * what you want to look at, then how.
 *
 * Their age is shown, because a stale picture that looks current is worse than none.
 */
async function renderModels() {
  const status = await request('/api/mining/status');
  const available = status.models.filter((m) => m.available);
  const byName = new Map(available.map((model) => [model.name, model.url]));

  if (!available.length) {
    $('miningHint').textContent = `noch nicht gerechnet — ${status.hint}`;
    $('modelView').innerHTML = '<p class="empty">Noch keine Diagramme vorhanden.</p>';
    return;
  }

  const age = Math.max(...available.map((m) => m.ageMinutes ?? 0));
  $('miningHint').textContent = age > 90 ? `gerechnet vor ${Math.round(age / 60)} h` : `gerechnet vor ${age} min`;

  // The overview always exists; the per-process sets come from the miner's own report, so a process that was added
  // to the source appears here as soon as the next mining run has seen it.
  const perProcess = (status.stats?.processes ?? []).filter((p) => p.files && byName.has(p.files.frequency));
  const views = {
    all: [
      { label: 'Alle Prozesse: Häufigkeit', name: 'ocdfg-frequency.svg' },
      { label: 'Alle Prozesse: Dauer', name: 'ocdfg-performance.svg' },
      { label: 'Modell (Petri-Netz)', name: 'ocpn.svg' },
    ],
  };
  for (const process of perProcess) {
    views[process.slug] = [
      { label: 'Hauptpfade', name: process.files.main },
      { label: 'Alle Pfade', name: process.files.frequency },
      { label: 'Nach Dauer', name: process.files.performance },
    ];
  }

  const select = $('modelProcess');
  select.innerHTML =
    '<option value="all">Alle Prozesse (wo sie sich treffen)</option>' +
    perProcess.map((p) => `<option value="${escape(p.slug)}">${escape(p.object_type)}</option>`).join('');

  const show = (url) => {
    // An <img> and not an <object>: the picture has to be scaled from the outside, and a nested browsing context
    // decides its own size. Width in percent lets the SVG's own viewBox do the scaling, so the text stays sharp at
    // every step instead of being resampled.
    $('modelView').innerHTML = `<img class="model-image" src="${url}" alt="Prozessdiagramm" />`;
    // One viewer, attached once. It reads whatever picture is in the box, so switching tabs above does not need to
    // tell it anything.
    if (!$('modelView').dataset.viewer) {
      viewer = attachViewer($('modelView'), { title: 'Prozessdiagramm' });
      $('modelView').dataset.viewer = 'ready';
    }
    viewer?.setZoom(1);
  };

  const renderTabs = (key) => {
    const options = (views[key] ?? []).filter((view) => byName.has(view.name));
    if (!options.length) {
      $('modelTabs').innerHTML = '';
      $('modelView').innerHTML = '<p class="empty">Für diesen Prozess wurde kein Diagramm gerechnet.</p>';
      return;
    }

    $('modelTabs').innerHTML = options
      .map(
        (view, index) =>
          `<button type="button" class="model-tab${index === 0 ? ' active' : ''}" ` +
          `data-url="${byName.get(view.name)}">${escape(view.label)}</button>`
      )
      .join('');

    $('modelTabs')
      .querySelectorAll('.model-tab')
      .forEach((tab) =>
        tab.addEventListener('click', () => {
          $('modelTabs')
            .querySelectorAll('.model-tab')
            .forEach((other) => other.classList.remove('active'));
          tab.classList.add('active');
          show(tab.dataset.url);
        })
      );

    show(byName.get(options[0].name));
  };

  select.onchange = () => renderTabs(select.value);
  // Straight into the first real process rather than the combined wall — that picture is a comparison, and a
  // comparison is not where somebody starts.
  select.value = perProcess.length ? perProcess[0].slug : 'all';
  renderTabs(select.value);
}

export async function renderInsights() {
  // The drill-down first: it is the default view, and a reader who lands on it should not wait for six panels they
  // are not looking at.
  await renderDrill();
  // The report is assembled on demand rather than with every refresh: it fires a request per process, and nobody is
  // reading it while they work in the drill-down.
  await renderOverview();
  await renderInventory();
  // Naming is independent of the chosen process, and it must render even when there is no process yet: a fresh
  // installation is exactly the moment the list of unnamed types is longest.
  await renderNaming();
  if (!objectType) return;

  $('scopeLabel').textContent = $('objectTypeSelect').selectedOptions[0]?.textContent ?? objectType;
  // Says which window produced these numbers — a filter nobody can see is a filter nobody can check.
  const scopeNote = $('periodNote');
  if (scopeNote) scopeNote.textContent = periodLabel();
  // allSettled, not all: one slow or failing panel must not take the other eight with it. A single query that
  // needed thirteen seconds once left the entire page blank with nothing on screen to explain it.
  const panels = await Promise.allSettled([
    renderModels(),
    renderThroughput(),
    renderTransitions(),
    renderRework(),
    renderVariants(),
    renderAutomation(),
    renderEndpointsAndHandovers(),
  ]);

  for (const panel of panels.filter((p) => p.status === 'rejected'))
    console.error('Panel konnte nicht geladen werden:', panel.reason);
}

export function initInsights() {
  // Re-renders everything on change: the panels share one window, so a partial refresh would leave the reader
  // comparing a filtered number with an unfiltered one.
  initPeriod($('periodBar'), () => renderInsights());

  initNaming();
  initDrill();
  initReport();
  whenOpened('bericht', () => renderReport());
  // Built when opened: three queries that nobody is waiting for while they work in the path.
  whenOpened('jetzt', () => renderNow());

  $('objectTypeSelect').addEventListener('change', (event) => {
    objectType = event.target.value;
    renderInsights();
  });

  $('directoryBtn').addEventListener('click', async () => {
    $('directoryHint').textContent = 'läuft …';
    const result = await request('/api/directory/sync', { method: 'POST' });
    $('directoryHint').textContent = result.skipped
      ? result.skipped
      : `${nf.format(result.users)} Personen, ${nf.format(result.memberships)} Gruppenzuordnungen`;
    renderInsights();
  });

  $('projectBtn').addEventListener('click', async () => {
    $('projectHint').textContent = 'läuft …';
    const result = await request('/api/projection/run', { method: 'POST' });
    $('projectHint').textContent = `${nf.format(result.projected)} Ereignisse projiziert`;
    renderInsights();
  });
}
