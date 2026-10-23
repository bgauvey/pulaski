-- 0018 · Terminals and badges (D-049): shared shop-floor terminals where people badge to clock on and off and take breaks.
-- The planned sec.terminal and sec.badge tables were never written, so they are replaced with their enrolled, hashed
-- shape; labor recorded at a terminal gets its own source. Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one
-- transaction).

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'sec' AND table_name = 'terminal' AND column_name = 'secret_hash') THEN
        DROP TABLE IF EXISTS sec.terminal;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'sec' AND table_name = 'badge' AND column_name = 'issued_by') THEN
        DROP TABLE IF EXISTS sec.badge;
    END IF;
END $$;

-- Shop-floor terminals (D-049): shared tablets and panel PCs where people badge to record their labor. A terminal is
-- registered for a site, area or line (its scope: the lines it offers) and enrolled once with a one-time code, which
-- gives it a long random secret; only hashes of the code and the secret are kept. Switching it off cuts it off.
CREATE TABLE IF NOT EXISTS sec.terminal (
    id                      uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id                 uuid NOT NULL REFERENCES core.equipment(id),
    code                    text NOT NULL,                          -- upper case; fixed once created
    name                    text NOT NULL,
    scope_equipment_id      uuid NOT NULL REFERENCES core.equipment(id),
    default_equipment_id    uuid REFERENCES core.equipment(id),     -- the line offered first
    auth_mode               text NOT NULL CHECK (auth_mode IN ('badge','badge_plus_pin')),
    is_active               boolean NOT NULL DEFAULT true,
    secret_hash             bytea UNIQUE,                           -- SHA-256 of the device secret; null until enrolled
    enrolled_at             timestamptz,
    enrolment_code_hash     bytea,                                  -- SHA-256 of the pending one-time code
    enrolment_expires_at    timestamptz,
    last_seen_at            timestamptz,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT terminal_uq UNIQUE (site_id, code),
    CONSTRAINT terminal_enrolment_ck CHECK ((enrolment_code_hash IS NULL) = (enrolment_expires_at IS NULL))
);

-- Badges (D-049). The badge value is a credential: only a keyed hash (HMAC) is kept, never the raw value. A PIN, for
-- terminals that ask for one, is hashed like a password. Revoked, never deleted; at most one active badge per value
-- and per person. Wrong PINs lock the badge for a while.
CREATE TABLE IF NOT EXISTS sec.badge (
    id              uuid PRIMARY KEY DEFAULT uuidv7(),
    person_id       uuid NOT NULL REFERENCES core.person(id),
    badge_hash      bytea NOT NULL,
    pin_hash        text,
    issued_at       timestamptz NOT NULL DEFAULT now(),
    issued_by       text NOT NULL,
    revoked_at      timestamptz,
    revoked_by      text,
    failed_pins     integer NOT NULL DEFAULT 0,
    locked_until    timestamptz,
    last_used_at    timestamptz
);
CREATE UNIQUE INDEX IF NOT EXISTS badge_active_value_uq ON sec.badge (badge_hash) WHERE revoked_at IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS badge_active_person_uq ON sec.badge (person_id) WHERE revoked_at IS NULL;

ALTER TABLE core.labor_session DROP CONSTRAINT IF EXISTS labor_session_source_check;
ALTER TABLE core.labor_session ADD CONSTRAINT labor_session_source_check CHECK (source IN ('self','supervisor','crew','terminal','correction'));
ALTER TABLE core.labor_break DROP CONSTRAINT IF EXISTS labor_break_source_check;
ALTER TABLE core.labor_break ADD CONSTRAINT labor_break_source_check CHECK (source IN ('self','supervisor','terminal','correction'));

INSERT INTO sec.permission (code, description) VALUES
    ('terminals.manage', 'Register and enrol shop-floor terminals')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, 'terminals.manage' FROM sec.role r WHERE r.code = 'manager' AND r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;
