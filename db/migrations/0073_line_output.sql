-- 0073 · Report defaults and automatic labels (#122, D-111): a line's default location for good output, prefilled on its reports, and the
-- label printer a lot's label prints on once the lot is closed. Re-runnable; no BEGIN/COMMIT.

ALTER TABLE core.equipment ADD COLUMN IF NOT EXISTS output_location_id uuid REFERENCES core.equipment(id);
ALTER TABLE core.equipment ADD COLUMN IF NOT EXISTS label_printer_id uuid REFERENCES core.label_printer(id);
