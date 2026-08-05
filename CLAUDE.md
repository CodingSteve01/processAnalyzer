# ProcessAnalyzer

A sidecar that mirrors a business-event journal into its own PostgreSQL database, projects it into an object-centric
event log, and answers questions about how work actually flows through an organisation. The source schema it expects
is in the README.

It reads the source read-only, through its own login. There is no code path that could write to it.

**Language rule:** code, comments, commits, test messages and every document in the repository — `README.md`,
`CLAUDE.md`, `CONTRIBUTING.md`, `STYLE_GUIDE.md` — are English. The frontend is German: every string in
`wwwroot` and every label in `ocel.label`, because the people who read them work in German. The two things that
stay German inside English prose are on-screen text quoted verbatim and house vocabulary with no English
equivalent — translating those invents a second name for a thing that
already has one. Do not otherwise mix the two inside one file; that is how this documentation had to be rewritten
once already.

## What it is for

Not monitoring, not reporting. The question is *what do people actually do*: which steps a case really goes
through, who decides about whom, where cases wait, which handovers leave the organisation, and what is done by hand that
looks automated from the outside.

That aim decides design questions. Two examples that came up and were settled by it:

- **Names, not pseudonyms** (behind a switch). For some questions the name *is* the answer and a pseudonym cannot
  give it. So `analytics.show_identity()` is a setting with two honest positions, and the choice belongs to the
  deployment. What it never does is rank people against each other.
- **Deleted cases are marked, not hidden.** A discarded case is a process that ran and was then thrown away, which
  is exactly what a process analysis should surface. Filtering it out would hide abandonment and flatter the
  throughput of everything else.

## Layout

```
ProcessAnalyzer.Web/
  Program.cs                    DI, middleware order, endpoint mapping
  Options/                      ProcessAnalyzerOptions — one options class, section "ProcessAnalyzer"
  Sync/
    SqlJournalReader.cs     the ONLY place that talks to SQL Server. Raw ADO.NET, never EF
    UnconfiguredJournalSource.cs no source configured — the app still starts and says so
    JournalPullService*.cs      BackgroundService: PullOnceAsync, SweepOnceAsync
    JournalMirror*.cs           the write side (Postgres), ON CONFLICT DO NOTHING
    DirectorySync.cs            names, groups and the business calendar, when the source offers them
  Projection/ProjectionService  journal.* → ocel.*, its own cursor, replayable from scratch
  Analytics/                    read-only SQL against analytics.* — one repository per question class
  Export/OcelSqliteExporter.cs  OCEL 2.0 SQLite for pm4py
  Auth/DashboardAuth.cs         PBKDF2 cookie login, mapped BEFORE UseStaticFiles
  Endpoints/                    Health, Sync, Analytics, Mining — MapGroup extensions on WebApplication
  Data/Sql/0NN-*.sql            the schema and every rule that lives in SQL, run by EF migrations
  Vocabulary/VocabularyLoader   the four vocabulary files -> ocel.label, discriminator_rule, payload_allowlist
  wwwroot/                      seven views, vanilla ES modules, no build step, no CDN
vocabulary.example/             an invented source's vocabulary: committed, and what the tests run against
miner/                          pm4py container: OCEL export in, SVG diagrams out
scripts/                        demo stack, secrets, the read-only login
```

## The four layers, and why they are separate

```
source (SQL Server, read only)
   │  contiguous-prefix pull, lag window, hourly gap sweep
   ▼
journal.*    an append-only copy of the source. Never edited in place.
   │  projection (own cursor, replayable)
   ▼
ocel.*       events, objects, e2o relations — the object-centric log
   │  views and functions
   ▼
analytics.*  what the screens and the OCEL export read
```

`journal.*` is the reproducibility base: if the projection is wrong, it is re-run from the mirror rather than
re-pulled from the source. That is the whole reason for two layers instead of one, and it is why the projection is
a *reader* of `journal.*` and never a second writer of it.

## Rules that are load-bearing

**The pull.** A journal row becomes visible at commit time, not insert time, so a lower id can appear after a
higher one. The pull walks a page in id order and **stops at the first row inside the lag window** — it does not
skip it and continue. Skipping would advance the watermark past a row that then never gets pulled. Held-back rows
are counted, not dropped. The hourly sweep re-reads the last few days, asks the mirror what it is missing, and
refills it — **the sweep never moves the watermark.** Idempotent writes are the third leg.

**Activity = event type plus the attribute that names the step.** `analytics.activity_of` appends `role`,
`actionType` or the classification `method`. Without it, two approvals by two different roles are one activity,
every multi-role approval reads as 100 % rework, and the variant list collapses to "granted → granted". This has
already gone wrong twice: once by not discriminating at all, once by discriminating on a type name that only the
demo seed produced.

**Never aggregate across object types.** An event touching a document and a workflow is one event, not two.
Counting it under both is convergence error, and it silently doubles every figure it touches.

**Duration means working time.** `analytics.biz_seconds` walks the business calendar synced from the source —
hours per weekday plus half-day holidays. Wall-clock duration makes every Friday-afternoon case the worst case and
turns the bottleneck ranking into a weekend detector.

**Every type gets a German label before anybody sees it.** `LabelCoverageTests` fails when a declared type has no
label, when a label is a copy of the technical key, when two event types share a label, or when a verb of the generic
tier has no word. Refresh `source-catalogue.txt` when the source adds a type — the test failing is the point.

**The vocabulary is configuration, not code.** Labels, the discriminator rules, the payload allowlist and the type
catalogue are four files under `VocabularyPath`, read at startup and upserted. They are per-installation: which types
a source declares and what a step is called differ, and a corrected word must not need a release. `vocabulary/` is
gitignored; `vocabulary.example/` describes an invented source, is committed, and is what the suite asserts against.
Nothing that ships may hard-code a source's type names — a `LIKE 'somemodule.something-%'` inside a shipped function
is a rule that belongs in `discriminator-rules.tsv`.

**House vocabulary beats the schema.** A label is what somebody reads, so it has to use the words that organisation
actually uses — which are frequently not the words in the database. A term that looks obvious from a class name is
the dangerous case: a plausible wrong word gets believed and never checked again. Ask whoever knows the process
instead of deriving it, and record the answer next to the label.

## The licence constrains the code

AGPL-3.0-or-later, because pm4py's community edition is AGPL and its authors read "covered work" broadly. Two
consequences that are code rules, not paperwork:

- **Nothing from this repository may be linked into the source application**, and that application must never import
  pm4py. Reading a database does not make a covered work; linking would, and it would put the other application under
  the AGPL as well.
- **The repository is public**, so the bar for what may be committed is higher than "not a secret": no connection
  strings, no host names, no credentials, no personal data, no customer data, not even in fixtures or commit
  messages. History is published too — a value committed once and removed later is still public.

## Non-negotiables

- **The source is read only.** Raw ADO.NET, `ApplicationIntent=ReadOnly` enforced in the reader's constructor.
  Startup refuses a write-capable login unless `AllowWriteCapableLogin` is set.
- **No secrets, no customer data, no real host names in the repo** — not in code, fixtures, commit messages or PR
  text. `appsettings.json` ships empty strings; `.env.local` is gitignored.
- **Comments explain why and what breaks otherwise**, never what the line does. Most comments in this repo name a
  defect that actually shipped; keep it that way.
- **No CDN, no build step for the frontend.** An external `<script>` fails closed on a host without internet access:
  the page renders blank and holds a connection slot. Everything ships in `wwwroot`.
- **Singletons and `IDbContextFactory`**: `await using var db = await factory.CreateDbContextAsync(ct)`.
- **Minimal API only.** No controllers, no Swagger.
- A change to the pull, the projection or a label ships with a test named for the failure it prevents.

## Build and run

```bash
dotnet build ProcessAnalyzer.Web
dotnet test ProcessAnalyzer.Tests                    # needs a Docker daemon (Testcontainers)

scripts/demo-stack.sh                                # fake source + app, no real source needed
docker compose --profile mining run --rm miner       # pm4py diagrams from the OCEL export

dotnet ef migrations add <Name> --project ProcessAnalyzer.Web
```

A new rule in SQL is a new `Data/Sql/0NN-*.sql` plus a migration that executes it. **Never edit a shipped SQL
file:** it has already run on every existing database, so an edit only changes what a fresh install gets, and the
running installations keep the old definition and quietly disagree with the code.

The one deliberate exception, recorded here so it is not mistaken for drift: the files that used to carry label rows
were emptied when the vocabulary moved out of the schema. Removing rows a running installation already holds changes
nothing for it — the loader upserts the same rows on the next start — and the numbered files stay in place because the
migrations that run them have been applied everywhere. Never do this to a *definition*; data seeded by a shipped file
is the only case where it is safe.

## Where it runs today

Container images are built and released by CI. The tool is designed to run next to the source database rather than
on it: one read-only login, individual table grants instead of a blanket reader role, and a low-priority workload
group where the instance offers one. `scripts/sql/create-readonly-login.sql` creates that account and verifies
itself — reading allowed, writing denied.

**Metadata queries lie under a restricted login.** `INFORMATION_SCHEMA` and `OBJECT_NAME` answer in the context of
the asking account and the current database, so a table the account cannot see simply does not appear. Before
concluding that a column or an object does not exist, ask with an account that can see everything.
