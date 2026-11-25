-- System health (#141, D-118): the permission to see it. No role holds it to start with; the administrator has every permission.

INSERT INTO sec.permission (code, description) VALUES
    ('system.view', 'See system health: edge nodes, deliveries, connections, background services and the database')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;
