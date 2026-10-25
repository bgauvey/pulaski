-- 0036 · Material connections (D-068): external sources of material master data Pulaski pulls from. Re-runnable; no BEGIN/COMMIT.
-- db/app-role.sql's grant on every core table covers the new one; repeated here for an existing database.

SET LOCAL client_min_messages = warning;

-- An external source of material master data that Pulaski pulls from (D-068): an OData v4 service and the entity set holding its items,
-- with a map from Pulaski's import fields to the service's property names. Rows go through the materials import (D-034, D-053) as a
-- file's do. No secrets here: credentials are in the API's configuration, under MaterialConnections:<code>.
CREATE TABLE IF NOT EXISTS core.material_connection (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    code                text NOT NULL UNIQUE,                   -- upper case, fixed once created; names the configuration section
    name                text NOT NULL,
    kind                text NOT NULL DEFAULT 'odata' CHECK (kind IN ('odata')),
    base_url            text NOT NULL,                          -- the service root, https
    entity_set          text NOT NULL,                          -- e.g. Products
    filter              text,                                   -- an OData $filter, as the service takes it
    external_system     text NOT NULL,                          -- what the items are matched on with their key (D-033)
    field_map           jsonb NOT NULL,                         -- {"code": "ProductNumber", "external_key": "ProductID", ...}
    is_active           boolean NOT NULL DEFAULT true,
    last_run_at         timestamptz,
    last_run_by         text,
    last_run_summary    text,                                   -- what the last import (not a check) did, for the screen
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT material_connection_external_ck CHECK (length(external_system) BETWEEN 1 AND 100)
);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulaski_app') THEN
        GRANT SELECT, INSERT, UPDATE ON core.material_connection TO pulaski_app;
    END IF;
END $$;
