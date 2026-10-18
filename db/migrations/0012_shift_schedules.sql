-- 0012 · Shift schedules (D-038): a site's schedules, and which working time each piece of equipment follows
-- (its parent's, its own schedule, or around the clock). Shift times are worked out from the schedule on read,
-- so the unused, never-filled pattern and definition tables go. Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one transaction).

-- The planned shift_pattern and shift_definition tables were never filled (nothing wrote them), so they are replaced
-- rather than altered. Shift instances lose their link to a definition first.
ALTER TABLE core.shift_instance DROP COLUMN IF EXISTS shift_definition_id;
DROP TABLE IF EXISTS core.shift_definition;
DROP TABLE IF EXISTS core.shift_pattern;

CREATE TABLE IF NOT EXISTS core.shift_schedule (
    id          uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id     uuid NOT NULL REFERENCES core.equipment(id),
    code        text NOT NULL,
    name        text NOT NULL,
    description text,
    cycle_days  integer NOT NULL CHECK (cycle_days BETWEEN 1 AND 56),
    cycle_start date NOT NULL,
    shifts      jsonb NOT NULL DEFAULT '[]' CHECK (jsonb_typeof(shifts) = 'array'),
    is_active   boolean NOT NULL DEFAULT true,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT shift_schedule_uq UNIQUE (site_id, code)
);

-- Shift instances now record shifts as worked (planned); they name the schedule and shift code.
ALTER TABLE core.shift_instance
    ADD COLUMN IF NOT EXISTS shift_schedule_id uuid REFERENCES core.shift_schedule(id),
    ADD COLUMN IF NOT EXISTS shift_code text;
ALTER TABLE core.equipment
    ADD COLUMN IF NOT EXISTS calendar_mode text NOT NULL DEFAULT 'inherit',
    ADD COLUMN IF NOT EXISTS shift_schedule_id uuid REFERENCES core.shift_schedule(id);
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'equipment_calendar_mode_ck') THEN
        ALTER TABLE core.equipment ADD CONSTRAINT equipment_calendar_mode_ck CHECK (calendar_mode IN ('inherit', 'schedule', 'around_the_clock'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'equipment_calendar_schedule_ck') THEN
        ALTER TABLE core.equipment ADD CONSTRAINT equipment_calendar_schedule_ck CHECK ((calendar_mode = 'schedule') = (shift_schedule_id IS NOT NULL));
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS equipment_shift_schedule_ix ON core.equipment (shift_schedule_id) WHERE shift_schedule_id IS NOT NULL;

-- Who may maintain them (D-037): the permission, and the starting Manager role.
INSERT INTO sec.permission (code, description)
VALUES ('shifts.manage', 'Maintain shift schedules and choose which one equipment follows')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;
INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, 'shifts.manage' FROM sec.role r WHERE r.code = 'manager' AND r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.shift_schedule TO pulaski_app;
    END IF;
END $$;
