-- Local replica and outbox for one cashier terminal.
-- Money is integer minor units. Quantities are milli-units (1000 = 1).

CREATE TABLE terminal (
  id TEXT PRIMARY KEY,
  store_code TEXT NOT NULL,
  name TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE TABLE app_setting (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE product (
  id TEXT PRIMARY KEY,
  sku TEXT NOT NULL,
  barcode TEXT,
  name_ar TEXT NOT NULL,
  name_en TEXT,
  name_zh TEXT,
  unit_ar TEXT NOT NULL DEFAULT 'قطعة',
  tax_rate_bps INTEGER NOT NULL DEFAULT 0,
  is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
  updated_at TEXT NOT NULL,
  server_revision TEXT
);

CREATE UNIQUE INDEX ux_product_sku ON product (sku);

CREATE UNIQUE INDEX ux_product_barcode ON product (barcode)
  WHERE barcode IS NOT NULL AND barcode <> '';

CREATE TABLE product_price (
  product_id TEXT NOT NULL REFERENCES product (id),
  currency TEXT NOT NULL CHECK (currency IN ('EGP', 'CNY')),
  amount_minor INTEGER NOT NULL CHECK (amount_minor >= 0),
  PRIMARY KEY (product_id, currency)
);

CREATE TABLE exchange_rate (
  id TEXT PRIMARY KEY,
  base_currency TEXT NOT NULL,
  quote_currency TEXT NOT NULL,
  rate_numerator INTEGER NOT NULL,
  rate_denominator INTEGER NOT NULL CHECK (rate_denominator > 0),
  margin_bps INTEGER NOT NULL DEFAULT 0,
  effective_at TEXT NOT NULL,
  pulled_at TEXT NOT NULL
);

CREATE TABLE cashier (
  id TEXT PRIMARY KEY,
  display_name TEXT NOT NULL,
  role TEXT NOT NULL CHECK (role IN ('cashier', 'supervisor', 'manager')),
  pin_hash TEXT NOT NULL,
  pin_salt TEXT NOT NULL,
  is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
  updated_at TEXT NOT NULL
);

CREATE TABLE shift (
  id TEXT PRIMARY KEY,
  server_id TEXT,
  cashier_id TEXT NOT NULL REFERENCES cashier (id),
  opened_at TEXT NOT NULL,
  closed_at TEXT,
  opening_float_minor INTEGER NOT NULL,
  closing_cash_minor INTEGER,
  status TEXT NOT NULL CHECK (status IN ('open', 'closed')),
  sync_status TEXT NOT NULL CHECK (sync_status IN ('pending', 'synced', 'error'))
);

CREATE TABLE sale (
  id TEXT PRIMARY KEY,
  server_id TEXT,
  shift_id TEXT REFERENCES shift (id),
  cashier_id TEXT NOT NULL REFERENCES cashier (id),
  receipt_no INTEGER,
  status TEXT NOT NULL CHECK (status IN ('draft', 'completed', 'voided')),
  currency TEXT NOT NULL CHECK (currency IN ('EGP', 'CNY')),
  subtotal_minor INTEGER NOT NULL DEFAULT 0,
  discount_minor INTEGER NOT NULL DEFAULT 0,
  tax_minor INTEGER NOT NULL DEFAULT 0,
  total_minor INTEGER NOT NULL DEFAULT 0,
  fx_base_currency TEXT,
  fx_rate_numerator INTEGER,
  fx_rate_denominator INTEGER,
  fx_margin_bps INTEGER,
  note TEXT,
  created_at TEXT NOT NULL,
  completed_at TEXT,
  sync_status TEXT NOT NULL CHECK (sync_status IN ('pending', 'synced', 'error'))
);

CREATE UNIQUE INDEX ux_sale_receipt_no ON sale (receipt_no)
  WHERE receipt_no IS NOT NULL;

CREATE INDEX ix_sale_sync ON sale (sync_status, created_at);

CREATE TABLE sale_line (
  id TEXT PRIMARY KEY,
  sale_id TEXT NOT NULL REFERENCES sale (id),
  line_no INTEGER NOT NULL,
  product_id TEXT REFERENCES product (id),
  barcode TEXT,
  name_ar TEXT NOT NULL,
  qty_milli INTEGER NOT NULL CHECK (qty_milli > 0),
  unit_price_minor INTEGER NOT NULL,
  discount_minor INTEGER NOT NULL DEFAULT 0,
  line_total_minor INTEGER NOT NULL,
  UNIQUE (sale_id, line_no)
);

CREATE TABLE payment (
  id TEXT PRIMARY KEY,
  sale_id TEXT NOT NULL REFERENCES sale (id),
  method TEXT NOT NULL CHECK (method IN ('cash_egp', 'cash_cny', 'wechat', 'instapay', 'card')),
  currency TEXT NOT NULL,
  amount_minor INTEGER NOT NULL,
  amount_egp_minor INTEGER NOT NULL,
  reference TEXT,
  created_at TEXT NOT NULL
);

CREATE INDEX ix_payment_sale ON payment (sale_id);

CREATE TABLE outbox (
  id TEXT PRIMARY KEY,
  aggregate_type TEXT NOT NULL,
  aggregate_id TEXT NOT NULL,
  event_type TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  idempotency_key TEXT NOT NULL UNIQUE,
  created_at TEXT NOT NULL,
  attempt_count INTEGER NOT NULL DEFAULT 0,
  next_attempt_at TEXT NOT NULL,
  status TEXT NOT NULL CHECK (status IN ('pending', 'inflight', 'done', 'dead')),
  last_error TEXT
);

CREATE INDEX ix_outbox_due ON outbox (status, next_attempt_at);

CREATE TABLE sync_cursor (
  stream TEXT PRIMARY KEY,
  cursor TEXT,
  last_success_at TEXT,
  last_error TEXT
);

CREATE TABLE qr_asset (
  code TEXT PRIMARY KEY CHECK (code IN ('instapay', 'wechat')),
  image_blob BLOB,
  content_hash TEXT,
  updated_at TEXT NOT NULL
);

CREATE TABLE receipt_counter (
  id INTEGER PRIMARY KEY CHECK (id = 1),
  next_no INTEGER NOT NULL
);

INSERT INTO receipt_counter (id, next_no) VALUES (1, 1);
