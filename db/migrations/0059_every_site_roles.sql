-- 0059 · Roles held at every site (#49, D-092): a role an administrator allows may also be given everywhere, not only at a site.
-- Re-runnable; no BEGIN/COMMIT.

ALTER TABLE sec.role ADD COLUMN IF NOT EXISTS allows_every_site boolean NOT NULL DEFAULT false;
