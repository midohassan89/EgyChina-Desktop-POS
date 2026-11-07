-- Local category shelf mirrored from ERP GET /api/store/categories.
-- category_id on product is a soft link (no FK) so catalog sync can land before category rows.

CREATE TABLE IF NOT EXISTS category (
  id TEXT PRIMARY KEY,
  name_ar TEXT NOT NULL,
  name_en TEXT,
  name_zh TEXT,
  slug TEXT,
  image_url TEXT,
  product_count INTEGER NOT NULL DEFAULT 0,
  updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_category_name_ar ON category (name_ar);

ALTER TABLE product ADD COLUMN category_id TEXT;

CREATE INDEX IF NOT EXISTS ix_product_category_id ON product (category_id);
