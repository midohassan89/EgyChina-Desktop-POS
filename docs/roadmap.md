# Roadmap

Exit criteria, not dates. Each phase ends when the checks pass on a real Souq El Obour till (i5-6300U class, 8 GB, SSD, the Xprinter if the phase prints).

## Phase 0 — this repo

Architecture, the ERP contract (all proposed routes marked **ASSUMPTION**), and a .NET 8 shell.

Done when:

- `dotnet test` passes.
- The app opens the cashier window and the customer window.
- The cashier is Arabic RTL. The customer window shows the same cart.
- `%LOCALAPPDATA%\EgyChina\Pos\pos.db` (or the Linux equivalent) is WAL mode and contains the migration.
- The sync worker records `not-configured` and does not POST.

## Phase 1 — MVP lane

A cashier can sell for cash in EGP with the network unplugged, then the sale reaches the ERP when it returns.

Cashier:

- PIN login, 6 digits, lockout, roles loaded from the ERP.
- Shift open/close with opening float.
- Scanner wedge, search, cart, line quantity.
- Cash EGP tender and change. One transaction as in the architecture: receipt number, sale, outbox, commit, then print.
- Reprint last receipt.
- Customer window follows the cart and shows the amount due and the change.
- Pay stays disabled until `catalog.source=erp`.

Sync:

- Enroll the PC once. Device token in DPAPI or Credential Manager.
- Pull catalog, EGP prices, and cashier hashes.
- Push `SaleCompleted`, `ShiftOpened`, `ShiftClosed`.
- Status bar shows pending count and the last error a person can act on.

Print:

- Raster ESC/POS to the XP-P323B via the RAW spooler.
- Arabic receipt. No GDI.

Install:

- Auto-logon of the `pos` user, Startup shortcut, publish folder, Authenticode.
- Antivirus exclusion on the data directory.
- Written down `winver`, printer queue name, and which screen Windows calls primary.

ERP work sits in this phase too. The contract in [erp-sync-contract.md](erp-sync-contract.md) has to exist, or the till can only sell offline. Local selling does not wait on the server; the first pilot does, because the pilot has to prove a sale lands in Prisma.

Exit: ten real baskets, cable pulled mid-sale, cable restored, ten rows in the ERP, ten readable receipts, customer screen correct, no use of `/pos`.

## Phase 2 — parity with the current web POS

- CNY cash and WeChat, customer rate snapshotted with `Money.ApplyRate`, margin stored and not recomputed.
- Instapay and WeChat QR images on the customer window only.
- Mixed tenders on one sale.
- Discount, void, and return. Supervisor PIN. Each is an outbox event, not a silent edit.
- X and Z totals on the till.
- Drawer kick if a drawer is on the printer.
- Receipt layout matched to a redacted sample from the current web POS.
- Update feed applied only when no shift is open, with a one-version rollback. Velopack is allowed here.

Exit: a supervisor can run a Saturday shift on this app without opening `/pos` for a feature that the browser till still has.

## Phase 3 — many tills

- Enrollment per PC. Receipt numbers are unique per terminal and are not a single store-wide sequence.
- Cashiers, rates, QR assets, and catalog are server-owned. Sales are client-owned and idempotent.
- Oversell policy stays server-side. The till warns only if we later choose a soft local quantity, and it still completes the sale.
- A back-office view on the ERP lists tills whose outbox heartbeat is late or whose catalog cursor is old. That view is not a screen on the cashier.
- Optional second printer. No peer-to-peer sync between PCs.

Exit: two tills offline at once, both selling the same SKU, both syncing without duplicate receipts or lost baskets.

## Not in this product

- A rewrite of `egychina-pos-app`.
- Replacing egychina-erp, Prisma, or the online SQLite database.
- WordPress, WooCommerce, or re-enabling the Woo sync.
- Card acquiring, wallet APIs inside the till, or storing PAN data.
- A second UI toolkit, a web view of `/pos`, or Electron.
- Inventory counting, purchasing, or label printing.
- Weighing scales, unless a later visit shows a scale on the lane. None is assumed.
