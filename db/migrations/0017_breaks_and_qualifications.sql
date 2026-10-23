-- 0017 · Breaks and qualifications (D-047, D-048). The planned qualification tables were never written, so they are replaced
-- with their maintained shape (grants with days and revocation, requirements switched off rather than deleted); breaks are a
-- table of their own, so sessions never change. Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one transaction).

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'qualification' AND column_name = 'validity_months') THEN
        DROP TABLE IF EXISTS core.equipment_qualification;
        DROP TABLE IF EXISTS core.person_qualification;
        DROP TABLE IF EXISTS core.qualification;
    END IF;
END $$;

-- Qualifications (D-048): a site's skills and certifications, who holds them for which days, and which equipment needs
-- them (a requirement applies to the equipment and everything below it). Pulaski warns, never refuses, when someone
-- without one is put on a line. Nothing is deleted: grants are revoked, requirements switched off.
CREATE TABLE IF NOT EXISTS core.qualification (
    id              uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id         uuid NOT NULL REFERENCES core.equipment(id),
    code            text NOT NULL,                                  -- upper case; fixed once created
    name            text NOT NULL,
    description     text,
    validity_months integer CHECK (validity_months BETWEEN 1 AND 120),  -- default length of a grant; null: doesn't expire
    is_active       boolean NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT qualification_uq UNIQUE (site_id, code)
);

-- A grant (D-048): valid from valid_from to expires_on, whole site-local days, both inclusive. A renewal is a new grant.
CREATE TABLE IF NOT EXISTS core.person_qualification (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    person_id           uuid NOT NULL REFERENCES core.person(id),
    qualification_id    uuid NOT NULL REFERENCES core.qualification(id),
    valid_from          date NOT NULL,
    expires_on          date,                                       -- last valid day; null: doesn't expire
    note                text,
    granted_by          text NOT NULL,
    revoked_at          timestamptz,
    revoked_by          text,
    revoke_reason       text,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT person_qualification_days_ck CHECK (expires_on IS NULL OR expires_on >= valid_from),
    CONSTRAINT person_qualification_revoked_ck CHECK ((revoked_at IS NULL) = (revoked_by IS NULL))
);
CREATE INDEX IF NOT EXISTS person_qualification_person_ix ON core.person_qualification (person_id, qualification_id);

-- Qualifications required to work on equipment, inherited down the hierarchy (D-048).
CREATE TABLE IF NOT EXISTS core.equipment_qualification (
    equipment_id        uuid NOT NULL REFERENCES core.equipment(id),
    qualification_id    uuid NOT NULL REFERENCES core.qualification(id),
    is_required         boolean NOT NULL DEFAULT true,              -- false once taken off
    updated_at          timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (equipment_id, qualification_id)
);

-- A person's break (D-047): its time comes out of every session of theirs it overlaps, without changing them, and is
-- reported on its own, paid or unpaid. It belongs to the shift of the session it started in, and locks with that shift's
-- approval; after that a correction replaces it (corrects_break_id) or, with is_void, removes it.
CREATE TABLE IF NOT EXISTS core.labor_break (
    id                      uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id                 uuid NOT NULL REFERENCES core.equipment(id),
    person_id               uuid NOT NULL REFERENCES core.person(id),
    started_at              timestamptz NOT NULL,
    ended_at                timestamptz,                            -- null while on break
    is_paid                 boolean NOT NULL DEFAULT false,
    production_date         date NOT NULL,
    shift_schedule_id       uuid REFERENCES core.shift_schedule(id),
    shift_code              text,
    source                  text NOT NULL CHECK (source IN ('self','supervisor','correction')),
    corrects_break_id       uuid REFERENCES core.labor_break(id),
    is_void                 boolean NOT NULL DEFAULT false,
    note                    text,
    recorded_by             text NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT labor_break_span_ck CHECK (ended_at IS NULL OR ended_at > started_at),
    CONSTRAINT labor_break_shift_ck CHECK ((shift_schedule_id IS NULL) = (shift_code IS NULL)),
    CONSTRAINT labor_break_correction_ck CHECK ((source = 'correction') = (corrects_break_id IS NOT NULL)),
    CONSTRAINT labor_break_void_ck CHECK (NOT is_void OR corrects_break_id IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS labor_break_person_ix ON core.labor_break (person_id, started_at);
CREATE INDEX IF NOT EXISTS labor_break_shift_ix ON core.labor_break (site_id, production_date, shift_code);
CREATE UNIQUE INDEX IF NOT EXISTS labor_break_one_open_per_person ON core.labor_break (person_id) WHERE ended_at IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS labor_break_corrected_once ON core.labor_break (corrects_break_id) WHERE corrects_break_id IS NOT NULL;
