-- Audit trail (#129, D-128): the permission to browse evt.event, held at every site (events don't record a site). No role holds it to
-- start with; the administrator has every permission. Searching by subject uses the existing (subject, sequence) index.

INSERT INTO sec.permission (code, description) VALUES
    ('audit.view', 'Browse the audit trail: who changed what, and when, everywhere')
ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description;
