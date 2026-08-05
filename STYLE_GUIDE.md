# UI style guide

The screens exist to make a process readable by somebody who has never seen it. Everything below serves that, and
the rules that look like taste are mostly the outcome of a page that was unreadable once.

`wwwroot/styles.css` is the single stylesheet — ~490 lines, no framework, no build step, no CDN. An external
`<script>` or font fails closed on a host without internet access: the page renders blank *and* holds a connection
slot open.
That happened once and cost an afternoon, so everything ships in `wwwroot`.

## Tokens

Defined once in `:root`, dark only (`color-scheme: dark`). Never hard-code a colour in a rule — a literal hex is how
one panel ends up a shade off every other one.

```
--bg #0d1117   --bg-card #161b22   --bg-hover #21262d   --bg-active #30363d   --border #30363d
--text #e6edf3          --text-muted #8b949e
--accent #58a6ff        --accent-dim  rgba(88,166,255,.15)
--success #3fb950       --success-dim rgba(63,185,80,.15)
--warning #d29922       --warning-dim rgba(210,153,34,.15)
--error #f85149         --error-dim   rgba(248,81,73,.15)
--topbar-height 56px    --radius 6px   --radius-lg 12px
```

Every state colour has a `-dim` companion for fills. Pair them: `--error` text on `--error-dim` background, never
full-strength fill behind text.

## The pieces

| Class | What it is |
|---|---|
| `.topbar`, `.brand`, `.scope` | Header, product name, and which object type the page is currently about |
| `.viewnav`, `.viewtab`, `.viewtab-sub` | The seven views. The subtitle says what the view answers, not what it is named |
| `.panel`, `.panel-head` | One question per panel. A panel needing two headings is two panels |
| `.stats-grid`, `.stat-card`, `.metric-value`, `.metric-label` | Headline figures; label below the value, unit spelled out |
| `.reading` | A sentence interpreting the figures above it — a number nobody can read is not an insight |
| `.variant` | One path through a process, as a chain of steps |
| `.chart-box`, `.legend`, `.legend-dot` | The hand-drawn SVG line chart (`js/linechart.js`) |
| `.case-layout`, `.case-search`, `.detail-list`, `.detail-row` | One case, step by step, with the wait before each step |
| `.model-tabs`, `.model-view` | The pm4py diagrams with their age |
| `.status-pill`, `.banner`, `.caveat`, `.hint`, `.empty` | State, warnings, the limits of a figure, and the empty case |
| `.btn-primary`, `.btn-secondary`, `.btn-icon`, `.actions` | Controls |
| `.login-*` | The login page, deliberately its own small set |

## Rules

- **A figure carries its caveat on screen.** `.caveat` exists so "laufende Fälle sind ausgenommen" or "weniger als
  fünf Personen, daher unterdrückt" stands next to the number instead of in a document nobody opens.
- **`.empty` is a sentence, not a dash.** "Noch keine Fälle in diesem Zeitraum" tells the reader the tool works and
  the data does not exist yet. A blank cell reads as a bug and gets reported as one.
- **Panels load independently.** The page fetches with `allSettled`; a failing panel shows its own error rather than
  blanking the page. One `Promise.all` once turned a single slow query into an empty dashboard.
- **Never rank people.** No leaderboard, no "fastest/slowest" column. "Who works with whom and who decides what" is
  answerable here; "who is faster" is not, and that is a decision rather than a gap.
- **German on screen, English in the code.** Labels come from `ocel.label`, never from a lookup table in JS — one
  place to correct a wrong word, and wrong words do get corrected.
- **Numbers get a unit and a sensible precision.** Hours to one decimal, percentages whole. A `13.999999` on screen
  is a raw division that escaped.
- Soft cap 400 LOC per `.js`. `js/` splits by view (`views.js`, `insights.js`, `cases.js`, `readings.js`) plus
  `api.js`, `utils.js`, `linechart.js`, `login.js`, `app.js`.
