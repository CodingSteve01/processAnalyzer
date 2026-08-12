// What is left once everything about a single process moved under that process.
//
// This file used to hold a page of its own: a dropdown to pick a process, and eleven tables about it. The path in
// drill.js already knows which process the reader is looking at, so the dropdown was a second, competing answer to the
// same question, and the two could disagree. What remains here is the two things that are not about one process:
//
//   Menschen    roles, who does which step, who decides about whom, who works with whom
//   Datenstand  what we do not record yet, which calendar the durations use, what the words mean
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
import { renderActors } from './actors.js';
import { whenOpened } from './views.js';

const nf = new Intl.NumberFormat('de-DE');

/**
 * A duration in the unit it deserves.
 *
 * Releases happen minutes apart, and rounded to hours every one of them read "0,0 h", a column of zeroes that looks
 * like a broken measurement rather than a fast step.
 */
function duration(seconds) {
  if (seconds === null || seconds === undefined) return '—';
  const value = Number(seconds);
  if (value < 90) return `${Math.round(value)} s`;
  if (value < 5400) return `${Math.round(value / 60)} min`;
  if (value < 360000) return `${(value / 3600).toFixed(1)} h`;
  return `${nf.format(Math.round(value / 3600))} h`;
}

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
  // reader is asking about. The other is one click further, in the collaboration table below.
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

  // Loaded alongside: they belong to this screen, and the actor list is the only panel here that can be edited.
  renderActors();
  renderReleases();

  $('roleHandovers').innerHTML = table(roleHandovers, [
    { label: 'von', render: (r) => escape(r.von) },
    { label: 'an', render: (r) => escape(r.an) },
    { label: 'Fälle', numeric: true, render: (r) => nf.format(r.faelle) },
    { label: 'Übergaben', numeric: true, render: (r) => nf.format(r.uebergaben) },
  ]);
}

/**
 * The release ladder, with the people on it.
 *
 * The interesting rows are the ones a settings screen cannot show: a stage held by exactly one person, a stage that is
 * refused rather than granted, and two stages that arrive in either order, which means they are not a ladder at all.
 */
async function renderReleases() {
  const [stages, chain] = await Promise.all([
    request(`/api/discovery/release-stages${scopeQuery()}`),
    request(`/api/discovery/release-chain${scopeQuery()}`),
  ]);
  if (!stages || !chain) return;

  const alone = stages.filter((row) => row.eine_person);
  const refused = stages.reduce((sum, row) => sum + Number(row.verweigert ?? 0), 0);
  reading(
    'rReleaseStages',
    stages.length
      ? `${nf.format(stages.length)} Freigabestufen im Log. ${
          alone.length
            ? `${nf.format(alone.length)} davon hängen an einer einzigen Person: ${alone
                .slice(0, 3)
                .map((row) => `„${row.stufe}"`)
                .join(', ')}. `
            : 'Keine hängt an einer einzigen Person. '
        }${refused ? `${nf.format(refused)} Freigaben wurden verweigert.` : 'Keine Freigabe wurde verweigert.'}`
      : null
  );

  $('releaseStages').innerHTML = table(
    stages,
    [
      { label: 'Stufe', render: (r) => `<strong>${escape(r.stufe)}</strong>` },
      { label: 'Ablauf', render: (r) => escape(r.prozess) },
      { label: 'wer', render: (r) => escape(r.wer ?? '—') },
      { label: 'erteilt', numeric: true, render: (r) => nf.format(r.wie_oft) },
      {
        label: 'verweigert',
        numeric: true,
        render: (r) => (Number(r.verweigert) ? `<span class="attention">${nf.format(r.verweigert)}</span>` : '—'),
      },
      {
        label: 'Personen',
        numeric: true,
        render: (r) => (r.eine_person ? `<span class="attention">1</span>` : nf.format(r.personen)),
      },
      { label: 'Wartezeit davor', numeric: true, render: (r) => duration(r.wartezeit_sekunden) },
    ],
    (row) => (row.prozess_key && row.stufe_key ? { process: row.prozess_key, step: row.stufe_key } : null)
  );
  wireDrill('releaseStages');

  $('releaseChain').innerHTML = table(
    chain,
    [
      { label: 'nach', render: (r) => escape(r.von) },
      { label: 'kommt', render: (r) => escape(r.an) },
      { label: 'Ablauf', render: (r) => escape(r.prozess) },
      { label: 'wie oft', numeric: true, render: (r) => nf.format(r.wie_oft) },
      {
        label: 'dieselbe Person',
        numeric: true,
        render: (r) =>
          Number(r.dieselbe_person) ? `<span class="attention">${nf.format(r.dieselbe_person)}</span>` : '—',
      },
      { label: 'dazwischen', numeric: true, render: (r) => duration(r.wartezeit_sekunden) },
    ],
    (row) => (row.prozess_key && row.an_key ? { process: row.prozess_key, step: row.an_key } : null)
  );
  wireDrill('releaseChain');
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
