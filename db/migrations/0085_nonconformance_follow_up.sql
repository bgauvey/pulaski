-- Nonconformance follow-up (#133b, D-124): NCRs raised from a run, and e-mail alerts for overdue NCR actions.

ALTER TABLE core.nonconformance DROP CONSTRAINT IF EXISTS nonconformance_source_ck;
ALTER TABLE core.nonconformance ADD CONSTRAINT nonconformance_source_ck
    CHECK (source_kind IN ('manual', 'lot_hold', 'lot_rejection', 'quality_result', 'spc_alert', 'checklist', 'complaint', 'run'));

ALTER TABLE core.alert_subscription DROP CONSTRAINT IF EXISTS alert_subscription_kind_ck;
ALTER TABLE core.alert_subscription ADD CONSTRAINT alert_subscription_kind_ck
    CHECK (kind IN ('long_stop', 'uncoded_stop', 'andon_escalated', 'spc_alert', 'lot_hold', 'shortage', 'ncr_action_overdue'));

CREATE INDEX IF NOT EXISTS nonconformance_action_due_ix ON core.nonconformance_action (due_on) WHERE status = 'open';
