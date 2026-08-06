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
import { periodQuery, periodLabel, setPeriod, setStepFilter, scopeQuery } from './period.js';
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

/**
 * The miner's report, fetched once per screen.
 *
 * Two things on the process level need it: the picture and the mined findings. The fetch layer keys its aborts by route,
 * so two concurrent calls to the same route means the second cancels the first — and the loser resolves to null and
 * quietly renders "for this process no diagram has been computed" while the file sits right there. Ask once, read twice.
 */
let miningPromise = null;

function miningStatus() {
  miningPromise ??= request('/api/mining/status');
  return miningPromise;
}

export async function renderDrill() {
  // A new screen asks again: a mining run may have finished since.
  miningPromise = null;

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
  const [processes, boundary] = await Promise.all([
    request(`/api/discovery/processes${periodQuery() ? `?${periodQuery().slice(1)}` : ''}`),
    request(`/api/discovery/handovers${scopeQuery()}`),
  ]);
  const rows = rowsOf(processes);

  $('drillReading').textContent = rows.length ? readings.processes(rows) ?? '' : '';
  $('drillReading').hidden = !rows.length;

  // The landscape first: before choosing a process, somebody wants to see what there is end to end. The cards below are
  // the same list as a choice.
  const landscape = rows.length
    ? `<h3>Die Landschaft</h3>
       <p class="caveat">Wer gibt wem Arbeit. Klick auf einen Kasten öffnet den Ablauf.</p>
       <div class="model-view model-view-inline" id="drillLandscape"></div>
       <div class="chart-readout" id="drillLandscapeReadout"></div>

       <section id="drillAllSection" hidden>
         <h3>Alle Abläufe in einem Bild</h3>
         <p class="caveat">
           Aus denselben Ereignissen gerechnet, eine Farbe pro Ablauf: wo sich Farben in einem Kasten treffen, arbeiten
           mehrere Abläufe am selben Schritt. Klick auf einen Kasten öffnet den Ablauf, in dem dieser Schritt meistens
           vorkommt.
         </p>
         <div class="model-tabs" id="drillAllTabs"></div>
         <div id="drillAll"></div>
       </section>

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
    : '<p class="empty">Noch keine Prozesse im Spiegel.</p>') + boundarySection(rowsOf(boundary));

  if (rows.length) {
    // The mined pictures of everything at once, next to the drawn landscape. The drawn one answers "who gives whom work"
    // and the mined ones answer "where do the processes touch the same step" — different questions, same level.
    showLandscapeDiagrams();

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

/**
 * The mined pictures of everything at once, on the level where everything at once is the subject.
 *
 * These used to be a page of their own, which is why they disappeared when that page did. Inlined and clickable like the
 * per-process ones, but against a map of every step and the process it belongs to — a box in here says "Beleg freigegeben"
 * and nothing about which process that is, and a picture a reader can see but not enter is the one dead end left.
 */
async function showLandscapeDiagrams() {
  const [status, home] = await Promise.all([miningStatus(), request('/api/discovery/step-home')]);
  const host = $('drillAll');
  if (!host) return;

  const available = (status?.models ?? []).filter((model) => model.available);
  const views = [
    ['ocdfg-frequency.svg', 'Wie oft'],
    ['ocdfg-performance.svg', 'Wie lange'],
    ['ocpn.svg', 'Als Modell'],
  ].filter(([name]) => available.some((model) => model.name === name));

  if (!views.length) {
    noteGap('die Bilder über alle Abläufe sind noch nicht gerechnet');
    return;
  }

  const steps = rowsOf(home).map((row) => ({
    key: row.schritt_key,
    label: row.schritt,
    process: row.prozess_key,
    processLabel: row.prozess,
  }));

  $('drillAllSection').hidden = false;
  $('drillAllTabs').innerHTML = views
    .map(
      ([name, label], index) =>
        `<button type="button" class="model-tab${index === 0 ? ' active' : ''}" data-file="${escape(name)}">${label}</button>`
    )
    .join('');

  const paint = async (name) => {
    const model = available.find((candidate) => candidate.name === name);
    if (!model) return;
    await paintInto(
      { host: 'drillAll', box: 'drillAllBox', hint: 'drillAllHint', tabs: 'drillAllTabs' },
      model,
      'Alle Abläufe',
      () => wireDiagram(null, 'drillAllBox', steps)
    );
    if ($('drillAllHint').textContent.startsWith('Ein Klick'))
      $('drillAllHint').textContent = 'Ein Klick auf einen Kasten öffnet den Ablauf, in dem dieser Schritt zu Hause ist.';
  };

  for (const tab of $('drillAllTabs').querySelectorAll('.model-tab')) {
    tab.addEventListener('click', () => {
      for (const other of $('drillAllTabs').querySelectorAll('.model-tab')) other.classList.remove('active');
      tab.classList.add('active');
      paint(tab.dataset.file);
    });
  }

  await paint(views[0][0]);
}

/** What crosses the company boundary. A statement about the landscape, so it belongs to the landscape. */
function boundarySection(rows) {
  if (!rows.length) return '';
  return `
    <h3>Was hereinkommt und was hinausgeht</h3>
    <p class="caveat">
      Übergaben sind die Stellen, an denen etwas das Haus verlässt oder von aussen hereinkommt. Ein Blick auf einen
      Bildschirm ist keine Übergabe — hier steht nur, womit eine andere Seite weiterarbeitet.
    </p>
    <table class="data">
      <thead><tr><th>Richtung</th><th>Vorgang</th><th class="num">Anzahl</th><th class="num">an Tagen</th>
                 <th>ausgelöst von</th></tr></thead>
      <tbody>
        ${rows
          .map(
            (row) => `
          <tr><td>${escape(row.richtung)}</td><td>${escape(row.vorgang)}</td>
              <td class="num">${nf.format(row.anzahl)}</td><td class="num">${nf.format(row.an_tagen)}</td>
              <td>${escape(row.ausgeloest_von)}</td></tr>`
          )
          .join('')}
      </tbody>
    </table>`;
}

// ── level 1: how does this one process run ────────────────────────────────────────────────────────────────────────

async function renderProcess(process) {
  const scoped = (route) => request(`/api/${route}?objectType=${encodeURIComponent(process)}${periodQuery()}`);
  const [
    inventory,
    throughput,
    activities,
    transitions,
    rework,
    variants,
    automation,
    drivers,
    negatives,
    endpoints,
    handovers,
    candidates,
  ] = await Promise.all([
    request(`/api/inventory${scopeQuery()}`),
    scoped('throughput'),
    scoped('activities'),
    scoped('transitions'),
    scoped('rework'),
    scoped('variants'),
    scoped('automation'),
    scoped('drivers'),
    scoped('negative-outcomes'),
    scoped('endpoints'),
    scoped('handovers'),
    scoped('automation-candidates'),
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
      <div class="model-tabs" id="drillDiagramTabs" hidden></div>
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

    <section id="drillRulesSection" hidden>
      <h3>Regeln, die dieser Ablauf bricht</h3>
      <p class="caveat">
        Aus dem Log gelernt, nicht konfiguriert: was immer vorher passiert, was nie zusammen vorkommt, wie oft ein
        Schritt vorkommen darf. Ein Verstoss ist kein Fehler an sich — die Regel kommt aus dem Verhalten, ein seltener
        aber richtiger Weg sieht genauso aus.
      </p>
      <div id="drillRules"></div>
    </section>

    <section id="drillBatchSection" hidden>
      <h3>Stapelarbeit</h3>
      <p class="caveat">
        Erledigt in Blöcken statt laufend. Wer zwanzig Papiere um vier Uhr abzeichnet, ist nicht langsam, sondern
        stapelt, und im Mittelwert sehen beide gleich aus. Der Stapel ist eine Warteschlange, die jemand absichtlich
        gebaut hat.
      </p>
      <div id="drillBatches"></div>
    </section>

    <section id="drillTrendSection" hidden>
      <h3>Verlauf je Woche</h3>
      <div class="chart-box"><svg id="drillTrend" preserveAspectRatio="none"></svg></div>
      <div class="chart-legend" id="drillTrendLegend"></div>
    </section>

    ${detailBlock({
      process,
      summary,
      transitions: rowsOf(transitions),
      rework: rowsOf(rework),
      negatives: rowsOf(negatives),
      variants: rowsOf(variants),
      endpoints: rowsOf(endpoints),
      automation: rowsOf(automation)[0],
      candidates: rowsOf(candidates),
      handovers: rowsOf(handovers),
    })}

    <p class="hint" id="drillGaps"></p>`;

  // Both are additions to a level that already stands, so they load after it and their absence costs a picture rather
  // than the page.
  drawProcessTrend(process);
  showProcessDiagram(process, names.process);
  showMinedInsights(names.process);

  for (const item of $('drillBody').querySelectorAll('li[data-finding]')) {
    item.addEventListener('click', () => {
      const finding = findings[Number(item.dataset.finding)];
      names.step = finding.stepLabel;
      goTo({ process, step: finding.step });
    });
  }
  wireStepRows(process);
  for (const row of $('drillBody').querySelectorAll('tr[data-drill]')) {
    row.addEventListener('click', () => drillTo(JSON.parse(row.dataset.drill)));
  }
}

/**
 * The figures below the fold, for one process.
 *
 * These used to be a page of their own with a process picker on top, which meant the same choice was made twice: once by
 * clicking a process here, once by picking it in a dropdown there, and the two could disagree. They belong to the
 * process, so they live under it — folded away, because a reader arrives with a question and not with a wish for
 * eleven tables.
 */
function detailBlock({
  process,
  summary,
  transitions,
  rework,
  negatives,
  variants,
  endpoints,
  automation,
  candidates,
  handovers,
}) {
  const toStep = (row, key = 'event_type_key') => (row[key] ? { process, step: row[key] } : null);

  const blocks = [
    dataSection(
      'Wo die Zeit hingeht',
      transitions.slice(0, 12),
      [
        ['Übergang', (r) => `${escape(r.from_activity)} → ${escape(r.to_activity)}`],
        ['Fälle', (r) => nf.format(r.n), true],
        ['Median', (r) => hours(r.median_seconds / 3600), true],
        ['Summe', (r) => hours(r.total_seconds / 3600), true],
      ],
      (r) => toStep(r, 'to_activity_key'),
      'Verstrichene Zeit, nicht Bearbeitungszeit: ein Ereignis trägt einen Zeitpunkt, keine Dauer. Sortiert nach ' +
        'Gesamtzeit, weil die langsamste Kante meist ein Einzelfall ist und die teuerste die, die oft passiert.'
    ),
    dataSection(
      'Nacharbeit',
      rework,
      [
        ['Schritt', (r) => escape(r.event_type)],
        ['Fälle', (r) => nf.format(r.rework_cases), true],
        ['Quote', (r) => pf.format(Number(r.rework_rate)), true],
        ['zusätzliche Ausführungen', (r) => nf.format(r.extra_executions), true],
      ],
      toStep
    ),
    dataSection(
      'Rückläufer',
      negatives,
      [
        ['Schritt', (r) => escape(r.event_type)],
        ['Fälle', (r) => nf.format(r.cases), true],
        ['Anteil', (r) => pf.format(Number(r.case_share)), true],
        ['Median mit', (r) => hours(r.median_with / 3600), true],
        ['Median ohne', (r) => hours(r.median_without / 3600), true],
      ],
      toStep
    ),
    dataSection(
      'Varianten',
      variants.slice(0, 10),
      [
        ['Weg durch den Ablauf', (r) => `<span class="variant" title="${escape(r.variant)}">${escape(r.variant)}</span>`],
        ['Fälle', (r) => nf.format(r.n), true],
        ['Anteil', (r) => pf.format(Number(r.share)), true],
        ['kumuliert', (r) => pf.format(Number(r.cum_share)), true],
        ['Ø Dauer', (r) => hours(r.avg_seconds / 3600), true],
      ],
      null,
      'Jede Zeile ist eine Reihenfolge von Schritten, die tatsächlich vorkam. Viele Varianten mit je wenigen Fällen ' +
        'heissen: der Ablauf ist nicht einer, sondern mehrere.'
    ),
    dataSection(
      'Wo Fälle enden',
      endpoints.slice(0, 8),
      [
        ['letzter Schritt', (r) => escape(r.last_activity)],
        ['Fälle', (r) => nf.format(r.cases), true],
        ['Anteil', (r) => pf.format(Number(r.share)), true],
        ['Median bis dahin', (r) => hours(r.median_seconds / 3600), true],
      ],
      (r) => toStep(r, 'last_activity_key')
    ),
    automationSection(automation, summary),
    dataSection(
      'Was sich automatisieren liesse',
      candidates.slice(0, 10),
      [
        ['Schritt', (r) => escape(r.event_type)],
        ['Häufigkeit', (r) => nf.format(r.freq), true],
        ['von Hand', (r) => pf.format(Number(r.manual)), true],
        ['Vorhersagbarkeit', (r) => pf.format(1 - Number(r.outcome_entropy)), true],
      ],
      toStep,
      'Vorhersagbar heisst: nach diesem Schritt kommt fast immer derselbe nächste. Genau das macht einen Schritt ' +
        'mechanisch. Einen unvorhersagbaren zu automatisieren erzeugt eine Rückfrage-Warteschlange statt einer Ersparnis.'
    ),
    dataSection(
      'Übergaben zwischen Rollen',
      handovers.slice(0, 12),
      [
        ['von', (r) => escape(r.from_actor)],
        ['an', (r) => escape(r.to_actor)],
        ['Fälle', (r) => nf.format(r.cases), true],
        ['Übergaben', (r) => nf.format(r.handovers), true],
      ],
      null,
      'Rolle an Rolle, nicht Person an Person, und erst ab fünf gemeinsamen Fällen, damit eine Zufallsbegegnung nicht wie eine Zuständigkeit aussieht.'
    ),
  ].filter(Boolean);

  if (!blocks.length) return '';

  return `
    <details class="drill-more">
      <summary>Zahlen im Detail (${blocks.length} Auswertungen)</summary>
      ${blocks.join('')}
    </details>`;
}

/** One titled table, or nothing at all. An empty section is a promise the data does not keep. */
function dataSection(title, rows, columns, link = null, caveat = null) {
  if (!rows?.length) return '';
  const head = columns.map(([label, , numeric]) => `<th${numeric ? ' class="num"' : ''}>${escape(label)}</th>`).join('');
  const body = rows
    .map((row) => {
      const target = link?.(row);
      const cells = columns
        .map(([, render, numeric]) => `<td${numeric ? ' class="num"' : ''}>${render(row)}</td>`)
        .join('');
      return target
        ? `<tr class="clickable" data-drill="${escape(JSON.stringify(target))}">${cells}<td class="num"><span class="drill-go">›</span></td></tr>`
        : `<tr>${cells}<td></td></tr>`;
    })
    .join('');

  return `
    <h3>${escape(title)}</h3>
    ${caveat ? `<p class="caveat">${caveat}</p>` : ''}
    <table class="data"><thead><tr>${head}<th></th></tr></thead><tbody>${body}</tbody></table>`;
}

function automationSection(automation, summary) {
  if (!automation) return '';
  const straight = automation.straight_through_share;
  const manual = automation.manual_event_share;
  if (straight === null && manual === null) return '';

  return `
    <h3>Automatisierung</h3>
    <div class="stats-grid">
      <div class="stat-card"><h3>ohne Menschen durchgelaufen</h3>
        <div class="metric-value">${straight === null ? '—' : pf.format(Number(straight))}</div>
        <div class="metric-label">Fälle ganz ohne manuellen Schritt</div></div>
      <div class="stat-card"><h3>Schritte von Hand</h3>
        <div class="metric-value">${manual === null ? '—' : pf.format(Number(manual))}</div>
        <div class="metric-label">Anteil aller Ereignisse</div></div>
      ${
        summary?.outside_hours_share
          ? `<div class="stat-card"><h3>außerhalb der Arbeitszeit</h3>
               <div class="metric-value">${pf.format(Number(summary.outside_hours_share))}</div>
               <div class="metric-label">Anteil der Kalenderzeit ohne Arbeitstag</div></div>`
          : ''
      }
    </div>`;
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
    <p class="step-filters">
      <button type="button" class="btn-secondary" id="stepOnlyWith">Nur Fälle mit diesem Schritt</button>
      <button type="button" class="btn-secondary" id="stepOnlyWithout">Nur Fälle ohne ihn</button>
      <span class="hint">Gilt für das ganze Werkzeug, sichtbar oben als Filter.</span>
    </p>

    <h3>Wann dieser Schritt passiert</h3>
    <div id="drillStepChart"></div>

    <h3>Fälle, die durch diesen Schritt gelaufen sind</h3>
    <p class="caveat">Nicht die Fälle, die hier stehen geblieben sind, sondern alle, die ihn passiert haben. Ein Klick öffnet den Fall.</p>
    ${caseTable(rowsOf(cases))}`;

  drawActivityTrend(process, step);

  // The comparison, from the place where somebody is looking at one step: these cases, or the ones without it. Both go
  // through the same scope every panel honours, so the answer is consistent wherever the reader goes next.
  $('stepOnlyWith').addEventListener('click', () => setStepFilter('has', step, names.step));
  $('stepOnlyWithout').addEventListener('click', () => {
    setStepFilter('without', step, names.step);
    // Staying on a step while filtering it away would show an empty list and look broken. The process is where the
    // difference is visible.
    goTo({ process });
  });

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
  const status = await miningStatus();
  const host = $('drillDiagram');
  if (!host) return;

  const available = (status?.models ?? []).filter((model) => model.available);
  const entry = (status?.stats?.processes ?? []).find((row) => row.object_type === label);
  const file = entry?.files?.flow ?? entry?.files?.main ?? entry?.files?.frequency;
  const model = file ? available.find((candidate) => candidate.name === file) : null;

  if (!model) {
    noteGap('für diesen Ablauf ist noch kein Diagramm gerechnet');
    return;
  }

  $('drillDiagramSection').hidden = false;

  // Every rendering of THIS process, in one place. The diagram page used to own this switch, which meant leaving the
  // process to see it drawn differently and finding your way back afterwards.
  const renderings = [
    // First, because it is the only one that cannot be wrong about the process: box A was followed by box B this many
    // times. Everything after it generalises — invents gateways, inserts silent steps, drops rare paths.
    ['flow', 'Fluss'],
    ['flowTime', 'Fluss nach Dauer'],
    ['main', 'Hauptpfade'],
    ['frequency', 'Alle Pfade'],
    ['performance', 'Nach Dauer'],
    ['bpmn', 'BPMN'],
    ['petri', 'Petri-Netz'],
  ].filter(([key]) => entry?.files?.[key] && available.some((row) => row.name === entry.files[key]));

  if (renderings.length > 1) {
    $('drillDiagramTabs').innerHTML = renderings
      .map(
        ([key, label], index) =>
          `<button type="button" class="model-tab${index === 0 ? ' active' : ''}" data-file="${escape(
            entry.files[key]
          )}">${label}</button>`
      )
      .join('');
    $('drillDiagramTabs').hidden = false;

    for (const tab of $('drillDiagramTabs').querySelectorAll('.model-tab')) {
      tab.addEventListener('click', async () => {
        for (const other of $('drillDiagramTabs').querySelectorAll('.model-tab')) other.classList.remove('active');
        tab.classList.add('active');
        await paintDiagram(available.find((row) => row.name === tab.dataset.file), process, label);
      });
    }
  }

  await paintDiagram(model, process, label);
}

/**
 * Draws one rendering into the box.
 *
 * Inlined rather than shown as an image, because an image cannot be clicked: the boxes carry the same German labels the
 * tables use — we generate both — so each one can be matched back to a step and lead into it. A BPMN or a Petri net has
 * no such labels on every node, and then the picture is simply a picture.
 */
async function paintDiagram(model, process, label) {
  await paintInto(
    { host: 'drillDiagram', box: 'drillDiagramBox', hint: 'drillDiagramHint', tabs: 'drillDiagramTabs' },
    model,
    `Ablauf ${label ?? process}`,
    () => wireDiagram(process)
  );
}

/**
 * Draws a rendering into a picture box, reusing the box itself.
 *
 * The box is created once and only its contents are replaced afterwards. That is not a micro-optimisation: in the
 * preview the box has been MOVED into the overlay, so rebuilding the surrounding markup would create a second box in the
 * page while the reader keeps looking at the old one in front of them — switching renderings inside the preview did
 * nothing at all. Same element, new picture, and the viewer that is attached to it keeps its state.
 *
 * Inlined rather than shown as an image, because an image cannot be clicked: the boxes carry the same German labels the
 * tables use — we generate both — so each one can be matched back to a step and lead into it. A BPMN or a Petri net has
 * no such labels on every node, and then the picture is simply a picture.
 */
async function paintInto(ids, model, title, wire) {
  const host = $(ids.host);
  if (!host || !model) return;

  // getElementById directly: $() throws on a missing id, which is right at a call site that expects the element and
  // wrong here, where its absence is the signal to create it.
  if (!document.getElementById(ids.box)) {
    host.innerHTML = `<div class="model-view model-view-inline" id="${ids.box}"></div>
      <p class="hint" id="${ids.hint}"></p>`;
    // The rendering switch travels into the preview with the picture, so a reader can change the drawing without
    // leaving the large view first.
    attachViewer($(ids.box), { title, switcher: $(ids.tabs) });
  }

  let svg;
  try {
    const response = await fetch(model.url);
    svg = await response.text();
  } catch {
    $(ids.box).innerHTML = `<img class="model-image" src="${model.url}" alt="${escape(title)}" />`;
    return;
  }

  $(ids.box).innerHTML = svg;
  const clickable = wire();
  $(ids.hint).textContent = clickable
    ? 'Ein Klick auf einen Kasten führt zu den Fällen, die durch diesen Schritt gelaufen sind.'
    : 'In dieser Darstellung tragen nicht alle Knoten einen Schritt: runde Knoten sind Zustände, leere Kästen sind Verzweigungen des Miners.';
}

/**
 * Makes the boxes of a mined diagram lead somewhere.
 *
 * Graphviz writes one group per node with its text inside. The text is the label plus the count ("Beleg zugeordnet
 * E=335"), so the label is matched by prefix against the steps of this process — no id is available in the picture, and
 * inventing one on the miner side would mean the two sides agreeing on a format forever.
 */
function wireDiagram(process, boxId = 'drillDiagramBox', steps = null) {
  const box = $(boxId);
  if (!box) return false;

  // Either the steps of the process on screen, or a map handed in — the combined pictures draw steps from every process
  // at once, and each of those has to lead into its own.
  const list =
    steps ??
    [...document.querySelectorAll('#drillBody tr[data-step]')].map((row) => ({
      key: row.dataset.step,
      label: row.dataset.label,
    }));
  if (!list.length) return false;

  let wired = 0;

  for (const node of box.querySelectorAll('g.node')) {
    const text = [...node.querySelectorAll('text')].map((element) => element.textContent).join(' ');
    // Longest label first: "Freigabe erteilt" would otherwise swallow "Freigabe erteilt (Aufkäufer)".
    const match = list
      .filter((step) => step.label && text.includes(step.label))
      .sort((a, b) => b.label.length - a.label.length)[0];
    if (!match) continue;

    node.classList.add('node-clickable');
    wired++;
    node.addEventListener('click', () => {
      names.step = match.label;
      names.process = match.processLabel ?? names.process;
      goTo({ process: match.process ?? process, step: match.key });
    });
  }

  return wired > 0;
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

/**
 * What the miner found beyond the picture: broken rules and batching.
 *
 * Read from the same status the diagram comes from, because both are the product of one mining run and a page that
 * showed one without the other would suggest the second had nothing to say.
 */
async function showMinedInsights(label) {
  const status = await miningStatus();
  const stats = status?.stats;
  if (!stats) return;

  const rules = (stats.rules ?? []).find((row) => row.object_type === label);
  if (rules?.violations?.length) {
    $('drillRulesSection').hidden = false;
    $('drillRules').innerHTML = `
      <p class="reading">${nf.format(rules.cases)} von ${nf.format(rules.checked)} Fällen brechen mindestens eine Regel.</p>
      <table class="data">
        <thead><tr><th>Regel</th><th>betrifft</th><th class="num">Fälle</th></tr></thead>
        <tbody>
          ${rules.violations
            .map(
              (violation) =>
                `<tr><td>${escape(violation.regel)}</td><td>${escape(violation.betrifft)}</td>` +
                `<td class="num">${nf.format(violation.faelle)}</td></tr>`
            )
            .join('')}
        </tbody>
      </table>`;
  } else if (rules) {
    noteGap(`dieser Ablauf bricht keine der ${nf.format(rules.checked)} geprüften Regeln`);
  }

  const batches = (stats.batches ?? []).find((row) => row.object_type === label);
  if (batches?.batches?.length) {
    $('drillBatchSection').hidden = false;
    $('drillBatches').innerHTML = `
      <table class="data">
        <thead><tr><th>Schritt</th><th>Person</th><th class="num">Stapel</th><th>Art</th></tr></thead>
        <tbody>
          ${batches.batches
            .map(
              (row) =>
                `<tr><td>${escape(row.schritt)}</td><td>${escape(row.person)}</td>` +
                `<td class="num">${nf.format(row.stapel)}</td><td>${escape(row.art)}</td></tr>`
            )
            .join('')}
        </tbody>
      </table>`;
  }
}
