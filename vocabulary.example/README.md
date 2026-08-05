# The vocabulary

Four files decide what a screen says. They are configuration, not code: which types a source declares, and what each
step is called by the people who do it, differ per installation — so the tool ships the rules that render a label and
takes the words from here.

This directory is the example. It describes an invented source, it is committed, and the test suite runs against it,
so every rule below is exercised on any machine. A real installation puts its own four files in `vocabulary/`
(gitignored) and points `ProcessAnalyzer:VocabularyPath` at it.

```
vocabulary/
  labels.tsv               what a person reads instead of a technical key
  discriminator-rules.tsv  which payload attribute names the step
  payload-allowlist.tsv    which payload attributes may be projected at all
  source-catalogue.txt     every type the source declares — the list coverage is measured against
```

All four are read at startup and upserted, so correcting a word means editing the file and restarting the container.
Lines starting with `#` are comments. Fields are tab-separated, except the catalogue, which is `kind type`.

## labels.tsv

`kind` `type_name` `label_de` `hint_de`

| kind | what it names | number |
|---|---|---|
| `object` | what a case is about | plural — every screen counts them |
| `event` | a named fact | a completed fact in the past tense |
| `entity` | the generic tier: one noun per entity | singular |
| `verb` | the generic tier: `created`, `updated`, `deleted`, `copied` | — |
| `discriminator` | the value of the attribute that names the step | — |

`hint_de` explains the step to somebody who has never seen the process, or is empty when the label is already the whole
story. A hint that restates the label is noise.

Two event types must never share a label: that merges two steps into one line on every screen, and the line still
reads perfectly well, which is why it goes unnoticed.

**Ask, do not derive.** A term that looks obvious from a class name is the dangerous case — a plausible wrong word gets
believed and never checked again. Ask whoever runs the process, and record the answer next to the label.

## discriminator-rules.tsv

`priority` `type_match` `attr_name` — lowest priority wins, `type_match` is a `LIKE` pattern against the event type,
`%` is a rule for every type.

This is what makes an activity more than its event type. Two approvals by two different roles are two steps of the
process; with the bare type they are indistinguishable, every multi-stage approval reads as 100 % rework, and the
variant list collapses to "granted → granted". A source that names the deciding attribute `role` needs one row; a
family of types that carries its own attribute needs one more.

## payload-allowlist.tsv

`event_type` `attr_name` — default deny. An attribute not listed here stays in `journal.payload` and never reaches the
analytical model, which is what keeps that model widely readable.

An attribute used by a discriminator rule has to be allow-listed too, or the rule will never see a value.

## source-catalogue.txt

`kind type`, one per line, `kind` being `event`, `object` or `entity`. This is the list `LabelCoverageTests` measures
coverage against, so it has to be refreshed whenever the source adds a type — the test failing is the point, because a
new type must be named before anybody sees it on a screen.

Keep a retired type in the catalogue as long as the journal still holds events of that name. Removing it only removes
the label, and the events do not disappear with it.
