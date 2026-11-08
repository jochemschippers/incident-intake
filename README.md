# Incident Intake — Slack ⇄ Cosmos DB ⇄ GitHub (Assignment 3)

Proof of concept: an engineer mentions the bot in `#development-incidents`
(`@incidentbot sev1 Payments API returns 500`). The incident is logged in Cosmos DB,
a GitHub issue is opened, GitHub comments are mirrored back into the Slack thread,
and closing the issue resolves the incident (→ MTTR analytics). No frontend.

## Architecture

```
Slack (app_mention) ──HMAC──► API  POST /api/slack/events ─► ReportIncidentCommand ─► [incident-intake]
                                                                                           │
Fn1 PersistIncident     ◄──────────────────────────────────────────────────────────────────┘
   └─ Cosmos incidents (write model, PK /id) ─► [github-create]
Fn2 OpenGitHubIssue     ◄── [github-create]
   ├─ GitHub REST: create issue (PAT)
   ├─ Table IssueLookup (issue# → incident id)
   ├─ Cosmos PATCH gitHubIssueNumber
   └─ Slack chat.postMessage (thread reply)
GitHub webhook ──HMAC──► API  POST /api/github/webhook ─► ForwardGitHubEventCommand ─► [github-events]
Fn3 ApplyGitHubEvent    ◄── [github-events]
   ├─ comment → Slack thread
   └─ closed  → Cosmos PATCH status/resolvedAt + Slack "resolved in X min"
Fn4 ProjectIncidents    ◄── Cosmos change feed (incidents)
   └─ Cosmos incident-analytics (read model, PK /period)
API  GET /api/incidents/{id} | /api/incidents?period=&severity= | /api/analytics/mttr?period=   (X-Api-Key)
```

## Rubric mapping

| Criterion | Where |
|---|---|
| **Cloud DB architecture (45)** | See *Data design* below. `src/Incidents.Infrastructure/Cosmos.cs`, `Storage.cs`, `DependencyInjection.cs` |
| **N-tier + ≥2 Functions (10)** | `Api` (presentation) → `Application` (business: commands, queries, validation, rules) → `Infrastructure` (data). 4 Functions in `src/Incidents.Functions` |
| **Loosely coupled + testable (20)** | Application depends only on interfaces (`Abstractions/Contracts.cs`); services talk via queue message contracts. 18 xUnit tests with NSubstitute mocks (`tests/`) |
| **CQRS (25)** | MediatR commands vs queries (`Application/Commands`, `Application/Queries`), `ValidationBehavior` pipeline, writes go through Azure Storage Queues, reads come from a separate read model fed by the change feed |

## Data design

| Store | Key | Reasoning |
|---|---|---|
| Cosmos `incidents` (write model) | PK `/id` = Slack `event_id` | High cardinality → writes spread evenly, no hot partition. All write-side reads are point reads (~1 RU). Creating an existing id returns 409 → Slack retries and queue redeliveries are deduplicated for free. **Indexing disabled** (`/*` excluded) since only point reads are used → cheaper writes. Updates are field-level **PATCH** so concurrent functions never overwrite each other (no lost update). |
| Cosmos `incident-analytics` (read model) | PK `/period` (`yyyy-MM`) | Every list/MTTR query is “for month X” → always single-partition, never fan-out. One doc per incident, **upserted** from the change feed → idempotent projection (at-least-once delivery is safe). A month of incidents stays far below the 20 GB logical partition limit. |
| Table Storage `IssueLookup` | PK `owner_repo`, RK issue number | Directory-based lookup: GitHub only knows the issue number. PK+RK → point lookup, cheapest Table read. |
| Storage Queues `incident-intake`, `github-create`, `github-events` | — | Load leveling + decoupling: Slack needs a reply < 3 s, so the API only enqueues. Retries 5× then `-poison` queue. Handlers are idempotent because Storage Queues are at-least-once and unordered. |

Throughput: Cosmos **serverless** for this PoC (bursty, low volume, pay per RU). Production: autoscale
(e.g. 1000 RU/s max) on `incidents`; analytics can share database throughput.

## Security

- **Inbound webhooks are authenticated**: Slack `X-Slack-Signature` and GitHub `X-Hub-Signature-256`
  are HMAC-SHA256 over the raw body, compared in constant time. Slack requests older than 5 min are rejected (replay protection).
  (`Application/Security/WebhookSignatures.cs`)
- **Read API** requires `X-Api-Key` (constant-time compare). Production upgrade: App Service Easy Auth / Entra ID.
- **Least privilege tokens**: GitHub fine-grained PAT for one repo, *Issues: read & write* only.
  Slack bot scopes: `app_mentions:read`, `chat:write`.
- **No secrets in source**: locally `dotnet user-secrets` (API) and git-ignored `local.settings.json` (Functions).
  In Azure: secrets in **Key Vault**, referenced from app settings as `@Microsoft.KeyVault(SecretUri=...)`,
  read by the app's **Managed Identity**.
- **No storage/DB keys in Azure**: set `Storage:AccountName` / `Cosmos:Endpoint` instead of connection strings and
  the code uses `DefaultAzureCredential` (Managed Identity + RBAC roles: *Storage Queue Data Contributor*,
  *Storage Table Data Contributor*, *Cosmos DB Built-in Data Contributor*).
- Bot messages are ignored (`bot_id`) to prevent loops.

## Run locally

Requirements: .NET 8 SDK, Azure Functions Core Tools v4, Docker.

### Configuration of this PoC

| Setting | Value | Stored in |
|---|---|---|
| GitHub repo for incident issues | `jochemschippers/incident-intake` | `appsettings.Development.json`, `local.settings.json` |
| Slack workspace channel | `#development-incidents` | — (bot is only invited there) |
| Slack bot | `@incidentbot` | — |
| Public URL (ngrok static domain) | `https://unplenteously-unshaping-maynard.ngrok-free.dev` → `localhost:5063` | — |
| Slack Request URL | `https://unplenteously-unshaping-maynard.ngrok-free.dev/api/slack/events` | Slack app config |
| GitHub webhook URL | `https://unplenteously-unshaping-maynard.ngrok-free.dev/api/github/webhook` | repo webhook config |
| `Slack:SigningSecret`, `GitHub:WebhookSecret`, `Api:ReadKey` | secret | API user-secrets (outside repo) |
| `GitHub__Token`, `Slack__BotToken` | secret | `src/Incidents.Functions/local.settings.json` (git-ignored) |

Secrets are deliberately **not** in this repository (see *Security*).

```bash
docker compose up -d                          # Azurite + Cosmos emulator (UI: http://localhost:1234)

cd src/Incidents.Api
dotnet user-secrets set "Slack:SigningSecret"  "<Slack app > Basic Information > Signing Secret>"
dotnet user-secrets set "GitHub:WebhookSecret" "<same secret as in the GitHub webhook>"
dotnet user-secrets set "Api:ReadKey"          "<any random string>"
dotnet run --launch-profile http              # http://localhost:5063/swagger

cd ../Incidents.Functions
cp local.settings.example.json local.settings.json   # paste GitHub__Token and Slack__BotToken
func start

ngrok http --url=unplenteously-unshaping-maynard.ngrok-free.dev 5063

cd ../.. && dotnet test
```

Containers, queues and table are created automatically on startup in Development.

### Wire up Slack and GitHub

1. **Slack app** (api.slack.com/apps): Bot scopes `app_mentions:read`, `chat:write` → install to workspace →
   copy *Bot User OAuth Token* (`xoxb-…`) and *Signing Secret*. *Event Subscriptions* → Request URL
   `https://unplenteously-unshaping-maynard.ngrok-free.dev/api/slack/events` → subscribe to bot event `app_mention`.
   Invite the bot to `#development-incidents`.
2. **GitHub PAT**: Settings → Developer settings → Fine-grained token → only `incident-intake` → *Issues: Read and write*.
3. **GitHub webhook** (repo → Settings → Webhooks): URL
   `https://unplenteously-unshaping-maynard.ngrok-free.dev/api/github/webhook`, content type
   `application/json`, secret = `GitHub:WebhookSecret`, events: *Issues*, *Issue comments*.

### Try without Slack

`POST /api/slack/events` with a correctly signed body; the signature is
`v0=` + hex(HMAC-SHA256(signingSecret, "v0:{timestamp}:{body}")) in `X-Slack-Signature`,
timestamp in `X-Slack-Request-Timestamp`.

## Message format

`@incidentbot sev<1-4> <description>` — no severity → `sev3`.
