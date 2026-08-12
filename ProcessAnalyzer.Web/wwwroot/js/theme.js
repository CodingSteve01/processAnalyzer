// Light or dark: the system decides, unless somebody says otherwise.
//
// Three states and not two. A plain toggle loses the connection to the system setting the moment it is touched once,
// the page then stays light through the evening because of a click that morning. So: follow the system, or override it,
// and a way back to following.
//
// The resolved theme is already on the root element when this runs; the inline script in index.html puts it there before
// the first paint. This module owns the switching from then on.

const Storage = 'pa-theme';

/** What the system currently prefers. */
const systemTheme = () => (window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark');

/** What the reader chose, or 'system' when they never did. */
function chosen() {
  try {
    const stored = localStorage.getItem(Storage);
    return stored === 'light' || stored === 'dark' ? stored : 'system';
  } catch {
    // Private mode, or storage disabled. Following the system is the right answer then, not an error message.
    return 'system';
  }
}

function store(mode) {
  try {
    if (mode === 'system') localStorage.removeItem(Storage);
    else localStorage.setItem(Storage, mode);
  } catch {
    // Nothing to do: the theme still applies to this page, it just will not survive a reload.
  }
}

const Faces = {
  system: { icon: '◐', title: 'Darstellung: wie das System. Klick: immer hell.' },
  light: { icon: '☀', title: 'Darstellung: immer hell. Klick: immer dunkel.' },
  dark: { icon: '☾', title: 'Darstellung: immer dunkel. Klick: wie das System.' },
};

const Next = { system: 'light', light: 'dark', dark: 'system' };

function paint(mode, button) {
  document.documentElement.dataset.theme = mode === 'system' ? systemTheme() : mode;
  if (!button) return;
  button.textContent = Faces[mode].icon;
  button.title = Faces[mode].title;
  button.setAttribute('aria-label', Faces[mode].title);
}

export function initTheme() {
  const button = document.getElementById('themeBtn');
  paint(chosen(), button);

  button?.addEventListener('click', () => {
    const mode = Next[chosen()];
    store(mode);
    paint(mode, button);
  });

  // While following the system, follow it live: somebody switching their desktop to dark in the evening should not have
  // to reload a page that is open in front of them.
  window.matchMedia('(prefers-color-scheme: light)').addEventListener('change', () => {
    if (chosen() === 'system') paint('system', button);
  });
}
