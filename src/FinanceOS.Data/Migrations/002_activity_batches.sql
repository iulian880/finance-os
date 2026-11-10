-- 002_activity_batches: group activity_log rows written by one user command (M6).
-- One command can change several entities (deleting a task also trashes its subtasks; deleting a tag
-- removes it from tasks). Undo reverts the whole batch, so the rows need a shared id.
-- Rows written before this migration have no batch; they stay readable history and are never undone.

ALTER TABLE activity_log ADD COLUMN batch_id TEXT;

CREATE INDEX ix_activity_batch ON activity_log (batch_id) WHERE batch_id IS NOT NULL;

-- Tags get a manual order like categories and projects (the three are handled the same way).
ALTER TABLE tags ADD COLUMN sort INTEGER NOT NULL DEFAULT 0;
