-- 0016 · Pulaski Edge (D-041): edge nodes, their connections and the metrics they have seen; tag bindings now name the
-- connection they are read through; each machine's live state and counters; the raw state and stop code on state
-- changes; the edge.ingest permission and the Edge node role. Nothing used the planned equipment_tag shape, so it is
-- reshaped in place. Re-runnable; no BEGIN/COMMIT (db/migrate.sh runs it in one transaction).

CREATE TABLE IF NOT EXISTS core.edge_node (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    site_id             uuid NOT NULL REFERENCES core.equipment(id),
    code                text NOT NULL,
    name                text NOT NULL,
    description         text,
    is_active           boolean NOT NULL DEFAULT true,
    stream_id           uuid,
    last_sequence       bigint NOT NULL DEFAULT 0,
    last_seen_at        timestamptz,
    agent_version       text,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT edge_node_code_uq UNIQUE (site_id, code)
);

CREATE TABLE IF NOT EXISTS core.edge_connection (
    id                  uuid PRIMARY KEY DEFAULT uuidv7(),
    edge_node_id        uuid NOT NULL REFERENCES core.edge_node(id),
    code                text NOT NULL,
    name                text NOT NULL,
    protocol            text NOT NULL CHECK (protocol IN ('sparkplug_b')),
    settings            jsonb NOT NULL DEFAULT '{}',
    is_active           boolean NOT NULL DEFAULT true,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT edge_connection_code_uq UNIQUE (edge_node_id, code)
);

CREATE TABLE IF NOT EXISTS core.edge_metric (
    connection_id       uuid NOT NULL REFERENCES core.edge_connection(id),
    address             text NOT NULL,
    data_type           text,
    last_value          text,
    last_seen_at        timestamptz NOT NULL,
    PRIMARY KEY (connection_id, address)
);

-- Tags are read through a connection: the protocol and endpoint name move there, the data type comes from the metric.
ALTER TABLE core.equipment_tag
    DROP CONSTRAINT IF EXISTS equipment_tag_source_uq,
    DROP COLUMN IF EXISTS source_protocol,
    DROP COLUMN IF EXISTS edge_node,
    DROP COLUMN IF EXISTS data_type,
    ADD COLUMN IF NOT EXISTS connection_id uuid NOT NULL REFERENCES core.edge_connection(id),
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
CREATE UNIQUE INDEX IF NOT EXISTS equipment_tag_role_uq ON core.equipment_tag (equipment_id, tag_role) WHERE is_active AND tag_role <> 'process_value';
CREATE UNIQUE INDEX IF NOT EXISTS equipment_tag_source_uq ON core.equipment_tag (connection_id, source_address) WHERE is_active;

CREATE TABLE IF NOT EXISTS core.equipment_live_state (
    equipment_id        uuid PRIMARY KEY REFERENCES core.equipment(id),
    raw_state           integer,
    packml_state        smallint NOT NULL REFERENCES core.packml_state(code),
    unit_mode           smallint NOT NULL DEFAULT 1,
    state_since         timestamptz NOT NULL,
    stop_reason         integer,
    machine_speed       numeric(18,6),
    signal_at           timestamptz NOT NULL,
    edge_node_id        uuid REFERENCES core.edge_node(id),
    updated_at          timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS core.equipment_counter (
    equipment_id        uuid NOT NULL REFERENCES core.equipment(id),
    count_kind          text NOT NULL CHECK (count_kind IN ('processed','defective','consumed')),
    raw_counter         bigint NOT NULL,
    read_at             timestamptz NOT NULL,
    PRIMARY KEY (equipment_id, count_kind)
);

ALTER TABLE ts.equipment_state_change
    ADD COLUMN IF NOT EXISTS raw_state integer,
    ADD COLUMN IF NOT EXISTS stop_reason integer;

INSERT INTO sec.permission (code, description) VALUES
    ('equipment.manage', 'Maintain equipment: sites, lines, locations, machine profiles, edge nodes and tags'),
    ('edge.ingest', 'Send machine data as a Pulaski Edge node: read its configuration and forward what it collects')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;

INSERT INTO sec.role (code, name, description, is_system) VALUES
    ('edge_node', 'Edge node', 'For the service user a Pulaski Edge node signs in as: reads its configuration and forwards machine data. Sees no screens.', true)
ON CONFLICT (code) DO NOTHING;

INSERT INTO sec.role_permission (role_id, permission_code)
SELECT r.id, 'edge.ingest' FROM sec.role r WHERE r.code = 'edge_node' AND r.is_system
ON CONFLICT (role_id, permission_code) DO NOTHING;
