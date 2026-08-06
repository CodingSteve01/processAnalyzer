// Following a thought instead of reading seven pages.
//
// Every other screen in this tool answers a question somebody has not asked yet. This one starts where a person starts
// — which processes are there — and then lets them go in: a process, a step inside it, the cases that went through that
// step, one of those cases, and from any step in that case back out to every case with the same step.
//
// The path lives in the URL, so the browser's back button works, a finding can be sent to somebody, and a reload lands
// where the reader was rather than at the beginning.
//
//   #prozesse
//   #prozesse/p:dispo-order-detail
//   #prozesse/p:dispo-order-detail/s:<step key>
//   #prozesse/p:dispo-order-detail/s:<step key>/f:<case id>
//
// Nothing here computes: it reads the same endpoints as the panels next to it. What is new is that the numbers are
// clickable and the way back is visible.

import { request } from './api.js';
import { periodQuery, periodLabel, setPeriod } from './period.js';
import { $, escape } from './utils.js';
import { drawLineChart } from './linechart.js';
import { attachViewer } from './imageview.js';
import { renderLandscape } from './landscape.js';
import * as readings from './readings.js';

const nf = new Intl.NumberFormat('de-DE');
const pf = new Intl.NumberFormat('de-DE', { style: 'percent', maximumFractionDigits: 0 });
const df = new Intl.DateTimeFormat('de-DE', { dateStyle: 'short', timeStyle: 'short' });

const hours = (value) =>
  value === null || value === undefined ? '—' : Number(value) >= 10 ? `${nf.format(Math.round(value))} h` : `${Number(value).toFixed(1)} h`;
const moment = (value) => (value ? df.format(new Date(value)) : '—');

/**
 * The rows of a response, or none.
 *
 * `request` answers with null when its request was superseded — switching tabs while a level is loading does that, and
 * it is a normal event, not an error. Reading `.rows` off it threw and killed the render, which is how a routine race
 * turned into an empty page with a stack trace in the console.
 */
const rowsOf = (response) => (Array.isArray(response) ? response : (response?.rows ?? []));

/** Labels for the crumbs, kept so a step or case reads as a word rather than a key while the reader is inside it. */
const names = { process: null, step: null, case: null, actor: null };

/** Object type to URL template, from the installation's configuration. Empty when nothing is configured. */
let sourceLinks = {};

// ===== the path =====

function path() {
  const parts = window.location.hash.slice(1).split('/');
  const grab = (prefix) =>
    parts
      .slice(1)
      .filter((part) => part.startsWith(prefix))
      .map((part) => decodeURIComponent(part.slice(prefix.length)))[0] ?? null;

  return { process: grab('p:'), step: grab('s:'), case: grab('f:'), actor: grab('x:') };
}

function goTo({ process = null, step = null, case: caseId = null, actor = null }) {
  const parts = ['prozesse'];
  // A person is not inside a process: they work across them. So this is its own branch of the path rather than a level
  // under a process.
  if (actor) {
    parts.push(`x:${encodeURIComponent(actor)}`);
    window.location.hash = parts.join('/');
    return;
  }
  if (process) parts.push(`p:${encodeURIComponent(process)}`);
  if (process && step) parts.push(`s:${encodeURIComponent(step)}`);
  if (process && step && caseId) parts.push(`f:${encodeURIComponent(caseId)}`);
  window.location.hash = parts.join('/');
}

// ===== rendering =====

export async function renderDrill() {
  const here = path();
  renderCrumbs(here);

  if (here.actor) return renderActor(here.actor);
  if (!here.process) return renderProcesses();
  if (!here.step) return renderProcess(here.process);
  if (!here.case) return renderStep(here.process, here.step);
  return renderCase(here.process, here.step, here.case);
}

function renderCrumbs(here) {
  const crumbs = [{ label: 'Alle Prozesse', target: {} }];
  if (here.actor) crumbs.push({ label: names.actor ?? here.actor, target: { actor: here.actor } });
  if (here.process)
    crumbs.push({ label: names.process ?? here.process, target: { process: here.process } });
  if (here.step)
    crumbs.push({ label: names.step ?? here.step, target: { process: here.process, step: here.step } });
  if (here.case)
    crumbs.push({ label: names.case ?? here.case, target: here });

  $('drillCrumbs').innerHTML = crumbs
    .map((crumb, index) =>
      index === crumbs.length - 1
        ? `<span class="crumb current">${escape(crumb.label)}</span>`
        : `<button type="button" class="crumb" data-step="${index}">${escape(crumb.label)}</button>`
    )
    .join('<span class="crumb-sep">›</span>');

  $('drillCrumbs')
    .querySelectorAll('.crumb[data-step]')
    .forEach((button) => button.addEventListener('click', () => goTo(crumbs[Number(button.dataset.step)].target)));

  $('drillScope').textContent = periodLabel();
}

// ── level 0: which processes are there ────────────────────────────────────────────────────────────────────────────

async function renderProcesses() {
  names.process = names.step = names.case = null;
  const rows = rowsOf(await request(`/api/discovery/processes${periodQuery() ? `?${periodQuery().slice(1)}` : ''}`));

  $('drillReading').textContent = rows.length ? readings.processes(rows) ?? '' : '';
  $('drillReading').hidden = !rows.length;

  // The landscape first: before choosing a process, somebody wants to see what there is end to end. The cards below are
  // the same list as a choice.
  const landscape = rows.length
    ? `<h3>Die Landschaft</h3>
       <p class="caveat">Wer gibt wem Arbeit. Klick auf einen Kasten öffnet den Ablauf.</p>
       <div class="model-view model-view-inline" id="drillLandscape"></div>
       <div class="chart-readout" id="drillLandscapeReadout"></div>
       <h3>Die Abläufe im Einzelnen</h3>`
    : '';

  // Cards, not a table: at this level the reader is choosing, and a choice reads better as a handful of tiles than as
  // twenty rows of numbers with no shape.
  $('drillBody').innerHTML = landscape + (rows.length
    ? `<div class="drill-cards">${rows
        .map(
          (row) => `
        <button type="button" class="drill-card" data-process="${escape(row.technischer_typ)}"
                data-label="${escape(row.prozess)}">
          <span class="drill-card-title">${escape(row.prozess)}</span>
          <span class="drill-card-figure">${nf.format(row.faelle)}<small> Fälle</small></span>
          <span class="drill-card-line">${escape(row.beginnt_mit ?? '—')} → ${escape(row.endet_mit ?? '—')}</span>
          <span class="drill-card-meta">
            ${row.dauer_stunden ? `${nf.format(row.dauer_stunden)} h im Median · ` : ''}
            ${nf.format(row.schritte)} Schritte · ${nf.format(row.automatisch_prozent)} % automatisch
          </span>
          <span class="drill-card-meta">${escape(row.beteiligte ?? '')}</span>
        </button>`
        )
        .join('')}</div>`
    : '<p class="empty">Noch keine Prozesse im Spiegel.</p>');

  if (rows.length) {
    // The landscape is a picture like any other here, so it gets the same controls: zoom, reading size, full screen.
    attachViewer($('drillLandscape'), { title: 'Prozesslandschaft' });
    renderLandscape($('drillLandscape'), $('drillLandscapeReadout'), (key) => {
      names.process = rows.find((row) => row.technischer_typ === key)?.prozess ?? key;
      goTo({ process: key });
    });
  }

  for (const card of $('drillBody').querySelectorAll('.drill-card')) {
    card.addEventListener('click', () => {
      names.process = card.dataset.label;
      goTo({ process: card.dataset.process });
    });
  }
}

// ── level 1: how does this one process run ────────────────────────────────────────────────────────────────────────

async function renderProcess(process) {
  const scoped = (route) => request(`/api/${route}?objectType=${encodeURIComponent(process)}${periodQuery()}`);
  const [inventory, throughput, activities, transitions, rework, variants, automation, drivers] = await Promise.all([
    request('/api/inventory'),
    scoped('throughput'),
    scoped('activities'),
    scoped('transitions'),
    scoped('rework'),
    scoped('variants'),
    scoped('automation'),
    scoped('drivers'),
  ]);

  const inventoryRow = rowsOf(inventory).find((row) => row.object_type === process);
  names.process = inventoryRow?.bezeichnung ?? process;
  renderCrumbs(path());

  const summary = rowsOf(throughput)[0];
  const findings = collectFindings({
    summary,
    transitions: rowsOf(transitions),
    rework: rowsOf(rework),
    variants: rowsOf(variants),
    automation: rowsOf(automation)[0],
    drivers: rowsOf(drivers),
  });

  $('drillReading').hidden = true;
  $('drillBody').innerHTML = `
    ${summaryTiles(summary, inventoryRow)}

    <section id="drillDiagramSection" hidden>
      <h3>Der Ablauf</h3>
      <div id="drillDiagram"></div>
    </section>

    ${
      findings.length
        ? `<h3>Was auffällt</h3>
           <ul class="drill-findings">${findings
             .map(
               (finding, index) =>
                 `<li${finding.step ? ` class="clickable" data-finding="${index}"` : ''}>${escape(finding.text)}${
                   finding.step ? '<span class="drill-go">ansehen ›</span>' : ''
                 }</li>`
             )
             .join('')}</ul>`
        : ''
    }

    <h3>Die Schritte</h3>
    <p class="caveat">Ein Klick auf einen Schritt zeigt die Fälle, die durch ihn gelaufen sind.</p>
    ${stepTable(rowsOf(activities))}

    <section id="drillTrendSection" hidden>
      <h3>Verlauf je Woche</h3>
      <div class="chart-box"><svg id="drillTrend" preserveAspectRatio="none"></svg></div>
      <div class="chart-legend" id="drillTrendLegend"></div>
    </section>

    <p class="hint" id="drillGaps"></p>`;

  // Both are additions to a level that already stands, so they load after it and their absence costs a picture rather
  // than the page.
  drawProcessTrend(process);
  showProcessDiagram(process, names.process);

  for (const item of $('drillBody').querySelectorAll('li[data-finding]')) {
    item.addEventListener('click', () => {
      const finding = findings[Number(item.dataset.finding)];
      names.step = finding.stepLabel;
      goTo({ process, step: finding.step });
    });
  }
  wireStepRows(process);
}

function summaryTiles(summary, inventoryRow) {
  if (!summary) return '';
  const all = inventoryRow ? Number(inventoryRow.objects) : null;
  const closed = Number(summary.cases ?? 0);

  // Percentiles need finished cases. Three dashes in a row are a placeholder pretending to be a figure, so the state
  // gets one sentence and the tiles stay away until there is something in them.
  if (!closed) {
    return `
      <p class="drill-state">
        ${all === null ? '' : `<strong>${nf.format(all)} Fälle</strong>, alle noch offen. `}
        Durchlaufzeiten entstehen erst, wenn Fälle abgeschlossen sind: entweder mit einem Endschritt dieses Ablaufs oder
        nach längerer Stille.
      </p>`;
  }

  return `
    <div class="stats-grid">
      <div class="stat-card"><h3>Fälle</h3><div class="metric-value">${all === null ? '—' : nf.format(all)}</div>
        <div class="metric-label">${
          all === null ? '' : `${nf.format(closed)} abgeschlossen, ${nf.format(all - closed)} laufen noch`
        }</div></div>
      <div class="stat-card"><h3>Median</h3><div class="metric-value">${hours(summary.p50_seconds ? summary.p50_seconds / 3600 : null)}</div>
        <div class="metric-label">${closed ? 'Arbeitszeit, nicht Kalenderzeit' : 'noch kein Fall abgeschlossen'}</div></div>
      <div class="stat-card"><h3>Jeder zwanzigste</h3><div class="metric-value">${hours(summary.p95_seconds ? summary.p95_seconds / 3600 : null)}</div>
        <div class="metric-label">p95 — so lange dauert der langsame Rest</div></div>
      <div class="stat-card"><h3>Schritte je Fall</h3><div class="metric-value">${summary.avg_steps ? Number(summary.avg_steps).toFixed(1) : '—'}</div></div>
    </div>`;
}

/**
 * The sentences, ranked, with a target where one exists.
 *
 * Percentiles do not tell a dispatcher anything. "Between these two steps four hours of working time go, in 57 of 82
 * cases" does, and it can be clicked. Rank is by how much time or how many cases a finding is about, because that is
 * the order in which somebody would work through them.
 */
function collectFindings({ summary, transitions, rework, variants, automation, drivers }) {
  const findings = [];

  // First, because it is the only kind of statement here that compares: these cases against those, with the difference
  // in hours. A factor without the hours behind it is a curiosity; the hours are the reason to do something.
  for (const driver of (drivers ?? []).slice(0, 3)) {
    const withHours = Number(driver.median_with_seconds) / 3600;
    const withoutHours = Number(driver.median_without_seconds) / 3600;
    const extraHours = Number(driver.extra_seconds) / 3600;
    const factor = withoutHours > 0 ? withHours / withoutHours : null;
    findings.push({
      // Weighted above everything else on purpose: this is the one finding with a value attached.
      weight: Number(driver.extra_seconds ?? 0) * 10,
      text:
        `Fälle mit „${driver.event_type}" dauern ${factor ? `${nf.format(Math.round(factor * 10) / 10)}-mal so lange` : 'deutlich länger'} ` +
        `(${hours(withHours)} statt ${hours(withoutHours)}), ${nf.format(driver.with_cases)} von ` +
        `${nf.format(Number(driver.with_cases) + Number(driver.without_cases))} Fällen. Darin stecken ${hours(extraHours)} ` +
        `mehr als in den übrigen.`,
      step: driver.event_type_key,
      stepLabel: driver.event_type,
    });
  }

  for (const edge of (transitions ?? []).slice(0, 4)) {
    const seconds = Number(edge.total_seconds ?? 0);
    const outsideHours = seconds < 60;
    findings.push({
      weight: outsideHours ? Number(edge.n ?? 0) : seconds,
      text: outsideHours
        ? `„${edge.from_activity}" → „${edge.to_activity}" passiert ausserhalb der Arbeitszeit: ` +
          `${nf.format(edge.n)} Übergänge, und keiner davon fällt in einen Arbeitstag. Nachtarbeit oder Automatik, ` +
          `aber keine Wartezeit, die jemandem gehört.`
        : `Zwischen „${edge.from_activity}" und „${edge.to_activity}" liegen im Median ` +
          `${hours(edge.median_seconds / 3600)} Arbeitszeit, über ${nf.format(edge.n)} Übergänge zusammen ` +
          `${hours(seconds / 3600)}.`,
      step: edge.to_activity_key,
      stepLabel: edge.to_activity,
    });
  }

  for (const repeat of (rework ?? []).slice(0, 2)) {
    findings.push({
      weight: Number(repeat.extra_executions ?? 0) * 3600,
      text:
        `„${repeat.event_type}" passiert in ${nf.format(repeat.rework_cases)} Fällen mehr als einmal ` +
        `(${pf.format(Number(repeat.rework_rate))}), zusammen ${nf.format(repeat.extra_executions)} zusätzliche Ausführungen.`,
      step: repeat.event_type_key,
      stepLabel: repeat.event_type,
    });
  }

  if (variants?.length) {
    const top = variants[0];
    findings.push({
      weight: Number(top.n ?? 0) * 1800,
      text:
        `Der häufigste Ablauf deckt ${pf.format(Number(top.share))} der Fälle ab; es gibt ` +
        `${nf.format(variants.length)} verschiedene Wege durch diesen Prozess.`,
    });
  }

  if (automation && automation.straight_through_share !== null && automation.straight_through_share !== undefined) {
    findings.push({
      weight: 1,
      text: `${pf.format(Number(automation.straight_through_share))} der Fälle laufen ohne einen einzigen menschlichen Schritt durch.`,
    });
  }

  if (summary?.outside_hours_share) {
    findings.push({
      weight: 0,
      text: `${pf.format(Number(summary.outside_hours_share))} der Kalenderzeit dieser Fälle liegt ausserhalb der Arbeitszeit — dort wartet die Arbeit, ohne dass jemand daran ist.`,
    });
  }

  return findings.sort((a, b) => b.weight - a.weight);
}

function stepTable(rows) {
  if (!rows?.length) return '<p class="empty">Keine Schritte im gewählten Umfang.</p>';
  return `
    <table class="data">
      <thead><tr><th>Schritt</th><th class="num">wie oft</th><th class="num">Fälle</th>
                 <th class="num">von Hand</th><th class="num">als erster Schritt</th><th></th></tr></thead>
      <tbody>
        ${rows
          .map(
            (row) => `
          <tr class="clickable" data-step="${escape(row.event_type_key)}" data-label="${escape(row.event_type)}">
            <td>${escape(row.event_type)}</td>
            <td class="num">${nf.format(row.events)}</td>
            <td class="num">${nf.format(row.objects)}</td>
            <td class="num">${row.manual_share === null ? '—' : pf.format(Number(row.manual_share))}</td>
            <td class="num">${nf.format(row.as_first_step)}</td>
            <td class="num"><span class="drill-go">Fälle ›</span></td>
          </tr>`
          )
          .join('')}
      </tbody>
    </table>`;
}

function wireStepRows(process) {
  for (const row of $('drillBody').querySelectorAll('tr[data-step]')) {
    row.addEventListener('click', () => {
      names.step = row.dataset.label;
      goTo({ process, step: row.dataset.step });
    });
  }
}

// ── level 2: the cases that went through this step ────────────────────────────────────────────────────────────────

async function renderStep(process, step) {
  const [activities, cases] = await Promise.all([
    request(`/api/activities?objectType=${encodeURIComponent(process)}${periodQuery()}`),
    request(
      `/api/cases?objectType=${encodeURIComponent(process)}&withActivity=${encodeURIComponent(step)}${periodQuery()}`
    ),
  ]);

  const activity = rowsOf(activities).find((row) => row.event_type_key === step);
  names.step = activity?.event_type ?? step;
  renderCrumbs(path());

  $('drillReading').hidden = true;
  $('drillBody').innerHTML = `
    ${
      activity
        ? `<div class="stats-grid">
             <div class="stat-card"><h3>Wie oft</h3><div class="metric-value">${nf.format(activity.events)}</div></div>
             <div class="stat-card"><h3>Fälle</h3><div class="metric-value">${nf.format(activity.objects)}</div></div>
             <div class="stat-card"><h3>Von Hand</h3><div class="metric-value">${
               activity.manual_share === null ? '—' : pf.format(Number(activity.manual_share))
             }</div></div>
             <div class="stat-card"><h3>Als erster Schritt</h3><div class="metric-value">${nf.format(activity.as_first_step)}</div></div>
           </div>`
        : ''
    }
    <h3>Wann dieser Schritt passiert</h3>
    <div id="drillStepChart"></div>

    <h3>Fälle, die durch diesen Schritt gelaufen sind</h3>
    <p class="caveat">Nicht die Fälle, die hier stehen geblieben sind, sondern alle, die ihn passiert haben. Ein Klick öffnet den Fall.</p>
    ${caseTable(rowsOf(cases))}`;

  drawActivityTrend(process, step);

  for (const row of $('drillBody').querySelectorAll('tr[data-case]')) {
    row.addEventListener('click', () => {
      names.case = row.dataset.label;
      goTo({ process, step, case: row.dataset.case });
    });
  }
}

function caseTable(rows) {
  if (!rows?.length) return '<p class="empty">Keine Fälle im gewählten Umfang.</p>';
  return `
    <table class="data">
      <thead><tr><th>Nummer</th><th>steht bei</th><th class="num">Schritte</th>
                 <th class="num">Dauer</th><th>zuletzt</th><th></th></tr></thead>
      <tbody>
        ${rows
          .slice(0, 100)
          .map(
            (row) => `
          <tr class="clickable" data-case="${escape(row.schluessel)}" data-label="${escape(row.nummer)}">
            <td><strong>${escape(row.nummer)}</strong></td>
            <td>${escape(row.steht_bei ?? '—')}</td>
            <td class="num">${nf.format(row.schritte)}</td>
            <td class="num">${row.dauer_stunden === null ? '—' : `${nf.format(row.dauer_stunden)} h`}</td>
            <td>${moment(row.letzter_schritt)}</td>
            <td class="num"><span class="drill-go">öffnen ›</span></td>
          </tr>`
          )
          .join('')}
      </tbody>
    </table>`;
}

// ── level 3: this one case ────────────────────────────────────────────────────────────────────────────────────────

async function renderCase(process, step, caseId) {
  const [response, chain] = await Promise.all([
    request(`/api/case/${encodeURIComponent(caseId)}`),
    request(`/api/case/${encodeURIComponent(caseId)}/chain`),
  ]);
  const key = caseId.split(':')[1] ?? caseId;
  names.case = key;
  renderCrumbs(path());

  const steps = rowsOf(response);
  // The link into the source system, where the reader can actually do something about what they just found.
  const template = sourceLinks[process];
  const link = template
    ? `<p><a class="drill-source-link" href="${escape(template.replace('{id}', encodeURIComponent(key)))}"
             target="_blank" rel="noopener">Diesen Fall im System öffnen ›</a></p>`
    : '';

  $('drillReading').hidden = true;
  $('drillBody').innerHTML = `
    <h3>Was mit diesem Fall passiert ist</h3>
    ${link}
    ${caseBar(steps)}
    <p class="caveat">
      „davor" ist die Wartezeit vor dem Schritt, in Arbeitszeit — die Frage ist ja, wo die Zeit hingeht. Ein Klick auf
      einen Schritt zeigt alle Fälle, die denselben Schritt hatten.
    </p>
    ${chainSection(rowsOf(chain))}
    <h4>Nur dieser Fall</h4>
    ${
      steps.length
        ? `<table class="data">
             <thead><tr><th class="num">#</th><th>Was</th><th>Wann</th><th>Wer</th>
                        <th class="num">davor</th><th>zusammen mit</th></tr></thead>
             <tbody>
               ${steps
                 .map(
                   (row) => `
                 <tr${row.was_key ? ` class="clickable${row.was_key === step ? ' selected' : ''}" data-step="${escape(row.was_key)}" data-label="${escape(row.was)}"` : ''}>
                   <td class="num">${row.schritt}</td>
                   <td>${escape(row.was)}</td>
                   <td>${moment(row.wann)}</td>
                   <td>${escape(row.wer ?? '—')}</td>
                   <td class="num">${row.wartezeit_stunden === null ? '—' : `${nf.format(row.wartezeit_stunden)} h`}</td>
                   <td>${escape(row.auch_beteiligt ?? '—')}</td>
                 </tr>`
                 )
                 .join('')}
             </tbody>
           </table>`
        : '<p class="empty">Keine Schritte zu diesem Fall.</p>'
    }`;

  for (const row of $('drillBody').querySelectorAll('tr[data-chain]')) {
    row.addEventListener('click', (event) => {
      event.stopPropagation();
      if (row.dataset.chain === caseId) return;
      names.case = null;
      names.step = row.dataset.label;
      goTo({ process: row.dataset.process, step: row.dataset.step, case: row.dataset.chain });
    });
  }

  for (const row of $('drillBody').querySelectorAll('tr[data-step]:not([data-chain])')) {
    row.addEventListener('click', () => {
      names.step = row.dataset.label;
      names.case = null;
      goTo({ process, step: row.dataset.step });
    });
  }
}

// ===== wiring =====

/**
 * Jump into the path from anywhere.
 *
 * The other screens name the same things — a process in the overview, a step in the analysis, a case in the case list —
 * and every one of them was a dead end. Exported so those tables can hand over instead of duplicating the levels.
 */
export function drillTo(target) {
  goTo(target);
  const tab = document.querySelector('.viewtab[data-view="prozesse"]');
  if (tab) tab.click();
  renderDrill();
}

export function initDrill() {
  // Loaded once and tolerated when absent: a missing link map costs a link, not a page.
  request('/api/source-links')
    .then((map) => {
      sourceLinks = map ?? {};
    })
    .catch(() => {});

  // The browser's back button IS the way back, so the path lives in the hash and every change re-renders from it.
  window.addEventListener('hashchange', () => {
    if (window.location.hash.slice(1).split('/')[0] === 'prozesse') renderDrill();
  });
}


// ===== pictures =====

/**
 * The weekly line for a process: how many cases and how long they took.
 *
 * Drawn from the same endpoint the Entwicklung tab uses. It belongs here as well, because "the median is six hours" and
 * "the median has doubled since Tuesday" are different statements and only one of them is a reason to act.
 */
async function drawProcessTrend(process) {
  const response = await request(`/api/trend?objectType=${encodeURIComponent(process)}${periodQuery()}`);
  const rows = rowsOf(response);
  const host = $('drillTrend');
  if (!host) return;

  if (rows.length < 2) {
    // Nothing to draw, so nothing is shown. The absence is noted once at the bottom of the page.
    noteGap('für einen Wochenverlauf reicht der Spiegel noch nicht weit zurück');
    return;
  }

  $('drillTrendSection').hidden = false;

  const dayFormat = new Intl.DateTimeFormat('de-DE', { day: '2-digit', month: '2-digit' });
  const legend = drawLineChart(
    host,
    rows.map((row) => dayFormat.format(new Date(row.woche))),
    [
      { name: 'Fälle', colour: 'var(--accent)', values: rows.map((row) => Number(row.faelle ?? 0)) },
      {
        name: 'Median in Stunden',
        colour: 'var(--warning)',
        values: rows.map((row) => Number(row.p50_stunden ?? 0)),
        axis: 'right',
      },
    ]
  );
  $('drillTrendLegend').innerHTML = legend ?? '';
}

/** The mined diagram of this process, fitted, if a mining run has produced one. */
async function showProcessDiagram(process, label) {
  const status = await request('/api/mining/status');
  const host = $('drillDiagram');
  if (!host) return;

  const available = (status?.models ?? []).filter((model) => model.available);
  const entry = (status?.stats?.processes ?? []).find((row) => row.object_type === label);
  const file = entry?.files?.main ?? entry?.files?.frequency;
  const model = file ? available.find((candidate) => candidate.name === file) : null;

  if (!model) {
    noteGap('für diesen Ablauf ist noch kein Diagramm gerechnet');
    return;
  }

  $('drillDiagramSection').hidden = false;

  // Inlined rather than shown as an image, because an image cannot be clicked. The boxes carry the same German labels
  // the tables use — we generate both — so each one can be matched back to a step and lead into it.
  let svg;
  try {
    const response = await fetch(model.url);
    svg = await response.text();
  } catch {
    host.innerHTML = `<div class="model-view model-view-inline"><img class="model-image" src="${model.url}" alt="Ablauf" /></div>`;
    return;
  }

  host.innerHTML = `<div class="model-view model-view-inline" id="drillDiagramBox">${svg}</div>
    <p class="hint">Ein Klick auf einen Kasten führt zu den Fällen, die durch diesen Schritt gelaufen sind.</p>`;
  // Same controls as everywhere else, including full screen: a mined graph is metres wide and this box is not.
  attachViewer($('drillDiagramBox'), { title: `Ablauf ${label ?? process}` });
  wireDiagram(process);
}

/**
 * Makes the boxes of a mined diagram lead somewhere.
 *
 * Graphviz writes one group per node with its text inside. The text is the label plus the count ("Beleg zugeordnet
 * E=335"), so the label is matched by prefix against the steps of this process — no id is available in the picture, and
 * inventing one on the miner side would mean the two sides agreeing on a format forever.
 */
function wireDiagram(process) {
  const box = $('drillDiagramBox');
  if (!box) return;

  const steps = [...document.querySelectorAll('#drillBody tr[data-step]')].map((row) => ({
    key: row.dataset.step,
    label: row.dataset.label,
  }));
  if (!steps.length) return;

  for (const node of box.querySelectorAll('g.node')) {
    const text = [...node.querySelectorAll('text')].map((element) => element.textContent).join(' ');
    // Longest label first: "Freigabe erteilt" would otherwise swallow "Freigabe erteilt (Aufkäufer)".
    const match = steps
      .filter((step) => step.label && text.includes(step.label))
      .sort((a, b) => b.label.length - a.label.length)[0];
    if (!match) continue;

    node.classList.add('node-clickable');
    node.addEventListener('click', () => {
      names.step = match.label;
      goTo({ process, step: match.key });
    });
  }
}

/**
 * One step over time, as bars.
 *
 * Bars and not a line: a step happens on days, and between two days there is nothing to interpolate. A line would draw
 * a slope across a weekend on which nothing happened at all.
 */
async function drawActivityTrend(process, step) {
  const response = await request(
    `/api/activity-trend?objectType=${encodeURIComponent(process)}&activity=${encodeURIComponent(step)}${periodQuery()}`
  );
  const rows = rowsOf(response);
  const host = $('drillStepChart');
  if (!host) return;

  if (!rows.length) {
    host.innerHTML = '';
    return;
  }

  const max = Math.max(...rows.map((row) => Number(row.wie_oft)));
  const dayFormat = new Intl.DateTimeFormat('de-DE', { day: '2-digit', month: '2-digit' });
  host.innerHTML = `
    <div class="daybars" id="drillDayBars">
      ${rows
        .map((row, index) => {
          const height = Math.max(3, Math.round((Number(row.wie_oft) / max) * 100));
          return `<button type="button" class="daybar" style="height:${height}%" data-day="${index}"
                          aria-label="${dayFormat.format(new Date(row.tag))}"></button>`;
        })
        .join('')}
    </div>
    <div class="chart-readout" id="drillDayReadout">
      ${dayFormat.format(new Date(rows[0].tag))} bis ${dayFormat.format(new Date(rows[rows.length - 1].tag))},
      höchster Tag ${nf.format(max)}×. Ein Klick auf einen Balken setzt den Zeitraum auf diesen Tag.
    </div>`;

  // A tooltip of our own rather than the browser's: the browser's takes a second to appear, cannot be read while
  // moving along the bars, and disappears before the reader has compared two of them.
  const readout = $('drillDayReadout');
  const rest = readout.innerHTML;
  for (const bar of $('drillDayBars').querySelectorAll('.daybar')) {
    const row = rows[Number(bar.dataset.day)];
    const day = new Date(row.tag);
    bar.addEventListener('mouseenter', () => {
      readout.innerHTML =
        `<strong>${dayFormat.format(day)}</strong>: ${nf.format(row.wie_oft)}× in ${nf.format(row.faelle)} Fällen, ` +
        `${pf.format(Number(row.von_hand ?? 0))} von Hand`;
    });
    bar.addEventListener('mouseleave', () => {
      readout.innerHTML = rest;
    });
    // Clicking narrows the whole application to that day, controls included. Everything else on screen follows, which
    // is the difference between a picture and an instrument.
    bar.addEventListener('click', () => {
      const iso = new Date(day.getTime() - day.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
      const next = new Date(day.getTime() + 86400000 - day.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
      setPeriod(iso, next);
    });
  }
}

/**
 * The case as one bar: each step a mark, the gap between them proportional to the wait.
 *
 * A table of timestamps makes the reader do the subtraction and then imagine the shape. The shape is the finding: three
 * steps in a minute and then eleven hours of nothing is a different story from four evenly spaced hours, and the table
 * reads the same either way.
 */
function caseBar(steps) {
  if (steps.length < 2) return '';

  const start = new Date(steps[0].wann).getTime();
  const end = new Date(steps[steps.length - 1].wann).getTime();
  const span = Math.max(1, end - start);

  return `
    <div class="casebar" aria-hidden="true">
      ${steps
        .map((step) => {
          const at = ((new Date(step.wann).getTime() - start) / span) * 100;
          const human = step.wer && step.wer !== '—';
          return `<span class="casebar-mark${human ? ' human' : ''}" style="left:${at.toFixed(2)}%"
                        title="${escape(step.was)} · ${moment(step.wann)}"></span>`;
        })
        .join('')}
    </div>
    <p class="hint">
      ${moment(steps[0].wann)} bis ${moment(steps[steps.length - 1].wann)} · gefüllte Marken sind Schritte von Menschen,
      offene sind Automatik. Abstände sind echte Abstände.
    </p>`;
}


// ── one person: what they actually do ─────────────────────────────────────────────────────────────────────────────

/**
 * The steps of one person, by process.
 *
 * The people screens answered "who works with whom" and "which role does which step" and then stopped. The question
 * after that is always the same and had nowhere to go: what does this person do. At the level of the step, never at the
 * level of the keystroke — this is a process tool and it stays one.
 */
async function renderActor(actorKey) {
  const response = await request(
    `/api/actor?key=${encodeURIComponent(actorKey)}${periodQuery()}`
  );
  names.actor = response?.name ?? actorKey;
  renderCrumbs(path());

  const rows = rowsOf(response);
  const processes = new Map();
  for (const row of rows) {
    const entry = processes.get(row.prozess) ?? { label: row.prozess, key: row.prozess_key, steps: [], events: 0 };
    entry.steps.push(row);
    entry.events += Number(row.wie_oft);
    processes.set(row.prozess, entry);
  }

  $('drillReading').hidden = rows.length === 0;
  $('drillReading').textContent = rows.length
    ? `${nf.format(rows.reduce((sum, row) => sum + Number(row.wie_oft), 0))} Schritte in ` +
      `${nf.format(processes.size)} Abläufen, ${nf.format(
        new Set(rows.map((row) => row.schritt)).size
      )} verschiedene Tätigkeiten.`
    : '';

  $('drillBody').innerHTML = rows.length
    ? [...processes.values()]
        .sort((a, b) => b.events - a.events)
        .map(
          (entry) => `
        <h3>${escape(entry.label)}</h3>
        <table class="data">
          <thead><tr><th>Schritt</th><th class="num">wie oft</th><th class="num">Fälle</th>
                     <th>zuletzt</th><th></th></tr></thead>
          <tbody>
            ${entry.steps
              .map(
                (step) => `
              <tr class="clickable" data-step="${escape(step.schritt_key)}" data-label="${escape(step.schritt)}"
                  data-process="${escape(entry.key)}">
                <td>${escape(step.schritt)}</td>
                <td class="num">${nf.format(step.wie_oft)}</td>
                <td class="num">${nf.format(step.faelle)}</td>
                <td>${moment(step.zuletzt)}</td>
                <td class="num"><span class="drill-go">Fälle ›</span></td>
              </tr>`
              )
              .join('')}
          </tbody>
        </table>`
        )
        .join('')
    : '<p class="empty">Für diese Person liegen im gewählten Umfang keine Schritte vor.</p>';

  for (const row of $('drillBody').querySelectorAll('tr[data-step]')) {
    row.addEventListener('click', () => {
      names.step = row.dataset.label;
      names.actor = null;
      goTo({ process: row.dataset.process, step: row.dataset.step });
    });
  }
}

/**
 * The whole transaction: this case and everything it touches, on one timeline.
 *
 * An object-centric log has no single case, and that is the right model — an order, its tour, its papers and its
 * accounting rows each have a life of their own. But the question somebody asks about an order is what happened with
 * this order, and the answer spans all of them. So the case keeps its own table below, and this is the sequence a person
 * would tell: created, planned onto a tour, papers back from the driver, filed, reconciled.
 */
function chainSection(rows) {
  if (rows.length < 2) return '';

  const parts = new Map();
  for (const row of rows) parts.set(row.schluessel, row.gehoert_zu);
  if (parts.size < 2) return '';

  const involved = [...new Set(rows.map((row) => row.gehoert_zu))];

  return `
    <h4>Der ganze Vorgang</h4>
    <p class="caveat">
      Dieser Fall und alles, was ihn berührt: ${escape(involved.join(', '))}. Ein Schritt aus einem anderen Teil des
      Vorgangs ist eingerückt und führt beim Klick dorthin.
    </p>
    <table class="data">
      <thead><tr><th>gehört zu</th><th>Nummer</th><th>Was</th><th>Wann</th><th>Wer</th><th></th></tr></thead>
      <tbody>
        ${rows
          .map(
            (row) => `
          <tr class="clickable${row.ist_dieser_fall ? ' selected' : ''}"
              data-chain="${escape(row.schluessel)}" data-process="${escape(row.prozess_key)}"
              data-step="${escape(row.was_key)}" data-label="${escape(row.was)}">
            <td>${escape(row.gehoert_zu)}</td>
            <td>${escape(row.nummer ?? '')}</td>
            <td${row.ist_dieser_fall ? '' : ' class="chain-other"'}>${escape(row.was)}</td>
            <td>${moment(row.wann)}</td>
            <td>${escape(row.wer ?? '—')}</td>
            <td class="num">${row.ist_dieser_fall ? '' : '<span class="drill-go">öffnen ›</span>'}</td>
          </tr>`
          )
          .join('')}
      </tbody>
    </table>`;
}

/**
 * Collects what this level could not show, as one line at the bottom.
 *
 * The alternative was a heading, an explanation and an empty box per missing thing, which is how a working tool starts to
 * read like a demo with placeholders. What is missing still has to be said — silence would leave the reader believing
 * they have seen everything — but it belongs in one quiet sentence, not in three holes in the middle of the page.
 */
function noteGap(text) {
  const host = $('drillGaps');
  if (!host) return;
  const gaps = host.dataset.gaps ? `${host.dataset.gaps}; ${text}` : text;
  host.dataset.gaps = gaps;
  host.textContent = `Nicht dargestellt: ${gaps}.`;
}
