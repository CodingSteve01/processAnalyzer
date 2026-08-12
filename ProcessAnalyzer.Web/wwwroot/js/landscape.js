// The process landscape: what this company does end to end.
//
// The mined pictures answer how ONE process runs, and the combined one is a wall of crossing edges. Neither answers the
// question somebody asks first, in front of a tool like this: what happens here, from one end to the other. That answer
// needs no mining, because an event touching two kinds of object is itself the handover between them, and there are
// them in the log.
//
// Drawn here rather than by the miner, for three reasons: it has to be clickable, it has to redraw when the scope
// changes, and twenty nodes do not need a layout engine. Columns come from how far a process sits from the start of a
// chain, which is computed from the direction of its handovers.

import { request } from './api.js';
import { periodQuery } from './period.js';
import { escape } from './utils.js';

const nf = new Intl.NumberFormat('de-DE');

const NODE_WIDTH = 168;
const NODE_HEIGHT = 46;
const COLUMN_GAP = 96;
const ROW_GAP = 20;
const PADDING = 16;

/**
 * Draws the landscape into a host element and reports what was drawn.
 *
 * @param {(processKey: string) => void} onPick what to do when somebody clicks a process
 */
export async function renderLandscape(host, readoutHost, onPick) {
  const rows = await request(`/api/discovery/landscape${periodQuery() ? `?${periodQuery().slice(1)}` : ''}`);
  const edges = Array.isArray(rows) ? rows : [];

  if (!edges.length) {
    host.innerHTML =
      '<p class="empty">Noch keine Übergaben im Spiegel. Eine Landschaft entsteht erst, wenn Ereignisse zwei Arten von Objekt gleichzeitig berühren.</p>';
    return;
  }

  const nodes = collectNodes(edges);
  rank(nodes, edges);
  place(nodes);

  host.innerHTML = draw(nodes, edges);

  const svg = host.querySelector('svg');
  for (const group of svg.querySelectorAll('g.lsnode')) {
    group.addEventListener('click', () => onPick(group.dataset.key));
    group.addEventListener('mouseenter', () => {
      const node = nodes.get(group.dataset.key);
      readoutHost.innerHTML =
        `<strong>${escape(node.label)}</strong>: ${nf.format(node.outgoing)} Übergaben hinaus, ` +
        `${nf.format(node.incoming)} herein. Klick öffnet den Ablauf.`;
    });
  }

  for (const path of svg.querySelectorAll('path.lsedge')) {
    path.addEventListener('mouseenter', () => {
      readoutHost.innerHTML = path.dataset.readout;
    });
  }

  const rest = `${nf.format(nodes.size)} Abläufe, ${nf.format(edges.length)} Übergaben. Eine Kante ist ein Ereignis, das beide Seiten gleichzeitig berührt; die Richtung kommt daraus, welcher Fall zuerst begonnen hat.`;
  readoutHost.innerHTML = rest;
  svg.addEventListener('mouseleave', () => {
    readoutHost.innerHTML = rest;
  });
}

function collectNodes(edges) {
  const nodes = new Map();
  const touch = (key, label) => {
    if (!nodes.has(key)) nodes.set(key, { key, label, rank: 0, incoming: 0, outgoing: 0 });
    return nodes.get(key);
  };

  for (const edge of edges) {
    touch(edge.von_key, edge.von).outgoing += Number(edge.ereignisse);
    touch(edge.an_key, edge.an).incoming += Number(edge.ereignisse);
  }
  return nodes;
}

/**
 * How far each process sits from the start of a chain.
 *
 * Longest path, relaxed as many times as there are nodes. Cycles exist in real data, such as an order that spawns a
 * tour whose papers come back to the order, and a strict topological sort would refuse to draw anything. Bounding the
 * relaxation resolves a cycle arbitrarily but always terminates, which is the right trade for a picture.
 */
function rank(nodes, edges) {
  for (let round = 0; round < nodes.size; round++) {
    let moved = false;
    for (const edge of edges) {
      const from = nodes.get(edge.von_key);
      const to = nodes.get(edge.an_key);
      if (to.rank < from.rank + 1) {
        to.rank = from.rank + 1;
        moved = true;
      }
    }
    if (!moved) break;
  }
}

function place(nodes) {
  const columns = new Map();
  for (const node of [...nodes.values()].sort((a, b) => b.outgoing + b.incoming - (a.outgoing + a.incoming))) {
    const column = columns.get(node.rank) ?? [];
    column.push(node);
    columns.set(node.rank, column);
  }

  for (const [rankIndex, column] of columns) {
    column.forEach((node, row) => {
      node.x = PADDING + rankIndex * (NODE_WIDTH + COLUMN_GAP);
      node.y = PADDING + row * (NODE_HEIGHT + ROW_GAP);
    });
  }
}

function draw(nodes, edges) {
  const all = [...nodes.values()];
  const width = Math.max(...all.map((node) => node.x)) + NODE_WIDTH + PADDING;
  const height = Math.max(...all.map((node) => node.y)) + NODE_HEIGHT + PADDING;
  const maxEvents = Math.max(...edges.map((edge) => Number(edge.ereignisse)));

  const paths = edges
    .map((edge) => {
      const from = nodes.get(edge.von_key);
      const to = nodes.get(edge.an_key);
      const x1 = from.x + NODE_WIDTH;
      const y1 = from.y + NODE_HEIGHT / 2;
      const x2 = to.x;
      const y2 = to.y + NODE_HEIGHT / 2;
      const bend = Math.max(24, Math.abs(x2 - x1) / 2);
      // Thickness by volume, on a square root so the busiest edge does not turn every other one into a hairline.
      const weight = 1 + Math.sqrt(Number(edge.ereignisse) / maxEvents) * 5;
      // A pair without a clear direction is drawn dashed rather than as a confident arrow.
      const unclear = Number(edge.richtung_klarheit) < 0.3;
      const readout =
        `<strong>${escape(edge.von)} → ${escape(edge.an)}</strong>: ${nf.format(edge.ereignisse)} gemeinsame ` +
        `Ereignisse in ${nf.format(edge.faelle)} Fällen` +
        (unclear ? '. Richtung unklar, die Reihenfolge wechselt von Fall zu Fall.' : '.');

      return `<path class="lsedge${unclear ? ' unclear' : ''}"
                    d="M ${x1} ${y1} C ${x1 + bend} ${y1}, ${x2 - bend} ${y2}, ${x2} ${y2}"
                    stroke-width="${weight.toFixed(1)}" data-readout="${escape(readout)}" />`;
    })
    .join('');

  const boxes = all
    .map(
      (node) => `
      <g class="lsnode" data-key="${escape(node.key)}" transform="translate(${node.x},${node.y})">
        <rect width="${NODE_WIDTH}" height="${NODE_HEIGHT}" rx="8" />
        <text x="${NODE_WIDTH / 2}" y="${NODE_HEIGHT / 2 + 5}" text-anchor="middle">${escape(
          node.label.length > 22 ? `${node.label.slice(0, 21)}…` : node.label
        )}</text>
      </g>`
    )
    .join('');

  return `
    <svg class="landscape" viewBox="0 0 ${width} ${height}" width="100%" height="${Math.min(height, 620)}"
         role="img" aria-label="Prozesslandschaft">
      <defs>
        <marker id="lsarrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
          <path d="M 0 0 L 10 5 L 0 10 z" />
        </marker>
      </defs>
      ${paths}
      ${boxes}
    </svg>`;
}
