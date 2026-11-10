-- 003_reminders: what the in-process reminder scheduler (M9) needs on top of the 001 tables.
-- notifications.task_id: the notification center opens the task a notification is about.
-- reminder_occurrences.origin: 'plan' occurrences follow the task (replanned when the due date moves);
-- 'snooze' occurrences were asked for by the user and are kept until acted on or the task closes.

ALTER TABLE notifications ADD COLUMN task_id TEXT;

ALTER TABLE reminder_occurrences ADD COLUMN origin TEXT NOT NULL DEFAULT 'plan' CHECK (origin IN ('plan', 'snooze'));

CREATE INDEX ix_occurrences_reminder ON reminder_occurrences (reminder_id, state);
CREATE INDEX ix_notifications_created ON notifications (created_at);
