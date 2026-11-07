-- Local PIN lockout counters. Never stores the PIN itself.
ALTER TABLE cashier ADD COLUMN failed_attempts INTEGER NOT NULL DEFAULT 0;
ALTER TABLE cashier ADD COLUMN locked_until TEXT;
