-- 0058 · Sign-in through identity providers (#50, D-091): a local account may be marked as a machine's, so it keeps its password when only
-- administrators sign in with one. Re-runnable; no BEGIN/COMMIT.

ALTER TABLE sec.app_user ADD COLUMN IF NOT EXISTS is_service_account boolean NOT NULL DEFAULT false;

-- The edge's accounts (D-050) are machines' accounts.
UPDATE sec.app_user u SET is_service_account = true
WHERE NOT u.is_service_account
  AND EXISTS (SELECT 1 FROM sec.role_assignment a JOIN sec.role r ON r.id = a.role_id WHERE a.app_user_id = u.id AND r.code = 'edge_node');
