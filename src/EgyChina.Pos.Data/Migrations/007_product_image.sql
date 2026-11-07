-- Remote product image URL from ERP (relative /uploads/... or absolute https://...).
-- Binary files are cached under LocalAppData/EgyChina/Pos/product-images/.

ALTER TABLE product ADD COLUMN image_url TEXT;
