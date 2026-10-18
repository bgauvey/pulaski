-- 0014 · Labor (D-040): crews and people kept in Pulaski, labor sessions against lines, jobs and shifts, and shift
-- approval. The planned labor_session table was never written, so it is replaced. Re-runnable; no BEGIN/COMMIT
-- (db/migrate.sh runs it in one transaction).

ALTER TABLE core.crew
    ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();

ALTER TABLE core.person
    ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();

-- The planned shape (with shift_instance_id, source badge/manual/auto_shift) goes; nothing wrote it.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'labor_session' AND column_name = 'shift_instance_id') THEN
        DROP TABLE core.labor_session;
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS core.labor_session (
    id                      uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id                 uuid NOT NULL REFERENCES core.equipment(id),
    person_id               uuid NOT NULL REFERENCES core.person(id),
    equipment_id            uuid NOT NULL REFERENCES core.equipment(id),   -- the line, or other equipment for indirect time
    job_id                  uuid REFERENCES core.job(id),                  -- only when the time is for one job
    labor_type              text NOT NULL CHECK (labor_type IN ('direct','setup','indirect','training')),
    reason_code_id          uuid REFERENCES core.reason_code(id),          -- kind indirect_labor
    started_at              timestamptz NOT NULL,
    ended_at                timestamptz,
    allocation_pct          numeric(5,2) NOT NULL DEFAULT 100 CHECK (allocation_pct > 0 AND allocation_pct <= 100),
    production_date         date NOT NULL,                                 -- the shift's day, or the site-local day
    shift_schedule_id       uuid REFERENCES core.shift_schedule(id),
    shift_code              text,
    source                  text NOT NULL CHECK (source IN ('self','supervisor','crew','correction')),
    corrects_session_id     uuid REFERENCES core.labor_session(id),
    is_void                 boolean NOT NULL DEFAULT false,                -- a correction that removes the session it corrects
    note                    text,
    recorded_by             text NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT labor_span_ck CHECK (ended_at IS NULL OR ended_at >= started_at),
    CONSTRAINT labor_indirect_ck CHECK (labor_type <> 'indirect' OR reason_code_id IS NOT NULL),
    CONSTRAINT labor_setup_ck CHECK (labor_type <> 'setup' OR job_id IS NOT NULL),
    CONSTRAINT labor_shift_ck CHECK ((shift_schedule_id IS NULL) = (shift_code IS NULL)),
    CONSTRAINT labor_correction_ck CHECK ((source = 'correction') = (corrects_session_id IS NOT NULL)),
    CONSTRAINT labor_void_ck CHECK (NOT is_void OR corrects_session_id IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS labor_person_ix ON core.labor_session (person_id, started_at);
CREATE INDEX IF NOT EXISTS labor_shift_ix  ON core.labor_session (site_id, production_date, shift_code);
CREATE INDEX IF NOT EXISTS labor_equipment_ix ON core.labor_session (equipment_id, started_at);
CREATE UNIQUE INDEX IF NOT EXISTS labor_one_open_per_person_equipment ON core.labor_session (person_id, equipment_id) WHERE ended_at IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS labor_corrected_once ON core.labor_session (corrects_session_id) WHERE corrects_session_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS core.labor_approval (
    id                      uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id                 uuid NOT NULL REFERENCES core.equipment(id),
    production_date         date NOT NULL,
    shift_schedule_id       uuid REFERENCES core.shift_schedule(id),
    shift_code              text,
    approved_by             text NOT NULL,
    approved_by_user_id     uuid REFERENCES sec.app_user(id),
    approved_at             timestamptz NOT NULL,
    note                    text,
    CONSTRAINT labor_approval_shift_ck CHECK ((shift_schedule_id IS NULL) = (shift_code IS NULL)),
    CONSTRAINT labor_approval_uq UNIQUE NULLS NOT DISTINCT (site_id, production_date, shift_schedule_id, shift_code)
);

INSERT INTO sec.permission (code, description) VALUES
    ('labor.view', 'See labor: who is on, shift review and labor reports'),
    ('labor.record_own', 'Clock yourself on and off lines and jobs'),
    ('labor.record', 'Record labor for others and check crews onto lines'),
    ('labor.approve', 'Approve a shift''s labor and correct approved labor'),
    ('people.manage', 'Maintain people and crews')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

-- The starting roles gain theirs; other roles gain nothing (an administrator decides).
INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, v.permission_code FROM (VALUES
    ('supervisor', 'labor.view'),
    ('supervisor', 'labor.record'),
    ('supervisor', 'labor.approve'),
    ('operator', 'labor.record_own'),
    ('manager', 'labor.view'),
    ('manager', 'people.manage'),
    ('manager', 'labor.approve'),
    ('auditor', 'labor.view')
) AS v(role_code, permission_code)
JOIN sec.role r ON r.code = v.role_code AND r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;

UPDATE sec.role SET description = v.description FROM (VALUES
    ('supervisor', 'Runs the floor: jobs, reports, receipts, moves, holds and counts; approves partial batches; records and approves labor. Doesn''t correct inventory.'),
    ('operator', 'Starts, pauses and completes jobs, reports production and consumption, receives and moves lots, clocks on and off.'),
    ('manager', 'Views everything; reopens jobs, closes work orders, approves labor, maintains reason codes, shift schedules, people and the site''s settings.')
) AS v(code, description)
WHERE sec.role.code = v.code AND sec.role.is_system
  AND sec.role.description IN (
      'Starts, pauses and completes jobs, reports production and consumption, receives and moves lots.',
      'Runs the floor: jobs, reports, receipts, moves, holds and counts; approves partial batches. Doesn''t correct inventory.',
      'Views everything; reopens jobs, closes work orders, maintains reason codes, shift schedules and the site''s settings.');

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.labor_session, core.labor_approval, core.crew, core.person TO pulaski_app;
    END IF;
END $$;
