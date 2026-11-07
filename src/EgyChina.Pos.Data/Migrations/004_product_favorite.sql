-- Local POS favorites (till-side). Independent of ERP Product.isFavorite until catalog sync maps it.
CREATE TABLE product_favorite (
  product_id TEXT PRIMARY KEY REFERENCES product (id) ON DELETE CASCADE,
  pinned_at TEXT NOT NULL
);
