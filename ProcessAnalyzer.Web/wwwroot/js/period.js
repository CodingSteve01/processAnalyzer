// What every question is asked about: a window in time and, optionally, one group of people. One place, because a
// filter that some panels honour and others ignore is worse than none: the reader compares two numbers on the same
// screen and they answer different questions.
//
// Both scope whole CASES, not loose events. A case that began before the window, or whose other participants are not
// in the chosen group, would otherwise show a truncated lifecycle whose first step is whatever survived the
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
/** Cases that went through this step, and cases that never did. Both are keys, both are optional. */
let steps = { has: null, hasLabel: null, without: null, withoutLabel: null };
/**
 * What the case IS: one classification and, optionally, one of its values.
 *
 * A name without a value asks "classified at all", which is the question behind every coverage gap, so the value slot
 * is genuinely optional rather than defaulting to the first option.
 */
let property = { name: null, value: null };
let properties = [];
let onChange = () => {};

/** The query suffix for an analytical request. Empty when nothing is filtered, so an unfiltered call stays unchanged. */
export function periodQuery() {
  const { from, until } = resolve();
  const parts = [];
  if (from) parts.push(`from=${encodeURIComponent(from)}`);
  if (until) parts.push(`until=${encodeURIComponent(until)}`);
  if (group) parts.push(`group=${encodeURIComponent(group)}`);
  if (steps.has) parts.push(`hasStep=${encodeURIComponent(steps.has)}`);
  if (steps.without) parts.push(`withoutStep=${encodeURIComponent(steps.without)}`);
  if (property.name) parts.push(`property=${encodeURIComponent(property.name)}`);
  if (property.name && property.value) parts.push(`propertyValue=${encodeURIComponent(property.value)}`);
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
 * Narrows to cases that went through a step, or to those that never did.
 *
 * Two separate slots rather than a list, because these two are what a comparison needs and a stack of five conditions is
 * a query language nobody asked for. Setting one replaces it; passing null clears it.
 */
export function setStepFilter(kind, key, label) {
  if (kind === 'has') steps = { ...steps, has: key, hasLabel: label };
  if (kind === 'without') steps = { ...steps, without: key, withoutLabel: label };
  onChange();
  renderChips();
}

/** What is currently narrowing the view, as chips. */
export function activeFilters() {
  const chips = [];
  if (group) chips.push({ kind: 'group', label: `Gruppe: ${group}` });
  if (steps.has) chips.push({ kind: 'has', label: `mit „${steps.hasLabel ?? steps.has}"` });
  if (steps.without) chips.push({ kind: 'without', label: `ohne „${steps.withoutLabel ?? steps.without}"` });
  if (property.name) {
    chips.push({
      kind: 'property',
      // Without a value the filter says "carries this classification at all", and the chip has to say so, otherwise
      // the reader takes it for a value filter and cannot explain the case count.
      label: property.value
        ? `${property.name}: ${property.value}`
        : `${property.name}: überhaupt gesetzt`,
    });
  }
  return chips;
}

function clearFilter(kind) {
  if (kind === 'group') {
    group = '';
    const select = document.getElementById('scopeGroup');
    if (select) select.value = '';
  }
  if (kind === 'has') steps = { ...steps, has: null, hasLabel: null };
  if (kind === 'without') steps = { ...steps, without: null, withoutLabel: null };
  if (kind === 'property') {
    property = { name: null, value: null };
    const name = document.getElementById('scopeProperty');
    const value = document.getElementById('scopePropertyValue');
    if (name) name.value = '';
    if (value) fillPropertyValues(value, null);
  }
  onChange();
  renderChips();
}

/**
 * Draws the chips.
 *
 * A filter nobody can see is a filter nobody can check, and a filter nobody can remove is worse: the reader ends up
 * reloading the page to get out of a state they cannot find.
 */
function renderChips() {
  const host = document.getElementById('scopeChips');
  if (!host) return;

  const chips = activeFilters();
  host.innerHTML = chips
    .map(
      (chip) =>
        `<button type="button" class="chip" data-kind="${chip.kind}">${chip.label}<span class="chip-x">×</span></button>`
    )
    .join('');
  host.hidden = chips.length === 0;

  for (const button of host.querySelectorAll('.chip')) {
    button.addEventListener('click', () => clearFilter(button.dataset.kind));
  }
}

/**
 * Sets the window from somewhere else in the application, and tells everybody.
 *
 * A chart that cannot change the scope is a picture. Clicking a day, or dragging across three weeks, is the same act as
 * typing two dates, so it goes through the same state, the controls show it, and every panel follows.
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
  const extra = activeFilters()
    .filter((chip) => chip.kind !== 'group')
    .map((chip) => chip.label);
  const parts = [window];
  if (group) parts.push(`nur Fälle mit Beteiligung von „${group}"`);
  parts.push(...extra);
  return parts.join(', ');
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
    </label>
    <label class="scope">
      Merkmal
      <select id="scopeProperty"><option value="">ohne Merkmal</option></select>
    </label>
    <label class="scope">
      Wert
      <select id="scopePropertyValue" disabled><option value="">alle Werte</option></select>
    </label>`;

  const preset = container.querySelector('#periodPreset');
  const from = container.querySelector('#periodFrom');
  const until = container.querySelector('#periodUntil');
  const groupSelect = container.querySelector('#scopeGroup');
  const propertySelect = container.querySelector('#scopeProperty');
  const propertyValueSelect = container.querySelector('#scopePropertyValue');

  propertySelect.addEventListener('change', () => {
    // Changing the classification drops the old value: a value picked for one property, carried over to another, is a
    // filter that matches nothing, and an empty screen reads as missing data rather than as a stale selection.
    property = { name: propertySelect.value || null, value: null };
    fillPropertyValues(propertyValueSelect, property.name);
    onChange();
    renderChips();
  });

  propertyValueSelect.addEventListener('change', () => {
    property = { ...property, value: propertyValueSelect.value || null };
    onChange();
    renderChips();
  });

  loadProperties(propertySelect);

  groupSelect.addEventListener('change', () => {
    group = groupSelect.value;
    onChange();
    renderChips();
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
/**
 * Fills the classification select. Silent on failure for the same reason as the group list.
 *
 * The control stays hidden until there is something to choose: the source only started stating classifications with
 * the object-attribute change, so on a mirror that has none yet an empty select would promise a distinction the data
 * cannot make.
 */
async function loadProperties(select) {
  try {
    const response = await fetch('/api/properties', { headers: { Accept: 'application/json' } });
    if (!response.ok) return;
    properties = await response.json();
  } catch {
    return;
  }

  const names = [...new Set(properties.map((row) => row.name))].sort();
  if (names.length === 0) {
    for (const id of ['scopeProperty', 'scopePropertyValue']) {
      const label = document.getElementById(id)?.closest('label');
      if (label) label.hidden = true;
    }
    return;
  }

  for (const name of names) {
    const option = document.createElement('option');
    option.value = name;
    option.textContent = name;
    select.append(option);
  }
}

/**
 * Fills the value select for one classification, or empties and disables it for none.
 *
 * The case count travels with each value, because it is what tells a reader whether a value is the main path or an
 * exception, and because a value covering four cases out of nine thousand should not look like a serious filter.
 */
function fillPropertyValues(select, name) {
  const nf = new Intl.NumberFormat('de-DE');
  select.innerHTML = '<option value="">alle Werte</option>';
  select.disabled = !name;
  if (!name) return;

  for (const row of properties.filter((entry) => entry.name === name).sort((a, b) => b.faelle - a.faelle)) {
    const option = document.createElement('option');
    option.value = row.value;
    option.textContent = `${row.value} (${nf.format(row.faelle)})`;
    select.append(option);
  }
}

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
