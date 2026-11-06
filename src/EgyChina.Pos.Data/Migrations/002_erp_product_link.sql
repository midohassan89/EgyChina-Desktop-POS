-- Link a local product to the numeric id the live ERP checkout call sends.
-- Prices are not changed here. The shelf price stays the local minor-unit value.

ALTER TABLE product ADD COLUMN erp_numeric_id INTEGER;

ALTER TABLE product ADD COLUMN prisma_id TEXT;
