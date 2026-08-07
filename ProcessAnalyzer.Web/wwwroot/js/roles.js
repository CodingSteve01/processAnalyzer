// Who somebody IS, as opposed to what they did.
//
// Every other panel here is derived from the log, and the log cannot answer this one: many people work in the same
// module and do completely different things with it, so any figure grouped by module is an average over roles that have
// nothing to do with each other. The group directory does not answer it either — a department is not a role.
//
// The views people saved for themselves do. Two people on the same screen keep different columns and narrow by
// different properties, and that difference is the role. Nobody had to be asked and nothing had to be instrumented —
// they configured it themselves, and it is dated.
//
// Not scoped by the period, on purpose. A role is not something that happened between two dates, and narrowing it by the
// current window would make the same person look like somebody else in March.

import { request } from './api.js';
import { $, escape } from './utils.js';

const nf = new Intl.NumberFormat('de-DE');

const rowsOf = (response) => (Array.isArray(response) ? response : (response?.rows ?? []));

const day = (value) => (value ? new Date(value).toLocaleDateString('de-DE') : '—');

export async function renderRoleSignals() {
  const [vocabulary, profiles, screens] = await Promise.all([
    request('/api/roles/vocabulary').then(rowsOf),
    request('/api/roles/profiles').then(rowsOf),
    request('/api/roles/screens').then(rowsOf),
  ]);

  // Nothing to show is the normal state until the views have been pulled once. Saying so beats three empty tables that
  // read as "nobody configured anything".
  if (!vocabulary.length && !profiles.length) {
    for (const id of ['roleVocabulary', 'roleProfiles', 'roleScreens']) {
      const host = $(id);
      if (host) host.innerHTML = '';
    }
    const first = $('roleVocabulary');
    if (first)
      first.innerHTML =
        '<p class="empty">Noch keine gespeicherten Ansichten im Spiegel. Auf dem Datenstand einmal übernehmen.</p>';
    return;
  }

  renderReading(vocabulary, profiles);
  renderVocabulary(vocabulary);
  renderProfiles(profiles);
  renderScreens(screens);
}

/** What the three tables add up to, in one sentence — including the part nobody would notice by scrolling. */
function renderReading(vocabulary, profiles) {
  const reading = $('rRoleSignals');
  if (!reading) return;

  const specialists = profiles.filter((row) => Number(row.masken) === 1).length;
  const broadest = profiles.reduce((max, row) => Math.max(max, Number(row.masken ?? 0)), 0);
  // A property two people use is a role two people hold. That is the interesting end of this list, not the top.
  const narrow = vocabulary.filter((row) => Number(row.personen) <= 5).length;

  reading.hidden = false;
  reading.textContent =
    `${nf.format(profiles.length)} Personen haben sich Ansichten eingerichtet, mit ${nf.format(
      vocabulary.length
    )} verschiedenen Filtermerkmalen. ` +
    `${nf.format(specialists)} arbeiten auf genau einer Maske, die breiteste Person auf ${nf.format(broadest)}. ` +
    `${nf.format(narrow)} Merkmale nutzen fünf Leute oder weniger — das sind die engen Rollen, ` +
    'und die sind in jeder Zahl nach Modul unsichtbar.';
}

function renderVocabulary(rows) {
  const host = $('roleVocabulary');
  if (!host) return;

  host.innerHTML = `
    <table class="data">
      <thead><tr><th>Filtermerkmal</th><th class="num">Personen</th><th class="num">Masken</th>
                 <th class="num">Ansichten</th></tr></thead>
      <tbody>
        ${rows
          .slice(0, 60)
          .map(
            (row) => `
          <tr${Number(row.personen) <= 5 ? ' class="attention-row"' : ''}>
            <td><code>${escape(row.property)}</code></td>
            <td class="num">${nf.format(row.personen)}</td>
            <td class="num">${nf.format(row.masken)}</td>
            <td class="num">${nf.format(row.ansichten)}</td>
          </tr>`
          )
          .join('')}
      </tbody>
    </table>`;
}

function renderProfiles(rows) {
  const host = $('roleProfiles');
  if (!host) return;

  host.innerHTML = `
    <table class="data">
      <thead><tr><th>Person</th><th class="num">Masken</th><th class="num">Ansichten</th>
                 <th class="num">Filtermerkmale</th><th>zuletzt gepflegt</th></tr></thead>
      <tbody>
        ${rows
          .slice(0, 200)
          .map(
            (row) => `
          <tr>
            <td>${escape(row.person ?? row.actor_key)}</td>
            <td class="num">${nf.format(row.masken)}</td>
            <td class="num">${nf.format(row.ansichten)}</td>
            <td class="num">${nf.format(row.filtermerkmale)}</td>
            <td>${escape(day(row.zuletzt_gepflegt))}</td>
          </tr>`
          )
          .join('')}
      </tbody>
    </table>`;
}

function renderScreens(rows) {
  const host = $('roleScreens');
  if (!host) return;

  host.innerHTML = `
    <table class="data">
      <thead><tr><th>Maske</th><th class="num">Personen</th><th class="num">Ansichten</th>
                 <th class="num">Filter</th><th class="num">Spalten</th></tr></thead>
      <tbody>
        ${rows
          .slice(0, 100)
          .map(
            (row) => `
          <tr>
            <td><code>${escape(row.path)}</code></td>
            <td class="num">${nf.format(row.personen)}</td>
            <td class="num">${nf.format(row.ansichten)}</td>
            <td class="num">${nf.format(row.verschiedene_filter)}</td>
            <td class="num">${nf.format(row.verschiedene_spalten)}</td>
          </tr>`
          )
          .join('')}
      </tbody>
    </table>`;
}
