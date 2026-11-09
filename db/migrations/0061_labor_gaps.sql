-- 0061 · Labor gaps (#84, D-095): people working at other sites, planned breaks on shifts (in the schedules' jsonb, nothing here), and
-- qualification expiry notices. Re-runnable; no BEGIN/COMMIT.

-- Other sites a person also works at (#84, D-095): there they appear in check-in and clock-on lists and badge at terminals; their time
-- belongs to the site worked. Switched off, never deleted.
CREATE TABLE IF NOT EXISTS core.person_site (
    person_id       uuid NOT NULL REFERENCES core.person(id),
    site_id         uuid NOT NULL REFERENCES core.equipment(id),
    is_active       boolean NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (person_id, site_id)
);
CREATE INDEX IF NOT EXISTS person_site_site_ix ON core.person_site (site_id) WHERE is_active;

-- Expiry notices: how many days ahead a qualification's grants are noticed, and when a grant's notice went out.
ALTER TABLE core.qualification ADD COLUMN IF NOT EXISTS notice_days smallint;
ALTER TABLE core.person_qualification ADD COLUMN IF NOT EXISTS expiry_noticed_at timestamptz;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'qualification_notice_days_ck') THEN
        ALTER TABLE core.qualification ADD CONSTRAINT qualification_notice_days_ck CHECK (notice_days BETWEEN 1 AND 365);
    END IF;
END $$;
