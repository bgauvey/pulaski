-- 0075 · Languages (#128, D-114): the languages the screens can show, and their translations keyed by the English text (the term). A plant
-- edits them on Configure → Languages; Pulaski adds its shipped starter translations at start without overwriting a plant's. A translation
-- is never deleted: cleared, it is empty (null) and the screens show the English. Users and terminals each choose a language and a region
-- (a culture name, for dates and numbers). Permission languages.manage, held by administrators only until a role is given it.
-- Re-runnable; no BEGIN/COMMIT.

CREATE TABLE IF NOT EXISTS core.language (
    code                text PRIMARY KEY CONSTRAINT language_code_ck CHECK (code ~ '^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$'),
    name                text NOT NULL CONSTRAINT language_name_ck CHECK (length(name) BETWEEN 1 AND 100),
    is_active           boolean NOT NULL DEFAULT true,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS core.translation (
    language_code       text NOT NULL REFERENCES core.language(code),
    term                text NOT NULL CONSTRAINT translation_term_ck CHECK (length(term) BETWEEN 1 AND 2000),
    text                text CONSTRAINT translation_text_ck CHECK (length(text) BETWEEN 1 AND 4000),   -- null: cleared, the English shows
    is_shipped          boolean NOT NULL DEFAULT false,                                              -- Pulaski's starter text, not yet changed here
    updated_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          text,
    PRIMARY KEY (language_code, term)
);

ALTER TABLE sec.app_user ADD COLUMN IF NOT EXISTS language text;
ALTER TABLE sec.app_user ADD COLUMN IF NOT EXISTS region text;
ALTER TABLE sec.terminal ADD COLUMN IF NOT EXISTS language text;
ALTER TABLE sec.terminal ADD COLUMN IF NOT EXISTS region text;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'app_user_culture_ck') THEN
        ALTER TABLE sec.app_user ADD CONSTRAINT app_user_culture_ck CHECK (length(language) <= 20 AND length(region) <= 20);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'terminal_culture_ck') THEN
        ALTER TABLE sec.terminal ADD CONSTRAINT terminal_culture_ck CHECK (length(language) <= 20 AND length(region) <= 20);
    END IF;
END $$;

INSERT INTO sec.permission (code, description) VALUES
    ('languages.manage', 'Add languages and keep their translations')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.language, core.translation TO pulaski_app;
    END IF;
END $$;
