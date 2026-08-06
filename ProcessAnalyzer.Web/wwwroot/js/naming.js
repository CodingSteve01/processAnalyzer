// Naming things, in the tool.
//
// A type without a word is not a cosmetic gap: an unnamed activity is an unexplained one, and the whole claim of this
// tool is that somebody who has never seen the process can read what happened. Until now the only way to write a word
// down was a file on a server plus a restart, which means in practice that the word was never written down.
//
// So this list is the work itself: what is unnamed, most frequent first, with the field to name it right there. What is
// typed here beats the vocabulary file and survives a restart, and it can be exported back into the deployment.

import { request } from './api.js';
import { $, escape } from './utils.js';

const nf = new Intl.NumberFormat('de-DE');

const KindNames = {
  event: 'Ereignis',
  object: 'Prozess',
  entity: 'Entität',
  verb: 'Verb',
  discriminator: 'Unterscheidung',
  qualifier: 'Rolle im Fall',
};

/** What the reader is looking at when a kind needs explaining. */
const KindHints = {
  entity: 'Ein Substantiv im Singular. Eine Zeile benennt alle vier Verben: angelegt, geändert, gelöscht, kopiert.',
  object: 'Plural, weil Bildschirme davon zählen: „Aufträge", nicht „Auftrag".',
  discriminator: 'Was einen Schritt vom nächsten unterscheidet, etwa die Rolle oder die Aktionsart.',
};

export async function renderNaming() {
  const [gaps, labels] = await Promise.all([
    request('/api/vocabulary/gaps'),
    request('/api/vocabulary/labels'),
  ]);

  renderGaps(gaps);
  renderOverrides(labels.filter((row) => row.herkunft === 'ui'));

  const total = labels.length;
  $('namingSummary').textContent = gaps.length
    ? `${nf.format(gaps.length)} ohne Namen, ${nf.format(total)} benannt`
    : `alles benannt (${nf.format(total)} Begriffe)`;
}

function renderGaps(gaps) {
  if (!gaps.length) {
    $('namingGaps').innerHTML =
      '<p class="empty">Jeder Typ, der im Log vorkommt, hat einen deutschen Namen. Das ist der Zustand, in dem kein Bildschirm einen technischen Schlüssel zeigt.</p>';
    return;
  }

  const rows = gaps
    .map(
      (gap) => `
      <tr data-kind="${escape(gap.kind)}" data-type="${escape(gap.technischer_typ)}">
        <td>${escape(KindNames[gap.kind] ?? gap.kind)}</td>
        <td><code>${escape(gap.technischer_typ)}</code></td>
        <td class="num">${nf.format(gap.beobachtet)}</td>
        <td><input class="naming-input" type="text" placeholder="${escape(placeholderFor(gap.kind))}" /></td>
        <td><input class="naming-input naming-hint" type="text" placeholder="Erklärung (optional)" /></td>
        <td><button type="button" class="btn-secondary naming-save">Übernehmen</button></td>
      </tr>`
    )
    .join('');

  $('namingGaps').innerHTML = `
    <table class="data">
      <thead>
        <tr><th>Art</th><th>technischer Typ</th><th class="num">beobachtet</th>
            <th>Bezeichnung</th><th>Erklärung</th><th></th></tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>`;

  for (const button of $('namingGaps').querySelectorAll('.naming-save')) {
    button.addEventListener('click', () => save(button.closest('tr'), button));
  }

  // Enter saves the row it was typed in. Naming forty types with the mouse is how a list like this stops being used.
  for (const input of $('namingGaps').querySelectorAll('.naming-input')) {
    input.addEventListener('keydown', (event) => {
      if (event.key !== 'Enter') return;
      const row = input.closest('tr');
      save(row, row.querySelector('.naming-save'));
    });
  }
}

function placeholderFor(kind) {
  return KindHints[kind] ? KindHints[kind].split('.')[0] : 'Bezeichnung auf Deutsch';
}

async function save(row, button) {
  const label = row.querySelector('.naming-input').value.trim();
  const hint = row.querySelector('.naming-hint').value.trim();
  if (!label) {
    row.querySelector('.naming-input').focus();
    return;
  }

  button.disabled = true;
  button.textContent = 'speichert …';
  try {
    await request('/api/vocabulary/label', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ kind: row.dataset.kind, typeName: row.dataset.type, label, hint }),
    });
    // The row goes, because it is no longer a gap. The screens read the same table, so the name is live on the next
    // panel render — no restart, no deployment.
    row.remove();
    await renderNaming();
  } catch (error) {
    button.disabled = false;
    button.textContent = 'Übernehmen';
    row.querySelector('.naming-input').setAttribute('title', String(error.message ?? error));
    row.classList.add('naming-failed');
  }
}

function renderOverrides(overrides) {
  if (!overrides.length) {
    $('namingOverrides').innerHTML =
      '<p class="empty">Bisher wurde nichts hier benannt — alle Namen kommen aus dem mitgelieferten Vokabular.</p>';
    return;
  }

  const rows = overrides
    .map(
      (row) => `
      <tr data-kind="${escape(row.kind)}" data-type="${escape(row.technischer_typ)}">
        <td>${escape(KindNames[row.kind] ?? row.kind)}</td>
        <td><code>${escape(row.technischer_typ)}</code></td>
        <td>${escape(row.bezeichnung)}</td>
        <td>${row.bezeichnung_aus_datei ? escape(row.bezeichnung_aus_datei) : '<span class="hint">nicht im Vokabular</span>'}</td>
        <td><button type="button" class="btn-secondary naming-reset">zurücksetzen</button></td>
      </tr>`
    )
    .join('');

  $('namingOverrides').innerHTML = `
    <table class="data">
      <thead><tr><th>Art</th><th>technischer Typ</th><th>hier benannt</th><th>im Vokabular</th><th></th></tr></thead>
      <tbody>${rows}</tbody>
    </table>`;

  for (const button of $('namingOverrides').querySelectorAll('.naming-reset')) {
    button.addEventListener('click', async () => {
      const row = button.closest('tr');
      button.disabled = true;
      await request('/api/vocabulary/label/reset', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ kind: row.dataset.kind, typeName: row.dataset.type }),
      });
      await renderNaming();
    });
  }
}

export function initNaming() {
  $('namingExport').addEventListener('click', () => {
    // A plain download of the file the vocabulary is read from, so what was named here can be committed rather than
    // living in one database until somebody rebuilds the container.
    window.location.href = '/api/vocabulary/export';
  });
}
