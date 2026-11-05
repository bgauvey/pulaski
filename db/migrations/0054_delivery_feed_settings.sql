-- 0054 · Delivery feed settings and acknowledgements (#73, D-087): a feed's settings kept from Configure → Integrations (all but the secret),
-- and the receiver's acknowledgement recorded on each delivery. Re-runnable; no BEGIN/COMMIT.

ALTER TABLE evt.delivery ADD COLUMN IF NOT EXISTS ack_status text CHECK (ack_status IN ('accepted','rejected'));
ALTER TABLE evt.delivery ADD COLUMN IF NOT EXISTS ack_reference text;
ALTER TABLE evt.delivery ADD COLUMN IF NOT EXISTS ack_message text;
ALTER TABLE evt.delivery ADD COLUMN IF NOT EXISTS ack_source text CHECK (ack_source IN ('response','callback'));
ALTER TABLE evt.delivery ADD COLUMN IF NOT EXISTS ack_at timestamptz;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'delivery_ack_ck') THEN
        ALTER TABLE evt.delivery ADD CONSTRAINT delivery_ack_ck
            CHECK ((ack_status IS NULL) = (ack_at IS NULL) AND (ack_status IS NULL) = (ack_source IS NULL));
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS core.delivery_feed (
    feed                text PRIMARY KEY CHECK (feed IN ('erp','documents')),
    enabled             boolean NOT NULL DEFAULT false,
    url                 text,
    event_kinds         text[] NOT NULL DEFAULT '{}',
    include_files       boolean NOT NULL DEFAULT false,
    max_attempts        integer NOT NULL DEFAULT 12 CHECK (max_attempts BETWEEN 1 AND 50),
    timeout_seconds     integer NOT NULL DEFAULT 15 CHECK (timeout_seconds BETWEEN 1 AND 120),
    poll_seconds        integer NOT NULL DEFAULT 5 CHECK (poll_seconds BETWEEN 1 AND 300),
    start_from_now      boolean NOT NULL DEFAULT true,
    updated_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          text
);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.delivery_feed TO pulaski_app;
    END IF;
END $$;
