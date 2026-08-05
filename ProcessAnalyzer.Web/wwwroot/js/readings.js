// What the numbers mean.
//
// A table of percentiles is not an insight, it is raw material. Every panel therefore carries one sentence derived
// from its own data, saying what the figures amount to and what would follow from it. The sentences are computed,
// never canned: a reading that stays the same when the data changes is decoration.

const nf = new Intl.NumberFormat('de-DE');

const h = (seconds) => `${nf.format(Math.round(Number(seconds ?? 0) / 3600))} Stunden`;

/** German counts need the singular. "1 Schritte" is the tell that a sentence was assembled, not written. */
const count = (n, singular, plural) => `${nf.format(n)} ${n === 1 ? singular : plural}`;
const pct = (share) => `${nf.format(Math.round(Number(share ?? 0) * 100))} %`;

/** Throughput: the spread matters more than the middle, because that is what nobody can plan around. */
export function throughput(row) {
  if (!row?.cases) return null;
  const ratio = Number(row.p95_seconds) / Math.max(1, Number(row.p50_seconds));
  const spread =
    ratio >= 3
      ? `Der langsamste Zwanzigstel-Fall braucht ${ratio.toFixed(1)}-mal so lang wie der mittlere — die Streuung ist das Problem, nicht die Geschwindigkeit.`
      : `Die Fälle liegen dicht beieinander (p95 nur ${ratio.toFixed(1)}-mal p50), der Ablauf ist also berechenbar.`;
  const outside =
    Number(row.outside_hours_share) > 0.5
      ? ` ${pct(row.outside_hours_share)} der Kalenderzeit fällt in Zeiten, in denen niemand arbeiten konnte — diese Wartezeit lässt sich nur durch früheren Anstoß verkürzen, nicht durch schnelleres Arbeiten.`
      : '';
  return `Die Hälfte der ${nf.format(row.cases)} Fälle ist in ${h(row.p50_seconds)} durch. ${spread}${outside}`;
}

/** Transitions: name the single edge that consumes the most calendar time, and what share of the whole that is. */
export function transitions(rows) {
  if (!rows?.length) return null;
  const total = rows.reduce((sum, r) => sum + Number(r.total_seconds ?? 0), 0);
  const top = rows[0];
  const share = total > 0 ? Number(top.total_seconds) / total : 0;
  return (
    `Der Übergang „${top.from_activity} → ${top.to_activity}" verbraucht ${h(top.total_seconds)} und damit ` +
    `${pct(share)} der gesamten erfassten Zeit — bei ${nf.format(top.n)} Fällen und ${h(top.median_seconds)} im Mittel. ` +
    `Hier liegt der größte Hebel, nicht bei der langsamsten Einzelkante.`
  );
}

/** Rework: repeats are unambiguous waste; negative outcomes are the detours that actually cost. */
export function rework(repeats, negatives) {
  const parts = [];
  if (repeats?.length) {
    const worst = repeats[0];
    parts.push(
      `„${worst.event_type}" passiert bei ${pct(worst.rework_rate)} der Fälle mehr als einmal, ` +
        `insgesamt ${nf.format(worst.extra_executions)} Mal zusätzlich.`
    );
  }
  if (negatives?.length) {
    const worst = negatives[0];
    const delta = Number(worst.median_with) - Number(worst.median_without);
    parts.push(
      `${nf.format(worst.cases)} Fälle (${pct(worst.case_share)}) laufen über „${worst.event_type}" und brauchen ` +
        `dadurch ${h(delta)} länger als die übrigen.`
    );
  }
  if (!parts.length) return 'Keine Wiederholungen und keine Rückläufer — dieser Ablauf läuft beim ersten Versuch durch.';
  return parts.join(' ');
}

/** Variants: how much of the work runs on the standard path, and how long the tail is. */
export function variants(rows) {
  if (!rows?.length) return null;
  const covering80 = rows.findIndex((r) => Number(r.cum_share) >= 0.8) + 1;
  const standard = rows[0];
  const slowest = [...rows].sort((a, b) => Number(b.avg_seconds) - Number(a.avg_seconds))[0];
  const detour =
    slowest !== standard
      ? ` Die teuerste häufige Abweichung braucht ${h(slowest.avg_seconds)} statt ${h(standard.avg_seconds)}.`
      : '';
  return (
    `${covering80 > 0 ? covering80 : rows.length} Varianten decken 80 % der Fälle ab. ` +
    `Die häufigste macht ${pct(standard.share)} aus.${detour}`
  );
}

/** Automation: straight-through processing is the honest headline; the candidate list is the actionable part. */
export function automation(summary, candidates) {
  if (!summary) return null;
  const stp = Number(summary.straight_through_share ?? 0);
  const head =
    stp === 0
      ? 'Kein Fall läuft ohne Menschen durch.'
      : `${pct(stp)} der Fälle laufen ohne einen einzigen manuellen Schritt durch.`;
  if (!candidates?.length) return head;
  const top = candidates[0];
  return (
    `${head} Der Schritt mit dem größten Automatisierungspotenzial ist „${top.event_type}": ` +
    `${nf.format(top.freq)} Mal, ${pct(top.manual)} davon von Hand, und der nächste Schritt ist zu ` +
    `${pct(1 - Number(top.outcome_entropy))} vorhersagbar — je vorhersagbarer, desto mechanischer die Entscheidung.`
  );
}

/** Where cases end: the difference between finishing and stopping. */
export function endings(rows) {
  if (!rows?.length) return null;
  const main = rows[0];
  const others = rows.slice(1).reduce((sum, r) => sum + Number(r.share ?? 0), 0);
  return (
    `${pct(main.share)} der Fälle enden mit „${main.last_activity}". Die übrigen ${pct(others)} enden woanders — ` +
    `das sind entweder noch laufende oder liegengebliebene Fälle, und der Unterschied ist es wert, nachgesehen zu werden.`
  );
}

/** Processes: what the log covers at all, and how much of it runs without people. */
export function processes(rows) {
  if (!rows?.length) return null;
  // Configuration records are excluded from the count: they are things cases refer to, not cases.
  const flows = rows.filter((r) => r.art !== 'Stammdaten');
  const reference = rows.length - flows.length;
  const cases = flows.reduce((sum, r) => sum + Number(r.faelle ?? 0), 0);
  const manual = flows.filter((r) => Number(r.automatisch_prozent) < 50);
  const referenceNote = reference
    ? ` Dazu ${count(reference, 'ein Stammdatensatz', 'Stammdatensätze')}, auf den sich viele Fälle beziehen — kein eigener Ablauf.`
    : '';
  return (
    `${count(flows.length, 'Ablauf', 'Abläufe')} mit zusammen ${nf.format(cases)} Fällen, nichts davon vorher ` +
    `festgelegt. ${count(manual.length, 'Ablauf braucht', 'Abläufe brauchen')} Menschen: ` +
    `${manual.map((r) => r.prozess).join(', ')}. Der Rest läuft als Schnittstelle oder Automatik.${referenceNote}`
  );
}

/** Roles: how much of the recorded work is machine work, and where the human work concentrates. */
export function roles(rows) {
  if (!rows?.length) return null;
  const total = rows.reduce((sum, r) => sum + Number(r.schritte ?? 0), 0);
  const machine = rows
    .filter((r) => ['Automatischer Job', 'Systemdienst', 'Fremdsystem', 'Gerät'].includes(r.rolle))
    .reduce((sum, r) => sum + Number(r.schritte ?? 0), 0);
  const people = rows.filter((r) => !['Automatischer Job', 'Systemdienst', 'Fremdsystem', 'Gerät'].includes(r.rolle));
  const top = people[0];
  const idle = people.filter((r) => Number(r.davon_aktiv) < Number(r.personen));
  const idleNote = idle.length
    ? ` In ${count(idle.length, 'Gruppe ist', 'Gruppen sind')} nicht jedes Mitglied aktiv — dort ist die Arbeit auf wenige verteilt.`
    : '';
  return (
    `${pct(machine / Math.max(1, total))} aller Schritte macht die Maschine. ` +
    `Die meiste menschliche Arbeit liegt bei „${top?.rolle}" (${nf.format(top?.schritte ?? 0)} Schritte, ` +
    `${nf.format(top?.personen ?? 0)} Personen).${idleNote}`
  );
}

/** Who does what: a step performed by several roles is either shared or unclear. */
export function whoDoesWhat(rows) {
  if (!rows?.length) return null;
  const shared = rows.filter((r) => Number(r.rollen_am_schritt) > 1);
  const steps = new Set(shared.map((r) => r.schritt));
  if (!steps.size)
    return 'Jeder Schritt hat genau eine ausführende Rolle — die Zuständigkeiten sind eindeutig verteilt.';
  const verb = steps.size === 1 ? 'wird' : 'werden';
  return (
    `${count(steps.size, 'Schritt', 'Schritte')} ${verb} von mehr als einer Rolle ausgeführt ` +
    `(${[...steps].slice(0, 3).join(', ')}). Das ist entweder bewusst geteilte Zuständigkeit oder eine unklare ` +
    `Grenze — beides lohnt einen Blick.`
  );
}

/** Handovers: the outline of the company's dealings with the outside. */
export function handovers(rows) {
  if (!rows?.length) return null;
  const incoming = rows.filter((r) => r.richtung === 'kommt rein');
  const outgoing = rows.filter((r) => r.richtung === 'geht raus');
  const sum = (list) => list.reduce((total, r) => total + Number(r.anzahl ?? 0), 0);
  const failed = rows.filter((r) => /nicht möglich|fehlgeschlagen/i.test(r.vorgang));
  const failNote = failed.length
    ? ` ${nf.format(sum(failed))} Übergaben sind gescheitert — eine Übergabe, die nicht stattfand, ist der Vorgang, der jemanden aufhält.`
    : '';
  return (
    `${nf.format(sum(outgoing))} Vorgänge verlassen das Haus, ${nf.format(sum(incoming))} kommen von außen herein.` +
    failNote
  );
}

/** Role handovers: off-diagonal mass is coordination cost; a pair in both directions is a bounce. */
export function roleHandovers(rows) {
  if (!rows?.length) return null;
  const pairs = new Map(rows.map((r) => [`${r.von}→${r.an}`, Number(r.uebergaben)]));
  const bounces = rows.filter((r) => pairs.has(`${r.an}→${r.von}`) && r.von < r.an);
  const top = rows[0];
  const bounceNote = bounces.length
    ? ` ${bounces.length} Paare übergeben in beide Richtungen (z. B. ${bounces[0].von} ↔ ${bounces[0].an}) — dort geht Arbeit hin und her, und jedes Zurück ist Wartezeit.`
    : '';
  return `Die stärkste Übergabe läuft von „${top.von}" an „${top.an}" (${nf.format(top.uebergaben)} Mal).${bounceNote}`;
}

/** Coverage: absence has to be readable as absence. */
export function coverage(rows) {
  if (!rows?.length) return null;
  const unlabelled = rows.filter((r) => r.bezeichnung.startsWith('—'));
  if (!unlabelled.length)
    return `Alle ${rows.length} beobachteten Ereignisarten haben eine Bezeichnung. Was hier nicht steht, wird auch nicht erfasst.`;
  return (
    `${unlabelled.length} von ${rows.length} beobachteten Ereignisarten sind noch nicht benannt. ` +
    `Solange das so ist, tauchen sie in Auswertungen mit ihrem technischen Namen auf.`
  );
}

/**
 * What every duration in this tool is measured against.
 *
 * Stated openly because it decides every number here: if the calendar is wrong, all figures are wrong in the same
 * direction and nothing about them looks suspicious.
 */
export function calendar(row) {
  if (!row) return null;
  const source = row.arbeitszeit_quelle ? `„${row.arbeitszeit_quelle}" aus der Quelle` : 'Standardannahme';
  const holidays = Number(row.feiertage ?? 0);
  const full = Number(row.ganze_tage ?? 0);
  const half = Number(row.davon_halbe_tage ?? 0);
  const holidayNote = holidays
    ? `${count(full, 'ganzer Feiertag', 'ganze Feiertage')} und ${count(half, 'halber Tag', 'halbe Tage')} aus dem the source-Kalender.`
    : 'Keine Feiertage geladen — Zeiträume über Feiertage werden dadurch zu lang gemessen.';
  return `Alle Dauern zählen nur Arbeitszeit: ${row.arbeitszeit}, Quelle ${source}. ${holidayNote} ` +
    `Den Tagesbeginn kennt die Quelle nicht — er ist hier eingestellt und verschiebt das Fenster, nicht seine Länge.`;
}

/**
 * Who decides about whose work.
 *
 * The reading names the longest wait rather than the most frequent pair: a relation that works is invisible, and
 * the one where things sit is the one worth knowing about.
 */
export function decisions(rows) {
  if (!rows?.length) return 'Keine Entscheidungen zwischen zwei Personen gefunden.';
  const slowest = [...rows].filter((r) => Number(r.wie_oft) >= 3).sort((a, b) => Number(b.wartezeit_stunden) - Number(a.wartezeit_stunden))[0];
  const pairs = new Set(rows.map((r) => `${r.eingereicht_von}|${r.entschieden_von}`));
  const slow = slowest
    ? ` Am längsten wartet „${slowest.eingereicht_von}" auf „${slowest.entschieden_von}": ` +
      `${nf.format(slowest.wartezeit_stunden)} Arbeitsstunden im Mittel über ${nf.format(slowest.wie_oft)} Vorgänge.`
    : '';
  return `${count(pairs.size, 'Entscheidungsbeziehung', 'Entscheidungsbeziehungen')} im Log.${slow}`;
}
