# Notification Hub

A C# / ASP.NET Core 10 notification service with PostgreSQL durability, Redis shared admission and dispatch limits, supplier-defined request mapping, leased workers, expiration, retries, incidents, and signed business webhooks.

The application follows **Controllers -> Services -> Persistence**, with reusable infrastructure in **Commons** and background execution in **Workers**. API and worker replicas use the same executable and database. They can be deployed independently.

## Browser console

The service now hosts a responsive, bilingual browser console at `/`. Its pages are:

- `/#overview`: delivery totals, recent notifications, and open incidents.
- `/#providers`: register suppliers, create capability drafts, preview mapped requests, and publish versions.
- `/#connections`: create tenant connections, update encrypted credentials, toggle connections, and inspect health.
- `/#send`: select published capabilities and connections, enter business JSON, choose an expiration, and submit with an idempotency key.
- `/#notifications`: server-paginated notification records and status filters. Open a record for attempt history and audited retry/cancel/confirm actions.
- `/#tickets`: incident records, details, and resolution forms.
- `/#settings`: set business/provider API keys in page memory. Keys are never persisted to browser storage.

The language selector switches between Chinese and English and preserves unfinished page forms. The console uses the existing authorization and validation rules; hiding an action in the UI is not an authorization boundary. All user-provided text is escaped before rendering.

In this workspace, `scripts/start-local.ps1` starts the local dependencies and opens ports **5055** (API) and **8090** (development console gateway). It requires the workspace-local tools and existing demonstration database created during setup. Run it after a Release build. Processes run in the background and write persistent logs under `.local`, so closing the initiating terminal does not break HTTP responses.

The loopback-only gateway in `examples/console_gateway.py` serves the same production console assets and supplies the two configured demo identities on the server side. It validates Host, Origin, request size, and API path; it does not send keys to the browser. **Never expose this development gateway publicly.** Normal production hosting serves the console directly from ASP.NET Core and requires API keys through Access settings.

## Run with Docker

Requirements: Docker Engine with Compose. For development and tests, install the .NET 10 SDK.

```sh
cp .env.example .env
# Replace the example values in .env.
docker compose up --build -d
curl http://localhost:8080/health/ready
```

The Compose deployment includes PostgreSQL 17, Redis 7, one API instance, one worker instance, and a local-only Python supplier simulator. API access is bound to localhost. PostgreSQL and Redis have persistent volumes, and API/worker replicas share the Data Protection key volume.

Scale workers independently:

```sh
docker compose up -d --scale worker=3
```

The Compose configuration permits the private `mock` hostname **only for the local demo**. For deployment, set `Hub__AllowPrivateNetworks=false`, provide an explicit `Hub__AllowedHosts__N` list of approved HTTPS destinations, replace API keys with secrets from your environment, and place the service behind a TLS ingress. Do not expose the simulator.

## Run without Docker

Start PostgreSQL and Redis, create an empty database, and set configuration through environment variables. PowerShell example:

```powershell
$env:ConnectionStrings__Postgres = 'Host=localhost;Database=notification_hub;Username=hub;Password=YOUR_PASSWORD'
$env:ConnectionStrings__Redis = 'localhost:6379'
$env:Clients__0__Id = 'business-app'
$env:Clients__0__Key = 'YOUR_BUSINESS_API_KEY_AT_LEAST_32_CHARACTERS'
$env:Clients__0__Role = 'business'
$env:Clients__1__Id = 'supplier-team'
$env:Clients__1__Key = 'YOUR_PROVIDER_API_KEY_AT_LEAST_32_CHARACTERS'
$env:Clients__1__Role = 'provider'
$env:Hub__AllowedHosts__0 = 'api.supplier.example'
$env:ASPNETCORE_URLS = 'http://localhost:8080'
dotnet run --project src/NotificationHub.Api
```

`Hub__RunWorkers=false` starts an API-only instance. `true` enables delivery, recovery, and outbox workers. Every instance still hosts controllers, so worker deployments should have no public ingress. `Hub__MigrateOnStartup=true` applies the idempotent initial schema under a PostgreSQL advisory lock; use a dedicated migration identity and disable this option on runtime replicas in production.

## Demonstration

After starting Compose, export `PROVIDER_API_KEY` and `BUSINESS_API_KEY` with the same values as `.env`, then:

```sh
python examples/demo.py
```

The demo registers and publishes a provider capability, creates a tenant connection, subscribes to health events, submits the same event twice, checks its idempotent receipt, and polls until the delivery completes. The default supplier fails its first two attempts and then succeeds.

Alternative supplier URLs:

| Simulator path | Behavior |
| --- | --- |
| `/flaky` | First two requests fail; third succeeds |
| `/always-fail` | Persistent 503; the demo's three-attempt budget creates a ticket |
| `/timeout-after-commit` | Commits the first request, delays its response, then deduplicates retries |
| `/json` | Accepts JSON immediately |
| `/form` | Accepts form-encoded requests; register the operation with `encoding=form` |
| `/events`, `/tickets` | Records platform events and ticket webhook requests |
| `GET /` | Shows processed identifiers, attempt counts, and received events |

Set `SUPPLIER_URL=http://mock:8081/always-fail` when using Compose. When running everything directly on the host, run `python examples/mock_supplier.py`, approve `127.0.0.1` with private networking enabled for this local test, and use `SUPPLIER_URL=http://127.0.0.1:8081/flaky`.

## API contracts

Authenticate with `X-Api-Key`. Roles are `provider`, `business`, and `admin`; identifiers and roles come from server-side configuration, never from a request-supplied tenant ID. Provider ownership is enforced in services. Business connections, notifications, subscriptions, and events are tenant-scoped. Admins can inspect cross-tenant tickets and operational metrics; delivery mutation remains scoped to the caller's tenant.

### Provider registration and mapping

```http
POST /v1/providers
X-Api-Key: <provider key>
Content-Type: application/json

{"name":"Example CRM"}
```

Add an immutable draft version:

```http
POST /v1/providers/{providerId}/operations/contact.update/versions
```

```json
{
  "eventType": "contact.updated.v1",
  "description": "Synchronize a contact's paid status with the CRM after a successful subscription payment.",
  "url": "https://api.supplier.example/contacts/update",
  "method": "POST",
  "encoding": "json",
  "headers": {"X-Integration": "notification-hub"},
  "inputs": [
    {"name": "contact_id", "type": "string", "required": true},
    {"name": "status", "type": "string", "required": true}
  ],
  "body": {
    "customer.id": {"source": "data.contact_id"},
    "state": {"source": "data.status", "values": {"paid": 1}},
    "source": {"constant": "internal"}
  },
  "success": {"httpStatuses": [200], "bodyPath": "code", "expectedValue": 0},
  "authType": "bearer",
  "authHeader": "Authorization",
  "idempotencyHeader": "Idempotency-Key",
  "idempotencyRetentionHours": 168,
  "timeoutSeconds": 15,
  "maxAttempts": 8,
  "maxAgeHours": 24,
  "requestsPerMinute": 60
}
```

Supported methods: POST, PUT, PATCH. Supported encodings: JSON and URL-encoded form. Sources use dotted object paths. JSON output keys support dotted nesting; form keys are literal. Mappings preserve JSON value types, support constants and enum translations, and fail on missing mapped inputs. Input types: string, number, boolean, object, array. This is a bounded mapping language, not arbitrary code execution or full JSON Schema.

`description` is optional plain-text capability documentation, limited to 2,000 characters. It is stored with each immutable operation version and shown in supplier management and recipient selection. Descriptions are metadata and are not sent to suppliers. Existing versions without descriptions continue to work; add a new version to document an already published capability. Console labels are localized; supplier-authored descriptions are displayed as entered, without automatic translation.

Authentication modes: `none`, `bearer`, `header`. Secrets belong in a tenant connection, never in shared static headers. Both operation registration and publication validate destination policy. `/preview` generates a sample request with redacted credentials; it does not send anything or establish ownership of a remote domain. Publication assumes the configured provider identity and operator-managed host allowlist are trusted. A production onboarding workflow can add domain ownership verification and approval.

Preview, then publish:

```http
POST /v1/providers/{providerId}/versions/{versionId}/preview
{"data":{"contact_id":"c123","status":"paid"}}

POST /v1/providers/{providerId}/versions/{versionId}/publish
```

### Connection and notification

```http
POST /v1/connections
X-Api-Key: <business key>
Content-Type: application/json

{"providerId":"<uuid>","name":"Main CRM","credential":"<supplier token>"}
```

The caller may choose a published provider and supply its own authorized supplier credential. Connections cannot be accessed by other tenants. Connection credentials and subscription signing secrets are protected using ASP.NET Core Data Protection and excluded from response serialization.

```http
POST /v1/notifications
X-Api-Key: <business key>
Idempotency-Key: subscription-paid-order123
Content-Type: application/json

{
  "eventType":"contact.updated.v1",
  "targets":[{"connectionId":"<uuid>","operation":"contact.update"}],
  "data":{"contact_id":"c123","status":"paid"}
}
```

An optional `expiresAt` must be a future ISO 8601 timestamp within every selected operation's maximum age. Without it, each delivery receives its operation's default maximum age. Up to 20 distinct targets are allowed per notification. The entire request validates and commits atomically; individual deliveries then progress independently.

New notifications return `202` plus `Location`. Duplicate identical submissions return `200` with the original ID and current delivery states. Different content under the same tenant/key returns `409`. Targets and JSON object properties are canonicalized for hashing. The caller must retry with the original logical request; generating a different explicit expiration on every attempt constitutes different content.

### Complete endpoint inventory

| Method | Path | Role / behavior |
| --- | --- | --- |
| GET | `/v1/providers` | Any authenticated caller; provider directory |
| POST | `/v1/providers` | Provider/admin; register |
| GET | `/v1/providers/{id}/versions` | Published versions; owners can also view drafts |
| POST | `/v1/providers/{id}/operations/{operation}/versions` | Owner; create draft |
| POST | `/v1/providers/{id}/versions/{versionId}/preview` | Owner; validate sample mapping |
| POST | `/v1/providers/{id}/versions/{versionId}/publish` | Owner; publish immutable version |
| PATCH | `/v1/providers/{id}/enabled` | Owner; emergency enable/disable |
| GET | `/v1/connections` | Business/admin; own connections |
| POST | `/v1/connections` | Create own supplier connection |
| PATCH | `/v1/connections/{id}/enabled` | Pause/resume own connection |
| PUT | `/v1/connections/{id}/credential` | Rotate own encrypted credential |
| GET | `/v1/connections/{id}/health` | Health per capability |
| POST | `/v1/notifications` | Submit using Idempotency-Key |
| GET | `/v1/notifications?page=1&pageSize=20&status=stopped` | Tenant-scoped pagination; optional delivery status filter |
| GET | `/v1/notifications/{id}` | Notification and target delivery states |
| GET | `/v1/deliveries/{id}/attempts` | Attempt history, latest 200 |
| POST | `/v1/deliveries/{id}/retry` | Replay stopped, unexpired task; reason required |
| POST | `/v1/deliveries/{id}/cancel` | Cancel active/stopped task; reason required |
| POST | `/v1/deliveries/{id}/confirm` | Confirm an externally verified stopped task; evidence in reason |
| GET | `/v1/tickets` | Own tickets, or all for admin |
| GET | `/v1/tickets/{id}` | Ticket and up to 200 linked delivery IDs |
| POST | `/v1/tickets/{id}/resolve` | Resolution and reason; does not mutate deliveries |
| GET | `/v1/subscriptions` | Own event subscriptions |
| POST | `/v1/subscriptions` | Register signed event webhook |
| DELETE | `/v1/subscriptions/{id}` | Disable subscription and suppress unsent events |
| GET | `/v1/metrics` | Delivery counts, oldest pending age, stopped outbox count |
| GET | `/v1/outbox` | Event delivery state, latest 200 |
| POST | `/v1/outbox/{id}/retry` | Replay a stopped event with the same event ID |
| GET | `/health/live` | Public liveness |
| GET | `/health/ready` | Public database/Redis readiness; expose only to infrastructure |

Enabled request: `{"enabled":false}`. Credential request: `{"credential":"..."}`. Delivery/outbox actions: `{"reason":"Investigation reference and evidence"}`. Ticket resolution: `{"resolution":"fixed","reason":"Credential rotated"}`; allowed resolutions are `fixed`, `acknowledged`, `no_action`.

Notification records support page-based pagination with page sizes up to 100 and optional delivery status filtering. Other lists are bounded at 200 items in this first version. Cursor pagination, OAuth token refresh, custom supplier signatures, dynamic URL templates, and supplier asynchronous completion callbacks are not included. The definition interface and HTTP sender are extension points for those requirements.

## Language switching

English is the default. Send `Accept-Language: zh-CN` or `?culture=zh-CN` for Chinese errors. `?culture=en` overrides the header for that request. `Content-Language` identifies the selected language. Machine error codes and state identifiers stay stable across languages; consumer applications should localize those identifiers when rendering a UI.

Translations live in `Resources/messages.en.json` and `messages.zh-CN.json`. Chinese strings are Unicode-escaped so source files remain ASCII. No business logic depends on translated text. To add a language, add a catalog and register its culture in `MessageCatalog` and `Program.cs`.

## Reliability guarantees and boundaries

- `202` is returned only after the PostgreSQL transaction commits. It is not a statement that the supplier completed its business action.
- Delivery is at least once within its retry budget and lifetime. A supplier must atomically deduplicate the stable delivery ID together with its business operation. A lock alone cannot provide exactly-once external effects.
- Supplier success follows the configured HTTP status and optional JSON equality rule. Malformed success bodies are uncertain/retryable; explicit mismatches are terminal business rejections. Retryable HTTP statuses can be explicitly configured, otherwise 408/429/5xx retry.
- `FOR UPDATE SKIP LOCKED` atomically claims a task. Claims have a 60-second default lease and a new token on each acquisition. HTTP total timeout is at most 30 seconds and additionally bounded by remaining notification lifetime. HTTP calls never hold database transactions open. Workers claim one task per available loop, not large idle batches.
- Completion, defer, and attempt-start operations check current ownership. An expired lease allows recovery; the former owner cannot overwrite a new owner's state. A paused process may still perform a remote action, so supplier idempotency remains necessary.
- Expired tasks are not dispatched or manually replayed. A retry that cannot fit before expiry terminates early. Requests already sent before expiry/cancellation may still execute remotely. Strict execution-time expiration requires supplier enforcement.
- Attempt budgets count durable attempt starts, including a crash after the start record. Waiting for rate limits or circuits does not consume attempts. Manual replay preserves total attempts and the delivery ID, while starting a new bounded retry round with an audit reason.
- The original mapping version and input are pinned; authorized credential rotation affects subsequent attempts. Provider/connection disabling is checked again before attempt start and suppresses pending tasks.
- An uncertain outcome is retained as history even if a later attempt succeeds. A stopped task can only be confirmed after external verification.
- No strict event ordering is promised. For state updates, include an entity version and have the supplier reject stale changes. Automatic replacement of old state events is not implemented.
- Before platform acceptance, the business application must use its own transactional Outbox or equivalent durable submission mechanism. This platform cannot recover an event that was never submitted.
- Notification/key records are not automatically deleted. Establish an explicit payload retention and archival policy before production, retaining deduplication tombstones for the contractual period. Supplier idempotency retention must cover allowed replay. Backups and PostgreSQL HA are deployment responsibilities.

## Quotas, circuits, and incidents

Redis atomically implements per-tenant read/write admission token buckets, per-tenant and per-connection/capability send token buckets, and expiring concurrency slots. API replicas share keys. Redis failure fails API admission closed with `503`; workers stop sending and their durable task leases recover. Redis counters are traffic controls, not the source of task durability. Redis failover can reset approximate traffic state.

PostgreSQL serializes each tenant's new-task quota and backlog admission in the notification transaction. A fan-out to N destinations costs N new deliveries. Duplicate requests consume ingress capacity but no new-task budget. Rejected requests are not accepted silently. Retry traffic shares sending quotas with new work. Delayed tasks move behind ready work; this is quota-based isolation, not a weighted fair scheduler.

Important defaults (all under `Hub`):

| Setting | Default |
| --- | --- |
| `IngressPerMinute` | 300 for each read/write lane; token bucket allows a full-capacity burst |
| `NewDeliveriesPerMinute` | 600; database window resets 60 seconds after its start |
| `MaxPendingPerTenant` | 10,000 |
| `TenantSendsPerMinute` | 600 |
| `TenantConcurrency` / `ConnectionConcurrency` | 16 / 4 |
| `WorkerConcurrency` / `LeaseSeconds` | 4 / 60 |
| `CircuitFailureThreshold` | 5 consecutive systemic failures |
| `CircuitCooldownSeconds` | 60 |
| `HealthReminderMinutes` | 60 |
| `OutboxMaxAttempts` | 20 |

Circuits are scoped to a connection/capability, and also open when at least 20 completed attempts in 10 minutes have an 80% systemic failure rate. Authentication errors open immediately. Permanent payload errors do not degrade an entire endpoint. PostgreSQL coordinates a single half-open probe; two successful recovery probes return the endpoint to healthy. New submissions can still be accepted while open, within backlog and expiration limits; receipts report endpoint health.

Terminal failures and circuit failures create internal tickets in the same transaction as task/health updates. Open tickets aggregate by tenant, connection, capability, and failure category. Health transitions and periodic reminders create tenant event Outbox entries. Expiration emits an event but does not create a ticket for each expired task.

Subscribe using:

```json
{"url":"https://business.example/notification-events","signingSecret":"A_SECRET_OF_AT_LEAST_32_CHARACTERS"}
```

Events include `endpoint.health_changed`, `endpoint.health_reminder`, `delivery.expired`, and `ticket.created`. Each webhook sends a stable `eventId`, `type`, and `data`, plus:

- `Idempotency-Key`: event UUID, unchanged across retries.
- `X-Hub-Timestamp`: Unix seconds for this attempt.
- `X-Hub-Signature`: lowercase hex HMAC-SHA256 of `timestamp + "." + exact UTF-8 request body`.

Receivers must verify the signature and timestamp, deduplicate event IDs, and return 2xx only after durable acceptance. Outbox delivery uses separate HTTP clients and worker loops from supplier delivery. It still shares the platform database and deployment; it is not an independent disaster-notification system.

Set `Hub__TicketWebhookUrl` and `Hub__TicketWebhookSecret` to forward new ticket events to an external ticket gateway. The gateway should upsert by `data.ticketId` and map this generic event to Jira/ServiceNow/etc. No vendor-specific connector is claimed. Internal ticket details always show current affected counts; only ticket creation is pushed to the external gateway in this version. If the external service lacks idempotent upsert, it must reconcile ambiguous responses to avoid duplicate tickets.

Monitor `/v1/metrics`, stopped Outbox entries, and structured error logs with an independent monitoring system. This repository exposes the signals; it does not configure your paging system. Closing a ticket does not mark its deliveries successful. Use audited retry, confirm, or cancel actions explicitly.

## Security and operations

- All SQL values are parameterized. Provider definitions are data, not executable templates.
- Only operator-allowlisted hosts can be targeted. DNS answers are checked both during validation and in the actual socket connection. Private/reserved destinations are rejected by default, redirects are disabled, and proxy/cookie reuse is disabled.
- Request size is capped at 256 KiB by Kestrel; response reads are bounded at 64 KiB. Field/target counts and HTTP timeouts are bounded.
- Outbound credentials are encrypted through Data Protection. Protect and back up the shared key ring; it is a root secret, and its default filesystem persistence does not encrypt the key ring itself on Linux. Use a restricted volume and your platform's key-management integration for production.
- API keys are a small self-contained authentication adapter. Configure strong keys through secrets, terminate TLS, restrict admin access, and replace with OIDC/service identity where appropriate. Unknown and invalid keys never reach PostgreSQL business operations. Apply network-level limits to unauthenticated traffic at ingress.
- HTTP request/response payloads and credential headers are not logged. Attempt records retain error categories and HTTP codes rather than raw supplier responses. Notification payloads are durable business data; apply database access controls, encryption at rest, backups, and retention policies to them.
- Lease clocks and quota windows use PostgreSQL time. Synchronize application hosts for HTTP expiration checks and webhook timestamps. Current leases are intentionally longer than bounded HTTP operations; longer workflows would need a renewal protocol.

## Tests

Every controller action has a unit test. HTTP pipeline tests cover authentication on every business endpoint, role rejection, validation, localization, throttling, receipts, and health endpoints. Protocol and HTTP sender tests cover mapping, error classification, safe destinations, response bounds, expiration, and retry headers.

```sh
dotnet test NotificationHub.sln -c Release
```

Without integration settings, real database/Redis tests are explicitly skipped. Enable them to verify concurrency and durability:

```powershell
$env:TEST_POSTGRES = 'Host=localhost;Database=postgres;Username=postgres;Password=YOUR_TEST_PASSWORD'
$env:TEST_REDIS = 'localhost:6379'
dotnet test NotificationHub.sln -c Release --logger trx --results-directory TestResults
```

On Windows, `scripts/test.ps1` runs the non-integration suite; `scripts/test.ps1 -Integration` requires both integration environment variables and runs the full suite. It can use the workspace-local SDK in a sibling `.tools/dotnet` directory when `dotnet` is not on PATH.

The PostgreSQL test principal must have `CREATEDB`. Tests create a random `hub_test_<uuid>` database, run migrations, and drop only that database afterward. Redis tests use random, expiring keys. Integration tests verify simultaneous submissions, quota races, disjoint claims, stale-owner fencing, expiry, tenant isolation, pinned versions, ticket aggregation, single-probe recovery, manual replay, cancellation, and signed Outbox retry. GitHub Actions runs both unit and real-service tests.

See [architecture.md](docs/architecture.md) for diagrams and [validation.md](docs/validation.md) for the local verification record.

Frontend validation uses Node.js and JSDOM:

```sh
npm ci
npm test
```

Set `TEST_UI_URL=http://127.0.0.1:8090` to include the real-API console workflow test. This drives the DOM forms through provider registration, preview, publication, connection creation, notification submission, duplicate handling, delivery inspection, and language switching. It creates isolated named demo records in the running demonstration environment. Without this variable, that integration test is explicitly skipped. The frontend itself uses native browser modules and requires no Node.js runtime or bundling in production.

If submitting the original exercise, export the complete unedited AI conversation from the client and include its ZIP in the repository. This project does not fabricate or truncate that transcript.
