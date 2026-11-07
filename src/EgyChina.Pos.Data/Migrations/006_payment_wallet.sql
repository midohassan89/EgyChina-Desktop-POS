-- Allow wallet beside existing till payment methods (SQLite cannot ALTER CHECK in place).
PRAGMA foreign_keys=OFF;

CREATE TABLE payment_new (
  id TEXT PRIMARY KEY,
  sale_id TEXT NOT NULL REFERENCES sale (id) ON DELETE CASCADE,
  method TEXT NOT NULL CHECK (method IN ('cash_egp', 'cash_cny', 'wechat', 'instapay', 'card', 'wallet')),
  currency TEXT NOT NULL CHECK (currency IN ('EGP', 'CNY')),
  amount_minor INTEGER NOT NULL CHECK (amount_minor >= 0),
  amount_egp_minor INTEGER NOT NULL CHECK (amount_egp_minor >= 0),
  reference TEXT,
  created_at TEXT NOT NULL
);

INSERT INTO payment_new (
  id, sale_id, method, currency, amount_minor, amount_egp_minor, reference, created_at
)
SELECT id, sale_id, method, currency, amount_minor, amount_egp_minor, reference, created_at
FROM payment;

DROP TABLE payment;
ALTER TABLE payment_new RENAME TO payment;

CREATE INDEX ix_payment_sale ON payment (sale_id);

PRAGMA foreign_keys=ON;
