# ERP sync contract

This file separates what was read from the running ERP on 6 Nov 2026 from what is still blocked. The desktop app posts only the call the browser already makes. It does not invent a device API.

## Source access

| Check | Result |
| --- | --- |
| Origin `repo list` for this token (cloud session) | Empty. The ERP repository was not in that account. |
| Local workspace (7 Nov 2026) | `egychina-erp` is available beside this repo as `../ERP` in `EgyChina.code-workspace`. Facts below under **Verified from local ERP source** come from that tree. |
| `gh` auth (cloud session) | Not logged in. |
| Public web search for `removedPrismaIds` and `supermarket-pos` | No matching repository. |
| Production source maps | 404. |

Do not treat the local HTTP test stand-in in `EgyChina.Pos.Tests` as the ERP. Cloud sessions without the ERP tree must keep money scale and payment method unverified until someone reads the source or captures a browser request.

## Verified from the live client

Page `https://erp.echinamart.com/login` loaded

`/_next/static/chunks/9608-782a6c6dc71e73b4.js`

and the auth chunk `6199-69bb8dda627e9669.js`. Build id `iQKkIOQUffLs92zrpKc_h`.

Auth is Auth.js (next-auth). The browser calls `fetch` with no `Authorization` header, so the credential is the same-origin session cookie. Cookie names observed on the login redirect: `__Host-authjs.csrf-token` and `__Secure-authjs.callback-url`. The session cookie itself is not set until someone logs in. There is no device or terminal enrollment call in this bundle.

Catalog:

- `GET /api/products` for a full copy
- `GET /api/products?updatedSince=<ISO>` when a local copy and `lastSyncedAt` already exist
- Success JSON uses `serverTime`, `products`, and on a delta `removedPrismaIds`
- Failure JSON uses `{ "error": "<string>" }`
- Product objects in that client expose `id`, `sku`, `prismaId`, `name`, `posKey`, `parentId`, `isPackage`
- A missing `posKey` is filled as `prismaId`, or `wc-<id>-<sku>`

Checkout:

`POST /api/checkout` with `Content-Type: application/json` and this body:

```json
{
  "paymentMethod": "<value from the sale>",
  "amount": "<absolute value of total>",
  "total": "<sale total>",
  "isReturn": false,
  "shiftId": null,
  "localId": "<client sale id>",
  "createdAt": "<ISO time>",
  "customerName": null,
  "employeeId": null,
  "wooOrderId": null,
  "lines": [
    {
      "productId": 0,
      "name": "",
      "qty": 1,
      "unitPrice": 0,
      "lineTotal": 0,
      "stockMultiplier": 1
    }
  ]
}
```

`productId` is a positive number. The same field is sent to `POST /api/pos/restock` as `wcId` for returns. This desktop build does not call restock and does not talk to WooCommerce. `wooOrderId` is sent as null because the live client includes the property.

The client treats any completed HTTP success as synced. It does not read a server id from the body. On failure it keeps the sale pending and stores `error`. A network failure stops the batch. The retry key the client uses is `localId`.

`/api/products` and `/api/checkout` without a session returned the same 401 `{"error":"Unauthorized"}` gate as the other `/api/*` paths checked on 5 Nov 2026. That confirms the routes are behind the auth gate. It does not show a success body.

## Verified from local ERP source (workspace `ERP`, 7 Nov 2026)

Readable beside this repo. Re-checked against current files (not the earlier cloud-only notes).

| Item | Evidence |
| --- | --- |
| POS catalog `id` | Full catalog `GET /api/products` (no `page`) returns `CachedProduct` via `expandPosCatalog` / `prismaProductToCached`: **`id` is already `wcId` (number)**; **`prismaId` is the Prisma cuid**. Do not treat POS `id` as a cuid. |
| Admin catalog shape | With `?page=`, the same route returns Prisma `id` (cuid string) and separate `wcId`. Desktop linker prefers `wcId` when present. |
| `localId` after success | Fast-path lookup returns `alreadySynced`. |
| `localId` under concurrency (phase 2) | `commitPosCheckout` (`ERP/src/lib/pos/commitPosCheckout.ts`) inserts the sale first inside one transaction (claims `localId`), then applies stock/shift. Concurrent twin or lost-2xx retry → one sale, one stock move. Proven by `npm run test:checkout-idemp` on a temp SQLite DB — not against production. |
| Money unit | `roundMoney` → two decimal pound amounts. Desktop: `EGYCHINA_POS_MONEY_SCALE=major`. |
| Cash `paymentMethod` | `cash` (also `visa`, `wallet`, `instapay`, `wechat`, `STAFF_MEAL`). |
| Line `productId` | Numeric `wcId` for stock/sale lines — not the cuid. |
| Desktop catalog link (phase 1) | Exact non-empty SKU → `erp_numeric_id`; never changes `product_price`; never links `DEMO-*` / `demo-*` rows by guess. Unlinked lines stay outbox-blocked (`product-id-not-numeric`). |
| Auth (phase 3) | Desktop tills use enrolled **device Bearer** (`PosDevice.tokenHash`). Staff cookie still works for browser `/pos`. Cookie alone does **not** open desktop sync. Enroll/revoke: `/api/pos/device/*` (MANAGER/ADMIN). |
| Outbox recovery (phase 4) | `inflight` → `pending` on worker start and each tick. Poison map blocks → `dead` (sale kept). `product-id-not-numeric` stays `pending`. First failure retries immediately (`delay=0`); later failures back off 15s…15m. Cashier bar shows pending + dead + last error. Proven by `OutboxRecoveryTests` (local stub only). |
| Live catalog pull (phase 5) | With `EGYCHINA_POS_CATALOG_PULL=1`, worker calls `ApplyCatalogFromErpJson`: upsert shelf price/barcode/names, set `catalog.source=erp`, purge unused `DEMO-*` (referenced demos stay inactive for FK/history), deactivate `removedPrismaIds`. Cursor `catalog` stores `serverTime` for next `updatedSince`. Never rewrites `sale_line`. Proven by `CatalogPullTests` (local stub only). |
| Local cashier/shift (phase 6+) | Desktop requires PIN + open local shift before sale. Open/close push to `POST /api/shifts/open|close` with device Bearer + `cashierUserId`. Checkout posts numeric ERP `shiftId` + `cashierUserId` once `shift.server_id` is bound. Offline tills keep local SQLite and retry on the next sync tick. |

Default env with sync variables unset still posts nothing.

## Not verified / still open for production till use

| Gap | Why it matters |
| --- | --- |
| End-to-end post to production | Device auth + idempotency are local-proven only. Do not enable live till sync without a deliberate ops window. |
| Enroll UX in the Avalonia app | Token is imported from env/DPAPI; no in-app enroll dialog yet. |
| Refuse pay until `catalog.source=erp` | Still deferred; demo cash sales remain allowed offline. |

The earlier `/api/pos/v1/*` device API in this document was a proposal. It is not what the browser calls. Do not implement it unless it appears in the ERP source.

## What the desktop does with this

`EGYCHINA_POS_API` is the site origin, for example `https://erp.echinamart.com/`. The worker appends `api/checkout` and `api/products`.

Transmission starts only when all of these are set:

- `EGYCHINA_POS_API`
- `EGYCHINA_POS_DEVICE_TOKEN` (enrolled device Bearer; stored under DPAPI on Windows — never commit it)
- `EGYCHINA_POS_MONEY_SCALE` = `major` or `minor`
- `EGYCHINA_POS_PAYMENT_METHOD`

Optional: `EGYCHINA_POS_CATALOG_PULL=1` also calls `GET /api/products` (full, then `?updatedSince=` from the catalog cursor) and **applies** shelf prices, barcodes, and names. After a successful apply, `catalog.source=erp` and unused DEMO seed rows are removed. Committed `sale_line` name/price snapshots are never rewritten.

Interactive till calls (UI-6), same device Bearer as checkout:

- `GET /api/pos/queue-tickets/{code}` — claim `Q-xxxx` into the cart (PENDING → CLAIMED)
- `PATCH /api/pos/queue-tickets/{code}` — mark COMPLETED after local sale commit
- `GET /api/admin/customers/search?phone=` — loyalty lookup for `01…` / `201…` scans
- `GET /api/pos/online-orders` — read-only online order desk list (UI-7; status changes stay on web admin)

Local Z-report (UI-7) is built from SQLite for the open shift (no ERP call). Expected cash = opening float + net cash sales (returns with `note=return` reduce cash).

Checkout JSON may include `customerId`, `pointsEarned`, and `pointsRedeemed` when a loyalty customer is attached. ERP routes above accept enrolled device Bearer via `resolvePosAccess` (same gate as `/api/checkout`).

A row is marked `done` and the sale `synced` only after the response body has been read and the status is 2xx. A dropped response, a 401, or a 5xx leaves the row pending. The next tick sends the same `localId`. Receipt numbers and minor-unit totals in SQLite are not rewritten.

Unlinked SKUs stay outbox-blocked (`product-id-not-numeric`) until a pull matches them. The worker does not invent a checkout `wcId`.

No test in this repo calls `erp.echinamart.com`. The HTTP tests use a local stand-in.
