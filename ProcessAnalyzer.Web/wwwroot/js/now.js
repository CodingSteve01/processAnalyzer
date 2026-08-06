// What is on the desk right now.
//
// Everything else in this tool looks backwards: how long did it take, how often was it repeated, where did the time go.
// Useful in a workshop, useless at ten in the morning. This page looks at the present and at single cases that went
// wrong, and every row leads to the thing itself.
//
// Three questions, in the order somebody would ask them:
//   what is waiting     — which cases are standing at which step, and how long already
//   what went wrong     — cases whose timing is out of the ordinary, not "slower than average" but different
//   what nobody saw     — the same person submitted and decided
//
// None of it is new data. It is the same log, asked a question about now instead of about the average.

import { request } from './api.js';
import { periodQuery, periodLabel } from './period.js';
import { $, escape } from './utils.js';
import { drillTo } from './drill.js';

const nf = new Intl.NumberFormat('de-DE');
const dtf = new Intl.DateTimeFormat('de-DE', { dateStyle: 'short', timeStyle: 'short' });

const rowsOf = (response) => (Array.isArray(response) ? response : (response?.rows ?? []));
const moment = (value) => (value ? dtf.format(new Date(value)) : '—');
const hours = (value) => (value === null || value === undefined ? '—' : `${nf.format(value)} h`);

export async function renderNow() {
  const scope = periodQuery() ? `?${periodQuery().slice(1)}` : '';
  const [queue, anomalies, fourEyes] = await Promise.all([
    request(`/api/queue${scope}`),
    request(`/api/anomalies${scope}`),
    request(`/api/four-eyes${scope}`),
  ]);

  $('nowScope').textContent = periodLabel();
  renderQueue(rowsOf(queue));
  renderAnomalies(rowsOf(anomalies));
  renderFourEyes(rowsOf(fourEyes));
}

function renderQueue(rows) {
  if (!rows.length) {
    $('nowQueue').innerHTML = '<p class="empty">Nichts offen im gewählten Umfang.</p>';
    $('rQueue').hidden = true;
    return;
  }

  const cases = rows.reduce((sum, row) => sum + Number(row.faelle), 0);
  const worst = rows[0];
  $('rQueue').hidden = false;
  // The sentence names the biggest pile, because a table of forty rows does not say where to start.
  $('rQueue').textContent =
    `${nf.format(cases)} Fälle stehen offen. Der grösste Stapel liegt bei „${worst.steht_bei}" in ` +
    `${worst.prozess}: ${nf.format(worst.faelle)} Fälle, im Median seit ${hours(worst.median_stunden)}, ` +
    `der älteste seit ${hours(worst.aeltester_stunden)}.`;

  $('nowQueue').innerHTML = `
    <table class="data">
      <thead><tr><th>Ablauf</th><th>steht bei</th><th class="num">Fälle</th>
                 <th class="num">Median wartet</th><th class="num">ältester</th><th></th></tr></thead>
      <tbody>
        ${rows
          .map(
            (row) => `
          <tr class="clickable" data-process="${escape(row.prozess_key)}" data-step="${escape(row.steht_bei_key)}">
            <td>${escape(row.prozess)}</td>
            <td>${escape(row.steht_bei)}</td>
            <td class="num">${nf.format(row.faelle)}</td>
            <td class="num">${hours(row.median_stunden)}</td>
            <td class="num${Number(row.aeltester_stunden) > 48 ? ' attention' : ''}">${hours(row.aeltester_stunden)}</td>
            <td class="num"><span class="drill-go">Fälle ›</span></td>
          </tr>`
          )
          .join('')}
      </tbody>
    </table>`;

  for (const row of $('nowQueue').querySelectorAll('tr[data-step]')) {
    row.addEventListener('click', () => drillTo({ process: row.dataset.process, step: row.dataset.step }));
  }
}

function renderAnomalies(rows) {
  if (!rows.length) {
    $('nowAnomalies').innerHTML =
      '<p class="empty">Kein Fall weicht deutlich ab. Dafür braucht ein Übergang mindestens zwanzig Beobachtungen — darunter ist die Streuung Rauschen.</p>';
    return;
  }

  $('nowAnomalies').innerHTML = `
    <table class="data">
      <thead><tr><th>Ablauf</th><th>Nummer</th><th>Übergang</th><th class="num">gedauert</th>
                 <th class="num">üblich</th><th class="num">Abweichung</th><th>wann</th><th></th></tr></thead>
      <tbody>
        ${rows
          .map(
            (row) => `
          <tr class="clickable" data-process="${escape(row.prozess_key)}" data-step="${escape(row.bis_key)}"
              data-case="${escape(row.schluessel)}">
            <td>${escape(row.prozess)}</td>
            <td><strong>${escape(row.nummer ?? '')}</strong></td>
            <td>${escape(row.von)} → ${escape(row.bis)}</td>
            <td class="num">${hours(row.stunden)}</td>
            <td class="num">${hours(row.ueblich_stunden)}</td>
            <td class="num attention">${nf.format(row.sigma)} σ</td>
            <td>${moment(row.wann)}</td>
            <td class="num"><span class="drill-go">öffnen ›</span></td>
          </tr>`
          )
          .join('')}
      </tbody>
    </table>`;

  for (const row of $('nowAnomalies').querySelectorAll('tr[data-case]')) {
    row.addEventListener('click', () =>
      drillTo({ process: row.dataset.process, step: row.dataset.step, case: row.dataset.case })
    );
  }
}

function renderFourEyes(rows) {
  if (!rows.length) {
    $('nowFourEyes').innerHTML =
      '<p class="empty">Keine Freigabe von derselben Person, die den Fall begonnen hat.</p>';
    return;
  }

  $('nowFourEyes').innerHTML = `
    <table class="data">
      <thead><tr><th>Ablauf</th><th>Person</th><th>begonnen mit</th><th>freigegeben mit</th>
                 <th class="num">Fälle</th><th>zuletzt</th><th></th></tr></thead>
      <tbody>
        ${rows
          .map(
            (row) => `
          <tr class="clickable" data-process="${escape(row.prozess_key)}">
            <td>${escape(row.prozess)}</td>
            <td>${escape(row.person)}</td>
            <td>${escape(row.eingereicht_mit)}</td>
            <td>${escape(row.entschieden_mit)}</td>
            <td class="num">${nf.format(row.faelle)}</td>
            <td>${moment(row.zuletzt)}</td>
            <td class="num"><span class="drill-go">Ablauf ›</span></td>
          </tr>`
          )
          .join('')}
      </tbody>
    </table>`;

  for (const row of $('nowFourEyes').querySelectorAll('tr[data-process]')) {
    row.addEventListener('click', () => drillTo({ process: row.dataset.process }));
  }
}
