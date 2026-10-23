-- 0019 · Machine profiles and utilization reasons (D-050): profiles map a machine's state values to PackML and its
-- stop codes to utilization reasons; downtime reasons become utilization reasons with a class; equipment gets its
-- profile, its line-state derivation and a 5-minute micro-stop default. Nothing used the planned columns, so they are
-- reshaped in place. Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one transaction).

-- ---- Utilization reasons ----

ALTER TABLE core.reason_code ADD COLUMN IF NOT EXISTS utilization_class text;

ALTER TABLE core.reason_code DROP CONSTRAINT IF EXISTS reason_code_reason_kind_check;
UPDATE core.reason_code SET reason_kind = 'utilization', utilization_class = 'downtime' WHERE reason_kind = 'downtime';
ALTER TABLE core.reason_code
    ADD CONSTRAINT reason_code_reason_kind_check CHECK (reason_kind IN (
        'utilization','scrap','hold','inventory_adjustment',
        'indirect_labor','run_end','spc_cause','sample_exclusion')),
    DROP CONSTRAINT IF EXISTS reason_code_utilization_class_check,
    ADD CONSTRAINT reason_code_utilization_class_check CHECK (utilization_class IN ('runtime','downtime','planned')),
    DROP CONSTRAINT IF EXISTS reason_code_utilization_ck,
    ADD CONSTRAINT reason_code_utilization_ck CHECK ((reason_kind = 'utilization') = (utilization_class IS NOT NULL));

-- ---- Equipment: line state and micro-stops ----

ALTER TABLE core.equipment
    DROP CONSTRAINT IF EXISTS equipment_state_derivation_check,
    DROP CONSTRAINT IF EXISTS equipment_derivation_ck;
UPDATE core.equipment SET state_derivation = CASE state_derivation WHEN 'worst_child' THEN 'series' WHEN 'constraint_machine' THEN 'constraint_machine' ELSE 'own_tags' END
WHERE state_derivation IS NULL OR state_derivation NOT IN ('own_tags','series','parallel','constraint_machine');
UPDATE core.equipment SET state_derivation = 'own_tags' WHERE state_derivation = 'constraint_machine' AND constraint_equipment_id IS NULL;
UPDATE core.equipment SET constraint_equipment_id = NULL WHERE state_derivation <> 'constraint_machine';
-- Nothing read the 2-minute default yet; 5 minutes suits more lines (an extruder can set 2).
UPDATE core.equipment SET micro_stop_threshold = interval '5 minutes' WHERE micro_stop_threshold = interval '2 minutes';
ALTER TABLE core.equipment
    ALTER COLUMN state_derivation SET DEFAULT 'own_tags',
    ALTER COLUMN state_derivation SET NOT NULL,
    ALTER COLUMN micro_stop_threshold SET DEFAULT interval '5 minutes',
    ADD CONSTRAINT equipment_state_derivation_check CHECK (state_derivation IN ('own_tags','series','parallel','constraint_machine')),
    ADD CONSTRAINT equipment_derivation_ck CHECK ((state_derivation = 'constraint_machine') = (constraint_equipment_id IS NOT NULL)),
    DROP CONSTRAINT IF EXISTS equipment_micro_stop_ck,
    ADD CONSTRAINT equipment_micro_stop_ck CHECK (micro_stop_threshold >= interval '0' AND micro_stop_threshold <= interval '1 hour');

-- ---- Machine profiles ----

CREATE TABLE IF NOT EXISTS core.machine_profile (
    id              uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id         uuid NOT NULL REFERENCES core.equipment(id),
    code            text NOT NULL,
    name            text NOT NULL,
    vendor          text,
    model           text,
    description     text,
    is_active       boolean NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT machine_profile_code_uq UNIQUE (site_id, code)
);

CREATE TABLE IF NOT EXISTS core.machine_profile_state (
    profile_id      uuid NOT NULL REFERENCES core.machine_profile(id),
    raw_value       integer NOT NULL,
    name            text NOT NULL,
    packml_state    smallint NOT NULL REFERENCES core.packml_state(code),
    reason_code_id  uuid REFERENCES core.reason_code(id),
    is_active       boolean NOT NULL DEFAULT true,
    PRIMARY KEY (profile_id, raw_value)
);

CREATE TABLE IF NOT EXISTS core.machine_profile_code (
    profile_id      uuid NOT NULL REFERENCES core.machine_profile(id),
    code_kind       text NOT NULL CHECK (code_kind IN ('stop_reason','alarm','warning')),
    raw_value       integer NOT NULL,
    message         text NOT NULL,
    category        text,
    reason_code_id  uuid REFERENCES core.reason_code(id),
    is_active       boolean NOT NULL DEFAULT true,
    PRIMARY KEY (profile_id, code_kind, raw_value)
);

CREATE TABLE IF NOT EXISTS core.machine_profile_reason (
    profile_id      uuid NOT NULL REFERENCES core.machine_profile(id),
    reason_code_id  uuid NOT NULL REFERENCES core.reason_code(id),
    is_offered      boolean NOT NULL DEFAULT true,
    PRIMARY KEY (profile_id, reason_code_id)
);

ALTER TABLE core.equipment ADD COLUMN IF NOT EXISTS machine_profile_id uuid REFERENCES core.machine_profile(id);
CREATE INDEX IF NOT EXISTS equipment_machine_profile_ix ON core.equipment (machine_profile_id) WHERE machine_profile_id IS NOT NULL;

-- Per-machine overrides of a profile's stop codes: a null reason leaves the code uncoded; switched off rather than deleted.
ALTER TABLE core.equipment_stop_reason_map
    ALTER COLUMN reason_code_id DROP NOT NULL,
    ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT true;
