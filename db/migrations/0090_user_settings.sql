-- A user's own view settings (#131, D-131): the filters each list page opens with, favorite pages and a home page, kept on the server so
-- they follow the user to any device and survive signing out. Personal view state, not plant records: no events (as language and region).

CREATE TABLE IF NOT EXISTS sec.user_setting (
    app_user_id     uuid NOT NULL REFERENCES sec.app_user(id),
    key             text NOT NULL CONSTRAINT user_setting_key_ck CHECK (key ~ '^(home|favorites|filters:[a-z0-9/_-]{1,80})$'),
    value           jsonb NOT NULL CONSTRAINT user_setting_size_ck CHECK (octet_length(value::text) <= 16384),   -- 'null' = cleared
    updated_at      timestamptz NOT NULL,
    PRIMARY KEY (app_user_id, key)
);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON sec.user_setting TO pulaski_app;
    END IF;
END $$;
