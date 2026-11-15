-- 0065 · Run rules (D-099): each characteristic chooses the Nelson rules its control chart checks, rule 1 always, the Western Electric set
-- (1, 2, 5 and 6) to start with. The drafted core.characteristic_rule link, never written to, is dropped. Re-runnable; no BEGIN/COMMIT.

ALTER TABLE core.quality_characteristic ADD COLUMN IF NOT EXISTS run_rules smallint[] NOT NULL DEFAULT '{1,2,5,6}';
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'characteristic_rules_ck') THEN
        ALTER TABLE core.quality_characteristic ADD CONSTRAINT characteristic_rules_ck
            CHECK (run_rules <@ ARRAY[1,2,3,4,5,6,7,8]::smallint[] AND 1 = ANY(run_rules));
    END IF;
END $$;

DROP TABLE IF EXISTS core.characteristic_rule;

INSERT INTO core.spc_rule (rule_number, name, description) VALUES
    (1, 'Beyond limits',      'One point beyond 3 sigma'),
    (2, 'Run of 9',           'Nine points in a row on one side of the center line'),
    (3, 'Trend of 6',         'Six points in a row steadily increasing or decreasing'),
    (4, 'Alternating 14',     'Fourteen points in a row alternating up and down'),
    (5, '2 of 3 beyond 2σ',   'Two of three points beyond 2 sigma on the same side'),
    (6, '4 of 5 beyond 1σ',   'Four of five points beyond 1 sigma on the same side'),
    (7, 'Stratification 15',  'Fifteen points in a row within 1 sigma'),
    (8, 'Mixture 8',          'Eight points in a row beyond 1 sigma on either side')
ON CONFLICT (rule_number) DO NOTHING;
