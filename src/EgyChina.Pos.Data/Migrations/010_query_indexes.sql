-- Checkout reads the newest completed sales, then the latest outbox row for each.
-- The Z-report filters one shift and orders by completed_at. completed_at is on that
-- index so the planner seeks the shift instead of walking every completed sale.
-- Demo purge probes sale_line by product.

CREATE INDEX IF NOT EXISTS ix_outbox_aggregate
  ON outbox (aggregate_type, aggregate_id, created_at);

CREATE INDEX IF NOT EXISTS ix_sale_completed_recent
  ON sale (status, completed_at DESC, receipt_no DESC);

CREATE INDEX IF NOT EXISTS ix_sale_shift_status
  ON sale (shift_id, status, completed_at);

CREATE INDEX IF NOT EXISTS ix_sale_line_product
  ON sale_line (product_id);
