# ERP sync contract (proposed)

Status: **proposal**. Nothing in this file was confirmed by an authenticated call.

The desktop till needs a device API. It must not scrape the browser POS and it must not reuse the Auth.js session cookie as its long-lived credential.

## What is actually known

On 5 Nov 2026, unauthenticated:

- `https://erp.echinamart.com/pos` redirects to the staff login. The browser POS is a Next.js page behind Auth.js credentials.
- `GET /api/auth/providers` returns a credentials provider. `GET /api/auth/session` returns `null` without a cookie.
- `GET /api/health`, `/api/pos`, `/api/products`, `/api/sales`, and `/api/shifts` each returned `401 {"error":"Unauthorized"}`.

The identical 401 means “not logged in”, not “this route is implemented”. The Expo app `egychina-pos-app` surely calls the ERP already. **ASSUMPTION:** that client uses a staff session, not a terminal outbox. This contract does not copy it. If the Expo app already has a bearer-token API that can accept an idempotency key, extend that instead of adding a parallel one — this needs a yes or no from the ERP repo, which is not in this workspace.

WooCommerce is out of scope. Do not add routes under a Woo name.

## Conventions

Base URL, **ASSUMPTION:** `https://erp.echinamart.com/api/pos/v1/`

The shell’s probe joins `health` onto `EGYCHINA_POS_API`. The value must be absolute and should end with `/`.

Headers once a device exists:

```http
Authorization: Bearer <device-token>
Content-Type: application/json
Accept: application/json
User-Agent: EgyChina-POS/<version>
```

Ids that the till creates are UUID strings. Money is a JSON integer in minor units. Rates are integers `rateNumerator` and `rateDenominator`. Times are UTC ISO-8601. The server treats timestamps as information. The idempotency key is the identity of the event.

Errors:

```json
{ "error": "short_code", "message": "Arabic or English, safe to log" }
```

| HTTP | Meaning |
| --- | --- |
| 401 | Missing or bad device token |
| 409 | Idempotency key reused with a different body |
| 422 | Body cannot be stored. Client may mark the outbox row `dead` |
| 429 | Back off. Honor `Retry-After` if present |
| 5xx or network | Stay `pending`. Never `dead` |

## Rounding

Both sides use half-up, away from zero on the absolute value:

```text
sign(amount) * floor((abs(amount) * rateNumerator + rateDenominator / 2) / rateDenominator)
```

This is `Money.ApplyRate` in `EgyChina.Pos.Core`. Example locked by a unit test: `100` minor at `7107/1000` = `711`.

**ASSUMPTION:** the server sends the customer rate with margin already included, and also sends `marginBps` so the till can store it. The till must not apply `marginBps` itself.

## Endpoints

All of the following are **ASSUMPTION**. None are implemented in this repo, and none were confirmed on the server.

### `GET /api/pos/v1/health`

No auth, so a new till can tell “DNS works” from “token rejected”.

```json
{ "ok": true, "version": "2026.11.0" }
```

The shell already GETs this URL when `EGYCHINA_POS_API` is set. It does not send a token and it does not POST.

### `POST /api/pos/v1/terminals/enroll`

First-run pairing. A supervisor does this once per PC. **ASSUMPTION:** the human proves they may enroll a till with a one-time code from the ERP admin UI, not with their everyday cashier PIN.

Request:

```json
{
  "enrollmentCode": "string",
  "terminalId": "uuid-generated-on-the-pc",
  "storeCode": "OBOUR",
  "name": "كاشير ١",
  "appVersion": "0.1.0",
  "os": "win-x64"
}
```

Response:

```json
{
  "deviceToken": "opaque",
  "terminalId": "uuid",
  "storeCode": "OBOUR",
  "storeNameAr": "سوق العبور"
}
```

The token is stored with DPAPI, not in SQLite. Compromised enrollment codes are revoked server-side.

### `GET /api/pos/v1/catalog/changes?cursor=<opaque>`

Server-wins replica. Cursor is opaque. Empty cursor means “from the start”.

```json
{
  "cursor": "opaque-next",
  "products": [
    {
      "id": "erp-product-id",
      "sku": "string",
      "barcode": "622… or null",
      "nameAr": "أرز",
      "nameEn": "optional",
      "nameZh": "optional",
      "unitAr": "قطعة",
      "taxRateBps": 0,
      "isActive": true,
      "prices": [{ "currency": "EGP", "amountMinor": 8500 }]
    }
  ],
  "deletedIds": ["erp-product-id"]
}
```

The till upserts `products`, deletes `deletedIds`, and advances `sync_cursor` for stream `catalog` in one local transaction. Prices on a product replace that product’s `product_price` rows.

**ASSUMPTION:** `amountMinor` is the tax-inclusive shelf price. See the architecture.

A page should be bounded (suggest 500 products) so a 2-core PC can commit and draw between pages.

### `GET /api/pos/v1/rates/current`

```json
{
  "id": "erp-rate-id",
  "baseCurrency": "CNY",
  "quoteCurrency": "EGP",
  "rateNumerator": 7107,
  "rateDenominator": 1000,
  "marginBps": 300,
  "effectiveAt": "2026-11-05T00:00:00Z"
}
```

### `GET /api/pos/v1/cashiers`

```json
{
  "cashiers": [
    {
      "id": "erp-user-id",
      "displayName": "الاسم",
      "role": "cashier",
      "pinHash": "base64",
      "pinSalt": "base64",
      "pinAlgorithm": "PBKDF2-HMAC-SHA256",
      "pinIterations": 210000,
      "isActive": true,
      "updatedAt": "2026-11-05T00:00:00Z"
    }
  ]
}
```

**ASSUMPTION:** the ERP is willing to sync PIN verifiers to tills so login works offline. If it is not, offline login is impossible and phase 1 needs a different answer (a small set of till PINs issued only for the lane). Do not send the plaintext PIN.

### `GET /api/pos/v1/assets/qr/{code}`

`code` is `instapay` or `wechat`. Response is the PNG bytes (`image/png`) or JSON `{ "sha256": "…", "notModified": true }` when the till sends `If-None-Match`. The till stores the blob in `qr_asset`.

**ASSUMPTION:** these are static merchant QRs, not per-sale amount QRs.

### `POST /api/pos/v1/sync/events`

Push a batch of outbox rows. Suggest a max of 50 events or 256 KB.

```json
{
  "terminalId": "uuid",
  "events": [
    {
      "idempotencyKey": "terminalId:sale:saleId",
      "type": "SaleCompleted",
      "occurredAt": "2026-11-05T12:00:00Z",
      "payload": {}
    }
  ]
}
```

```json
{
  "results": [
    {
      "idempotencyKey": "terminalId:sale:saleId",
      "status": "accepted",
      "serverId": "erp-sale-id"
    }
  ]
}
```

`status` is `accepted` or `rejected`. A replay of the same key and the same body returns the original `serverId` and `accepted`. The same key with a different body is `409` on the whole request or `rejected` on that row — pick one in the ERP implementation and test it. Recommended: **409 for the request**, so a mixed batch is not half-applied. The till then sends the offending event alone and can mark it `dead` only on 422.

Event types for phase 1: `ShiftOpened`, `ShiftClosed`, `SaleCompleted`.

Phase 2 adds `SaleVoided`, `ReturnCompleted`. Voids are new events. The till does not expect the server to delete the original sale.

#### `SaleCompleted` payload

Self-contained. The server must accept it even if the product id was deleted after the sale.

```json
{
  "saleId": "uuid",
  "shiftId": "uuid",
  "cashierId": "erp-user-id",
  "receiptNo": 1042,
  "currency": "EGP",
  "subtotalMinor": 14750,
  "discountMinor": 0,
  "taxMinor": 0,
  "totalMinor": 14750,
  "fx": null,
  "lines": [
    {
      "lineNo": 1,
      "productId": "erp-product-id",
      "barcode": "2000000000018",
      "nameAr": "أرز مصري ٥ كجم",
      "qtyMilli": 1000,
      "unitPriceMinor": 8500,
      "discountMinor": 0,
      "lineTotalMinor": 8500
    }
  ],
  "payments": [
    {
      "method": "cash_egp",
      "currency": "EGP",
      "amountMinor": 20000,
      "amountEgpMinor": 20000,
      "reference": null
    }
  ]
}
```

When CNY is used, `fx` is:

```json
{
  "baseCurrency": "CNY",
  "rateNumerator": 7107,
  "rateDenominator": 1000,
  "marginBps": 300
}
```

and the payment row carries `amountMinor` in fen plus `amountEgpMinor` computed with `Money.ApplyRate`.

#### `ShiftOpened` / `ShiftClosed`

```json
{
  "shiftId": "uuid",
  "cashierId": "erp-user-id",
  "openedAt": "2026-11-05T08:00:00Z",
  "openingFloatMinor": 500000,
  "closedAt": null,
  "closingCashMinor": null
}
```

`ShiftClosed` fills `closedAt` and `closingCashMinor`.

### `GET /api/pos/v1/releases/latest?channel=stable&os=win-x64`

Phase 1 can ship without this and copy folders by hand.

```json
{
  "version": "0.2.0",
  "url": "https://erp.echinamart.com/…/EgyChina.Pos-win-x64.zip",
  "sha256": "hex",
  "signature": "base64 Authenticode or a minisign signature"
}
```

The till downloads only when no shift is open, checks the hash and the signature, and keeps the previous directory.

## Open questions for the ERP repo

1. Does `egychina-pos-app` already have a token and a sale POST we should extend?
2. Are shelf prices tax-inclusive for every department that will hit this till?
3. Who is allowed to enroll a terminal, and where does the one-time code live in the admin UI?
4. Is offline oversell acceptable for the hypermarket?
5. Static QR, or does finance want an amount embedded in the QR?
6. Can PIN verifiers leave the server, or do cashiers need online login only?
7. A redacted copy of the current `/pos` receipt layout, so phase 2 matches it.
8. The Windows printer queue name on a cashier PC, and whether the Xprinter is RAW-capable in that driver.
