// View switching.
//
// The sections all live in one document and are shown one at a time. No router library and no server round trip:
// the whole app is five screens over the same already-loaded data, and a reload on every tab would throw that away.

const DEFAULT_VIEW = 'prozesse';

function show(view) {
  document.querySelectorAll('.view').forEach((section) => {
    section.hidden = section.dataset.view !== view;
  });
  document.querySelectorAll('.viewtab').forEach((tab) => {
    const active = tab.dataset.view === view;
    tab.classList.toggle('active', active);
    tab.setAttribute('aria-current', active ? 'page' : 'false');
  });
  // The hash keeps a view linkable and survives the reload the browser does on its own. Only the first segment names
  // the view; what follows belongs to the view itself (the drill-down keeps its path there), so switching tabs must
  // not throw that away and arriving with a path must not be mistaken for an unknown view.
  if (currentView() !== view) window.location.hash = view;
  window.scrollTo({ top: 0 });

  const build = onDemand.get(view);
  if (build) build();
}

/** Wires the tabs and restores whatever view the URL asks for. */
/** Views that build themselves when they are opened, because building them costs more than a tab switch should. */
const onDemand = new Map();

/**
 * Registers a builder for a view. Called once per opening, not per render of everything else.
 *
 * If that view is already the one on screen, it builds right away: the tabs are wired before the builders are known, so
 * arriving straight at a deep link (a bookmark, a reload, a link somebody sent) used to open the screen and leave it
 * blank until the reader clicked away and back.
 */
export function whenOpened(view, build) {
  onDemand.set(view, build);
  const section = document.querySelector(`.view[data-view="${CSS.escape(view)}"]`);
  if (section && !section.hidden) build();
}

export function initViews() {
  document.querySelectorAll('.viewtab').forEach((tab) => {
    tab.addEventListener('click', () => show(tab.dataset.view));
  });

  window.addEventListener('hashchange', () => show(currentView()));
  show(currentView());
}

function currentView() {
  const wanted = window.location.hash.slice(1).split('/')[0];
  return document.querySelector(`.view[data-view="${CSS.escape(wanted)}"]`) ? wanted : DEFAULT_VIEW;
}
