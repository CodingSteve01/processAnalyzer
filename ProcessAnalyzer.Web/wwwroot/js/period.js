// What every question is asked about: a window in time and, optionally, one group of people. One place, because a
// filter that some panels honour and others ignore is worse than none — the reader compares two numbers on the same
// screen and they answer different questions.
//
// Both scope whole CASES, not loose events. A case that began before the window, or whose other participants are not
// in the chosen group, would otherwise show a truncated lifecycle — its first step would be whatever survived the
// filter, and every duration derived from that is wrong rather than merely partial.

const Presets = [
  { id: 'all', label: 'Alles', days: null },
  { id: '30', label: 'Letzte 30 Tage', days: 30 },
  { id: '90', label: 'Letzte 90 Tage', days: 90 },
  { id: '365', label: 'Letzte 12 Monate', days: 365 },
];

let state = { preset: 'all', from: '', until: '' };
let group = '';
let groups = [];
let onChange = () => {};

/** The query suffix for an analytical request. Empty when nothing is filtered, so an unfiltered call stays unchanged. */
export function periodQuery() {
  const { from, until } = resolve();
  const parts = [];
  if (from) parts.push(`from=${encodeURIComponent(from)}`);
  if (until) parts.push(`until=${encodeURIComponent(until)}`);
  if (group) parts.push(`group=${encodeURIComponent(group)}`);
  return parts.length ? `&${parts.join('&')}` : '';
}

/** The same, for a request that has no other parameter — so the caller does not have to know about the leading "&". */
export function scopeQuery() {
  const suffix = periodQuery();
  return suffix ? `?${suffix.slice(1)}` : '';
}

/** The chosen group, or null for everybody. */
export function currentGroup() {
  return group || null;
}

/**
 * Sets the window from somewhere else in the application, and tells everybody.
 *
 * A chart that cannot change the scope is a picture. Clicking a day, or dragging across three weeks, is the same act as
 * typing two dates — so it goes through the same state, the controls show it, and every panel follows.
 */
export function setPeriod(from, until) {
  state = { preset: 'custom', from: from ?? '', until: until ?? '' };

  const preset = document.getElementById('periodPreset');
  const fromInput = document.getElementById('periodFrom');
  const untilInput = document.getElementById('periodUntil');
  if (fromInput) fromInput.value = state.from;
  if (untilInput) untilInput.value = state.until;
  // No preset matches a typed window, and leaving "Alles" selected would claim the opposite of what is applied.
  if (preset && !Presets.some((entry) => entry.id === state.preset)) preset.selectedIndex = 0;

  onChange();
}

/** What is currently applied, in German, for the caveat line under a panel heading. */
export function periodLabel() {
  const { from, until } = resolve();
  const window =
    !from && !until
      ? 'über den gesamten Zeitraum'
      : from && !until
        ? `ab ${formatDay(from)}`
        : !from && until
          ? `bis ${formatDay(until)}`
          : `vom ${formatDay(from)} bis ${formatDay(until)}`;

  // Named in the same breath as the window, because a figure that silently covers one group is the same trap as one
  // that silently covers one month.
  return group ? `${window}, nur Fälle mit Beteiligung von „${group}"` : window;
}

/** Explicit dates win over the preset — a typed date is a deliberate answer to the same question. */
function resolve() {
  if (state.from || state.until) return { from: state.from, until: state.until };

  const preset = Presets.find((p) => p.id === state.preset);
  if (!preset?.days) return { from: '', until: '' };

  const since = new Date(Date.now() - preset.days * 86400000);
  return { from: since.toISOString().slice(0, 10), until: '' };
}

function formatDay(iso) {
  const [y, m, d] = iso.slice(0, 10).split('-');
  return `${d}.${m}.${y}`;
}

/**
 * Renders the control into a container and calls back whenever the window changes.
 */
export function initPeriod(container, changed) {
  onChange = changed;
  container.innerHTML = `
    <label class="scope">
      Zeitraum
      <select id="periodPreset">
        ${Presets.map((p) => `<option value="${p.id}">${p.label}</option>`).join('')}
      </select>
    </label>
    <label class="scope">
      von
      <input type="date" id="periodFrom" />
    </label>
    <label class="scope">
      bis
      <input type="date" id="periodUntil" />
    </label>
    <label class="scope">
      Gruppe
      <select id="scopeGroup"><option value="">alle Gruppen</option></select>
    </label>`;

  const preset = container.querySelector('#periodPreset');
  const from = container.querySelector('#periodFrom');
  const until = container.querySelector('#periodUntil');
  const groupSelect = container.querySelector('#scopeGroup');

  groupSelect.addEventListener('change', () => {
    group = groupSelect.value;
    onChange();
  });

  // Loaded once and after the fact: the groups come out of the log, so the list cannot exist before the first pull,
  // and a filter that blocks the whole page until its options arrive is worse than one that fills in a moment later.
  loadGroups(groupSelect);

  preset.addEventListener('change', () => {
    // Picking a preset clears the typed dates, otherwise the preset would appear to do nothing and the reader would
    // be looking at a window they did not choose.
    state = { preset: preset.value, from: '', until: '' };
    from.value = '';
    until.value = '';
    onChange();
  });

  for (const input of [from, until]) {
    input.addEventListener('change', () => {
      state = { preset: preset.value, from: from.value, until: until.value };
      onChange();
    });
  }
}

/**
 * Fills the group select from the log. Failure is silent by design: without the list the filter stays on "all
 * groups", which is the state the page would have had anyway, and a red banner over a working dashboard because one
 * optional select box is empty would be the worse trade.
 */
async function loadGroups(select) {
  try {
    const response = await fetch('/api/groups', { headers: { Accept: 'application/json' } });
    if (!response.ok) return;
    groups = await response.json();
  } catch {
    return;
  }

  const nf = new Intl.NumberFormat('de-DE');
  for (const row of groups) {
    const option = document.createElement('option');
    option.value = row.gruppe;
    // The step count is what tells a reader which of forty groups is worth looking at.
    option.textContent = `${row.gruppe} (${nf.format(row.schritte)})`;
    select.append(option);
  }
}
