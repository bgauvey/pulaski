-- 0071 · E-mail alerts (#121, D-109): each user's subscriptions per site (optionally narrowed to a line or area), and the alerts sent, so each
-- goes once per subscription and subject however many API instances look. Re-runnable; no BEGIN/COMMIT.

CREATE TABLE IF NOT EXISTS core.alert_subscription (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    user_id             uuid NOT NULL REFERENCES sec.app_user(id),
    site_id             uuid NOT NULL REFERENCES core.equipment(id),
    equipment_id        uuid REFERENCES core.equipment(id),         -- only what happens at or below it; null = the whole site
    kind                text NOT NULL CONSTRAINT alert_subscription_kind_ck
                            CHECK (kind IN ('long_stop', 'uncoded_stop', 'andon_escalated', 'spc_alert', 'lot_hold', 'shortage')),
    minutes             integer CONSTRAINT alert_subscription_minutes_ck CHECK (minutes IS NULL OR minutes BETWEEN 1 AND 10080),
    is_active           boolean NOT NULL DEFAULT true,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS alert_subscription_uq
    ON core.alert_subscription (user_id, site_id, kind, (coalesce(equipment_id, '00000000-0000-0000-0000-000000000000'::uuid)));
CREATE INDEX IF NOT EXISTS alert_subscription_kind_ix ON core.alert_subscription (kind) WHERE is_active;

-- One row per alert sent: the subscription and what it was about (a stop, a call, ...; no FK, it may be history).
CREATE TABLE IF NOT EXISTS core.alert_sent (
    subscription_id     uuid NOT NULL REFERENCES core.alert_subscription(id),
    subject_id          uuid NOT NULL,
    sent_at             timestamptz NOT NULL,
    PRIMARY KEY (subscription_id, subject_id)
);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.alert_subscription, core.alert_sent TO pulaski_app;
    END IF;
END $$;
