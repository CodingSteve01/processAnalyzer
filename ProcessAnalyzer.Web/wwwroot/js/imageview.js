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
 * @param {{title?: string, switcher?: HTMLElement | null}} options `switcher` is a row of buttons that chooses which
 *   rendering is shown; it travels into the preview with the picture, because changing the drawing is exactly what
 *   somebody wants while looking at the large view.
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
    <button type="button" class="model-zoom" data-act="preview">Vorschau</button>
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

    // In the preview at 100 % the picture fills the window in both directions rather than only its width. Fitting the
    // width alone leaves a wide graph small in a tall frame, which is the opposite of what a preview is for.
    if (box().classList.contains('model-view-preview') && state.zoom === 1) {
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
    if (act === 'preview') openPreview(container, toolbar, options.title ?? 'Diagramm', apply, options.switcher);
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
 * Opens the picture as a preview over the whole window, with a way out.
 *
 * Three attempts at this. Full screen through the browser hid the application around the picture, so the reader lost the
 * breadcrumbs and the scope line. A wider box inside the page barely changed anything, because the panel was already
 * wide. What was actually meant is a preview: the picture over everything, as large as the window allows, and one
 * obvious way to close it.
 *
 * The viewer moves into the overlay rather than being rebuilt inside it — one instance, one zoom state, and the controls
 * are the same ones the reader was already using.
 */
function openPreview(container, toolbar, title, apply, switcher = null) {
  const overlay = document.createElement('div');
  overlay.className = 'preview-overlay';
  overlay.innerHTML = `
    <div class="preview-head">
      <span class="preview-title"></span>
      <span class="preview-switcher"></span>
      <span class="preview-tools"></span>
      <span class="spacer"></span>
      <button type="button" class="preview-close" aria-label="Vorschau schliessen">×</button>
    </div>
    <div class="preview-body"></div>`;
  overlay.querySelector('.preview-title').textContent = title;

  // Placeholders remember where the two elements came from, so closing puts them back exactly rather than appending them
  // to the end of whatever section happened to contain them.
  const toolbarSlot = document.createComment('viewer-toolbar');
  const containerSlot = document.createComment('viewer-container');
  toolbar.parentNode.insertBefore(toolbarSlot, toolbar);
  container.parentNode.insertBefore(containerSlot, container);

  // The switch comes along when there is one, and it keeps its listeners because it is moved rather than rebuilt.
  const switcherSlot = switcher ? document.createComment('viewer-switcher') : null;
  if (switcher && switcherSlot) {
    switcher.parentNode.insertBefore(switcherSlot, switcher);
    overlay.querySelector('.preview-switcher').append(switcher);
  }

  overlay.querySelector('.preview-tools').append(toolbar);
  overlay.querySelector('.preview-body').append(container);
  container.classList.add('model-view-preview');
  document.body.append(overlay);
  document.body.classList.add('preview-open');

  const close = () => {
    container.classList.remove('model-view-preview');
    if (switcher && switcherSlot) {
      switcherSlot.parentNode.insertBefore(switcher, switcherSlot);
      switcherSlot.remove();
    }
    toolbarSlot.parentNode.insertBefore(toolbar, toolbarSlot);
    containerSlot.parentNode.insertBefore(container, containerSlot);
    toolbarSlot.remove();
    containerSlot.remove();
    overlay.remove();
    document.body.classList.remove('preview-open');
    document.removeEventListener('keydown', onKey);
    apply();
  };

  const onKey = (event) => {
    if (event.key === 'Escape') close();
  };

  overlay.querySelector('.preview-close').addEventListener('click', close);
  // A click on the backdrop closes too, which is what every other preview in the world does. Clicks inside the picture
  // must not, or dragging it would close it.
  overlay.addEventListener('click', (event) => {
    if (event.target === overlay) close();
  });
  document.addEventListener('keydown', onKey);

  apply();
}


