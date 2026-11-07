# EgyChina POS

Local-first Windows cashier for **ايجي شاينا ماركت** at **سوق العبور** (Souq El Obour). The online system of record stays [egychina-erp](https://erp.echinamart.com). This repository is the native till: architecture, the proposed sync contract, and a .NET 8 shell.

Cashiers should not need the browser page at `/pos`. The Expo app `egychina-pos-app` stays the mobile terminal. Nothing here talks to WordPress or WooCommerce.

Cash EGP checkout works offline. The app opens two windows, stores the sale in SQLite, and enqueues it. It does not post sales. Print, PIN, and the ERP push are not built.

## Docs

- [Architecture](docs/architecture.md) — process, screens, schema, printing, security, the i5-6300U budget
- [Roadmap](docs/roadmap.md) — shell, then an offline cash lane, then parity with the web POS, then many tills
- [ERP contract](docs/erp-sync-contract.md) — live `/api/checkout` shape + gaps; local ERP source notes when available
- [Local workspace setup](docs/local-workspace.md) — how this repo sits next to `egychina-erp` in `EgyChina.code-workspace`, safe build/run commands, env names without secrets

## Stack

.NET 8, C#, Avalonia 12, SQLite (WAL), `Microsoft.Data.Sqlite`. On Windows the UI is forced to software rendering because the cashier PCs are Intel i5-6300U / HD Graphics 520 class machines with 8 GB of RAM. See the architecture doc for why this is not WinUI 3.

## Run on Windows

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The app still targets **.NET 8**, so a self-contained publish carries the .NET 8 runtime and the tills do not need a newer runtime. Avalonia 12's XAML compiler needs the Roslyn that ships with the .NET 10 SDK; the .NET 8 SDK alone stops at that compiler.

```powershell
dotnet test
dotnet run --project src\EgyChina.Pos.App
```

The cashier window is Arabic, right to left. The customer window opens beside it when Windows reports one screen, and fullscreen on the second screen when there is one. Closing the customer window does not exit. **شاشة العميل** opens it again.

Database and log:

```text
%LOCALAPPDATA%\EgyChina\Pos\pos.db
%LOCALAPPDATA%\EgyChina\Pos\logs\pos.log
```

Demo barcodes, shelf prices in Egyptian pounds:

| Barcode | Item | Price |
| --- | --- | --- |
| `2000000000018` | أرز مصري ٥ كجم | 85.00 |
| `2000000000025` | زيت ذرة ١ لتر | 62.50 |
| `2000000000032` | شاي أخضر ٤٠٠ جم | 45.00 |

Type a code into the barcode box and press Enter, or click the tile. The total on the customer window follows the cart. **إتمام الدفع** turns on when the cart has lines. Enter the cash received (or keep the exact amount) and confirm. The cart clears, both windows show the change and the receipt number, and the sale stays in the local outbox.

The sync line stays local until `EGYCHINA_POS_API`, `EGYCHINA_POS_COOKIE`, `EGYCHINA_POS_MONEY_SCALE` (`major` or `minor`), and `EGYCHINA_POS_PAYMENT_METHOD` are all set. Those last three are not filled in by this repo: the ERP source was not available, and the browser bundle does not show the money unit or the allowed payment-method strings. With them unset, nothing is posted. See [the contract note](docs/erp-sync-contract.md).

Optional probe only (still no sale upload):

```powershell
$env:EGYCHINA_POS_API = "https://erp.echinamart.com/api/pos/v1/"
dotnet run --project src\EgyChina.Pos.App
```

The base URL must be absolute. A trailing slash is safer. The worker GETs `{base}/health` and shows the status code. Today the live ERP has no confirmed device API; an unauthenticated `/api/*` call returns 401 from the auth gate. Do not point this at production during a shift.

## Publish / install for a till

Run on Windows, from the repo root:

```powershell
.\scripts\publish-win-x64.ps1
.\scripts\install-version.ps1 -Source .\artifacts\win-x64\0.1.0
.\scripts\launch-pos.ps1
```

Layout on the PC:

```text
C:\EgyChina\Pos\app\<version>\   self-contained build
C:\EgyChina\Pos\current\         junction → active version
C:\EgyChina\Pos\previous.version
%LOCALAPPDATA%\EgyChina\Pos\     pos.db, logs, backups, heartbeat.json
```

Update with heartbeat check + automatic rollback:

```powershell
.\scripts\apply-update.ps1 -Source .\artifacts\win-x64\0.1.0
```

Restore SQLite from a backup (POS must be closed; never restores `device.cred`):

```powershell
.\scripts\restore-database.ps1 -BackupPath "$env:LOCALAPPDATA\EgyChina\Pos\backups\pos-….db"
```

Auto-logon / Startup shortcut / Authenticode are still ops steps (see architecture). Confirm `winver` is 64-bit Windows 10 1809 or later. .NET 8 does not run on Windows 7.

## How this sits next to the ERP

| System | Role |
| --- | --- |
| egychina-erp | Source of truth online. Staff login is Auth.js. `/pos` is the browser till this app replaces for cashiers |
| This repo | One Windows terminal. Sells from a local replica. Pushes completed sales later, through the outbox |
| egychina-pos-app | Phones and tablets. Not this codebase |

The ERP changes (device token, catalog cursor, idempotent sale push) are a separate piece of work. Until they exist, a till built from this shell can hold sales locally and cannot deliver them.

## Layout

```text
src/EgyChina.Pos.App     Avalonia cashier and customer windows
src/EgyChina.Pos.Core    Money, catalog row, install paths, heartbeat
src/EgyChina.Pos.Data    SQLite migration, backup/restore
src/EgyChina.Pos.Sync    Outbox worker
src/EgyChina.Pos.Tools   restore / heartbeat-check CLI
scripts/                 publish, install, launch, update, rollback, restore
tests/EgyChina.Pos.Tests
docs/
```
