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
  const state = { zoom: 1, natural: false };
  const toolbar = document.createElement('div');
  toolbar.className = 'model-toolbar';
  toolbar.innerHTML = `
    <button type="button" class="model-zoom" data-act="fit">Einpassen</button>
    <button type="button" class="model-zoom" data-act="out" aria-label="Kleiner">−</button>
    <span class="model-zoom-level">100 %</span>
    <button type="button" class="model-zoom" data-act="in" aria-label="Größer">+</button>
    <button type="button" class="model-zoom" data-act="natural">Lesegröße</button>
    <button type="button" class="model-zoom" data-act="full">Vollbild</button>
    <span class="spacer"></span>
    <span class="hint">Ziehen verschiebt, Strg + Mausrad zoomt, Esc verlässt das Vollbild.</span>`;
  container.parentNode.insertBefore(toolbar, container);

  const level = toolbar.querySelector('.model-zoom-level');

  const target = () => container.querySelector('img, svg');

  const apply = () => {
    const element = target();
    if (!element) return;
    if (state.natural) {
      element.style.width = 'auto';
      element.style.maxWidth = 'none';
      level.textContent = 'Lesegröße';
      return;
    }
    element.style.maxWidth = 'none';
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
    if (act === 'full') toggleFull(container, options.title ?? 'Diagramm');
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
 * Full screen, through the browser's own API where it exists.
 *
 * Falls back to a class that fills the viewport: an internal tool has to work in a browser that refuses the request,
 * and "nothing happened when I clicked it" is the worst possible answer.
 */
function toggleFull(container, title) {
  const box = container.closest('.model-view') ?? container;

  if (document.fullscreenElement) {
    document.exitFullscreen?.();
    box.classList.remove('model-view-full');
    return;
  }

  box.setAttribute('aria-label', title);
  if (box.requestFullscreen) {
    box.requestFullscreen().catch(() => box.classList.add('model-view-full'));
    return;
  }
  box.classList.add('model-view-full');
}

// Esc leaves the fallback full screen too. The browser handles its own; this covers ours.
document.addEventListener('keydown', (event) => {
  if (event.key !== 'Escape') return;
  for (const box of document.querySelectorAll('.model-view-full')) box.classList.remove('model-view-full');
});
