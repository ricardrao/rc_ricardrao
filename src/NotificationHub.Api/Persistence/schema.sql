CREATE TABLE IF NOT EXISTS schema_versions(version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS providers(
 id uuid PRIMARY KEY, owner_id text NOT NULL, name text NOT NULL,
 enabled boolean NOT NULL DEFAULT true, created_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS operation_versions(
 id uuid PRIMARY KEY, provider_id uuid NOT NULL REFERENCES providers(id), operation text NOT NULL,
 version integer NOT NULL, definition jsonb NOT NULL, status text NOT NULL DEFAULT 'draft',
 created_at timestamptz NOT NULL DEFAULT now(), UNIQUE(provider_id,operation,version),
 CHECK(status IN ('draft','published')));
CREATE TABLE IF NOT EXISTS connections(
 id uuid PRIMARY KEY, tenant_id text NOT NULL, provider_id uuid NOT NULL REFERENCES providers(id),
 name text NOT NULL, credential text, enabled boolean NOT NULL DEFAULT true);
CREATE INDEX IF NOT EXISTS connections_tenant ON connections(tenant_id);
CREATE TABLE IF NOT EXISTS endpoint_health(
 connection_id uuid NOT NULL REFERENCES connections(id), operation text NOT NULL,
 state text NOT NULL DEFAULT 'healthy', consecutive_failures integer NOT NULL DEFAULT 0,
 recovery_successes integer NOT NULL DEFAULT 0, open_until timestamptz,
 last_success_at timestamptz, last_failure_at timestamptz, last_notice_at timestamptz,
 PRIMARY KEY(connection_id,operation));
CREATE TABLE IF NOT EXISTS tenant_quotas(
 tenant_id text PRIMARY KEY, window_start timestamptz NOT NULL DEFAULT now(), used integer NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS notifications(
 id uuid PRIMARY KEY, tenant_id text NOT NULL, idempotency_key text NOT NULL,
 request_hash text NOT NULL, event_type text NOT NULL, data jsonb NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(), UNIQUE(tenant_id,idempotency_key));
CREATE TABLE IF NOT EXISTS deliveries(
 id uuid PRIMARY KEY, notification_id uuid NOT NULL REFERENCES notifications(id), tenant_id text NOT NULL,
 connection_id uuid NOT NULL REFERENCES connections(id), version_id uuid NOT NULL REFERENCES operation_versions(id),
 operation text NOT NULL, status text NOT NULL DEFAULT 'pending',
 attempts integer NOT NULL DEFAULT 0, round_attempts integer NOT NULL DEFAULT 0, retry_round integer NOT NULL DEFAULT 0,
 expires_at timestamptz NOT NULL, dedup_until timestamptz NOT NULL, next_attempt_at timestamptz NOT NULL DEFAULT now(),
 lease_until timestamptz, claim_token uuid, last_error text, uncertain boolean NOT NULL DEFAULT false,
 created_at timestamptz NOT NULL DEFAULT now(), UNIQUE(notification_id,connection_id,operation),
 CHECK(status IN ('pending','processing','retry_wait','succeeded','stopped','expired','cancelled')));
CREATE INDEX IF NOT EXISTS deliveries_ready ON deliveries(next_attempt_at,id) WHERE status IN ('pending','retry_wait');
CREATE INDEX IF NOT EXISTS deliveries_leases ON deliveries(lease_until) WHERE status='processing';
CREATE INDEX IF NOT EXISTS deliveries_tenant ON deliveries(tenant_id,status);
CREATE TABLE IF NOT EXISTS delivery_attempts(
 id uuid PRIMARY KEY, delivery_id uuid NOT NULL REFERENCES deliveries(id), claim_token uuid NOT NULL UNIQUE,
 started_at timestamptz NOT NULL DEFAULT now(), finished_at timestamptz,
 category text NOT NULL DEFAULT 'in_flight', http_status integer, uncertain boolean NOT NULL DEFAULT false);
CREATE INDEX IF NOT EXISTS attempts_delivery ON delivery_attempts(delivery_id,started_at);
CREATE TABLE IF NOT EXISTS tickets(
 id uuid PRIMARY KEY, tenant_id text NOT NULL, connection_id uuid NOT NULL REFERENCES connections(id),
 operation text NOT NULL, category text NOT NULL, status text NOT NULL DEFAULT 'open',
 affected_count integer NOT NULL DEFAULT 0, resolution text,
 created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now());
CREATE UNIQUE INDEX IF NOT EXISTS tickets_open_group ON tickets(tenant_id,connection_id,operation,category) WHERE status='open';
CREATE TABLE IF NOT EXISTS ticket_deliveries(
 ticket_id uuid NOT NULL REFERENCES tickets(id), delivery_id uuid NOT NULL REFERENCES deliveries(id),
 PRIMARY KEY(ticket_id,delivery_id));
CREATE TABLE IF NOT EXISTS audit_entries(
 id uuid PRIMARY KEY, tenant_id text NOT NULL, resource_id uuid NOT NULL,
 action text NOT NULL, reason text NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS subscriptions(
 id uuid PRIMARY KEY, tenant_id text NOT NULL, url text NOT NULL,
 signing_secret text NOT NULL, enabled boolean NOT NULL DEFAULT true);
CREATE TABLE IF NOT EXISTS outbox(
 id uuid PRIMARY KEY, subscription_id uuid REFERENCES subscriptions(id), tenant_id text NOT NULL,
 kind text NOT NULL, payload jsonb NOT NULL, status text NOT NULL DEFAULT 'pending',
 attempts integer NOT NULL DEFAULT 0, next_attempt_at timestamptz NOT NULL DEFAULT now(),
 lease_until timestamptz, claim_token uuid, last_error text, created_at timestamptz NOT NULL DEFAULT now());
CREATE INDEX IF NOT EXISTS outbox_ready ON outbox(next_attempt_at) WHERE status IN ('pending','retry_wait');
INSERT INTO schema_versions(version) VALUES(1) ON CONFLICT DO NOTHING;
