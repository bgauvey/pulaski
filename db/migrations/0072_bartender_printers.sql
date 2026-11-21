-- 0072 · Label printers through BarTender (#168, D-110): a printer of method 'bartender' posts a BTXML script, filled in from its template per
-- label, to a BarTender web service; the .btw document comes from the printer's map of operations. Credentials stay in the API's configuration.
-- Re-runnable; no BEGIN/COMMIT.

ALTER TABLE core.label_printer ADD COLUMN IF NOT EXISTS endpoint_url text;
ALTER TABLE core.label_printer ADD COLUMN IF NOT EXISTS btxml_template text;
ALTER TABLE core.label_printer ADD COLUMN IF NOT EXISTS bartender_printer text;
ALTER TABLE core.label_printer ADD COLUMN IF NOT EXISTS btw_files jsonb NOT NULL DEFAULT '{}'::jsonb;
ALTER TABLE core.label_printer ALTER COLUMN template_set DROP NOT NULL;

ALTER TABLE core.label_printer DROP CONSTRAINT IF EXISTS label_printer_method_check;
ALTER TABLE core.label_printer ADD CONSTRAINT label_printer_method_check CHECK (method IN ('raw_tcp', 'drop_folder', 'bartender'));
ALTER TABLE core.label_printer DROP CONSTRAINT IF EXISTS label_printer_endpoint_ck;
ALTER TABLE core.label_printer ADD CONSTRAINT label_printer_endpoint_ck CHECK (endpoint_url ~ '^https?://' AND length(endpoint_url) <= 500);
ALTER TABLE core.label_printer DROP CONSTRAINT IF EXISTS label_printer_btxml_ck;
ALTER TABLE core.label_printer ADD CONSTRAINT label_printer_btxml_ck CHECK (length(btxml_template) <= 20000);
ALTER TABLE core.label_printer DROP CONSTRAINT IF EXISTS label_printer_bartender_printer_ck;
ALTER TABLE core.label_printer ADD CONSTRAINT label_printer_bartender_printer_ck CHECK (length(bartender_printer) BETWEEN 1 AND 200);
ALTER TABLE core.label_printer DROP CONSTRAINT IF EXISTS label_printer_btw_files_ck;
ALTER TABLE core.label_printer ADD CONSTRAINT label_printer_btw_files_ck CHECK (jsonb_typeof(btw_files) = 'object');
ALTER TABLE core.label_printer DROP CONSTRAINT IF EXISTS label_printer_target_ck;
ALTER TABLE core.label_printer ADD CONSTRAINT label_printer_target_ck CHECK (CASE method
    WHEN 'raw_tcp' THEN host IS NOT NULL AND port IS NOT NULL AND drop_folder IS NULL AND endpoint_url IS NULL AND template_set IS NOT NULL
    WHEN 'drop_folder' THEN drop_folder IS NOT NULL AND host IS NULL AND port IS NULL AND endpoint_url IS NULL AND template_set IS NOT NULL
    ELSE endpoint_url IS NOT NULL AND btxml_template IS NOT NULL AND host IS NULL AND port IS NULL AND drop_folder IS NULL END);
