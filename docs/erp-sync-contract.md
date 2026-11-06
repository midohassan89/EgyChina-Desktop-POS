# ERP sync contract

This file separates what was read from the running ERP on 6 Nov 2026 from what is still blocked. The desktop app posts only the call the browser already makes. It does not invent a device API.

## Source access

| Check | Result |
| --- | --- |
| Origin `repo list` for this token | Empty. The ERP repository is not in this account. |
| `gh` auth | Not logged in. |
| Public web search for `removedPrismaIds` and `supermarket-pos` | No matching repository. |
| Production source maps | 404. |

Blocker: the ERP project source is not available from this session. Server idempotency, the money unit, and the allowed `paymentMethod` strings cannot be read from Prisma. Do not treat the local test stand-in as the ERP.

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

## Not verified

These stay closed. The worker will not send a sale while any of them is unknown.

| Gap | Why it matters |
| --- | --- |
| ERP repository | Cannot add or review server idempotency. |
| `localId` dedupe | A lost 2xx followed by a retry may create a second stock movement if the server does not ignore a repeated `localId`. The client retries the same id. That is necessary and not proven safe. |
| Money unit | The bundle forwards `total` and `unitPrice` as JSON numbers. It does not say pounds versus piasters. `EGYCHINA_POS_MONEY_SCALE` must be `major` or `minor` only after someone checks the server. Default is unverified, and nothing is posted. |
| `paymentMethod` strings | The field is required by the client and the allowed values are not in the bundle. Set `EGYCHINA_POS_PAYMENT_METHOD` only to a value taken from the ERP source or a captured browser request. |
| Device credential | None exists in the shipped client. `EGYCHINA_POS_COOKIE` is a staff Auth.js session, it expires, and it is not a till identity. |
| Price field on `products` | Not referenced by the sync chunk. Catalog pull links `sku` to numeric `id` and does not change shelf prices. |

The earlier `/api/pos/v1/*` device API in this document was a proposal. It is not what the browser calls. Do not implement it unless it appears in the ERP source.

## What the desktop does with this

`EGYCHINA_POS_API` is the site origin, for example `https://erp.echinamart.com/`. The worker appends `api/checkout` and `api/products`.

Transmission starts only when all of these are set:

- `EGYCHINA_POS_API`
- `EGYCHINA_POS_COOKIE` (the session cookie; never commit it)
- `EGYCHINA_POS_MONEY_SCALE` = `major` or `minor`
- `EGYCHINA_POS_PAYMENT_METHOD`

Optional: `EGYCHINA_POS_CATALOG_PULL=1` also calls `GET /api/products` and stores the numeric id on the local product with the same `sku`. Prices stay as they were.

A row is marked `done` and the sale `synced` only after the response body has been read and the status is 2xx. A dropped response, a 401, or a 5xx leaves the row pending. The next tick sends the same `localId`. Receipt numbers and minor-unit totals in SQLite are not rewritten.

Demo barcodes stay pending until a catalog pull links their `sku` to a positive numeric `id`. The worker does not invent that id.

No test in this repo calls `erp.echinamart.com`. The HTTP tests use a local stand-in.
