-- Favorites must survive catalog upserts that replace product rows (ON DELETE CASCADE
-- previously wiped every pin when a DEMO purge or id remap deleted the product).
-- Store barcode + prisma_id so we can rebind product_id after sync.
CREATE TABLE product_favorite_v2 (
  product_id TEXT NOT NULL,
  pinned_at TEXT NOT NULL,
  barcode TEXT,
  prisma_id TEXT,
  PRIMARY KEY (product_id)
);

INSERT OR IGNORE INTO product_favorite_v2 (product_id, pinned_at, barcode, prisma_id)
SELECT
  f.product_id,
  f.pinned_at,
  p.barcode,
  p.prisma_id
FROM product_favorite f
LEFT JOIN product p ON p.id = f.product_id;

DROP TABLE product_favorite;
ALTER TABLE product_favorite_v2 RENAME TO product_favorite;

CREATE INDEX IF NOT EXISTS idx_product_favorite_barcode ON product_favorite (barcode);
CREATE INDEX IF NOT EXISTS idx_product_favorite_prisma ON product_favorite (prisma_id);
