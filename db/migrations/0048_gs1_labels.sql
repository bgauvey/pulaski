-- 0048 · GS1 labels, label printers and supplier barcodes (#75, D-080): materials may carry a GTIN and containers an SSCC from their site's
-- GS1 company prefix; each site chooses its label format, sets up label printers (raw TCP or a configured drop folder, from template
-- files) and supplier barcode formats. Re-runnable; no BEGIN/COMMIT.

ALTER TABLE core.material ADD COLUMN IF NOT EXISTS gtin text;
ALTER TABLE core.container ADD COLUMN IF NOT EXISTS sscc text;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'material_gtin_ck') THEN
        ALTER TABLE core.material ADD CONSTRAINT material_gtin_ck CHECK (gtin ~ '^[0-9]{14}$');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'container_sscc_ck') THEN
        ALTER TABLE core.container ADD CONSTRAINT container_sscc_ck CHECK (sscc ~ '^[0-9]{18}$');
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS material_gtin_uq ON core.material (gtin) WHERE gtin IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS container_sscc_uq ON core.container (sscc) WHERE sscc IS NOT NULL;

CREATE TABLE IF NOT EXISTS core.label_printer (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id             uuid NOT NULL REFERENCES core.equipment(id),
    code                text NOT NULL CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,29}$'),
    name                text NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    method              text NOT NULL CHECK (method IN ('raw_tcp','drop_folder')),
    host                text CHECK (length(host) <= 255),
    port                integer CHECK (port BETWEEN 1 AND 65535),
    drop_folder         text CHECK (drop_folder ~ '^[A-Za-z0-9_-]{1,50}$'),
    template_set        text NOT NULL CHECK (template_set ~ '^[A-Za-z0-9_-]{1,50}$'),
    is_active           boolean NOT NULL DEFAULT true,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT label_printer_uq UNIQUE (site_id, code),
    CONSTRAINT label_printer_target_ck CHECK (CASE method WHEN 'raw_tcp' THEN host IS NOT NULL AND port IS NOT NULL AND drop_folder IS NULL
                                                          ELSE drop_folder IS NOT NULL AND host IS NULL AND port IS NULL END)
);

CREATE TABLE IF NOT EXISTS core.label_settings (
    site_id             uuid PRIMARY KEY REFERENCES core.equipment(id),
    label_format        text NOT NULL DEFAULT 'pulaski' CHECK (label_format IN ('pulaski','gs1_128','gs1_datamatrix')),
    gs1_company_prefix  text CHECK (gs1_company_prefix ~ '^[0-9]{6,12}$'),
    sscc_extension      smallint NOT NULL DEFAULT 0 CHECK (sscc_extension BETWEEN 0 AND 9),
    sscc_next_serial    bigint NOT NULL DEFAULT 1 CHECK (sscc_next_serial >= 0),
    default_printer_id  uuid REFERENCES core.label_printer(id),
    updated_at          timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS core.supplier_barcode_format (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id             uuid NOT NULL REFERENCES core.equipment(id),
    code                text NOT NULL CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,29}$'),
    name                text NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    pattern             text NOT NULL CHECK (length(pattern) BETWEEN 1 AND 500),
    expiry_format       text CHECK (length(expiry_format) <= 30),
    sort_order          integer NOT NULL DEFAULT 0,
    is_active           boolean NOT NULL DEFAULT true,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT supplier_barcode_format_uq UNIQUE (site_id, code)
);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.label_printer, core.label_settings, core.supplier_barcode_format TO pulaski_app;
    END IF;
END $$;
