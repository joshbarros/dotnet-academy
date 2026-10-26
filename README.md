# dotnet-academy

Course cohorts and the seats their enrollments take. The scarce number is the **seats left** in a cohort, and the **Cohorts** module is its only writer: it holds seats with a single conditional `UPDATE` that matches only while the seats left still cover the enrollment, so when two learners fight for the last seat, one commits a hold and the other is told the cohort is full and no hold is written. A learner reference that contains the poison token `POISON` is committed as rejected by the Passes module, the message is dead-lettered, and a compensating command gives the seats back. A later withdrawal also gives the seats back, but it does not roll back the access pass that was already issued.

One ASP.NET Core process, one Postgres database. An HTTP write saves the enrollment and an outbox row in one local transaction; a `BackgroundService` relay publishes outbox rows to RabbitMQ (the work queue) and Kafka (the event log); the next module consumes from RabbitMQ, claims an inbox key in the same transaction as its own write, and writes its own row and outbox row. Saga steps never call each other in-process. Delivery is at-least-once: the relay can publish and crash before it marks a row sent, so the row goes out again, and every consumer dedupes with its inbox key. Each saga step only moves an enrollment forward from the status it expects.

> The Compose network is local and unauthenticated. There is no login, no token and no cookie. Do not publish these ports; Compose binds all of them to `127.0.0.1`.

![The academy UI after a few enrollments: two enrolled, one withdrawn with its access pass kept, one rejected for the poison token, one short](docs/screenshot.png)

*The UI reads everything from the API. Shown: enrolled learners, a withdrawal that gave 2 seats back and kept its access pass, an enrollment rejected for the poison token, and one that was short.*

## Enrollment statuses

```mermaid
flowchart LR
    Requested -->|seats held| Held
    Requested -->|short: no hold written| Rejected
    Held -->|access pass issued| Enrolled
    Held -->|poison reference: pass rejected| Rejected
    Held -->|withdrawal| Withdrawn
    Enrolled -->|withdrawal| Withdrawn
```

`Withdrawn` and `Rejected` (after a hold) give the seats back through a `ReleaseSeats` command, and Cohorts releases a hold at most once. The access pass has its own status (`Issued` or `Rejected`) and a withdrawal never changes it. If the withdrawal gets there before the final mark, the late mark finds the enrollment is no longer `Held` and does nothing.

## How the modules talk

```mermaid
sequenceDiagram
    participant C as Client
    participant E as Enrollments
    participant H as Cohorts
    participant P as Passes

    C->>E: POST /api/enrollments
    Note over E: enrollment + outbox row, one transaction
    E-->>C: 202 Requested
    E->>H: seats.reserve (relay → RabbitMQ, copy to Kafka)
    Note over H: inbox claim, conditional UPDATE, hold, outbox row
    alt seats left cover the enrollment
        H->>P: seats.held
        H->>E: seats.held (Requested → Held)
        alt reference is clean
            Note over P: access pass, outbox row
            P->>E: pass.issued (Held → Enrolled)
        else reference contains POISON
            Note over P: committed Rejected, then the message is dead-lettered
            P->>H: seats.release (compensating command)
            P->>E: pass.rejected (Held → Rejected)
            H->>E: seats.released
        end
    else cohort full
        H->>E: seats.short (Requested → Rejected, no hold)
    end
    C->>E: POST /api/enrollments/{id}/withdrawal
    E->>H: seats.release (Held or Enrolled → Withdrawn)
    H->>E: seats.released
```

| Module | Owns | Consumes | Emits |
| --- | --- | --- | --- |
| **Enrollments** (`Modules/Enrollments`) | `Enrollments`: status, seats, fee, learner reference. Validation, the poison rule, the fee. | `seats.held`, `seats.short`, `pass.issued`, `pass.rejected`, `seats.released` | `seats.reserve`, `seats.release` (withdrawal) |
| **Cohorts** (`Modules/Cohorts`) | `Cohorts.SeatsLeft` (sole writer after the cohort opens) and `SeatHolds`. | `seats.reserve`, `seats.release` (prefetch 1) | `seats.held`, `seats.short`, `seats.released` |
| **Passes** (`Modules/Passes`) | `AccessPasses`. | `seats.held` | `pass.issued`, `pass.rejected`, `seats.release` (poison) |
| **Messaging** (`Messaging`) | `OutboxMessages`, `InboxMessages`, the relay, the consumer loop with retry and dead-letter, the Kafka log. | | |

RabbitMQ topology is declared in code (`Messaging/Topology.cs`): exchange `academy.work`, one queue per module (`cohorts.commands`, `passes.work`, `enrollments.events`), a `.retry` queue per module (2 s TTL, then back to the main queue, 3 attempts), and a `.dlq` per module behind `academy.dlx`. A transient failure retries; a poison message goes straight to the dead-letter queue after its rejection is committed. Kafka has one topic, `academy.events`, one partition, replication factor 1, keyed by enrollment id.

## Run it

Needs Node 22 (npm 10), .NET SDK 10.0.401 (`global.json`), Docker (arm64 is fine) and python3 for the smoke script.

```bash
npm ci
docker compose up -d --wait db        # dotnet test uses Postgres on 5474 (database academy_test)
dotnet test tests/api/Academy.Api.Tests.csproj
docker compose up --build --wait      # db, kafka, rabbitmq, api, web
./scripts/smoke.sh                    # last line: academy smoke passed
npx playwright install chromium       # once
npx playwright test                   # one spec against http://127.0.0.1:8094
docker compose down                   # keeps the Postgres volume; `down -v` drops it
```

| Service | Host port |
| --- | --- |
| Web (nginx, proxies `/api/`) | 8094 |
| API (`/scalar/v1`, `/api/health`) | 5214 |
| Postgres 17 | 5474 |
| Kafka 3.8.1 (KRaft, one broker) | 9124 |
| RabbitMQ 3.13 / management UI (`platform` / `platform`) | 5704 / 15704 |

Inside the Compose network the API and nginx listen on 8080 and Kafka's internal listener is 9092.

## Try it with curl

```bash
curl -s -X POST http://127.0.0.1:5214/api/cohorts \
  -H 'content-type: application/json' \
  -d '{"code":"DIST-SYS-101","title":"Intro to Distributed Systems","track":"Engineering","instructor":"Ada Lovelace","seatCapacity":10}'

curl -s -X POST http://127.0.0.1:5214/api/enrollments \
  -H 'content-type: application/json' \
  -d '{"cohortCode":"DIST-SYS-101","reference":"LRN-1001","seats":3}'
# 202, status Requested, feeCents 15200. Use the returned id:

curl -s http://127.0.0.1:5214/api/enrollments/<id>          # Enrolled after a few seconds, with a pass number
curl -s http://127.0.0.1:5214/api/passes/<id>
curl -s http://127.0.0.1:5214/api/cohorts/DIST-SYS-101      # seatsLeft 7
curl -s http://127.0.0.1:5214/api/enrollments/<id>/events   # the Kafka log for this enrollment

curl -s -X POST http://127.0.0.1:5214/api/enrollments/<id>/withdrawal \
  -H 'content-type: application/json'                       # seats come back; the access pass stays
```

An enrollment is validated at the door: learner reference required (at most 40 characters), 1 to 5 seats. The fee is 4,900 cents per seat plus a 500 cent platform fee. The poison match is a case-sensitive substring: `LRN-POISON-7` and any longer token containing `POISON` match.

## Develop with Nx

The repository is an Nx workspace (`nx.json`, plugin `@nx/dotnet`). `npx nx show projects` lists `api` (the ASP.NET Core monolith), `web` (the Angular app) and the inferred xUnit project.

```bash
docker compose up -d --wait db kafka rabbitmq   # the brokers and Postgres, not the app
npx nx serve api                                # http://localhost:5214
npx nx serve web                                # http://localhost:4294, proxies /api to the API
WEB_URL=http://localhost:4294 npx playwright test   # the same browser specs, against the dev servers
npx nx run-many -t typecheck lint -p web        # tsc --noEmit and ESLint (angular-eslint)
npx nx build web && npx nx build api            # what CI builds
```

## What is tested, and where it runs

- **xUnit against real Postgres** (`tests/api`, 25 tests): two tasks with two `DbContext`s fight for one remaining seat (one `Held`, one `Short`, seats left 0, no hold for the loser); empty input, over-limit input, integer overflow (4294967297 must not wrap to 1), the fee, the poison substring; the same message delivered twice is claimed once; a release returns the seats once; the poison rejection is committed before the dead-letter; a withdrawal followed by the late final mark leaves the enrollment `Withdrawn` and the access pass `Issued`. The race test was run against a read-then-write version of the hold, where it failed, and against the conditional update, where it passes.
- **`scripts/smoke.sh`** drives the composed stack with a run id on every code, so a kept volume does not collide: validation, the happy path (fee, access pass, Kafka log), three concurrent enrollments for one seat (one enrolled, two short, no hold for the losers), the poison reference (rejected, `passes.work.dlq` gains one message, seats restored), and a withdrawal that lands between the access pass and the final mark (enrollment `Withdrawn`, pass `Issued`, seats back, second withdrawal `409`). To make that last window wide enough to hit on purpose, Compose sets `Enrollments__FinalMarkDelayMs=1500`; it is a latency knob and defaults to 0.
- **Playwright** (`e2e/specs/enroll.spec.ts`): opens a cohort in the UI, enrolls a learner, sees `Enrolled` and the access pass number, reloads to prove the page reads the API and keeps nothing in `localStorage`, then withdraws and sees the seats come back. It runs against the composed web port. A second spec, `e2e/specs/ui-flows.spec.ts`, drives the page through the refusal messages (over-limit, empty reference, duplicate), the poison rejection, the loser of a race for the last of the scarce number, and the Kafka event log panel. Both specs pass against the Compose stack and against the Nx dev servers.
- **CI** (`.github/workflows/ci.yml`, on push) has two jobs. `verify`: `npm ci`, Prettier check, `dotnet format whitespace --verify-no-changes`, `dotnet test` with a Postgres 17 service, `nx typecheck` and `nx lint` for web (ESLint with angular-eslint, template accessibility rules included, zero warnings allowed), and `nx build` for web and api. `e2e`: builds the Compose stack on the runner, runs `scripts/smoke.sh` and both Playwright specs against it, uploads the Playwright traces and prints the Compose logs on failure.

## API

All routes are unauthenticated JSON. Interactive docs are at `http://127.0.0.1:5214/scalar/v1` (OpenAPI at `/openapi/v1.json`); the web port proxies `/api/`, `/scalar/` and `/openapi/` too.

| Method and path | What it does | Responses |
| --- | --- | --- |
| `GET /api/health` | Liveness. | `200 Healthy` |
| `POST /api/cohorts` | Opens a cohort with its seat capacity. | `201`, `400` invalid, `409` code exists |
| `GET /api/cohorts` | Lists cohorts. | `200` |
| `GET /api/cohorts/{code}` | One cohort with the live seats left. | `200`, `404` |
| `POST /api/enrollments` | Accepts an enrollment (1 to 5 seats) and starts the saga. | `202` Requested, `400` invalid, `404` unknown cohort, `409` duplicate reference |
| `GET /api/enrollments?cohortCode=` | Lists the latest 100 enrollments, optionally for one cohort. | `200` |
| `GET /api/enrollments/{id}` | One enrollment and its status. | `200`, `404` |
| `POST /api/enrollments/{id}/withdrawal` | Moves a held or enrolled enrollment to `Withdrawn` and gives the seats back. The access pass is not touched. | `202`, `404`, `409` wrong status |
| `GET /api/enrollments/{id}/events` | The Kafka event log for one enrollment, read from the start of the topic. | `200` |
| `GET /api/passes/{enrollmentId}` | The access pass row. | `200`, `404` until the step has run |

Errors come back as `application/problem+json` with a `title`.

## Configuration

Compose sets these on the API container. Defaults in `apps/api/appsettings.json` point at the published host ports, so `nx serve api` works against `docker compose up -d --wait db kafka rabbitmq`.

| Setting | Compose value | Meaning |
| --- | --- | --- |
| `ConnectionStrings__Academy` | `Host=db;Port=5432;Database=academy;Username=academy;Password=academy` | The one Postgres database. Migrations run on startup. |
| `RabbitMq__Host` / `__Port` / `__User` / `__Password` | `rabbitmq` / `5672` / `platform` / `platform` | Work queue. Virtual host `/`. |
| `Kafka__BootstrapServers` | `kafka:9092` | Event log. |
| `Enrollments__FinalMarkDelayMs` | `1500` | Milliseconds the final mark waits. A latency knob so the smoke can land a withdrawal inside the window. `0` outside Compose. |
| `ACADEMY_TEST_DB` (tests only) | | Postgres connection for `dotnet test`. Defaults to `localhost:5474`, database `academy_test`. CI points it at its service container. |

## Look inside and fix things

```bash
docker compose ps                      # five services, all healthy
docker compose logs -f api             # relay, consumers, poison and retry lines
docker compose exec db psql -U academy -d academy -c 'select "RoutingKey","SentAt" from "OutboxMessages" order by "Id" desc limit 10'
open http://127.0.0.1:15704            # RabbitMQ management, platform / platform
```

- **Dead-lettered messages** sit in `passes.work.dlq`. Nothing replays them; inspect them in the management UI. Retries wait in `<queue>.retry` for 2 seconds.
- **A port is already in use**: `lsof -nP -i :5214` shows who holds it. Compose publishes only on `127.0.0.1`.
- **`dotnet test` cannot connect**: start Postgres first with `docker compose up -d --wait db`.
- **The smoke cannot reach the API**: run `docker compose up --build --wait` first; override the target with `API=... RABBIT=... ./scripts/smoke.sh`.
- **Start clean**: `docker compose down -v` drops Postgres, Kafka and RabbitMQ data. Plain `down` keeps them, and the smoke is safe on kept data because every code carries a run id.
- **The Kafka log looks empty right after an enrollment**: the relay publishes within a second or so; reload the event log.

## Known limits

- It is unauthenticated on purpose. Anything that can reach the API can read and write everything.
- There is no payment, no learner account and no course content: the scarce-number mechanics are the point, not a full LMS.
- Delivery is at-least-once, not exactly-once. The inbox makes repeats harmless for the saga; the Kafka log can hold a duplicate event after a relay crash.
- One broker, one partition, replication factor 1, one API instance. The relay does not coordinate across instances (the inbox would absorb the duplicates, but the outbox is never trimmed).
- `GET .../events` scans the whole topic for each call. Fine here, not something to expose at scale.
- Lists are capped at 100 rows and not paginated.
- Dead-lettered messages are not replayed automatically.
- The browser specs drive Chromium at a desktop size only, and the `e2e` CI job needs Docker and takes a few minutes.

## Cloud mapping

This repository runs on Compose. Nothing in it calls a cloud account.

| Here | On Azure |
| --- | --- |
| Postgres 17 | Azure Database for PostgreSQL |
| Kafka 3.8 | Event Hubs |
| RabbitMQ 3.13 | Service Bus queue with its dead-letter sub-queue |
| The API and the web app | Container Apps |

## Layout

```
apps/api      ASP.NET Core monolith: Modules/{Enrollments,Cohorts,Passes}, Messaging, Data, Migrations
apps/web      Angular 21, styled with Tailwind 4 utilities only
tests/api     xUnit against Postgres
e2e/          Playwright
scripts/      smoke.sh
compose.yaml  Postgres, Kafka, RabbitMQ, API, web
```

Nx 23.2.1 orchestrates `nx build web` and `nx build api`; EF Core migrations are committed and the API applies them on startup.

## Other systems in this .NET track

- [dotnet-freight](https://github.com/joshbarros/dotnet-freight): container space on ocean sailings.
- [dotnet-clinic](https://github.com/joshbarros/dotnet-clinic): appointment slots in a provider session.
- [dotnet-ledger](https://github.com/joshbarros/dotnet-ledger): outgoing payments from an account balance.
- [dotnet-warehouse](https://github.com/joshbarros/dotnet-warehouse): warehouse stock and outbound orders.
- [dotnet-compute](https://github.com/joshbarros/dotnet-compute): GPU-hour reservations in a pool.
- [dotnet-hotel](https://github.com/joshbarros/dotnet-hotel): hotel room-nights.
- [dotnet-tickets](https://github.com/joshbarros/dotnet-tickets): event ticket tiers and orders.
- [dotnet-carbon](https://github.com/joshbarros/dotnet-carbon): carbon credit batches and purchases.
- [dotnet-charging](https://github.com/joshbarros/dotnet-charging): EV charger slot reservations.
- [dotnet-promo](https://github.com/joshbarros/dotnet-promo): limited-use promo codes and redemptions.
