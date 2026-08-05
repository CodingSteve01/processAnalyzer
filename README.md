# Process Analyzer

[![CI](https://github.com/CodingSteve01/processAnalyzer/actions/workflows/ci.yml/badge.svg)](https://github.com/CodingSteve01/processAnalyzer/actions/workflows/ci.yml)
[![Release](https://github.com/CodingSteve01/processAnalyzer/actions/workflows/release.yml/badge.svg)](https://github.com/CodingSteve01/processAnalyzer/actions/workflows/release.yml)
[![GHCR](https://img.shields.io/badge/ghcr.io-processanalyzer-blue)](https://github.com/CodingSteve01/processAnalyzer/pkgs/container/processanalyzer)

A sidecar to a line-of-business application: it pulls the business events out of its database — read only — and
turns them into an answer to the question **what the users actually do**.

Not monitoring and not reporting. The questions are: which steps does a case really go through? Who decides about
whom? Where do cases wait, and on whom? What leaves the organisation, and with how much delay? And what is done by hand
that looks automated from the outside?

## Where to start

Without access to a source, on invented data in a realistic shape — good for getting to know the screens:

```bash
scripts/demo-stack.sh          # source with fake events + app, then http://localhost:5100
```

Against a real source it takes three things: a network route to the source database, the credentials of the
read-only account, and a password for signing in to the screens.

```bash
# Generate the password hash (the password itself is never stored anywhere)
docker compose run --rm processanalyzer hash-password

PA_POSTGRES_PASSWORD=…                                                \
PA_SOURCE_CONNECTION_STRING='Server=…;ApplicationIntent=ReadOnly;…'  \
PA_DASHBOARD_PASSWORD_HASH=…                                          \
PA_ACTOR_HASH_KEY=$(openssl rand -base64 32)                          \
scripts/apply-secrets.sh                      # fills the values into .env, aborts on a gap

docker compose up -d
curl -s localhost:5100/health
```

Generate `PA_ACTOR_HASH_KEY` **once** and keep it. It determines the pseudonyms; swap it and every key changes, and
the history loses its continuity.

The pull then runs by itself: once a minute it fetches new events, hourly it checks retroactively for gaps. Its
state is on the **Spiegel** page.

## The screens

| Page | What it answers |
|---|---|
| **Überblick** | How many cases, which processes, how long they take, how much runs automatically |
| **Analyse** | The ten analyses: step sequences, bottlenecks, rework, waiting times, handovers |
| **Menschen** | Who works with whom, who decides about whom, which groups exist at all |
| **Fälle** | A single case, step by step, with the wait before each step |
| **Entwicklung** | The same figures per week — the answer to "has it got better?" |
| **Diagramm** | The process models drawn by pm4py |
| **Spiegel** | State of the pull: watermark, last runs, held-back rows, gaps |

No screen shows a technical key. Every event and object type has a German label; if a dotted name does appear with
a ⚠ in front of it, a label is missing — that is a defect in the tool, not a data problem.

## How the figures come about

```
source (SQL Server, read only)
   │   pull: contiguous from the bottom up, with a lag window
   ▼
journal.*     unchanged copy of the source. The basis for everything after it
   │   projection
   ▼
ocel.*        events, objects and their relations
   │
   ▼
analytics.*   what the pages read
```

The copy is the reason for two layers: if an analysis is wrong, it is recomputed from the copy rather than pulled
from production again.

**Three things worth knowing about the figures:**

- **Duration means working time, not calendar time.** The business calendar is read from the source — hours per
  weekday plus half-day holidays. Otherwise every Friday afternoon would be the worst case and the bottleneck list
  would be a weekend detector.
- **A step is more than its event type.** The same event type performed by two different roles is two different
  steps. Merging them makes every multi-stage approval read as 100 % rework.
- **Running cases are out of the cycle times.** A week still in progress would otherwise look good, because its
  slow cases are not finished yet.

## The vocabulary

What a screen says comes from four files, not from the code: the label of every type, which payload attribute names a
step, which attributes may be projected at all, and the catalogue of types coverage is measured against. They are
configuration, because which types a source declares and what each step is called differ per installation.

```
vocabulary/
  labels.tsv               what a person reads instead of a technical key
  discriminator-rules.tsv  which payload attribute names the step
  payload-allowlist.tsv    which payload attributes may be projected at all
  source-catalogue.txt     every type the source declares
```

`vocabulary.example/` is committed, describes an invented source and is what the test suite runs against — so a fresh
checkout renders words rather than dotted identifiers. A real installation puts its own four files somewhere the
container can read and points `ProcessAnalyzer:VocabularyPath` at that directory. The files are read at startup and
upserted, so a corrected word takes effect on the next restart and needs no release.
[vocabulary.example/README.md](vocabulary.example/README.md) documents the format and the rules a label has to obey.

Labels speak the vocabulary of whoever is being analysed, not the vocabulary of the database. Where the two disagree,
the label follows the people: a term that looks obvious from a class name is the dangerous case, because a plausible
wrong word gets believed and never checked again. So a label is asked for rather than derived, and the answer is
recorded next to it.

## Names

By default pseudonyms appear. With `PA_SHOW_ACTOR_IDENTITY=true` real names stand there with their group, because for
some questions the name *is* the answer and a pseudonym cannot give it. The switch has two honest positions; which
one a deployment picks is its own call.

What the switch does not change: the source id never leaves the internal table, and no page ranks people against
each other. "Who works with whom and who decides what" is answerable, "who is faster" is not — that is a decision,
not an oversight.

## Process models with pm4py

Its own container, because pm4py is Python and the app is .NET. Both share a directory: the app writes the OCEL 2.0
export, the miner draws from it.

```bash
curl -s -X POST localhost:5100/api/export/ocel     # writes log.sqlite into the shared directory
docker compose --profile mining run --rm miner     # draws the models from it
```

The result is three images — frequency and time view of the process graph plus an object-centric Petri net — plus
figures. The **Diagramm** page shows them with their age, so nobody mistakes a three-week-old picture for the
current state. The run is on demand, not on a schedule: it takes minutes, and the data does not change by the
minute.

The exported `log.sqlite` is an OCEL 2.0 file and therefore also readable in ProM or Celonis, if somebody wants to
carry on with their own tools.

## Settings

Everything under the `ProcessAnalyzer` section, settable per environment variable
(`ProcessAnalyzer__LagSeconds=180`). The repository holds only the *shape* of the configuration, never a value;
`scripts/apply-secrets.sh` fills the values in and aborts loudly when one is missing. A connection string with a
password in git is one clone away from being everywhere, and rotating it afterwards does not undo the clone.

| Setting | Default | What for |
|---|---|---|
| `SourceConnectionString` | empty | The source. Without it the app starts and says it has nothing to pull |
| `PullIntervalSeconds` | 60 | Interval between two runs |
| `LagSeconds` | 120 | Lag window: rows younger than this are held back, not skipped |
| `BatchSize` / `MaxPagesPerRun` | 5000 / 40 | Rows per page, pages per run |
| `GapSweepIntervalMinutes` / `GapSweepDays` | 60 / 3 | Retroactive gap check |
| `ProjectionIntervalSeconds` | 60 | Interval of the projection |
| `RequireLogin` / `DashboardPasswordHash` | true / — | Sign-in to the screens |
| `ShowActorIdentity` | false | Real names instead of pseudonyms |
| `ActorHashKey` | — | Key of the pseudonyms. Set once, keep it |
| `VocabularyPath` | `vocabulary` | Directory holding the four vocabulary files, read at startup |
| `HolidayCalendarId` / `WorktimeCalendarName` | automatic | Which calendar determines the working time |
| `DayStartsAt` | 07:00 | Start of the working day for the duration arithmetic |
| `AllowWriteCapableLogin` | false | Emergency exit. Normally the app refuses to start with a write-capable account |

Plus `ConnectionStrings__DefaultConnection` (its own Postgres database), `POSTGRES_PASSWORD` and `TZ`.

Ports: app **5100**, Postgres **5434** — 5434 rather than 5432 so it does not collide with a Postgres already
running on the host.

## The account on the source

Everything hangs off a dedicated account rather than the application login, so reading a production database for
analysis cannot affect the application that owns it:

```bash
sqlcmd -S <host> -U <admin> -P <pw> -C \
       -v LoginPassword="…" -v DatabaseName="<database>" \
       -i scripts/sql/create-readonly-login.sql
```

The script creates the account, grants **individual table rights** instead of `db_datareader` — so only the tables
above are readable and a table added later is not opened along with them — denies every write explicitly, and caps
the account at low priority via the Resource Governor. At the end it checks itself: reading allowed, writing denied,
anything outside the grant list invisible.

The app does not rely on that: it enforces `ApplicationIntent=ReadOnly`, talks to the source exclusively over raw
ADO.NET, and refuses to start when the account may write.

## When something is wrong

| Symptom | Cause |
|---|---|
| Page loads but stays empty | Almost always the sign-in: `/api/…` answers 401, the page shows nothing. Sign in again |
| "Keine Quelle konfiguriert" on **Spiegel** | No connection string set, or the source is unreachable |
| Database sign-in fails inside the container | The container does not inherit host-level routes (VPN, tunnel, hosts entry). Check from the host first whether it works at all |
| A step is called `⚠ something.dotted.v1` | Label missing. New type at the source → refresh `source-catalogue.txt` and name it in `labels.tsv` |
| Every step is called `⚠ …` | The vocabulary directory is not mounted, or `VocabularyPath` points somewhere else. The startup log says how many labels it loaded |
| Held-back rows keep growing | The source writes faster than the lag window allows — check `LagSeconds` |
| Diagram is old | The miner only runs on demand. Its age is on the page |

## Container and development

```bash
# Build from source (tags ghcr.io/…:local, pulls nothing)
docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build

# Switch to the current release. A plain `docker compose pull` does nothing:
# docker-compose.yml sets `pull_policy: never` as a guard against falling back by accident.
PROCESSANALYZER_PULL_POLICY=always docker compose pull processanalyzer && docker compose up -d

dotnet build ProcessAnalyzer.Web
dotnet test ProcessAnalyzer.Tests          # needs a running Docker daemon
```

Architecture, rules and the reasons behind them are in [CLAUDE.md](CLAUDE.md), contributing in
[CONTRIBUTING.md](CONTRIBUTING.md).

## Source schema

The tool reads two tables. A producer — anything in the ASP.NET Core world that wants to be analysed — has to
provide them in this shape; nothing else about the producer matters.

```sql
CREATE TABLE dbo.BusinessEvents (
    Id                bigint IDENTITY PRIMARY KEY,  -- monotonic, the pull's watermark
    EventId           uniqueidentifier NOT NULL,    -- stable identity of the fact
    EventType         nvarchar(200)    NOT NULL,    -- '<module>.<object>.<past-tense>.v1'
    OccurredAt        datetime2        NOT NULL,    -- when it happened
    RecordedAt        datetime2        NOT NULL,    -- when it was written
    PerformerType     nvarchar(50)     NOT NULL,    -- User | System | ScheduledJob | ExternalSystem | Device
    PerformerId       nvarchar(450)    NULL,
    InitiatorType     nvarchar(50)     NULL,        -- who caused it, when that differs
    InitiatorId       nvarchar(450)    NULL,
    CorrelationId     nvarchar(100)    NULL,
    TraceId           nvarchar(100)    NULL,
    SourceApplication nvarchar(100)    NULL,
    SourceModule      nvarchar(100)    NULL,
    SourceVersion     nvarchar(50)     NULL,
    Payload           nvarchar(max)    NULL,        -- JSON object
    MandateId         bigint           NULL
);

CREATE TABLE dbo.BusinessEventObjects (
    Id              bigint IDENTITY PRIMARY KEY,
    BusinessEventId bigint        NOT NULL REFERENCES dbo.BusinessEvents(Id),
    ObjectType      nvarchar(100) NOT NULL,  -- 'document', 'tour', 'order-detail', …
    ObjectId        nvarchar(200) NOT NULL,
    Qualifier       nvarchar(100) NULL       -- the object's role in this event
);
```

Three properties the pull depends on, and they are the producer's job:

- **`Id` is monotonic and never reused.** It is the watermark.
- **The journal is append-only.** Rows are never updated or deleted.
- **One event may reference several objects.** That is what makes the log object-centric rather than a flat trace,
  and it is why `BusinessEventObjects` is a table and not a column.

Optionally, if a directory is available, names, groups and a business calendar are read from
`AspNetUsers`, `UserGroups`, `UserGroupMembers`, `HolidayCalendarEntries` and `WorktimeCalendarEntries`. Without
them the tool still works — actors appear as pseudonyms and durations fall back to calendar time.

## License

AGPL-3.0-or-later — see [LICENSE](LICENSE). pm4py's community edition is AGPL, and anything using it inherits that.
