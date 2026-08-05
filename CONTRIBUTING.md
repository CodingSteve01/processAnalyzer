# Contributing to ProcessAnalyzer

## Code Style

### C# (.cs)
- **Max 300 LOC per file** (soft limit). Split via `partial class` when needed.
- SOLID principles. One responsibility per class.
- Use `IDbContextFactory<AppDbContext>` for Postgres access (singleton-safe):
  `await using var db = await factory.CreateDbContextAsync(ct);`
- **Never EF against the source.** The source is read with raw ADO.NET via
  `Microsoft.Data.SqlClient`, behind `IJournalSource`. There is no second `DbContext`.
- Minimal API only — no controllers, no Swagger. Endpoint files use the `MapGroup`
  extension pattern in `Endpoints/`.
- `ct` is the last parameter and defaults to `default` where it makes sense.
- Comments explain **why**, never what. If a line encodes a decision, name the decision and
  what breaks when it is wrong.
- Run `csharpier` for formatting (enforced via lefthook pre-commit, checked in CI).

### JavaScript (.js)
- **Max 400 LOC per file** (soft limit).
- Native ES Modules (`import`/`export`). No bundler, no framework.
- HTML escaping for all data rendered into templates.

### CSS
- CSS custom properties defined in `css/base.css`. Never hardcode colors.
- See [STYLE_GUIDE.md](STYLE_GUIDE.md).

### Language
- Code, comments, commit messages, PR text: **English**.
- User-facing UI text: **German**.

## Directory Structure

| Directory | Purpose |
|---|---|
| `Options/` | Strongly typed configuration (`ProcessAnalyzerOptions`) |
| `Data/` | `AppDbContext`, migrations, and `Data/Sql/` — every rule that lives in SQL |
| `Models/` | Postgres entities (`JournalEvent`, `JournalEventObject`, `SyncCursor`, `SyncRun`) |
| `Sync/` | Source reader (`IJournalSource`), mirror writer, pull service, directory sync |
| `Projection/` | `journal.*` → `ocel.*`, with its own cursor |
| `Analytics/` | Read-only queries against `analytics.*`, one repository per question class |
| `Export/` | OCEL 2.0 SQLite export for pm4py |
| `Auth/` | Cookie login. Mapped **before** `UseStaticFiles`, or the API is protected and the page is not |
| `Endpoints/` | Minimal API route handlers (one file per area) |
| `Vocabulary/` | Reads the four vocabulary files into `ocel.label`, `ocel.discriminator_rule` and `ocel.payload_allowlist` |
| `wwwroot/` | `index.html`, `styles.css`, `js/` ES modules. No build step, no CDN |

## Database schemas

| Schema | Tables | Owner |
|---|---|---|
| `journal` | `event`, `event_object` | the mirror — a faithful copy of the source rows, never edited in place |
| `sync` | `cursor`, `run` | the pull's and the projection's bookkeeping |
| `ocel` | `event`, `object`, `e2o`, `label`, `discriminator_rule`, `payload_allowlist`, `type_registry` | the object-centric log; the last three are filled from the vocabulary at startup, not by a migration |
| `dim` | `actor`, `actor_role` | who acted, and in which group |
| `analytics` | views, functions, `setting` | what the screens read: working time, activity naming, identity switch |

A new rule in SQL is a new `Data/Sql/0NN-*.sql` plus a migration that runs it. **Never edit a shipped
SQL file** — it has already run everywhere, so an edit only changes what a fresh install gets while
running installations keep the old definition and quietly disagree with the code.

Columns are `snake_case`, configured explicitly in `AppDbContext`. No naming-convention package —
an implicit rename is invisible in review and a renamed column is a silent data loss on deploy.

## Never in the repository

- Secrets, passwords, tokens, real connection strings.
- Host names, server names, IP addresses of real systems.
- Customer data of any kind — names, identifiers, mail addresses, licence plates — in code, fixtures,
  test data, commit messages or PR text.
- An installation's vocabulary: its type catalogue and the words it uses for its own steps. Those go in
  `vocabulary/`, which is gitignored. `vocabulary.example/` describes an invented source and is committed.
- Comments that describe a particular organisation's process. A comment names the defect it prevents, in terms
  of the tool: "two roles collapse into one activity", not who those roles are.

`appsettings.json` ships empty strings. `.env` and `.env.local` are gitignored; `.env.template` is committed, carries placeholders only, and must
stay free of anything above. `scripts/apply-secrets.sh` fills it and aborts loudly on a missing value.

## API Conventions

- All endpoints under `/api/`, plus `/health`.
- Return JSON. Anonymous types are fine for simple responses.
- Group related endpoints with `MapGroup()`.

## Commit Convention

[Conventional Commits](https://www.conventionalcommits.org/) enforced by commitlint:

```
feat: add gap sweep to the pull loop
fix: hold back rows inside the lag window
refactor: split JournalMirror write path
docs: document the watermark rule
ci: run tests against a Postgres container
```

## Local Development

```bash
# Install hooks
npx lefthook install

# Build
dotnet build ProcessAnalyzer.Web

# Test — requires a running Docker daemon, the tests start a real Postgres
dotnet test ProcessAnalyzer.Tests

# Run locally with Docker
docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build

# Add EF migration
dotnet ef migrations add <Name> --project ProcessAnalyzer.Web
```

## Pre-commit Hooks (lefthook)

| Hook | Tool | Files |
|---|---|---|
| pre-commit | eslint | `*.js` |
| pre-commit | csharpier | `*.cs` |
| commit-msg | commitlint | commit message |
| pre-push | actionlint | `.github/workflows/*.yml` |

## Pull Request Process

1. Create a feature branch from `main` (`feat/`, `fix/`, `refactor/`).
2. `dotnet build ProcessAnalyzer.Web` succeeds with 0 errors and 0 warnings.
3. `dotnet test ProcessAnalyzer.Tests` is green.
4. Verify lefthook hooks pass (eslint, csharpier, commitlint).
5. Create a PR with a descriptive title and summary.

## Tests

A change to the pull path is not done until a test names the failure it prevents. Test names read
as the bug that would otherwise ship — `Pull_StopsAtTheFirstRowInsideTheLagWindow_AndDoesNotSkipPastIt`,
not `PullWorks`. A test that only restates the implementation is noise; delete it.

The pull tests run against a real PostgreSQL container (Testcontainers) because the behaviour under
test is `ON CONFLICT` and `jsonb` — exactly the parts an in-memory provider fakes away.

The same applies to labels, and those tests need no container: coverage is a question about two files.
`LabelCoverageTests` reads every vocabulary in the checkout — `vocabulary.example/` always, an installation's
`vocabulary/` when it is there — and fails when a declared type has no German label, when a label is a copy of
the technical key, when two event types share a label, or when a verb of the generic tier has no word. Adding a
type at the source therefore means refreshing `source-catalogue.txt` and naming the type in `labels.tsv` — the
failing test is the reminder, not an obstacle.

The rendering rules are tested separately, against the example vocabulary only. `AnalyticsSqlTests` asserts
rendered German sentences, so it needs known input: loading a second vocabulary would make the expected strings
depend on which machine the suite runs on, and would break the "no two steps share a label" rule for the honest
reason that two vocabularies describe two different sources.

A guard needs its own proof of life. Before trusting one, make it fail on purpose: the label guard
was checked by adding a type that does not exist and watching it go red. A checker that has gone
blind reports green forever, which is worse than no checker at all.
