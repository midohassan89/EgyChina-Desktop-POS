-- Suspended till baskets (local only — not synced to ERP).
CREATE TABLE held_cart (
  id TEXT PRIMARY KEY,
  label TEXT NOT NULL,
  held_at TEXT NOT NULL,
  item_count INTEGER NOT NULL CHECK (item_count > 0),
  total_minor INTEGER NOT NULL CHECK (total_minor >= 0),
  payload_json TEXT NOT NULL,
  cashier_id TEXT
);

CREATE INDEX ix_held_cart_held_at ON held_cart (held_at DESC);
