# Architecture and failure handling

```mermaid
flowchart TB
  Supplier[Supplier operator] --> Controllers[Registration controllers]
  Controllers --> Configuration[(Versioned supplier definitions)]
  Business[Business application and local Outbox] --> Admission[Authentication and Redis ingress limits]
  Admission --> API[Notification controller and service]
  API --> Transaction[Idempotency and quota transaction]
  Transaction --> PG[(PostgreSQL notifications and deliveries)]
  Transaction --> Receipt[202 persisted receipt]
  PG --> Claim[Worker atomic claim and lease]
  Claim --> Gates[Shared rate and concurrency gates]
  Configuration --> Mapping[Mapping and credential injection]
  Gates --> Mapping
  Mapping --> HTTP[Bounded HTTP request]
  HTTP --> External[Supplier API]
  HTTP --> Completion[Token-fenced result transaction]
  Completion --> PG
  Completion --> Health[(Endpoint health and tickets)]
  Completion --> Outbox[(Business and ticket event Outbox)]
  Outbox --> EventWorker[Independent signed webhook worker]
  EventWorker --> Business
  EventWorker --> TicketGateway[External ticket gateway]
```

## Atomic reception

The tenant quota row is locked before checking the idempotency key. All target validation, the notification row, its deliveries, and the new-delivery quota increment are in one transaction. A duplicate response can be reconstructed from existing tasks without spending another task quota. The database uniqueness constraint is a second guard against duplicate keys.

## Worker ownership

```mermaid
sequenceDiagram
  participant A as Worker A
  participant DB as PostgreSQL
  participant B as Worker B
  participant S as Supplier
  A->>DB: SKIP LOCKED claim with token A and deadline
  B->>DB: Claim another unlocked task
  A->>DB: Validate ownership and persist attempt start
  A->>S: Stable delivery ID, mapped request
  S->>S: Atomically deduplicate and process
  S--xA: Response lost
  Note over A: Worker exits before saving result
  DB->>DB: Recovery expires lease and invalidates token A
  B->>DB: Reclaim with token B
  B->>S: Retry same delivery ID
  S-->>B: Return previously accepted result
  B->>DB: Commit result using token B
  A->>DB: Late result using token A
  DB-->>A: Zero rows updated
```

The database token protects database state, not external side effects. Expiring Redis concurrency slots limit normal traffic but are also not an exactly-once mechanism. Supplier-side atomic deduplication is mandatory for operations such as inventory changes.

## Delivery lifecycle

```mermaid
stateDiagram-v2
  [*] --> pending
  pending --> processing: claim
  retry_wait --> processing: due and allowed
  processing --> succeeded: supplier accepted
  processing --> retry_wait: transient failure
  processing --> retry_wait: rate gate or lease recovery
  processing --> stopped: terminal failure or budget exhausted
  pending --> expired: deadline
  retry_wait --> expired: deadline
  processing --> expired: no viable retry before deadline
  stopped --> pending: audited safe replay
  stopped --> succeeded: audited external confirmation
  pending --> cancelled: caller cancellation
  processing --> cancelled: invalidate claim
  retry_wait --> cancelled: caller cancellation
  stopped --> cancelled: caller termination
```

Each replay retains the delivery UUID and historic attempt count. A new replay round gets a fresh per-round attempt budget, but never extends the original expiration or deduplication deadline. Cancellation cannot recall an HTTP request already in flight.

## Incident lifecycle

```mermaid
flowchart LR
  Failure[Failure or unhealthy endpoint] --> Ticket[Upsert grouped internal ticket]
  Ticket --> Event[Transactionally append event Outbox]
  Event --> Send[Signed webhook with stable event ID]
  Send -->|failure| Retry[Durable backoff]
  Retry --> Send
  Send -->|accepted| Linked[Business or external gateway notified]
  Linked --> Human[Operator investigation]
  Human --> Replay[Retry original delivery]
  Human --> Confirm[Confirm external result]
  Human --> Cancel[Cancel delivery]
  Human --> Close[Close ticket independently]
```

External ticket delivery failure cannot erase the internal ticket. Exhausted Outbox events remain queryable and replayable. Monitoring of the Outbox must use infrastructure independent of the failing outbound channel.

## Circuit lifecycle

Health is shared per tenant connection and capability. Systemic failures move healthy -> degraded -> open. Open circuits suppress claims until the cooldown ends. The health row is locked when starting a probe, admitting one recovery attempt per circuit. Failure reopens the circuit; two successful probes restore healthy state. State transitions and periodic reminders notify only the owning tenant.

## Scope decisions

- PostgreSQL is the initial task queue and authoritative state store. Redis handles shared traffic controls.
- Outbox events are persisted atomically with business state. There is no database-to-broker dual write.
- Published configurations are immutable. Updating a supplier creates a new version for new notifications.
- Supplier responses indicate delivery acceptance, not necessarily asynchronous business completion.
- The browser console uses the same controllers as other API clients. API keys stay in memory; the optional loopback development gateway injects demo identities only on the server side.
- Strict ordering, custom executable adapters, weighted fair scheduling, email paging, and vendor-specific ticket APIs are extension work rather than hidden guarantees.
