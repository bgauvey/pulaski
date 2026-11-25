-- Setup and first-article checklists (#126c, D-116): defined in an instruction's version, answered for a job on a line, kept with the run.

ALTER TABLE core.work_instruction_version ADD COLUMN IF NOT EXISTS checklists jsonb NOT NULL DEFAULT '[]';   -- [{phase, items: [...]}]

CREATE TABLE IF NOT EXISTS core.checklist_response (
    id              uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id         uuid NOT NULL REFERENCES core.equipment(id),
    job_id          uuid NOT NULL REFERENCES core.job(id),
    equipment_id    uuid NOT NULL REFERENCES core.equipment(id),           -- the line it was answered for
    run_id          uuid REFERENCES core.job_run(id),                      -- the run it belongs to: the open one, else the next started
    instruction_id  uuid NOT NULL REFERENCES core.work_instruction(id),
    version_id      uuid NOT NULL REFERENCES core.work_instruction_version(id),
    phase           text NOT NULL CONSTRAINT checklist_response_phase_ck CHECK (phase IN ('setup', 'first_article')),
    result          text NOT NULL CONSTRAINT checklist_response_result_ck CHECK (result IN ('passed', 'failed')),
    answers         jsonb NOT NULL,                                        -- [{item, value, ok}]
    completed_at    timestamptz NOT NULL,
    completed_by    text NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS checklist_response_job_ix ON core.checklist_response (job_id, equipment_id, phase);
CREATE INDEX IF NOT EXISTS checklist_response_run_ix ON core.checklist_response (run_id) WHERE run_id IS NOT NULL;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.checklist_response TO pulaski_app;
    END IF;
END $$;
