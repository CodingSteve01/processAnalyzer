// A line chart in plain SVG.
//
// It replaces a charting library loaded from a CDN. That dependency did not just add weight: with no route to the
// internet the request never finished, it held one of the browser\'s six connections to this host, and the ten
// analytical requests behind it waited forever. The page stayed blank with no error — the worst possible failure
// for a tool whose whole job is to be trusted about what the data says.
//
// An internal tool has to work on a machine with no internet. Forty lines of SVG buy that outright.

const PADDING = { top: 16, right: 48, bottom: 28, left: 48 };

/**
 * Draws one or more series against a shared x axis.
 *
 * @param {SVGElement} host   element to draw into
 * @param {string[]} labels   x labels, one per point
 * @param {{name: string, colour: string, values: number[], axis?: "left"|"right"}[]} series
 */
export function drawLineChart(host, labels, series) {
  const width = host.clientWidth || 900;
  const height = host.clientHeight || 300;
  const plotWidth = width - PADDING.left - PADDING.right;
  const plotHeight = height - PADDING.top - PADDING.bottom;

  // Two scales: hours and percent share one picture but not one range, and forcing them together would flatten
  // whichever is smaller into a straight line at the bottom.
  const scaleFor = (axis) => {
    const values = series.filter((s) => (s.axis ?? 'left') === axis).flatMap((s) => s.values);
    const max = Math.max(1, ...values);
    return (value) => PADDING.top + plotHeight - (value / max) * plotHeight;
  };
  const left = scaleFor('left');
  const right = scaleFor('right');
  const x = (index) => PADDING.left + (labels.length === 1 ? plotWidth / 2 : (index / (labels.length - 1)) * plotWidth);

  const maxOf = (axis) => Math.max(1, ...series.filter((s) => (s.axis ?? 'left') === axis).flatMap((s) => s.values));

  const parts = [];
  parts.push(`<line class="axis" x1="${PADDING.left}" y1="${PADDING.top + plotHeight}" x2="${width - PADDING.right}" y2="${PADDING.top + plotHeight}" />`);

  for (const tick of [0, 0.5, 1]) {
    const y = PADDING.top + plotHeight - tick * plotHeight;
    parts.push(`<line class="grid" x1="${PADDING.left}" y1="${y}" x2="${width - PADDING.right}" y2="${y}" />`);
    parts.push(`<text class="tick" x="${PADDING.left - 8}" y="${y + 4}" text-anchor="end">${Math.round(tick * maxOf('left'))}</text>`);
    if (series.some((s) => s.axis === 'right'))
      parts.push(`<text class="tick" x="${width - PADDING.right + 8}" y="${y + 4}">${Math.round(tick * maxOf('right'))}</text>`);
  }

  // Every second label, so a long period does not turn the axis into a grey smear.
  labels.forEach((label, index) => {
    if (labels.length > 8 && index % 2 === 1) return;
    parts.push(`<text class="tick" x="${x(index)}" y="${height - 8}" text-anchor="middle">${label}</text>`);
  });

  for (const line of series) {
    const scale = (line.axis ?? 'left') === 'right' ? right : left;
    const points = line.values.map((value, index) => `${x(index)},${scale(value)}`).join(' ');
    parts.push(`<polyline class="series" points="${points}" style="stroke:${line.colour}" />`);
    line.values.forEach((value, index) =>
      parts.push(`<circle cx="${x(index)}" cy="${scale(value)}" r="3" style="fill:${line.colour}"><title>${line.name}: ${value}</title></circle>`)
    );
  }

  host.setAttribute('viewBox', `0 0 ${width} ${height}`);
  host.innerHTML = parts.join('');

  return series
    .map((line) => `<span class="legend-item"><span class="legend-dot" style="background:${line.colour}"></span>${line.name}</span>`)
    .join('');
}
