// Who is a person and who is a program.
//
// Every other number in this tool is derived from the log. This one cannot be: the source reports the channel an event
// arrived through, and a channel is not an identity. A driver's tablet says 'device' and there is a person holding it; a
// nightly integration account says 'user' and there is nobody there. The log has no field for the difference and never
// will, because the difference is knowledge about the company.
//
// So this is a list and a control, like the vocabulary, and as there, what is set here wins over what was
// delivered and survives a restart. Unlike the vocabulary, it moves figures: "manual work" and "ran without a person"
// are counted from it, which is why a correction refreshes them immediately instead of at the next pull.

import { request } from './api.js';
import { $, escape } from './utils.js';

const nf = new Intl.NumberFormat('de-DE');

/** The kinds, in the order somebody would reach for them. */
const Kinds = [
  ['human', 'Mensch'],
  ['job', 'Automatischer Job'],
  ['service', 'Systemdienst'],
  ['device', 'Gerät ohne Bediener'],
  ['external', 'Fremdsystem'],
];

const rowsOf = (response) => (Array.isArray(response) ? response : (response?.rows ?? []));

const nameOf = (kind) => Kinds.find(([key]) => key === kind)?.[1] ?? kind;

export async function renderActors() {
  const rows = rowsOf(await request('/api/actors'));
  const host = $('actors');
  if (!host) return;

  if (!rows.length) {
    host.innerHTML = '<p class="empty">Noch kein Akteur im Spiegel.</p>';
    return;
  }

  // The sentence names what is actually in question: the accounts that arrived through more than one channel, and the
  // ones somebody has already corrected. A list of two hundred rows does not say where to start.
  const dual = rows.filter((row) => Number(row.kanaele) > 1);
  const corrected = rows.filter((row) => row.korrigiert);
  const steps = rows.reduce((sum, row) => sum + Number(row.schritte ?? 0), 0);
  const byProgram = rows
    .filter((row) => row.art !== 'human')
    .reduce((sum, row) => sum + Number(row.schritte ?? 0), 0);

  $('rActors').hidden = false;
  $('rActors').textContent =
    `${nf.format(rows.length)} Akteure, ${nf.format(steps)} Schritte, davon ${
      steps ? Math.round((byProgram / steps) * 100) : 0
    } % von Programmen. ` +
    `${nf.format(dual.length)} Kennungen kamen über mehr als einen Kanal — dort kann das Log allein nicht sagen, was sie sind. ` +
    (corrected.length ? `${nf.format(corrected.length)} sind hier korrigiert.` : 'Nichts ist korrigiert.');

  host.innerHTML = `
    <table class="data">
      <thead><tr><th>Akteur</th><th>Rolle</th><th class="num">Schritte</th><th class="num">Kanäle</th>
                 <th>aus dem Log</th><th>gilt als</th><th>Notiz</th><th></th></tr></thead>
      <tbody>
        ${rows
          .slice(0, 200)
          .map(
            (row) => `
          <tr data-key="${escape(row.schluessel)}"${Number(row.kanaele) > 1 ? ' class="attention-row"' : ''}>
            <td>${escape(row.person ?? row.schluessel)}</td>
            <td>${escape(row.rolle ?? '—')}</td>
            <td class="num">${nf.format(row.schritte)}</td>
            <td class="num">${nf.format(row.kanaele)}</td>
            <td>${escape(nameOf(row.art_aus_dem_log))}</td>
            <td>
              <select class="naming-input actor-kind">
                ${Kinds.map(
                  ([key, label]) =>
                    `<option value="${key}"${key === row.art ? ' selected' : ''}>${escape(label)}</option>`
                ).join('')}
              </select>
            </td>
            <td><input class="naming-input actor-note" value="${escape(row.notiz ?? '')}" placeholder="warum" /></td>
            <td class="num">
              ${row.korrigiert ? '<button type="button" class="btn-secondary" data-act="reset">zurück</button>' : ''}
            </td>
          </tr>`
          )
          .join('')}
      </tbody>
    </table>
    <p class="hint" id="actorHint"></p>`;

  wire(host);
}

function wire(host) {
  const save = async (tr) => {
    const kind = tr.querySelector('.actor-kind').value;
    const note = tr.querySelector('.actor-note').value.trim();
    $('actorHint').textContent = 'wird übernommen und neu gerechnet …';
    await request('/api/actors/kind', {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ actorKey: tr.dataset.key, kind, note: note || null }),
    });
    // Re-read rather than patch the row: the correction changes what the list says about itself (how many are
    // corrected, how much work is done by programs), and a stale sentence above a fresh table is worse than a redraw.
    await renderActors();
  };

  for (const select of host.querySelectorAll('.actor-kind')) {
    select.addEventListener('change', () => save(select.closest('tr')));
  }

  for (const note of host.querySelectorAll('.actor-note')) {
    // On leaving the field, not on every keystroke: each save refreshes two materialised views.
    note.addEventListener('change', () => save(note.closest('tr')));
  }

  for (const button of host.querySelectorAll('[data-act="reset"]')) {
    button.addEventListener('click', async () => {
      const tr = button.closest('tr');
      $('actorHint').textContent = 'wird zurückgesetzt …';
      await request('/api/actors/kind/reset', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ actorKey: tr.dataset.key }),
      });
      await renderActors();
    });
  }
}
