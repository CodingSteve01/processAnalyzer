// The report: everything worth reading, on paper.
//
// A screen is where somebody follows a thought. Paper is where they take it to a meeting, and a process analysis that
// cannot leave the browser stays an opinion of whoever was looking at it. So this collects the same figures the drill-
// down shows, for the scope that is set, in the order somebody would present them — and prints through the browser
// rather than through a rendering service, which keeps the tool self-contained and the output exactly what the reader
// saw.
//
// It states its own limits at the top: which window, which group, how far the mirror reaches, how much of the log
// carries a case at all. A report that hides that invites conclusions the data does not carry.

import { request } from './api.js';
import { periodQuery, periodLabel } from './period.js';
import { $, escape } from './utils.js';

const nf = new Intl.NumberFormat('de-DE');
const pf = new Intl.NumberFormat('de-DE', { style: 'percent', maximumFractionDigits: 0 });
const dtf = new Intl.DateTimeFormat('de-DE', { dateStyle: 'long', timeStyle: 'short' });

const hours = (value) =>
  value === null || value === undefined
    ? '—'
    : Number(value) >= 10
      ? `${nf.format(Math.round(value))} h`
      : `${Number(value).toFixed(1)} h`;

const rowsOf = (response) => (Array.isArray(response) ? response : (response?.rows ?? []));

/** How many processes go into the report. Beyond this it stops being read and starts being filed. */
const Processes = 6;

export async function renderReport() {
  $('reportBody').innerHTML = '<p class="empty">Bericht wird zusammengestellt …</p>';

  const [processes, health, inventory] = await Promise.all([
    request(`/api/discovery/processes${periodQuery() ? `?${periodQuery().slice(1)}` : ''}`),
    request('/health'),
    request('/api/inventory'),
  ]);

  const top = rowsOf(processes)
    .filter((row) => row.art === 'Ablauf')
    .slice(0, Processes);

  const sections = [];
  for (const process of top) {
    const type = process.technischer_typ;
    const scoped = (route) => request(`/api/${route}?objectType=${encodeURIComponent(type)}${periodQuery()}`);
    const [throughput, drivers, activities, rework] = await Promise.all([
      scoped('throughput'),
      scoped('drivers'),
      scoped('activities'),
      scoped('rework'),
    ]);
    sections.push({
      process,
      summary: rowsOf(throughput)[0],
      drivers: rowsOf(drivers),
      activities: rowsOf(activities),
      rework: rowsOf(rework),
      inventory: rowsOf(inventory).find((row) => row.object_type === type),
    });
  }

  $('reportBody').innerHTML = `
    ${cover(health, rowsOf(processes))}
    ${sections.map(section).join('')}
    ${provenance(health)}`;
}

function cover(health, processes) {
  const sync = health?.sync ?? {};
  return `
    <section class="report-block">
      <h2 class="report-title">Was im Unternehmen tatsächlich passiert</h2>
      <p class="report-sub">
        ${escape(periodLabel())} · zusammengestellt am ${dtf.format(new Date())}
      </p>
      <p>
        ${nf.format(processes.length)} Abläufe mit zusammen ${nf.format(
          processes.reduce((sum, row) => sum + Number(row.faelle ?? 0), 0)
        )} Fällen. Nichts davon ist vorher festgelegt worden: die Liste entsteht aus den Ereignissen selbst, ein Ablauf
        taucht auf, sobald die erste Tatsache ihn erwähnt.
      </p>
      <p class="report-limit">
        Gelesen wurden ${nf.format(sync.eventCount ?? 0)} Ereignisse mit ${nf.format(sync.objectCount ?? 0)}
        Objektbezügen. Ereignisse ohne Objektbezug können keinen Fall bilden und fehlen in jeder Auswertung unten.
        Die Dauern sind Arbeitszeit, wo die Arbeit in Bürozeiten fällt, und sonst Kalenderzeit; welche Uhr ein Ablauf
        benutzt, entscheidet sich daran, wo seine Zeit tatsächlich liegt.
      </p>
    </section>`;
}

function section({ process, summary, drivers, activities, rework, inventory }) {
  const all = inventory ? Number(inventory.objects) : null;
  const closed = Number(summary?.cases ?? 0);

  return `
    <section class="report-block">
      <h3>${escape(process.prozess)}</h3>
      <p>
        ${all === null ? '' : `${nf.format(all)} Fälle, davon ${nf.format(closed)} abgeschlossen. `}
        Beginnt mit „${escape(process.beginnt_mit ?? '—')}", endet mit „${escape(process.endet_mit ?? '—')}".
        ${nf.format(process.schritte)} Schritte je Fall, ${nf.format(process.automatisch_prozent)} % laufen ohne
        menschlichen Schritt. Beteiligt: ${escape(process.beteiligte ?? '—')}.
      </p>

      ${
        closed
          ? `<table class="data report-table">
               <tbody>
                 <tr><th>Median</th><td>${hours((summary.p50_seconds ?? 0) / 3600)}</td>
                     <th>p80</th><td>${hours((summary.p80_seconds ?? 0) / 3600)}</td>
                     <th>p95</th><td>${hours((summary.p95_seconds ?? 0) / 3600)}</td></tr>
               </tbody>
             </table>`
          : '<p class="report-limit">Noch kein Fall abgeschlossen, deshalb keine Durchlaufzeiten.</p>'
      }

      ${
        drivers.length
          ? `<h4>Woran es liegt</h4>
             <ul>${drivers
               .slice(0, 3)
               .map((driver) => {
                 const withHours = Number(driver.median_with_seconds) / 3600;
                 const withoutHours = Number(driver.median_without_seconds) / 3600;
                 return `<li>Fälle mit „${escape(driver.event_type)}" dauern ${hours(withHours)} statt ${hours(
                   withoutHours
                 )} (${nf.format(driver.with_cases)} Fälle). Darin stecken ${hours(
                   Number(driver.extra_seconds) / 3600
                 )} mehr als in den übrigen.</li>`;
               })
               .join('')}</ul>`
          : ''
      }

      ${
        rework.length
          ? `<h4>Doppelte Arbeit</h4>
             <ul>${rework
               .slice(0, 3)
               .map(
                 (repeat) =>
                   `<li>„${escape(repeat.event_type)}" passiert in ${nf.format(
                     repeat.rework_cases
                   )} Fällen mehr als einmal (${pf.format(Number(repeat.rework_rate))}), zusammen ${nf.format(
                     repeat.extra_executions
                   )} zusätzliche Ausführungen.</li>`
               )
               .join('')}</ul>`
          : ''
      }

      ${
        activities.length
          ? `<h4>Die Schritte</h4>
             <table class="data report-table">
               <thead><tr><th>Schritt</th><th class="num">wie oft</th><th class="num">Fälle</th>
                          <th class="num">von Hand</th></tr></thead>
               <tbody>
                 ${activities
                   .slice(0, 12)
                   .map(
                     (row) =>
                       `<tr><td>${escape(row.event_type)}</td><td class="num">${nf.format(
                         row.events
                       )}</td><td class="num">${nf.format(row.objects)}</td><td class="num">${
                         row.manual_share === null ? '—' : pf.format(Number(row.manual_share))
                       }</td></tr>`
                   )
                   .join('')}
               </tbody>
             </table>`
          : ''
      }
    </section>`;
}

function provenance(health) {
  const sync = health?.sync ?? {};
  return `
    <section class="report-block">
      <h3>Woher die Zahlen kommen</h3>
      <p class="report-limit">
        Gelesen aus dem Ereignis-Journal des Quellsystems, lesend, Stand Quell-Id ${nf.format(sync.watermark ?? 0)}.
        Der Spiegel ist eine Kopie: was hier steht, steht dort auch. Die Ableitungen darüber (Fälle, Dauern, Abläufe)
        werden aus dieser Kopie neu berechnet und nie erneut aus dem Quellsystem gelesen.
      </p>
      <p class="report-limit">
        Ein Ablauf gilt als abgeschlossen, wenn sein letzter Schritt ein Endschritt dieses Ablaufs ist, sonst nach
        einer Zeit ohne jeden Schritt. Laufende Fälle sind aus allen Dauern ausgenommen, weil sie eine Durchlaufzeit
        nach unten ziehen würden, die es noch nicht gibt.
      </p>
    </section>`;
}

export function initReport() {
  $('reportPrint').addEventListener('click', () => window.print());
}
