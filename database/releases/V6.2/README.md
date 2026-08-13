# Database update V6.2

Run `01-V6.2-update.sql` against the existing SQL Server database `ksb_pr`
before deploying the V6.2 backend/frontend.

The script is repeat-safe. It inserts missing permissions, links missing
release permissions only to Superadmin, preserves existing role assignments,
and restores only the explicitly requested user with mobile `9920037907` by
setting `deleted_at` to `NULL`.
