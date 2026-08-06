// The single case and the week-by-week trend.
//
// Everything else in this tool is an average over many cases. These two screens exist because an average nobody can
// check against one real example gets believed or dismissed as a whole, and because a snapshot cannot answer the
// question that follows every change: did it get better.

import { request } from './api.js';
import { periodQuery } from './period.js';
import { $, escape } from './utils.js';
import { drawLineChart } from './linechart.js';

const nf = new Intl.NumberFormat('de-DE');
const df = new Intl.DateTimeFormat('de-DE', { dateStyle: 'short', timeStyle: 'short' });
const dayFormat = new Intl.DateTimeFormat('de-DE', { day: '2-digit', month: '2-digit' });

const moment = (value) => (value ? df.format(new Date(value)) : '—');

let selectedCase = null;

function table(rows, columns, empty) {
  if (!rows.length) return `<p class="empty">${empty}</p>`;
  const head = columns.map((c) => `<th${c.numeric ? ' class="num"' : ''}>${escape(c.label)}</th>`).join('');
  const body = rows
    .map((row) => {
      const cells = columns.map((c) => `<td${c.numeric ? ' class="num"' : ''}>${c.render(row)}</td>`).join('');
      return `<tr${row.__key ? ` data-case="${escape(row.__key)}"` : ''}>${cells}</tr>`;
    })
    .join('');
  return `<table class="data"><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>`;
}

/** The list of cases. Clicking one opens its timeline next to it. */
export async function renderCases(objectType) {
  if (!objectType) return;

  const search = $('caseSearch').value.trim();
  const response = await request(
    `/api/cases?objectType=${encodeURIComponent(objectType)}&search=${encodeURIComponent(search)}${periodQuery()}`
  );
  if (!response) return;
  const rows = (response.rows ?? []).map((row) => ({ ...row, __key: row.schluessel }));

  $('caseHint').textContent = `${nf.format(rows.length)} Fälle${rows.length === 200 ? ' (Anzeige begrenzt)' : ''}`;
  $('caseList').innerHTML = table(
    rows,
    [
      { label: 'Nummer', render: (r) => `<strong>${escape(r.nummer)}</strong>` },
      { label: 'steht bei', render: (r) => escape(r.steht_bei ?? '—') },
      { label: 'Schritte', numeric: true, render: (r) => nf.format(r.schritte) },
      { label: 'Dauer', numeric: true, render: (r) => `${nf.format(r.dauer_stunden ?? 0)} h` },
      { label: 'zuletzt', render: (r) => moment(r.letzter_schritt) },
    ],
    'Keine Fälle gefunden.'
  );

  $('caseList')
    .querySelectorAll('tr[data-case]')
    .forEach((tr) => tr.addEventListener('click', () => openCase(tr.dataset.case)));

  // Open the first one straight away: an empty right-hand pane makes the screen look broken rather than ready.
  if (rows.length) await openCase(selectedCase && rows.some((r) => r.__key === selectedCase) ? selectedCase : rows[0].__key);
  else $('caseTimeline').innerHTML = '';
}

async function openCase(objectId) {
  selectedCase = objectId;
  $('caseList')
    .querySelectorAll('tr[data-case]')
    .forEach((tr) => tr.classList.toggle('selected', tr.dataset.case === objectId));

  const response = await request(`/api/case/${encodeURIComponent(objectId)}`);
  if (!response) return;
  const steps = response.rows ?? [];

  $('caseTimeline').innerHTML =
    `<h3>${escape(objectId.split(':')[1] ?? objectId)}</h3>` +
    table(
      steps,
      [
        { label: '#', numeric: true, render: (r) => r.schritt },
        { label: 'Was', render: (r) => escape(r.was) },
        { label: 'Wann', render: (r) => moment(r.wann) },
        { label: 'Wer', render: (r) => escape(r.wer ?? '—') },
        // The gap before the step, not the timestamp: the reader should not have to do the subtraction, and the
        // whole question is where the time went.
        { label: 'davor', numeric: true, render: (r) => (r.wartezeit_stunden === null ? '—' : `${nf.format(r.wartezeit_stunden)} h`) },
        { label: 'zusammen mit', render: (r) => escape(r.auch_beteiligt ?? '—') },
      ],
      'Keine Schritte.'
    );
}

/** The trend. A line chart plus the numbers, because a chart alone cannot be copied into a mail. */
export async function renderTrend(objectType) {
  if (!objectType) return;
  const response = await request(`/api/trend?objectType=${encodeURIComponent(objectType)}${periodQuery()}`);
  if (!response) return;
  const rows = response.rows ?? [];

  if (!rows.length) {
    $('trendTable').innerHTML = '<p class="empty">Noch keine abgeschlossenen Fälle.</p>';
    return;
  }

  drawTrend(rows);
  $('trendTable').innerHTML = table(
    [...rows].reverse(),
    [
      { label: 'Woche ab', render: (r) => dayFormat.format(new Date(r.woche)) },
      { label: 'Fälle', numeric: true, render: (r) => nf.format(r.faelle) },
      { label: 'Median', numeric: true, render: (r) => `${nf.format(r.p50_stunden)} h` },
      { label: 'p95', numeric: true, render: (r) => `${nf.format(r.p95_stunden)} h` },
      { label: 'Rückläufer', numeric: true, render: (r) => `${nf.format(r.ruecklaeufer_prozent)} %` },
      { label: 'automatisch', numeric: true, render: (r) => `${nf.format(r.automatisch_prozent)} %` },
    ],
    ''
  );

  reading(rows);
}

/** Says which way it is going, in one sentence, by comparing the last three weeks to the three before them. */
function reading(rows) {
  const element = $('rTrend');
  if (rows.length < 4) {
    element.textContent = `Erst ${rows.length} Wochen im Log — für eine Aussage über die Richtung zu wenig.`;
    element.hidden = false;
    return;
  }

  const mean = (list, key) => list.reduce((sum, r) => sum + Number(r[key]), 0) / list.length;
  const recent = rows.slice(-3);
  const earlier = rows.slice(-6, -3);
  const change = mean(recent, 'p50_stunden') - mean(earlier, 'p50_stunden');
  const direction = Math.abs(change) < 1 ? 'unverändert' : change < 0 ? 'schneller' : 'langsamer';

  element.textContent =
    `Die letzten drei Wochen liegen im Median bei ${nf.format(Math.round(mean(recent, 'p50_stunden')))} Stunden, ` +
    `die drei davor bei ${nf.format(Math.round(mean(earlier, 'p50_stunden')))} — also ${direction}` +
    (direction === 'unverändert' ? '. ' : ` um ${nf.format(Math.abs(Math.round(change)))} Stunden. `) +
    `Rückläuferquote zuletzt ${nf.format(mean(recent, 'ruecklaeufer_prozent').toFixed(1))} %.`;
  element.hidden = false;
}

function drawTrend(rows) {
  const legend = drawLineChart(
    $('trendChart'),
    rows.map((r) => dayFormat.format(new Date(r.woche))),
    [
      { name: 'Median (h)', colour: '#58a6ff', values: rows.map((r) => Number(r.p50_stunden)) },
      { name: 'p95 (h)', colour: '#d29922', values: rows.map((r) => Number(r.p95_stunden)) },
      { name: 'Rückläufer (%)', colour: '#f85149', values: rows.map((r) => Number(r.ruecklaeufer_prozent)), axis: 'right' },
    ]
  );
  $('trendLegend').innerHTML = legend;
}

export function initCases(onSearch) {
  let timer = null;
  $('caseSearch').addEventListener('input', () => {
    clearTimeout(timer);
    timer = setTimeout(onSearch, 250);
  });
}
