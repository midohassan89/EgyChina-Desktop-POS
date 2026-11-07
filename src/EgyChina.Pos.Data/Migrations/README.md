# Local SQLite migrations

Embedded as resources and applied in order by `PosDatabase.Initialize`.
Never run these against the ERP Prisma database.

| Version | File | Purpose |
| --- | --- | --- |
| 1 | `001_init.sql` | Core terminal tables (catalog, sales, outbox, settings, receipt counter) |
| 2 | `002_erp_product_link.sql` | ERP product link columns (`erp_numeric_id`, `erp_prisma_id`, …) |
| 3 | `003_cashier_lockout.sql` | Cashier auth lockout support |
| 4 | `004_product_favorite.sql` | Local till favorites (`product_favorite`) |
| 5 | `005_held_cart.sql` | Suspended baskets (`held_cart`) |
| 6 | `006_payment_wallet.sql` | Payment methods include `wallet` |

Current schema version on a fresh init: **6**.

Adding a migration:

1. Add `00N_name.sql` next to these files (embedded via `Migrations\*.sql`).
2. Extend the version ladder in `PosDatabase.Initialize`.
3. Cover with a unit test that asserts `GetSchemaVersion()`.
