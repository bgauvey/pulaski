-- 0060 · Password reset by e-mail (#87, D-093). Re-runnable; no BEGIN/COMMIT.

-- A link to reset a local password by e-mail (D-093): only the token's SHA-256 is kept; single use, expiring, ended when another is asked for.
CREATE TABLE IF NOT EXISTS sec.password_reset (
    id              uuid PRIMARY KEY DEFAULT uuidv7(),
    app_user_id     uuid NOT NULL REFERENCES sec.app_user(id),
    token_hash      text NOT NULL,                              -- hex SHA-256 of the token in the link
    requested_at    timestamptz NOT NULL,
    expires_at      timestamptz NOT NULL,
    used_at         timestamptz,                                -- the password was set with it
    revoked_at      timestamptz,                                -- another link was asked for, or the password changed otherwise
    CONSTRAINT password_reset_token_uq UNIQUE (token_hash),
    CONSTRAINT password_reset_end_ck CHECK (used_at IS NULL OR revoked_at IS NULL)
);
CREATE INDEX IF NOT EXISTS password_reset_user_ix ON sec.password_reset (app_user_id, requested_at DESC);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON sec.password_reset TO pulaski_app;
    END IF;
END $$;
