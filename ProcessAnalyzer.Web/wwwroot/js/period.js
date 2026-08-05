// The window every question is asked about. One place, because a filter that some panels honour and others ignore is
// worse than none: the reader compares two numbers on the same screen and they answer different questions.
//
// It scopes whole CASES by their start, not loose events. A case that began before the window would otherwise show a
// truncated lifecycle — its first step would be whatever happened to fall inside, and every duration derived from
// that is wrong rather than merely partial.

const Presets = [
  { id: 'all', label: 'Alles', days: null },
  { id: '30', label: 'Letzte 30 Tage', days: 30 },
  { id: '90', label: 'Letzte 90 Tage', days: 90 },
  { id: '365', label: 'Letzte 12 Monate', days: 365 },
];

let state = { preset: 'all', from: '', until: '' };
let onChange = () => {};

/** The query suffix for an analytical request. Empty when nothing is filtered, so an unfiltered call stays unchanged. */
export function periodQuery() {
  const { from, until } = resolve();
  const parts = [];
  if (from) parts.push(`from=${encodeURIComponent(from)}`);
  if (until) parts.push(`until=${encodeURIComponent(until)}`);
  return parts.length ? `&${parts.join('&')}` : '';
}

/** What is currently applied, in German, for the caveat line under a panel heading. */
export function periodLabel() {
  const { from, until } = resolve();
  if (!from && !until) return 'über den gesamten Zeitraum';
  if (from && !until) return `ab ${formatDay(from)}`;
  if (!from && until) return `bis ${formatDay(until)}`;
  return `vom ${formatDay(from)} bis ${formatDay(until)}`;
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
    </label>`;

  const preset = container.querySelector('#periodPreset');
  const from = container.querySelector('#periodFrom');
  const until = container.querySelector('#periodUntil');

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
