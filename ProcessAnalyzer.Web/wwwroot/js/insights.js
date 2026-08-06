// What is left once everything about a single process moved under that process.
//
// This file used to hold a page of its own: a dropdown to pick a process, and eleven tables about it. The path in
// drill.js already knows which process the reader is looking at, so the dropdown was a second, competing answer to the
// same question — and the two could disagree. What remains here is the two things that are NOT about one process:
//
//   Menschen  — roles, who does which step, who decides about whom, who works with whom
//   Datenstand — what we do not record yet, which calendar the durations use, what the words mean
//
// Plus the orchestration: which screen builds when, and the two maintenance buttons.

import { request } from './api.js';
import { initPeriod, scopeQuery } from './period.js';
import { $, escape } from './utils.js';
import * as readings from './readings.js';
import { renderNaming, initNaming } from './naming.js';
import { renderDrill, initDrill, drillTo } from './drill.js';
import { renderReport, initReport } from './report.js';
import { renderNow } from './now.js';
import { whenOpened } from './views.js';

const nf = new Intl.NumberFormat('de-DE');

/** Puts a computed sentence above a panel. Empty text removes the element rather than leaving a blank line. */
function reading(id, text) {
  const element = document.getElementById(id);
  if (!element) return;
  element.textContent = text ?? '';
  element.hidden = !text;
}

/**
 * A table. With `link`, every row carries what the path needs and becomes clickable.
 *
 * A table that names a process, a step or a person and then leaves the reader there is a dead end. One optional function
 * per table hands the row over to the path instead.
 */
function table(rows, columns, link = null) {
  if (!rows.length) return '<p class="empty">Keine Daten im gewählten Umfang.</p>';
  const head = columns.map((c) => `<th${c.numeric ? ' class="num"' : ''}>${escape(c.label)}</th>`).join('');
  const body = rows
    .map((row) => {
      const cells = columns.map((c) => `<td${c.numeric ? ' class="num"' : ''}>${c.render(row)}</td>`).join('');
      const target = link?.(row);
      return target
        ? `<tr class="clickable" data-drill="${escape(JSON.stringify(target))}">${cells}<td class="num"><span class="drill-go">›</span></td></tr>`
        : `<tr>${cells}${link ? '<td></td>' : ''}</tr>`;
    })
    .join('');
  return `<table class="data"><thead><tr>${head}${link ? '<th></th>' : ''}</tr></thead><tbody>${body}</tbody></table>`;
}

/** Wires every row a table marked as drillable. One call per container, after it was written. */
function wireDrill(containerId) {
  for (const tr of $(containerId).querySelectorAll('tr[data-drill]')) {
    tr.addEventListener('click', () => drillTo(JSON.parse(tr.dataset.drill)));
  }
}

/** Who does the work, and who decides about it. */
async function renderPeople() {
  const responses = await Promise.all([
    request(`/api/discovery/roles${scopeQuery()}`),
    request(`/api/discovery/who-does-what${scopeQuery()}`),
    request(`/api/discovery/role-handovers${scopeQuery()}`),
    request(`/api/discovery/decisions${scopeQuery()}`),
    request(`/api/discovery/collaboration${scopeQuery()}`),
  ]);

  // A cancelled request resolves to null. Painting that as an empty table would tell the reader there is no data when
  // the truth is that nobody finished asking. Says which one, out loud: a silent return here is indistinguishable from
  // "the data is empty", and that ambiguity cost an hour once already.
  if (responses.some((response) => response === null)) {
    const routes = ['roles', 'who-does-what', 'role-handovers', 'decisions', 'collaboration'];
    console.warn('Menschen abgebrochen, keine Antwort von:', routes.filter((_, i) => responses[i] === null).join(', '));
    return;
  }
  const [roles, whoDoesWhat, roleHandovers, decisions, collaboration] = responses;

  reading('rRoles', readings.roles(roles));
  reading('rWhoDoesWhat', readings.whoDoesWhat(whoDoesWhat));
  reading('rRoleHandovers', readings.roleHandovers(roleHandovers));
  reading('rDecisions', readings.decisions(decisions));

  const named = decisions.some((row) => row.entschieden_von && !row.entschieden_von.startsWith('a:'));
  $('identityHint').textContent = named
    ? 'Namen sichtbar (SHOW_ACTOR_IDENTITY=true)'
    : 'Pseudonyme — Namen über SHOW_ACTOR_IDENTITY=true';

  $('roles').innerHTML = table(roles, [
    { label: 'Rolle', render: (r) => escape(r.rolle) },
    { label: 'Personen', numeric: true, render: (r) => nf.format(r.personen) },
    { label: 'ausgeschieden', numeric: true, render: (r) => nf.format(r.ausgeschieden) },
    { label: 'davon aktiv', numeric: true, render: (r) => nf.format(r.davon_aktiv) },
    { label: 'Schritte', numeric: true, render: (r) => nf.format(r.schritte) },
    { label: 'Anteil', numeric: true, render: (r) => `${Number(r.anteil_prozent).toFixed(1)} %` },
  ]);

  // A step belongs to a process before it belongs to a role, so the row leads to the step in the process where it is
  // most at home. Without that this table named a step and stopped.
  $('whoDoesWhat').innerHTML = table(
    whoDoesWhat.slice(0, 25),
    [
      { label: 'Schritt', render: (r) => escape(r.schritt) },
      { label: 'wird ausgeführt von', render: (r) => escape(r.wer) },
      { label: 'wie oft', numeric: true, render: (r) => nf.format(r.wie_oft) },
      { label: 'Anteil am Schritt', numeric: true, render: (r) => `${nf.format(r.anteil_am_schritt)} %` },
      // More than one role on the same step is either shared responsibility or an unclear boundary; both are worth
      // seeing, and neither is visible from the count alone.
      { label: 'Rollen', numeric: true, render: (r) => nf.format(r.rollen_am_schritt) },
    ],
    (row) => (row.prozess_key && row.schritt_key ? { process: row.prozess_key, step: row.schritt_key } : null)
  );
  wireDrill('whoDoesWhat');

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

  $('roleHandovers').innerHTML = table(roleHandovers, [
    { label: 'von', render: (r) => escape(r.von) },
    { label: 'an', render: (r) => escape(r.an) },
    { label: 'Fälle', numeric: true, render: (r) => nf.format(r.faelle) },
    { label: 'Übergaben', numeric: true, render: (r) => nf.format(r.uebergaben) },
  ]);
}

/** What the numbers rest on: which calendar, what is not instrumented, what the words mean. */
async function renderDataStandard() {
  const [coverage, calendar] = await Promise.all([
    request('/api/discovery/coverage'),
    request('/api/discovery/calendar'),
  ]);
  if (!coverage || !calendar) return;

  reading('rCalendar', readings.calendar(calendar[0]));
  reading('rCoverage', readings.coverage(coverage));

  $('coverage').innerHTML = table(coverage, [
    { label: 'Bezeichnung', render: (r) => escape(r.bezeichnung) },
    { label: 'technischer Typ', render: (r) => `<code>${escape(r.technischer_typ)}</code>` },
    { label: 'beobachtet', numeric: true, render: (r) => nf.format(r.beobachtet) },
  ]);

  await renderNaming();
}

export async function renderInsights() {
  // The path first: it is the default screen, and a reader who lands on it should not wait for panels they are not
  // looking at. The other two build when they are opened.
  await renderDrill();
}

export function initInsights() {
  // Re-renders on change: the screens share one window, so a partial refresh would leave the reader comparing a
  // filtered number with an unfiltered one.
  initPeriod($('periodBar'), () => {
    renderInsights();
    // And whatever else is on screen. Only that: rebuilding a hidden screen costs queries nobody is waiting for, and it
    // rebuilds itself when it is opened anyway.
    for (const [view, build] of [
      ['menschen', renderPeople],
      ['spiegel', renderDataStandard],
      ['jetzt', renderNow],
      ['bericht', renderReport],
    ]) {
      if (!document.querySelector(`.view[data-view="${view}"]`)?.hidden) build();
    }
  });

  initNaming();
  initDrill();
  initReport();
  whenOpened('bericht', () => renderReport());
  whenOpened('jetzt', () => renderNow());
  whenOpened('menschen', () => renderPeople());
  whenOpened('spiegel', () => renderDataStandard());

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
