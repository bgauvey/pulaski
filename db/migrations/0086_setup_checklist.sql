-- First-setup checklist (#139, D-126): optional steps an administrator marked as not needed at a site. The steps themselves are worked out.

CREATE TABLE IF NOT EXISTS core.setup_step_skip (
    site_id         uuid NOT NULL REFERENCES core.equipment(id),
    step            text NOT NULL,                                  -- a SetupSteps key, such as 'shifts'
    is_skipped      boolean NOT NULL,                               -- false once taken back; rows are kept
    changed_by      text NOT NULL,
    changed_at      timestamptz NOT NULL,
    PRIMARY KEY (site_id, step)
);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.setup_step_skip TO pulaski_app;
    END IF;
END $$;
