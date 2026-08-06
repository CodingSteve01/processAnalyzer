// One viewer for every picture in this tool.
//
// The diagram page had zoom, fit and reading size; the picture inside the drill-down had none, and a third place would
// have grown a third set of buttons. Same controls everywhere, same behaviour, and full screen for all of them — a mined
// graph is metres wide and a browser window is not.
//
// Graphviz draws black on white, so a picture keeps its own light surface in either theme. That is not a missing dark
// mode, it is the only way the lines stay readable; inverting them turns a process model into a negative.

const ZoomSteps = [1, 1.5, 2, 3, 4, 6, 8];

/**
 * Attaches the viewer to a container that holds one picture.
 *
 * @param {HTMLElement} container element containing (or to contain) the image or inline SVG
 * @param {{title?: string}} options
 * @returns {{setZoom: (factor: number) => void}} handle for callers that want to reset the view
 */
export function attachViewer(container, options = {}) {
  if (options.title) container.setAttribute('aria-label', options.title);
  const state = { zoom: 1, natural: false };
  const toolbar = document.createElement('div');
  toolbar.className = 'model-toolbar';
  toolbar.innerHTML = `
    <button type="button" class="model-zoom" data-act="fit">Einpassen</button>
    <button type="button" class="model-zoom" data-act="out" aria-label="Kleiner">−</button>
    <span class="model-zoom-level">100 %</span>
    <button type="button" class="model-zoom" data-act="in" aria-label="Größer">+</button>
    <button type="button" class="model-zoom" data-act="natural">Lesegröße</button>
    <button type="button" class="model-zoom" data-act="wide">Grosse Ansicht</button>
    <span class="spacer"></span>
    <span class="hint">Ziehen verschiebt, Strg + Mausrad zoomt.</span>`;
  container.parentNode.insertBefore(toolbar, container);

  const level = toolbar.querySelector('.model-zoom-level');

  const target = () => container.querySelector('img, svg');

  const box = () => container.closest('.model-view') ?? container;

  const apply = () => {
    const element = target();
    if (!element) return;

    if (state.natural) {
      element.style.width = 'auto';
      element.style.height = 'auto';
      element.style.maxWidth = 'none';
      element.style.maxHeight = 'none';
      level.textContent = 'Lesegröße';
      return;
    }

    // In the large view at 100 % the picture fills the box in both directions rather than only its width. "As large as
    // the container allows" is what somebody means by full size, and fitting the width alone leaves a wide graph tiny in
    // a tall frame.
    if (box().classList.contains('model-view-wide') && state.zoom === 1) {
      element.style.width = 'auto';
      element.style.height = 'auto';
      element.style.maxWidth = '100%';
      element.style.maxHeight = '100%';
      level.textContent = 'einpassend';
      return;
    }

    element.style.maxWidth = 'none';
    element.style.maxHeight = 'none';
    element.style.width = `${state.zoom * 100}%`;
    element.style.height = 'auto';
    level.textContent = `${Math.round(state.zoom * 100)} %`;
  };

  const step = (direction) => {
    if (state.natural) {
      state.natural = false;
      state.zoom = ZoomSteps[ZoomSteps.length - 1];
    }
    const index = ZoomSteps.indexOf(state.zoom);
    const next = index < 0 ? 0 : Math.min(Math.max(index + direction, 0), ZoomSteps.length - 1);
    state.zoom = ZoomSteps[next];
    apply();
  };

  toolbar.addEventListener('click', (event) => {
    const act = event.target.closest('[data-act]')?.dataset.act;
    if (!act) return;
    if (act === 'fit') {
      state.natural = false;
      state.zoom = 1;
      apply();
      container.scrollTo({ left: 0, top: 0 });
    }
    if (act === 'in') step(1);
    if (act === 'out') step(-1);
    if (act === 'natural') {
      state.natural = true;
      apply();
    }
    if (act === 'wide') {
      const wide = toggleWide(container);
      event.target.closest('[data-act]').textContent = wide ? 'Kleine Ansicht' : 'Grosse Ansicht';
      // Refit after the box changed width, or a fitted picture would stay at the old size inside a wider frame.
      if (!state.natural) apply();
    }
  });

  // Ctrl + wheel only: the page scrolls with the wheel, and a picture that swallows that gesture traps the reader.
  container.addEventListener(
    'wheel',
    (event) => {
      if (!event.ctrlKey) return;
      event.preventDefault();
      step(event.deltaY < 0 ? 1 : -1);
    },
    { passive: false }
  );

  let drag = null;
  container.addEventListener('pointerdown', (event) => {
    if (event.button !== 0) return;
    // Not on something that leads somewhere. Capturing the pointer here swallowed the click, so a box in the landscape
    // and a step in a mined diagram stopped opening — the drag gesture ate the one interaction the picture is for.
    if (event.target.closest('.lsnode, .node-clickable, a, button')) return;
    drag = { x: event.clientX, y: event.clientY, left: container.scrollLeft, top: container.scrollTop };
    container.setPointerCapture(event.pointerId);
    container.classList.add('dragging');
  });
  container.addEventListener('pointermove', (event) => {
    if (!drag) return;
    container.scrollLeft = drag.left - (event.clientX - drag.x);
    container.scrollTop = drag.top - (event.clientY - drag.y);
  });
  const end = () => {
    drag = null;
    container.classList.remove('dragging');
  };
  container.addEventListener('pointerup', end);
  container.addEventListener('pointercancel', end);

  apply();
  return { setZoom: (factor) => { state.natural = false; state.zoom = factor; apply(); } };
}

/**
 * Makes the picture as large as the application allows, without leaving it.
 *
 * Full screen was the obvious thing and the wrong one: it hides the tool around the picture, so the reader loses the
 * breadcrumbs, the scope line and the tabs — the context that says what they are looking at and how they got there. This
 * grows the box to the width of the window and most of its height, and everything else stays where it was.
 *
 * @returns {boolean} whether the picture is now in the large view
 */
function toggleWide(container) {
  const box = container.closest('.model-view') ?? container;
  return box.classList.toggle('model-view-wide');
}

// Esc leaves the large view, because that is what Esc means everywhere else.
document.addEventListener('keydown', (event) => {
  if (event.key !== 'Escape') return;
  for (const box of document.querySelectorAll('.model-view-wide')) {
    box.classList.remove('model-view-wide');
    const button = box.parentNode.querySelector('[data-act="wide"]');
    if (button) button.textContent = 'Grosse Ansicht';
  }
});
