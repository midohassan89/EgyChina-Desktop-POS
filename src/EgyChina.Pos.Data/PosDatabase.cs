using System.Text.Json;
using EgyChina.Pos.Core;
using Microsoft.Data.Sqlite;

namespace EgyChina.Pos.Data;

/// <summary>
/// One cashier terminal database. Callers share a process lock; do not open the file elsewhere.
/// </summary>
public sealed class PosDatabase
{
    public const string DefaultStoreCode = "OBOUR";
    public const string LocalCashierId = "local-cashier";
    public const string LocalSupervisorId = "local-supervisor";
    public const string DemoRiceBarcode = "2000000000018";
    public const string DemoOilBarcode = "2000000000025";
    public const string DemoTeaBarcode = "2000000000032";
    public const int PinMaxFailures = 5;
    public static readonly TimeSpan PinLockoutDuration = TimeSpan.FromMinutes(5);

    private const string ProductSelect = """
        SELECT p.id, p.sku, p.barcode, p.name_ar, p.name_en, p.name_zh, pr.amount_minor,
               CASE WHEN f.product_id IS NULL THEN 0 ELSE 1 END AS is_favorite,
               p.image_url,
               p.category_id,
               c.name_ar AS category_name_ar
        FROM product p
        INNER JOIN product_price pr ON pr.product_id = p.id AND pr.currency = 'EGP'
        LEFT JOIN product_favorite f ON f.product_id = p.id
        LEFT JOIN category c ON c.id = p.category_id
        WHERE p.is_active = 1
        """;

    private readonly object _gate = new();
    private readonly string _path;

    public PosDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        _path = databasePath;
    }

    public string DatabasePath => _path;

    public void Initialize()
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var connection = Open();
            using var mode = connection.CreateCommand();
            mode.CommandText = "PRAGMA journal_mode=WAL;";
            var journal = (string)mode.ExecuteScalar()!;
            if (!string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"SQLite did not enable WAL (got '{journal}').");

            using var tx = connection.BeginTransaction();
            Exec(connection, """
                CREATE TABLE IF NOT EXISTS schema_migration (
                  version INTEGER PRIMARY KEY,
                  name TEXT NOT NULL,
                  applied_at TEXT NOT NULL
                );
                """);

            var version = ScalarLong(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_migration");
            if (version < 1)
            {
                ApplyMigrationScript(connection, "001_init.sql");
                MarkMigration(connection, 1, "init");
                version = 1;
            }

            if (version < 2)
            {
                ApplyMigrationScript(connection, "002_erp_product_link.sql");
                MarkMigration(connection, 2, "erp-product-link");
                version = 2;
            }

            if (version < 3)
            {
                ApplyMigrationScript(connection, "003_cashier_lockout.sql");
                MarkMigration(connection, 3, "cashier-lockout");
                version = 3;
            }

            if (version < 4)
            {
                ApplyMigrationScript(connection, "004_product_favorite.sql");
                MarkMigration(connection, 4, "product-favorite");
                version = 4;
            }

            if (version < 5)
            {
                ApplyMigrationScript(connection, "005_held_cart.sql");
                MarkMigration(connection, 5, "held-cart");
                version = 5;
            }

            if (version < 6)
            {
                ApplyMigrationScript(connection, "006_payment_wallet.sql");
                MarkMigration(connection, 6, "payment-wallet");
                version = 6;
            }

            if (version < 7)
            {
                ApplyMigrationScript(connection, "007_product_image.sql");
                MarkMigration(connection, 7, "product-image");
                version = 7;
            }

            if (version < 8)
            {
                ApplyMigrationScript(connection, "008_product_category.sql");
                MarkMigration(connection, 8, "product-category");
            }

            EnsureTerminal(connection);
            EnsureLocalCashier(connection);
            EnsureBootstrapCashiers(connection);
            SeedDemoCatalogIfNeeded(connection);
            tx.Commit();
        }
    }

    public IReadOnlyList<CatalogProduct> ListActiveProducts()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = ProductSelect + " ORDER BY p.name_ar";
            using var reader = command.ExecuteReader();
            var products = new List<CatalogProduct>();
            while (reader.Read())
                products.Add(ReadProduct(reader));
            return products;
        }
    }

    public CatalogProduct? FindByBarcode(string barcode)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = ProductSelect + " AND p.barcode = $barcode";
            command.Parameters.AddWithValue("$barcode", barcode.Trim());
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadProduct(reader) : null;
        }
    }

    /// <summary>
    /// Resolves a queue-ticket line against the local catalog (prisma id, numeric wcId, barcode, sku).
    /// </summary>
    public CatalogProduct? FindProductForQueueItem(string productId, string? barcode)
    {
        if (string.IsNullOrWhiteSpace(productId) && string.IsNullOrWhiteSpace(barcode))
            return null;

        lock (_gate)
        {
            using var connection = Open();
            var id = productId.Trim();
            if (id.Length > 0)
            {
                using (var byPrisma = connection.CreateCommand())
                {
                    byPrisma.CommandText = ProductSelect + " AND (p.prisma_id = $id OR p.id = $id)";
                    byPrisma.Parameters.AddWithValue("$id", id);
                    using var reader = byPrisma.ExecuteReader();
                    if (reader.Read())
                        return ReadProduct(reader);
                }

                if (long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var numeric)
                    && numeric > 0)
                {
                    using var byErp = connection.CreateCommand();
                    byErp.CommandText = ProductSelect + " AND p.erp_numeric_id = $erp";
                    byErp.Parameters.AddWithValue("$erp", numeric);
                    using var reader = byErp.ExecuteReader();
                    if (reader.Read())
                        return ReadProduct(reader);
                }
            }

            if (!string.IsNullOrWhiteSpace(barcode))
            {
                using var byBarcode = connection.CreateCommand();
                byBarcode.CommandText = ProductSelect + " AND p.barcode = $barcode";
                byBarcode.Parameters.AddWithValue("$barcode", barcode.Trim());
                using var reader = byBarcode.ExecuteReader();
                if (reader.Read())
                    return ReadProduct(reader);
            }

            return null;
        }
    }

    public int CountProducts()
    {
        lock (_gate)
        {
            using var connection = Open();
            return (int)ScalarLong(connection, "SELECT COUNT(*) FROM product");
        }
    }

    public string? GetSetting(string key)
    {
        lock (_gate)
        {
            using var connection = Open();
            return ReadSetting(connection, key);
        }
    }

    public void WriteSetting(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Setting key is required.", nameof(key));
        ArgumentNullException.ThrowIfNull(value);

        lock (_gate)
        {
            using var connection = Open();
            WriteSetting(connection, key, value);
        }
    }

    public int GetSchemaVersion()
    {
        lock (_gate)
        {
            using var connection = Open();
            return (int)ScalarLong(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_migration");
        }
    }

    /// <summary>
    /// Consistent online backup of the open database. Destination must not be the live path.
    /// Does not copy <c>device.cred</c>.
    /// </summary>
    public void BackupTo(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("Backup path is required.", nameof(destinationPath));

        var fullDest = Path.GetFullPath(destinationPath);
        var fullSource = Path.GetFullPath(_path);
        if (string.Equals(fullDest, fullSource, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backup destination cannot be the live database path.");

        var destDirectory = Path.GetDirectoryName(fullDest);
        if (!string.IsNullOrEmpty(destDirectory))
            Directory.CreateDirectory(destDirectory);

        if (File.Exists(fullDest))
            File.Delete(fullDest);

        lock (_gate)
        {
            using var source = Open();
            Exec(source, "PRAGMA wal_checkpoint(TRUNCATE);");

            // Pooling must stay off so prune can delete old snapshots without a locked handle.
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = fullDest,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ConnectionString);
            destination.Open();
            source.BackupDatabase(destination);
        }
    }

    public bool TryEnqueueOutbox(
        string aggregateType,
        string aggregateId,
        string eventType,
        string payloadJson,
        string idempotencyKey)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            var now = Now();
            command.CommandText = """
                INSERT OR IGNORE INTO outbox (
                  id, aggregate_type, aggregate_id, event_type, payload_json,
                  idempotency_key, created_at, attempt_count, next_attempt_at, status
                ) VALUES (
                  $id, $aggregate, $aggregateId, $event, $payload,
                  $key, $now, 0, $now, 'pending'
                );
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$aggregate", aggregateType);
            command.Parameters.AddWithValue("$aggregateId", aggregateId);
            command.Parameters.AddWithValue("$event", eventType);
            command.Parameters.AddWithValue("$payload", payloadJson);
            command.Parameters.AddWithValue("$key", idempotencyKey);
            command.Parameters.AddWithValue("$now", now);
            return command.ExecuteNonQuery() == 1;
        }
    }

    public int CountPendingOutbox()
    {
        lock (_gate)
        {
            using var connection = Open();
            return (int)ScalarLong(connection, "SELECT COUNT(*) FROM outbox WHERE status IN ('pending', 'inflight')");
        }
    }

    public int CountDeadOutbox()
    {
        lock (_gate)
        {
            using var connection = Open();
            return (int)ScalarLong(connection, "SELECT COUNT(*) FROM outbox WHERE status = 'dead'");
        }
    }

    public OutboxSnapshot ReadOutboxSnapshot()
    {
        lock (_gate)
        {
            using var connection = Open();
            var pending = (int)ScalarLong(connection, "SELECT COUNT(*) FROM outbox WHERE status = 'pending'");
            var inflight = (int)ScalarLong(connection, "SELECT COUNT(*) FROM outbox WHERE status = 'inflight'");
            var dead = (int)ScalarLong(connection, "SELECT COUNT(*) FROM outbox WHERE status = 'dead'");
            using var errorCmd = connection.CreateCommand();
            errorCmd.CommandText = """
                SELECT last_error FROM outbox
                WHERE last_error IS NOT NULL AND last_error <> ''
                ORDER BY CASE status WHEN 'inflight' THEN 0 WHEN 'pending' THEN 1 WHEN 'dead' THEN 2 ELSE 3 END,
                         created_at DESC
                LIMIT 1
                """;
            var lastError = errorCmd.ExecuteScalar() as string;
            return new OutboxSnapshot(pending, inflight, dead, lastError);
        }
    }

    /// <summary>
    /// After a crash mid-POST, rows left as <c>inflight</c> become <c>pending</c> again.
    /// Completed sales are never deleted.
    /// </summary>
    public int RecoverInflightOutbox()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox
                SET status = 'pending',
                    next_attempt_at = $now
                WHERE status = 'inflight'
                """;
            command.Parameters.AddWithValue("$now", Now());
            return command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<PendingOutboxRow> ListPendingSales(int limit)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            var now = Now();
            command.CommandText = """
                SELECT id, aggregate_id, event_type, payload_json, attempt_count
                FROM outbox
                WHERE status = 'pending'
                  AND event_type = 'SaleCompleted'
                  AND next_attempt_at <= $now
                ORDER BY created_at
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var rows = new List<PendingOutboxRow>();
            while (reader.Read())
            {
                rows.Add(new PendingOutboxRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4)));
            }

            return rows;
        }
    }

    public bool MarkOutboxInflight(string outboxId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox
                SET status = 'inflight'
                WHERE id = $id AND status = 'pending'
                """;
            command.Parameters.AddWithValue("$id", outboxId);
            return command.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>
    /// Transient failure: keep the sale, bump attempts, schedule backoff. Never deletes the sale.
    /// </summary>
    public void RecordOutboxFailure(string outboxId, string error)
    {
        lock (_gate)
        {
            using var connection = Open();
            int attempts;
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT attempt_count FROM outbox WHERE id = $id";
                read.Parameters.AddWithValue("$id", outboxId);
                attempts = Convert.ToInt32(read.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture);
            }

            var nextAttempts = attempts + 1;
            // First failure retries immediately (lost 2xx / blip). Later failures back off 15s…15m.
            var delaySeconds = nextAttempts <= 1
                ? 0
                : Math.Min(900, 15 * (1 << Math.Min(nextAttempts - 2, 6)));
            var nextAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds).ToString("O");

            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox
                SET status = 'pending',
                    attempt_count = $attempts,
                    last_error = $error,
                    next_attempt_at = $next
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", outboxId);
            command.Parameters.AddWithValue("$attempts", nextAttempts);
            command.Parameters.AddWithValue("$error", error);
            command.Parameters.AddWithValue("$next", nextAt);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Poison payload this build cannot send. Sale row stays in SQLite for reprint/audit.
    /// </summary>
    public void MarkOutboxDead(string outboxId, string error)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox
                SET status = 'dead',
                    attempt_count = attempt_count + 1,
                    last_error = $error,
                    next_attempt_at = $now
                WHERE id = $id AND status IN ('pending', 'inflight')
                """;
            command.Parameters.AddWithValue("$id", outboxId);
            command.Parameters.AddWithValue("$error", error);
            command.Parameters.AddWithValue("$now", Now());
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Test helper: force a pending row due immediately.</summary>
    public void ForceOutboxDueNow(string outboxId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox
                SET next_attempt_at = $now, status = 'pending'
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", outboxId);
            command.Parameters.AddWithValue("$now", Now());
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Acknowledges one sale only after the ERP returned a completed success response.
    /// Receipt totals are not rewritten.
    /// </summary>
    public void MarkSaleAccepted(string outboxId, string saleId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            using (var outbox = connection.CreateCommand())
            {
                outbox.CommandText = """
                    UPDATE outbox
                    SET status = 'done', last_error = NULL
                    WHERE id = $id AND status = 'inflight'
                    """;
                outbox.Parameters.AddWithValue("$id", outboxId);
                if (outbox.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Outbox row was not in flight.");
            }

            using (var sale = connection.CreateCommand())
            {
                sale.CommandText = """
                    UPDATE sale
                    SET sync_status = 'synced'
                    WHERE id = $id
                    """;
                sale.Parameters.AddWithValue("$id", saleId);
                sale.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public string? GetSaleCompletedAt(string saleId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT completed_at FROM sale WHERE id = $id";
            command.Parameters.AddWithValue("$id", saleId);
            return command.ExecuteScalar() as string;
        }
    }

    public string? GetLastCompletedSaleId()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id FROM sale
                WHERE status = 'completed'
                ORDER BY completed_at DESC, receipt_no DESC
                LIMIT 1
                """;
            return command.ExecuteScalar() as string;
        }
    }

    /// <summary>
    /// Builds a printable receipt from committed sale rows. Never mutates the sale.
    /// </summary>
    public ReceiptDocument? TryBuildReceiptDocument(string saleId, bool isReprint, bool kickDrawer = false)
    {
        if (string.IsNullOrWhiteSpace(saleId))
            return null;

        lock (_gate)
        {
            using var connection = Open();
            int receiptNo;
            string completedAtRaw;
            long totalMinor;
            string cashierId;
            using (var sale = connection.CreateCommand())
            {
                sale.CommandText = """
                    SELECT receipt_no, completed_at, total_minor, cashier_id
                    FROM sale
                    WHERE id = $id AND status = 'completed'
                    """;
                sale.Parameters.AddWithValue("$id", saleId);
                using var reader = sale.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
                    return null;
                receiptNo = reader.GetInt32(0);
                completedAtRaw = reader.GetString(1);
                totalMinor = reader.GetInt64(2);
                cashierId = reader.GetString(3);
            }

            if (!DateTimeOffset.TryParse(completedAtRaw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var completedAt))
                completedAt = DateTimeOffset.UtcNow;

            var cashierName = ReadCashier(connection, cashierId)?.DisplayName ?? cashierId;
            var lines = new List<ReceiptLine>();
            using (var lineCmd = connection.CreateCommand())
            {
                lineCmd.CommandText = """
                    SELECT name_ar, qty_milli, unit_price_minor, line_total_minor
                    FROM sale_line
                    WHERE sale_id = $sale
                    ORDER BY line_no
                    """;
                lineCmd.Parameters.AddWithValue("$sale", saleId);
                using var reader = lineCmd.ExecuteReader();
                while (reader.Read())
                {
                    lines.Add(new ReceiptLine(
                        reader.GetString(0),
                        null,
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3)));
                }
            }

            if (lines.Count == 0)
                return null;

            long tenderMinor = totalMinor;
            using (var pay = connection.CreateCommand())
            {
                pay.CommandText = """
                    SELECT amount_minor FROM payment
                    WHERE sale_id = $sale
                    ORDER BY created_at
                    LIMIT 1
                    """;
                pay.Parameters.AddWithValue("$sale", saleId);
                var value = pay.ExecuteScalar();
                if (value is not null and not DBNull)
                    tenderMinor = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            }

            return new ReceiptDocument(
                StoreNameAr: "ايجي شاينا ماركت",
                StoreNameZh: "埃及中国商场",
                ReceiptNo: receiptNo,
                CompletedAt: completedAt,
                CashierName: cashierName,
                Lines: lines,
                TotalMinor: totalMinor,
                TenderMinor: tenderMinor,
                ChangeMinor: tenderMinor - totalMinor,
                FooterAr: "سوق العبور",
                IsReprint: isReprint,
                KickDrawer: kickDrawer && !isReprint);
        }
    }

    public long? TryGetErpNumericId(string localProductId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT erp_numeric_id FROM product WHERE id = $id";
            command.Parameters.AddWithValue("$id", localProductId);
            var value = command.ExecuteScalar();
            if (value is null || value is DBNull)
                return null;
            try
            {
                var number = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
                return number > 0 ? number : null;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return null;
            }
        }
    }

    public void LinkErpNumericId(string localProductId, long erpNumericId, string? prismaId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE product
                SET erp_numeric_id = $erp, prisma_id = COALESCE($prisma, prisma_id)
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$erp", erpNumericId);
            command.Parameters.AddWithValue("$prisma", (object?)prismaId ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", localProductId);
            command.ExecuteNonQuery();
        }
    }

    public const string CatalogCursorStream = "catalog";

    /// <summary>
    /// Full catalog pull from <c>GET /api/products</c>: upsert names/barcodes/EGP prices, link
    /// <c>wcId</c>, deactivate removals, flip <c>catalog.source=erp</c>, and drop unused DEMO rows.
    /// Never updates <c>sale_line</c> (receipt snapshots stay frozen).
    /// </summary>
    public CatalogApplyResult ApplyCatalogFromErpJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string? serverTime = null;
        if (root.TryGetProperty("serverTime", out var serverNode)
            && serverNode.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(serverNode.GetString()))
        {
            serverTime = serverNode.GetString();
        }

        var upserted = 0;
        var deactivated = 0;
        var demoRemoved = 0;

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var now = Now();

            if (root.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
            {
                foreach (var product in products.EnumerateArray())
                {
                    if (!TryReadCatalogProduct(product, out var draft))
                        continue;
                    if (IsDemoSku(draft.Sku))
                        continue;

                    var existingId = FindCatalogMatchId(connection, draft);
                    if (existingId is null)
                    {
                        InsertCatalogProduct(connection, draft, now);
                        upserted++;
                    }
                    else
                    {
                        UpdateCatalogProduct(connection, existingId, draft, now);
                        upserted++;
                    }
                }
            }

            if (root.TryGetProperty("removedPrismaIds", out var removed) && removed.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in removed.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        continue;
                    var prismaId = item.GetString();
                    if (string.IsNullOrWhiteSpace(prismaId))
                        continue;
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        UPDATE product
                        SET is_active = 0, updated_at = $now
                        WHERE prisma_id = $id OR id = $id OR id LIKE $pkg
                        """;
                    command.Parameters.AddWithValue("$id", prismaId);
                    command.Parameters.AddWithValue("$pkg", prismaId + "-pkg-%");
                    command.Parameters.AddWithValue("$now", now);
                    deactivated += command.ExecuteNonQuery();
                }
            }

            WriteSetting(connection, "catalog.source", "erp");
            demoRemoved = PurgeDemoCatalog(connection, now);
            tx.Commit();
        }

        return new CatalogApplyResult(upserted, deactivated, demoRemoved, serverTime);
    }

    /// <summary>
    /// Applies a <c>GET /api/products</c> body. Links local rows by exact non-empty SKU to the
    /// numeric checkout id (<c>wcId</c>). Does not change shelf prices.
    /// <para>
    /// POS full-catalog shape (<c>expandPosCatalog</c>): <c>id</c> is already <c>wcId</c> (number),
    /// <c>prismaId</c> is the cuid. Admin paginated shape: <c>id</c> is cuid string and <c>wcId</c>
    /// is the number — prefer <c>wcId</c> when present.
    /// </para>
    /// Demo rows (<c>DEMO-*</c> / <c>demo-*</c>) are never linked by SKU guess.
    /// Prefer <see cref="ApplyCatalogFromErpJson"/> for live shelf sync (phase 5).
    /// </summary>
    public int LinkCatalogFromErpJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var linked = 0;
        if (root.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
        {
            foreach (var product in products.EnumerateArray())
            {
                if (!TryReadCatalogLink(product, out var sku, out var numericId, out var prismaId))
                    continue;

                lock (_gate)
                {
                    using var connection = Open();
                    using var command = connection.CreateCommand();
                    // Exact SKU only. Never touch product_price. Never link demo seed rows.
                    command.CommandText = """
                        UPDATE product
                        SET erp_numeric_id = $erp, prisma_id = COALESCE($prisma, prisma_id)
                        WHERE sku = $sku
                          AND id NOT LIKE 'demo-%'
                          AND sku NOT GLOB 'DEMO-*'
                          AND sku NOT GLOB 'demo-*'
                        """;
                    command.Parameters.AddWithValue("$erp", numericId);
                    command.Parameters.AddWithValue("$prisma", (object?)prismaId ?? DBNull.Value);
                    command.Parameters.AddWithValue("$sku", sku);
                    linked += command.ExecuteNonQuery();
                }
            }
        }

        if (root.TryGetProperty("removedPrismaIds", out var removed) && removed.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in removed.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;
                lock (_gate)
                {
                    using var connection = Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "UPDATE product SET is_active = 0 WHERE prisma_id = $id";
                    command.Parameters.AddWithValue("$id", item.GetString());
                    command.ExecuteNonQuery();
                }
            }
        }

        return linked;
    }

    /// <summary>
    /// Resolves checkout numeric id and prisma cuid from either POS CachedProduct or admin row JSON.
    /// </summary>
    public static bool TryReadCatalogLink(
        JsonElement product,
        out string sku,
        out long numericId,
        out string? prismaId)
    {
        sku = string.Empty;
        numericId = 0;
        prismaId = null;

        if (!product.TryGetProperty("sku", out var skuNode) || skuNode.ValueKind != JsonValueKind.String)
            return false;
        sku = (skuNode.GetString() ?? string.Empty).Trim();
        if (sku.Length == 0)
            return false;

        // Prefer explicit wcId (admin page shape). Else numeric id (POS CachedProduct = wcId).
        if (product.TryGetProperty("wcId", out var wcNode)
            && wcNode.ValueKind == JsonValueKind.Number
            && wcNode.TryGetInt64(out var wcId)
            && wcId > 0)
        {
            numericId = wcId;
        }
        else if (product.TryGetProperty("id", out var idNumber)
                 && idNumber.ValueKind == JsonValueKind.Number
                 && idNumber.TryGetInt64(out var asNumber)
                 && asNumber > 0)
        {
            numericId = asNumber;
        }
        else
        {
            return false;
        }

        if (product.TryGetProperty("prismaId", out var prismaNode)
            && prismaNode.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(prismaNode.GetString()))
        {
            prismaId = prismaNode.GetString();
        }
        else if (product.TryGetProperty("id", out var idString)
                 && idString.ValueKind == JsonValueKind.String
                 && !string.IsNullOrWhiteSpace(idString.GetString()))
        {
            // Admin shape: id is the Prisma cuid.
            prismaId = idString.GetString();
        }

        return true;
    }

    /// <summary>
    /// Reads a sellable POS/admin catalog row including shelf price and barcode.
    /// Packages may have an empty SKU; local id then uses <c>posKey</c>.
    /// </summary>
    public static bool TryReadCatalogProduct(JsonElement product, out CatalogProductDraft draft)
    {
        draft = default!;
        var isPackage = product.TryGetProperty("isPackage", out var pkgNode)
            && pkgNode.ValueKind == JsonValueKind.True;

        string? prismaId = null;
        if (product.TryGetProperty("prismaId", out var prismaNode)
            && prismaNode.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(prismaNode.GetString()))
        {
            prismaId = prismaNode.GetString()!.Trim();
        }
        else if (!isPackage
                 && product.TryGetProperty("id", out var idString)
                 && idString.ValueKind == JsonValueKind.String
                 && !string.IsNullOrWhiteSpace(idString.GetString()))
        {
            prismaId = idString.GetString()!.Trim();
        }

        string? posKey = null;
        if (product.TryGetProperty("posKey", out var posKeyNode)
            && posKeyNode.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(posKeyNode.GetString()))
        {
            posKey = posKeyNode.GetString()!.Trim();
        }

        long numericId = 0;
        if (product.TryGetProperty("wcId", out var wcNode)
            && wcNode.ValueKind == JsonValueKind.Number
            && wcNode.TryGetInt64(out var wcId)
            && wcId > 0)
        {
            numericId = wcId;
        }
        else if (product.TryGetProperty("id", out var idNumber)
                 && idNumber.ValueKind == JsonValueKind.Number
                 && idNumber.TryGetInt64(out var asNumber)
                 && asNumber > 0)
        {
            numericId = asNumber;
        }

        if (numericId <= 0)
            return false;

        var sku = string.Empty;
        if (product.TryGetProperty("sku", out var skuNode) && skuNode.ValueKind == JsonValueKind.String)
            sku = (skuNode.GetString() ?? string.Empty).Trim();

        var localId = isPackage
            ? (posKey ?? (prismaId is null ? null : prismaId + "-pkg"))
            : (prismaId ?? posKey);
        if (string.IsNullOrWhiteSpace(localId))
            return false;

        if (sku.Length == 0)
            sku = isPackage ? "PKG:" + localId : "ERP:" + numericId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (!product.TryGetProperty("name", out var nameNode)
            || nameNode.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameNode.GetString()))
        {
            return false;
        }

        var nameAr = nameNode.GetString()!.Trim();
        var nameEn = ReadOptionalString(product, "nameEn");
        var nameZh = ReadOptionalString(product, "nameZh");
        var unit = ReadOptionalString(product, "unit") ?? "قطعة";
        var barcode = ReadBarcode(product);
        if (!TryReadShelfPriceMinor(product, out var priceMinor))
            return false;

        draft = new CatalogProductDraft(
            localId,
            sku,
            barcode,
            nameAr,
            nameEn,
            nameZh,
            unit,
            numericId,
            isPackage ? prismaId : (prismaId ?? localId),
            priceMinor,
            isPackage,
            ReadImageUrl(product),
            ReadOptionalString(product, "categoryId"));
        return true;
    }

    private static string? ReadImageUrl(JsonElement product)
    {
        if (product.TryGetProperty("imageUrl", out var imageUrlNode)
            && imageUrlNode.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(imageUrlNode.GetString()))
        {
            return imageUrlNode.GetString()!.Trim();
        }

        if (product.TryGetProperty("images", out var images)
            && images.ValueKind == JsonValueKind.Array)
        {
            foreach (var image in images.EnumerateArray())
            {
                if (image.ValueKind != JsonValueKind.Object)
                    continue;
                if (image.TryGetProperty("src", out var src)
                    && src.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(src.GetString()))
                {
                    return src.GetString()!.Trim();
                }
            }
        }

        return null;
    }

    private static string? FindCatalogMatchId(SqliteConnection connection, CatalogProductDraft draft)
    {
        using (var byId = connection.CreateCommand())
        {
            byId.CommandText = "SELECT id FROM product WHERE id = $id LIMIT 1";
            byId.Parameters.AddWithValue("$id", draft.LocalId);
            if (byId.ExecuteScalar() is string idByKey)
                return idByKey;
        }

        if (!string.IsNullOrWhiteSpace(draft.PrismaId) && !draft.IsPackage)
        {
            using var byPrisma = connection.CreateCommand();
            byPrisma.CommandText = "SELECT id FROM product WHERE prisma_id = $prisma LIMIT 1";
            byPrisma.Parameters.AddWithValue("$prisma", draft.PrismaId);
            if (byPrisma.ExecuteScalar() is string idByPrisma)
                return idByPrisma;
        }

        if (!IsDemoSku(draft.Sku) && !draft.Sku.StartsWith("PKG:", StringComparison.Ordinal) && !draft.Sku.StartsWith("ERP:", StringComparison.Ordinal))
        {
            using var bySku = connection.CreateCommand();
            bySku.CommandText = """
                SELECT id FROM product
                WHERE sku = $sku
                  AND id NOT LIKE 'demo-%'
                  AND sku NOT GLOB 'DEMO-*'
                  AND sku NOT GLOB 'demo-*'
                LIMIT 1
                """;
            bySku.Parameters.AddWithValue("$sku", draft.Sku);
            if (bySku.ExecuteScalar() is string idBySku)
                return idBySku;
        }

        if (!string.IsNullOrWhiteSpace(draft.Barcode))
        {
            using var byBarcode = connection.CreateCommand();
            byBarcode.CommandText = """
                SELECT id FROM product
                WHERE barcode = $barcode
                  AND id NOT LIKE 'demo-%'
                LIMIT 1
                """;
            byBarcode.Parameters.AddWithValue("$barcode", draft.Barcode);
            if (byBarcode.ExecuteScalar() is string idByBarcode)
                return idByBarcode;
        }

        return null;
    }

    private static void InsertCatalogProduct(SqliteConnection connection, CatalogProductDraft draft, string now)
    {
        using (var product = connection.CreateCommand())
        {
            product.CommandText = """
                INSERT INTO product (
                  id, sku, barcode, name_ar, name_en, name_zh, unit_ar,
                  tax_rate_bps, is_active, updated_at, erp_numeric_id, prisma_id, image_url, category_id
                ) VALUES (
                  $id, $sku, $barcode, $nameAr, $nameEn, $nameZh, $unit,
                  0, 1, $now, $erp, $prisma, $image, $category
                );
                """;
            BindCatalogProduct(product, draft.LocalId, draft, now);
            product.ExecuteNonQuery();
        }

        using var price = connection.CreateCommand();
        price.CommandText = """
            INSERT INTO product_price (product_id, currency, amount_minor)
            VALUES ($id, 'EGP', $amount);
            """;
        price.Parameters.AddWithValue("$id", draft.LocalId);
        price.Parameters.AddWithValue("$amount", draft.PriceEgpMinor);
        price.ExecuteNonQuery();
    }

    private static void UpdateCatalogProduct(
        SqliteConnection connection,
        string existingId,
        CatalogProductDraft draft,
        string now)
    {
        using (var product = connection.CreateCommand())
        {
            product.CommandText = """
                UPDATE product
                SET sku = $sku,
                    barcode = $barcode,
                    name_ar = $nameAr,
                    name_en = $nameEn,
                    name_zh = $nameZh,
                    unit_ar = $unit,
                    is_active = 1,
                    updated_at = $now,
                    erp_numeric_id = $erp,
                    prisma_id = COALESCE($prisma, prisma_id),
                    image_url = COALESCE($image, image_url),
                    category_id = COALESCE($category, category_id)
                WHERE id = $id
                """;
            BindCatalogProduct(product, existingId, draft, now);
            product.ExecuteNonQuery();
        }

        using var price = connection.CreateCommand();
        price.CommandText = """
            INSERT INTO product_price (product_id, currency, amount_minor)
            VALUES ($id, 'EGP', $amount)
            ON CONFLICT (product_id, currency) DO UPDATE SET amount_minor = excluded.amount_minor;
            """;
        price.Parameters.AddWithValue("$id", existingId);
        price.Parameters.AddWithValue("$amount", draft.PriceEgpMinor);
        price.ExecuteNonQuery();
    }

    private static void BindCatalogProduct(
        SqliteCommand command,
        string id,
        CatalogProductDraft draft,
        string now)
    {
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$sku", draft.Sku);
        command.Parameters.AddWithValue("$barcode", (object?)draft.Barcode ?? DBNull.Value);
        command.Parameters.AddWithValue("$nameAr", draft.NameAr);
        command.Parameters.AddWithValue("$nameEn", (object?)draft.NameEn ?? DBNull.Value);
        command.Parameters.AddWithValue("$nameZh", (object?)draft.NameZh ?? DBNull.Value);
        command.Parameters.AddWithValue("$unit", draft.UnitAr);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$erp", draft.ErpNumericId);
        command.Parameters.AddWithValue("$prisma", (object?)draft.PrismaId ?? DBNull.Value);
        command.Parameters.AddWithValue("$image", (object?)draft.ImageUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)draft.CategoryId ?? DBNull.Value);
    }

    /// <summary>Active products that still need a remote image download.</summary>
    public IReadOnlyList<(string ProductId, string? ImageUrl)> ListProductsWithImageUrls(int limit = 5000)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, image_url
                FROM product
                WHERE is_active = 1
                  AND image_url IS NOT NULL
                  AND TRIM(image_url) <> ''
                ORDER BY name_ar
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
            using var reader = command.ExecuteReader();
            var rows = new List<(string, string?)>();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            return rows;
        }
    }

    /// <summary>
    /// Soft-deactivates active products whose id/prisma_id are not in the keep set
    /// (used after a live catalog replace so stale local rows disappear from the shelf).
    /// </summary>
    public int DeactivateProductsNotIn(IReadOnlyCollection<string> keepIds)
    {
        if (keepIds.Count == 0)
            return 0;

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            using (var clear = connection.CreateCommand())
            {
                clear.CommandText = "DELETE FROM temp.keep_ids";
                try { clear.ExecuteNonQuery(); } catch { /* temp table may not exist yet */ }
            }

            using (var create = connection.CreateCommand())
            {
                create.CommandText = "CREATE TEMP TABLE IF NOT EXISTS keep_ids (id TEXT PRIMARY KEY)";
                create.ExecuteNonQuery();
            }

            using (var wipe = connection.CreateCommand())
            {
                wipe.CommandText = "DELETE FROM keep_ids";
                wipe.ExecuteNonQuery();
            }

            foreach (var id in keepIds)
            {
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT OR IGNORE INTO keep_ids (id) VALUES ($id)";
                insert.Parameters.AddWithValue("$id", id.Trim());
                insert.ExecuteNonQuery();
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE product
                SET is_active = 0, updated_at = $now
                WHERE is_active = 1
                  AND id NOT IN (SELECT id FROM keep_ids)
                  AND IFNULL(prisma_id, '') NOT IN (SELECT id FROM keep_ids)
                """;
            command.Parameters.AddWithValue("$now", Now());
            var deactivated = command.ExecuteNonQuery();
            tx.Commit();
            return deactivated;
        }
    }

    /// <summary>
    /// Updates <c>image_url</c> for local products matched by id or prisma_id
    /// (storefront public catalog uses the Prisma cuid).
    /// </summary>
    public int UpdateProductImageUrls(IReadOnlyList<(string ProductId, string ImageUrl)> rows)
    {
        if (rows.Count == 0)
            return 0;

        var updated = 0;
        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var now = Now();
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.ProductId) || string.IsNullOrWhiteSpace(row.ImageUrl))
                    continue;

                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE product
                    SET image_url = $image,
                        updated_at = $now
                    WHERE id = $id OR prisma_id = $id
                    """;
                command.Parameters.AddWithValue("$image", row.ImageUrl.Trim());
                command.Parameters.AddWithValue("$now", now);
                command.Parameters.AddWithValue("$id", row.ProductId.Trim());
                updated += command.ExecuteNonQuery();
            }

            tx.Commit();
        }

        return updated;
    }

    /// <summary>
    /// Removes unused DEMO seed rows after an ERP catalog apply. Rows referenced by
    /// <c>sale_line</c> are only deactivated so receipt history stays intact.
    /// </summary>
    private static int PurgeDemoCatalog(SqliteConnection connection, string now)
    {
        using (var deactivate = connection.CreateCommand())
        {
            deactivate.CommandText = """
                UPDATE product
                SET is_active = 0, updated_at = $now
                WHERE id LIKE 'demo-%'
                   OR sku GLOB 'DEMO-*'
                   OR sku GLOB 'demo-*'
                """;
            deactivate.Parameters.AddWithValue("$now", now);
            deactivate.ExecuteNonQuery();
        }

        using (var prices = connection.CreateCommand())
        {
            prices.CommandText = """
                DELETE FROM product_price
                WHERE product_id IN (
                  SELECT p.id FROM product p
                  WHERE (p.id LIKE 'demo-%' OR p.sku GLOB 'DEMO-*' OR p.sku GLOB 'demo-*')
                    AND NOT EXISTS (SELECT 1 FROM sale_line sl WHERE sl.product_id = p.id)
                )
                """;
            prices.ExecuteNonQuery();
        }

        using var products = connection.CreateCommand();
        products.CommandText = """
            DELETE FROM product
            WHERE (id LIKE 'demo-%' OR sku GLOB 'DEMO-*' OR sku GLOB 'demo-*')
              AND NOT EXISTS (SELECT 1 FROM sale_line sl WHERE sl.product_id = product.id)
            """;
        return products.ExecuteNonQuery();
    }

    private static bool IsDemoSku(string sku) =>
        sku.StartsWith("DEMO-", StringComparison.OrdinalIgnoreCase)
        || sku.StartsWith("demo-", StringComparison.Ordinal);

    private static string? ReadOptionalString(JsonElement product, string name)
    {
        if (!product.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
            return null;
        var value = node.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? ReadBarcode(JsonElement product)
    {
        var top = ReadOptionalString(product, "barcode");
        if (!string.IsNullOrWhiteSpace(top))
            return top;

        if (!product.TryGetProperty("meta_data", out var meta) || meta.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var row in meta.EnumerateArray())
        {
            if (!row.TryGetProperty("key", out var keyNode) || keyNode.ValueKind != JsonValueKind.String)
                continue;
            var key = keyNode.GetString();
            if (!string.Equals(key, "_op_barcode", StringComparison.Ordinal)
                && !string.Equals(key, "op_barcode", StringComparison.Ordinal))
            {
                continue;
            }

            if (!row.TryGetProperty("value", out var valueNode))
                continue;
            if (valueNode.ValueKind == JsonValueKind.String)
            {
                var text = valueNode.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            else if (valueNode.ValueKind == JsonValueKind.Number)
            {
                return valueNode.ToString();
            }
        }

        return null;
    }

    private static bool TryReadShelfPriceMinor(JsonElement product, out long minor)
    {
        minor = 0;
        // Prefer active sale price when the POS card is on sale.
        if (TryReadMoneyProperty(product, "sale_price", out minor) && minor > 0)
            return true;
        if (TryReadMoneyProperty(product, "price", out minor))
            return true;
        return TryReadMoneyProperty(product, "regular_price", out minor);
    }

    private static bool TryReadMoneyProperty(JsonElement product, string name, out long minor)
    {
        minor = 0;
        if (!product.TryGetProperty(name, out var node))
            return false;

        if (node.ValueKind == JsonValueKind.String)
            return Money.TryParseEgp(node.GetString(), out minor);

        if (node.ValueKind == JsonValueKind.Number)
        {
            if (node.TryGetInt64(out var whole) && node.GetRawText().IndexOf('.') < 0)
            {
                // Integer pounds from admin JSON (e.g. 85 → 8500 piasters).
                try
                {
                    minor = checked(whole * 100);
                    return minor >= 0;
                }
                catch (OverflowException)
                {
                    return false;
                }
            }

            return Money.TryParseEgp(node.GetRawText(), out minor);
        }

        return false;
    }

    public void WriteCursor(string stream, string? cursor, string? error)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sync_cursor (stream, cursor, last_success_at, last_error)
                VALUES ($stream, $cursor, $at, $error)
                ON CONFLICT (stream) DO UPDATE SET
                  cursor = excluded.cursor,
                  last_success_at = excluded.last_success_at,
                  last_error = excluded.last_error;
                """;
            command.Parameters.AddWithValue("$stream", stream);
            command.Parameters.AddWithValue("$cursor", (object?)cursor ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", Now());
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public SyncCursorRow? ReadCursor(string stream)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT stream, cursor, last_success_at, last_error
                FROM sync_cursor
                WHERE stream = $stream
                """;
            command.Parameters.AddWithValue("$stream", stream);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            return new SyncCursorRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3));
        }
    }

    public string JournalMode()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            return (string)command.ExecuteScalar()!;
        }
    }

    public PinAuthResult TryAuthenticatePin(string pin)
    {
        if (!PinHasher.IsValidFormat(pin))
            return new PinAuthResult(PinAuthStatus.InvalidFormat, null, "الرقم السري يجب أن يكون ٦ أرقام على الأقل.");

        lock (_gate)
        {
            using var connection = Open();
            EnsureBootstrapCashiers(connection);
            var now = DateTimeOffset.UtcNow;
            if (ReadGlobalLockout(connection, now) is { } lockedUntil)
            {
                return new PinAuthResult(
                    PinAuthStatus.Locked,
                    null,
                    "الجهاز مقفل مؤقتاً بعد محاولات خاطئة.",
                    lockedUntil);
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, display_name, role, pin_hash, pin_salt, is_active
                FROM cashier
                ORDER BY CASE role WHEN 'manager' THEN 0 WHEN 'supervisor' THEN 1 ELSE 2 END, id
                """;
            using var reader = command.ExecuteReader();
            CashierRecord? matchedInactive = null;
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                var role = ParseRole(reader.GetString(2));
                var hash = reader.GetString(3);
                var salt = reader.GetString(4);
                var active = reader.GetInt32(5) == 1;
                if (!PinHasher.Verify(pin, hash, salt))
                    continue;

                if (!active)
                {
                    matchedInactive = new CashierRecord(id, name, role, false);
                    continue;
                }

                reader.Close();
                ClearGlobalLockout(connection);
                return new PinAuthResult(
                    PinAuthStatus.Ok,
                    new CashierRecord(id, name, role, true),
                    null);
            }

            reader.Close();
            if (matchedInactive is not null)
                return new PinAuthResult(PinAuthStatus.Inactive, matchedInactive, "هذا الحساب غير مفعّل.");

            var failures = BumpGlobalPinFailure(connection, now);
            if (failures >= PinMaxFailures)
            {
                var until = now.Add(PinLockoutDuration);
                return new PinAuthResult(
                    PinAuthStatus.Locked,
                    null,
                    "الجهاز مقفل مؤقتاً بعد محاولات خاطئة.",
                    until);
            }

            return new PinAuthResult(
                PinAuthStatus.WrongPin,
                null,
                $"الرقم السري غير صحيح. متبقّي {(PinMaxFailures - failures).ToString(System.Globalization.CultureInfo.InvariantCulture)} محاولات.");
        }
    }

    public void ClearPinLockout()
    {
        lock (_gate)
        {
            using var connection = Open();
            ClearGlobalLockout(connection);
        }
    }

    public ShiftRecord? GetOpenShift()
    {
        lock (_gate)
        {
            using var connection = Open();
            return ReadOpenShift(connection);
        }
    }

    /// <summary>
    /// Builds a local Z-report for an open (or just-closed) shift from sale + payment rows.
    /// Returns are detected via <c>sale.note = 'return'</c>; amounts stay non-negative in SQLite.
    /// </summary>
    public ShiftZReport? BuildShiftZReport(string shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return null;

        lock (_gate)
        {
            using var connection = Open();
            string cashierId;
            string openedAt;
            long openingFloat;
            using (var shift = connection.CreateCommand())
            {
                shift.CommandText = """
                    SELECT cashier_id, opened_at, opening_float_minor
                    FROM shift WHERE id = $id
                    """;
                shift.Parameters.AddWithValue("$id", shiftId);
                using var reader = shift.ExecuteReader();
                if (!reader.Read())
                    return null;
                cashierId = reader.GetString(0);
                openedAt = reader.GetString(1);
                openingFloat = reader.GetInt64(2);
            }

            var cashierName = ReadCashier(connection, cashierId)?.DisplayName ?? cashierId;
            long cash = 0, visa = 0, wallet = 0, instapay = 0, wechat = 0, cashRefunds = 0;
            var tickets = 0;
            var returns = 0;
            var cashTickets = 0;
            var cardTickets = 0;

            using (var sales = connection.CreateCommand())
            {
                sales.CommandText = """
                    SELECT s.total_minor, IFNULL(s.note, ''), IFNULL(p.method, 'cash_egp')
                    FROM sale s
                    LEFT JOIN payment p ON p.sale_id = s.id
                    WHERE s.shift_id = $shift AND s.status = 'completed'
                    ORDER BY s.completed_at
                    """;
                sales.Parameters.AddWithValue("$shift", shiftId);
                using var reader = sales.ExecuteReader();
                while (reader.Read())
                {
                    tickets++;
                    var total = reader.GetInt64(0);
                    var isReturn = string.Equals(reader.GetString(1), "return", StringComparison.OrdinalIgnoreCase);
                    var method = reader.GetString(2);
                    var signed = isReturn ? -total : total;
                    if (isReturn)
                        returns++;

                    var isCash = string.Equals(method, "cash_egp", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(method, "cash", StringComparison.OrdinalIgnoreCase);
                    if (isCash)
                        cashTickets++;
                    else
                        cardTickets++;

                    if (isReturn && isCash)
                        cashRefunds = checked(cashRefunds + total);

                    switch (method)
                    {
                        case "cash_egp":
                        case "cash":
                        case "cash_cny":
                            cash = checked(cash + signed);
                            break;
                        case "card":
                        case "visa":
                            visa = checked(visa + signed);
                            break;
                        case "wallet":
                            wallet = checked(wallet + signed);
                            break;
                        case "instapay":
                            instapay = checked(instapay + signed);
                            break;
                        case "wechat":
                            wechat = checked(wechat + signed);
                            break;
                        default:
                            cash = checked(cash + signed);
                            break;
                    }
                }
            }

            return new ShiftZReport(
                shiftId,
                cashierName,
                openedAt,
                openingFloat,
                cash,
                visa,
                wallet,
                instapay,
                wechat,
                cashRefunds,
                tickets,
                returns,
                cashTickets,
                cardTickets);
        }
    }

    public ShiftRecord OpenShift(string cashierId, long openingFloatMinor)
    {
        if (string.IsNullOrWhiteSpace(cashierId))
            throw new ArgumentException("Cashier id is required.", nameof(cashierId));
        if (openingFloatMinor < 0)
            throw new ArgumentOutOfRangeException(nameof(openingFloatMinor));

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            EnsureBootstrapCashiers(connection);

            if (ReadOpenShift(connection) is not null)
                throw new InvalidOperationException("هناك وردية مفتوحة بالفعل على هذا الجهاز.");

            if (!CashierExistsActive(connection, cashierId))
                throw new InvalidOperationException("الكاشير غير موجود أو غير مفعّل.");

            var shiftId = Guid.NewGuid().ToString("D");
            var now = Now();
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO shift (
                      id, server_id, cashier_id, opened_at, closed_at,
                      opening_float_minor, closing_cash_minor, status, sync_status
                    ) VALUES (
                      $id, NULL, $cashier, $now, NULL,
                      $float, NULL, 'open', 'pending'
                    );
                    """;
                insert.Parameters.AddWithValue("$id", shiftId);
                insert.Parameters.AddWithValue("$cashier", cashierId);
                insert.Parameters.AddWithValue("$now", now);
                insert.Parameters.AddWithValue("$float", openingFloatMinor);
                insert.ExecuteNonQuery();
            }

            // Shift push to ERP is not wired yet; keep sync_status=pending for a later phase.
            tx.Commit();
            return new ShiftRecord(shiftId, cashierId, now, null, openingFloatMinor, null, "open", "pending");
        }
    }

    public ShiftRecord CloseShift(string shiftId, string actorCashierId, long closingCashMinor)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            throw new ArgumentException("Shift id is required.", nameof(shiftId));
        if (closingCashMinor < 0)
            throw new ArgumentOutOfRangeException(nameof(closingCashMinor));

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var open = ReadOpenShift(connection);
            if (open is null || !string.Equals(open.Id, shiftId, StringComparison.Ordinal))
                throw new InvalidOperationException("لا توجد وردية مفتوحة مطابقة.");

            var actor = ReadCashier(connection, actorCashierId);
            if (actor is null || !actor.IsActive)
                throw new InvalidOperationException("الكاشير غير موجود أو غير مفعّل.");

            var canClose = string.Equals(actor.Id, open.CashierId, StringComparison.Ordinal)
                || actor.Role is CashierRole.Supervisor or CashierRole.Manager;
            if (!canClose)
                throw new InvalidOperationException("إغلاق الوردية متاح لصاحبها أو للمشرف.");

            var now = Now();
            using (var update = connection.CreateCommand())
            {
                update.CommandText = """
                    UPDATE shift
                    SET closed_at = $now,
                        closing_cash_minor = $cash,
                        status = 'closed',
                        sync_status = 'pending'
                    WHERE id = $id AND status = 'open'
                    """;
                update.Parameters.AddWithValue("$now", now);
                update.Parameters.AddWithValue("$cash", closingCashMinor);
                update.Parameters.AddWithValue("$id", shiftId);
                if (update.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("تعذر إغلاق الوردية.");
            }

            tx.Commit();
            return open with
            {
                ClosedAt = now,
                ClosingCashMinor = closingCashMinor,
                Status = "closed",
                SyncStatus = "pending",
            };
        }
    }

    /// <summary>
    /// Commits one cash EGP sale under an open local shift. Does not talk to the network.
    /// </summary>
    public CompletedCashSale CompleteCashSale(
        IReadOnlyList<CashSaleLine> lines,
        long tenderMinor,
        string cashierId,
        string shiftId) =>
        CompleteSale(lines, tenderMinor, cashierId, shiftId, PosPaymentMethod.Cash);

    /// <summary>
    /// Commits a sale under an open local shift. ERP still receives <c>shiftId: null</c> until shift push exists.
    /// Sync continues to require env gates; the outbox stores the real method for later mapping.
    /// </summary>
    public CompletedCashSale CompleteSale(
        IReadOnlyList<CashSaleLine> lines,
        long tenderMinor,
        string cashierId,
        string shiftId,
        PosPaymentMethod paymentMethod,
        decimal? wechatEgpPerCny = null,
        bool isReturn = false,
        long discountMinor = 0,
        string? loyaltyCustomerId = null,
        int pointsEarned = 0,
        int pointsRedeemed = 0)
    {
        if (lines is null || lines.Count == 0)
            throw new ArgumentException("A sale needs at least one line.", nameof(lines));
        if (tenderMinor < 0)
            throw new ArgumentOutOfRangeException(nameof(tenderMinor));
        if (discountMinor < 0)
            throw new ArgumentOutOfRangeException(nameof(discountMinor));
        if (string.IsNullOrWhiteSpace(cashierId))
            throw new ArgumentException("Cashier id is required.", nameof(cashierId));
        if (string.IsNullOrWhiteSpace(shiftId))
            throw new ArgumentException("Shift id is required.", nameof(shiftId));
        if (isReturn && discountMinor > 0)
            throw new InvalidOperationException("لا يُطبَّق خصم الولاء على الاسترجاع.");

        long subtotal = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.ProductId))
                throw new ArgumentException("Each line needs a product id.", nameof(lines));
            if (string.IsNullOrWhiteSpace(line.NameAr))
                throw new ArgumentException("Each line needs an Arabic name.", nameof(lines));
            if (line.Qty < 1)
                throw new ArgumentOutOfRangeException(nameof(lines), "Quantity must be at least 1.");
            if (line.UnitPriceMinor < 0)
                throw new ArgumentOutOfRangeException(nameof(lines), "Unit price cannot be negative.");
            subtotal = checked(subtotal + line.LineTotalMinor);
        }

        if (discountMinor > subtotal)
            throw new InvalidOperationException("خصم الولاء أكبر من الإجمالي.");

        var total = checked(subtotal - discountMinor);

        if (isReturn)
        {
            if (tenderMinor != total)
                throw new InvalidOperationException("الاسترجاع يرد المستحق بالكامل بدون باقي.");
        }
        else if (PosPaymentMethods.IsCash(paymentMethod))
        {
            if (tenderMinor < total)
                throw new InvalidOperationException("Tender is below the sale total.");
        }
        else if (tenderMinor != total)
        {
            throw new InvalidOperationException("هذه الطريقة تتطلب دفع المستحق بالكامل بدون باقي.");
        }

        var sign = isReturn ? -1 : 1;
        var signedTotal = checked(total * sign);

        var localMethod = PosPaymentMethods.ToLocalDbMethod(paymentMethod);
        var erpMethod = PosPaymentMethods.ToErpId(paymentMethod);

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            EnsureBootstrapCashiers(connection);

            var open = ReadOpenShift(connection);
            if (open is null || !string.Equals(open.Id, shiftId, StringComparison.Ordinal))
                throw new InvalidOperationException("يجب فتح وردية قبل إتمام البيع.");
            if (!CashierExistsActive(connection, cashierId))
                throw new InvalidOperationException("الكاشير غير موجود أو غير مفعّل.");

            var terminalId = TerminalId(connection);
            if (string.IsNullOrEmpty(terminalId))
                throw new InvalidOperationException("Terminal row is missing.");

            var receiptNo = checked((int)ScalarLong(connection, "SELECT next_no FROM receipt_counter WHERE id = 1"));
            using (var bump = connection.CreateCommand())
            {
                bump.CommandText = "UPDATE receipt_counter SET next_no = next_no + 1 WHERE id = 1";
                bump.ExecuteNonQuery();
            }

            var saleId = Guid.NewGuid().ToString("D");
            var now = Now();
            // Local sale/payment rows stay non-negative (SQLite CHECKs). Signed money lives in outbox JSON.
            using (var sale = connection.CreateCommand())
            {
                sale.CommandText = """
                    INSERT INTO sale (
                      id, server_id, shift_id, cashier_id, receipt_no, status, currency,
                      subtotal_minor, discount_minor, tax_minor, total_minor,
                      note, created_at, completed_at, sync_status
                    ) VALUES (
                      $id, NULL, $shift, $cashier, $receipt, 'completed', 'EGP',
                      $subtotal, $discount, 0, $total,
                      $note, $now, $now, 'pending'
                    );
                    """;
                sale.Parameters.AddWithValue("$id", saleId);
                sale.Parameters.AddWithValue("$shift", shiftId);
                sale.Parameters.AddWithValue("$cashier", cashierId);
                sale.Parameters.AddWithValue("$receipt", receiptNo);
                sale.Parameters.AddWithValue("$subtotal", subtotal);
                sale.Parameters.AddWithValue("$discount", discountMinor);
                sale.Parameters.AddWithValue("$total", total);
                sale.Parameters.AddWithValue("$note", isReturn ? (object)"return" : DBNull.Value);
                sale.Parameters.AddWithValue("$now", now);
                sale.ExecuteNonQuery();
            }

            var lineNo = 1;
            foreach (var line in lines)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO sale_line (
                      id, sale_id, line_no, product_id, barcode, name_ar,
                      qty_milli, unit_price_minor, discount_minor, line_total_minor
                    ) VALUES (
                      $id, $sale, $lineNo, $product, $barcode, $name,
                      $qty, $price, 0, $lineTotal
                    );
                    """;
                insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                insert.Parameters.AddWithValue("$sale", saleId);
                insert.Parameters.AddWithValue("$lineNo", lineNo);
                insert.Parameters.AddWithValue("$product", line.ProductId);
                insert.Parameters.AddWithValue("$barcode", (object?)line.Barcode ?? DBNull.Value);
                insert.Parameters.AddWithValue("$name", line.NameAr);
                insert.Parameters.AddWithValue("$qty", line.QtyMilli);
                insert.Parameters.AddWithValue("$price", line.UnitPriceMinor);
                insert.Parameters.AddWithValue("$lineTotal", line.LineTotalMinor);
                insert.ExecuteNonQuery();
                lineNo++;
            }

            using (var payment = connection.CreateCommand())
            {
                payment.CommandText = """
                    INSERT INTO payment (
                      id, sale_id, method, currency, amount_minor, amount_egp_minor, reference, created_at
                    ) VALUES (
                      $id, $sale, $method, 'EGP', $tender, $tender, NULL, $now
                    );
                    """;
                payment.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                payment.Parameters.AddWithValue("$sale", saleId);
                payment.Parameters.AddWithValue("$method", localMethod);
                payment.Parameters.AddWithValue("$tender", tenderMinor);
                payment.Parameters.AddWithValue("$now", now);
                payment.ExecuteNonQuery();
            }

            var idempotencyKey = $"{terminalId}:sale:{saleId}";
            var payload = BuildSalePayload(
                saleId,
                shiftId,
                cashierId,
                receiptNo,
                checked(subtotal * sign),
                discountMinor,
                signedTotal,
                checked(tenderMinor * sign),
                now,
                lines,
                localMethod,
                erpMethod,
                wechatEgpPerCny,
                isReturn,
                loyaltyCustomerId,
                pointsEarned,
                pointsRedeemed);
            using (var outbox = connection.CreateCommand())
            {
                outbox.CommandText = """
                    INSERT INTO outbox (
                      id, aggregate_type, aggregate_id, event_type, payload_json,
                      idempotency_key, created_at, attempt_count, next_attempt_at, status
                    ) VALUES (
                      $id, 'sale', $sale, 'SaleCompleted', $payload,
                      $key, $now, 0, $now, 'pending'
                    );
                    """;
                outbox.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                outbox.Parameters.AddWithValue("$sale", saleId);
                outbox.Parameters.AddWithValue("$payload", payload);
                outbox.Parameters.AddWithValue("$key", idempotencyKey);
                outbox.Parameters.AddWithValue("$now", now);
                outbox.ExecuteNonQuery();
            }

            tx.Commit();
            var completed = new CompletedCashSale(
                saleId,
                terminalId,
                cashierId,
                shiftId,
                receiptNo,
                signedTotal,
                checked(tenderMinor * sign),
                isReturn ? 0 : tenderMinor - total,
                idempotencyKey);
            PosLog.Info(
                $"{(isReturn ? "return" : "sale")} committed receipt={receiptNo} method={erpMethod} total={signedTotal} tender={tenderMinor * sign} cashier={cashierId}");
            return completed;
        }
    }

    /// <summary>
    /// Loads lines from a completed local sale by <c>POS-n</c> or sale GUID.
    /// Only original (non-return) sales are eligible so a return cannot be returned again.
    /// </summary>
    public IReadOnlyList<LocalReceiptLine>? FindSaleLinesByCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var code = raw.Trim();
        string? saleId = null;
        int? receiptNo = null;
        if (ReceiptCodes.TryParseReceiptNumber(code, out var parsedReceipt))
            receiptNo = parsedReceipt;
        else if (Guid.TryParse(code, out _))
            saleId = code;
        else
            return null;

        lock (_gate)
        {
            using var connection = Open();
            using (var sale = connection.CreateCommand())
            {
                if (saleId is not null)
                {
                    sale.CommandText = """
                        SELECT id, IFNULL(note, '') FROM sale
                        WHERE id = $id AND status = 'completed'
                        """;
                    sale.Parameters.AddWithValue("$id", saleId);
                }
                else
                {
                    sale.CommandText = """
                        SELECT id, IFNULL(note, '') FROM sale
                        WHERE receipt_no = $receipt AND status = 'completed'
                        ORDER BY completed_at DESC
                        LIMIT 1
                        """;
                    sale.Parameters.AddWithValue("$receipt", receiptNo!.Value);
                }

                using var reader = sale.ExecuteReader();
                if (!reader.Read())
                    return null;
                saleId = reader.GetString(0);
                if (string.Equals(reader.GetString(1), "return", StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            var lines = new List<LocalReceiptLine>();
            using (var lineCmd = connection.CreateCommand())
            {
                lineCmd.CommandText = """
                    SELECT sl.product_id, sl.barcode, sl.name_ar, p.name_zh,
                           sl.qty_milli, sl.unit_price_minor
                    FROM sale_line sl
                    LEFT JOIN product p ON p.id = sl.product_id
                    WHERE sl.sale_id = $sale
                    ORDER BY sl.line_no
                    """;
                lineCmd.Parameters.AddWithValue("$sale", saleId);
                using var reader = lineCmd.ExecuteReader();
                while (reader.Read())
                {
                    var qtyMilli = reader.GetInt64(4);
                    var qty = (int)Math.Max(1, qtyMilli / 1000);
                    lines.Add(new LocalReceiptLine(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        qty,
                        reader.GetInt64(5)));
                }
            }

            return lines.Count == 0 ? null : lines;
        }
    }

    public long ForeignKeysEnabled()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys;";
            return (long)command.ExecuteScalar()!;
        }
    }

    private SqliteConnection Open()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        Exec(connection, "PRAGMA foreign_keys=ON;");
        Exec(connection, "PRAGMA busy_timeout=5000;");
        Exec(connection, "PRAGMA synchronous=NORMAL;");
        return connection;
    }

    private static void ApplyMigrationScript(SqliteConnection connection, string fileName)
    {
        var sql = ReadMigration(fileName);
        foreach (var statement in SplitStatements(sql))
            Exec(connection, statement);
    }

    private static void MarkMigration(SqliteConnection connection, int version, string name)
    {
        using var mark = connection.CreateCommand();
        mark.CommandText = """
            INSERT INTO schema_migration (version, name, applied_at)
            VALUES ($version, $name, $at);
            """;
        mark.Parameters.AddWithValue("$version", version);
        mark.Parameters.AddWithValue("$name", name);
        mark.Parameters.AddWithValue("$at", Now());
        mark.ExecuteNonQuery();
    }

    private static string ReadMigration(string fileName)
    {
        var assembly = typeof(PosDatabase).Assembly;
        var name = assembly.GetManifestResourceNames().Single(resource =>
            resource.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded migration {fileName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static IEnumerable<string> SplitStatements(string sql)
    {
        foreach (var part in sql.Split(';'))
        {
            var statement = part.Trim();
            if (statement.Length == 0)
                continue;

            var code = string.Join(
                '\n',
                statement.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));
            if (string.IsNullOrWhiteSpace(code))
                continue;

            yield return statement;
        }
    }

    private static string BuildSalePayload(
        string saleId,
        string localShiftId,
        string cashierId,
        int receiptNo,
        long subtotalMinor,
        long discountMinor,
        long total,
        long tenderMinor,
        string createdAt,
        IReadOnlyList<CashSaleLine> lines,
        string localPaymentMethod,
        string erpPaymentMethod,
        decimal? wechatEgpPerCny,
        bool isReturn = false,
        string? loyaltyCustomerId = null,
        int pointsEarned = 0,
        int pointsRedeemed = 0)
    {
        // Local outbox document. ErpCheckoutMapper still posts env paymentMethod until it reads this field.
        object? fx = wechatEgpPerCny is { } rate
            ? new { quote = "CNY", egpPerCny = rate }
            : null;
        var payload = new
        {
            saleId,
            shiftId = (string?)null,
            localShiftId,
            cashierId,
            receiptNo,
            currency = "EGP",
            paymentMethod = erpPaymentMethod,
            isReturn,
            customerId = string.IsNullOrWhiteSpace(loyaltyCustomerId) ? null : loyaltyCustomerId,
            pointsEarned,
            pointsRedeemed,
            subtotalMinor,
            discountMinor,
            taxMinor = 0L,
            totalMinor = total,
            createdAt,
            fx,
            lines = lines.Select((line, index) => new
            {
                lineNo = index + 1,
                productId = line.ProductId,
                barcode = line.Barcode,
                nameAr = line.NameAr,
                qtyMilli = line.QtyMilli,
                unitPriceMinor = line.UnitPriceMinor,
                discountMinor = 0L,
                lineTotalMinor = line.LineTotalMinor,
            }).ToArray(),
            payments = new[]
            {
                new
                {
                    method = localPaymentMethod,
                    currency = "EGP",
                    amountMinor = tenderMinor,
                    amountEgpMinor = tenderMinor,
                    reference = (string?)null,
                },
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    private static void EnsureLocalCashier(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cashier (id, display_name, role, pin_hash, pin_salt, is_active, updated_at)
            VALUES ($id, 'كاشير محلي', 'cashier', 'pending-pin-sync', 'local', 1, $now)
            ON CONFLICT (id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", LocalCashierId);
        command.Parameters.AddWithValue("$now", Now());
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Ensures local demo staff have real PBKDF2 hashes (bootstrap PIN) until ERP cashier sync.
    /// </summary>
    private static void EnsureBootstrapCashiers(SqliteConnection connection)
    {
        EnsureLocalCashier(connection);
        UpsertBootstrapCashier(connection, LocalCashierId, "كاشير محلي", "cashier", PinHasher.BootstrapCashierPin);
        UpsertBootstrapCashier(connection, LocalSupervisorId, "مشرف محلي", "supervisor", PinHasher.BootstrapSupervisorPin);
    }

    private static void UpsertBootstrapCashier(
        SqliteConnection connection,
        string id,
        string displayName,
        string role,
        string pin)
    {
        string? hash;
        string? salt;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT pin_hash, pin_salt FROM cashier WHERE id = $id";
            read.Parameters.AddWithValue("$id", id);
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                hash = reader.GetString(0);
                salt = reader.GetString(1);
            }
            else
            {
                hash = null;
                salt = null;
            }
        }

        var needsHash = hash is null
            || salt is null
            || string.Equals(hash, "pending-pin-sync", StringComparison.Ordinal)
            || string.Equals(salt, "local", StringComparison.Ordinal);
        if (!needsHash && hash is not null)
            return;

        var (newHash, newSalt) = PinHasher.Hash(pin);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cashier (id, display_name, role, pin_hash, pin_salt, is_active, updated_at, failed_attempts, locked_until)
            VALUES ($id, $name, $role, $hash, $salt, 1, $now, 0, NULL)
            ON CONFLICT (id) DO UPDATE SET
              display_name = excluded.display_name,
              role = excluded.role,
              pin_hash = excluded.pin_hash,
              pin_salt = excluded.pin_salt,
              is_active = 1,
              updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$hash", newHash);
        command.Parameters.AddWithValue("$salt", newSalt);
        command.Parameters.AddWithValue("$now", Now());
        command.ExecuteNonQuery();
    }

    private static string TerminalId(SqliteConnection connection) =>
        ScalarString(connection, "SELECT id FROM terminal LIMIT 1") ?? string.Empty;

    private static ShiftRecord? ReadOpenShift(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, cashier_id, opened_at, closed_at, opening_float_minor, closing_cash_minor, status, sync_status
            FROM shift
            WHERE status = 'open'
            ORDER BY opened_at DESC
            LIMIT 1
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new ShiftRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.GetString(6),
            reader.GetString(7));
    }

    private static CashierRecord? ReadCashier(SqliteConnection connection, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, display_name, role, is_active
            FROM cashier WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new CashierRecord(
            reader.GetString(0),
            reader.GetString(1),
            ParseRole(reader.GetString(2)),
            reader.GetInt32(3) == 1);
    }

    private static bool CashierExistsActive(SqliteConnection connection, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM cashier WHERE id = $id AND is_active = 1";
        command.Parameters.AddWithValue("$id", id);
        return (long)command.ExecuteScalar()! == 1;
    }

    private static DateTimeOffset? ReadGlobalLockout(SqliteConnection connection, DateTimeOffset now)
    {
        var raw = ReadSetting(connection, "auth.locked_until");
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (!DateTimeOffset.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var until))
            return null;
        return until > now ? until : null;
    }

    private static int BumpGlobalPinFailure(SqliteConnection connection, DateTimeOffset now)
    {
        var raw = ReadSetting(connection, "auth.failed_attempts");
        var failures = 0;
        if (!string.IsNullOrWhiteSpace(raw))
            _ = int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out failures);
        failures++;
        WriteSetting(connection, "auth.failed_attempts", failures.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (failures >= PinMaxFailures)
            WriteSetting(connection, "auth.locked_until", now.Add(PinLockoutDuration).ToString("O"));
        return failures;
    }

    private static void ClearGlobalLockout(SqliteConnection connection)
    {
        WriteSetting(connection, "auth.failed_attempts", "0");
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM app_setting WHERE key = 'auth.locked_until'";
        command.ExecuteNonQuery();
    }

    private static CashierRole ParseRole(string role) => role switch
    {
        "supervisor" => CashierRole.Supervisor,
        "manager" => CashierRole.Manager,
        _ => CashierRole.Cashier,
    };

    private static void EnsureTerminal(SqliteConnection connection)
    {
        if (ScalarLong(connection, "SELECT COUNT(*) FROM terminal") > 0)
            return;

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO terminal (id, store_code, name, created_at)
            VALUES ($id, $store, 'كاشير ١', $now);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$store", DefaultStoreCode);
        command.Parameters.AddWithValue("$now", Now());
        command.ExecuteNonQuery();
    }

    private static void SeedDemoCatalogIfNeeded(SqliteConnection connection)
    {
        var source = ReadSetting(connection, "catalog.source");
        var count = ScalarLong(connection, "SELECT COUNT(*) FROM product");
        if (count > 0 || source == "erp")
            return;

        var now = Now();
        InsertDemo(connection, "demo-rice", "DEMO-RICE", DemoRiceBarcode, "أرز مصري ٥ كجم", "Egyptian rice 5kg", "埃及大米 5公斤", "كيس", 8500, now);
        InsertDemo(connection, "demo-oil", "DEMO-OIL", DemoOilBarcode, "زيت ذرة ١ لتر", "Corn oil 1L", "玉米油 1升", "زجاجة", 6250, now);
        InsertDemo(connection, "demo-tea", "DEMO-TEA", DemoTeaBarcode, "شاي أخضر ٤٠٠ جم", "Green tea 400g", "绿茶 400克", "علبة", 4500, now);
        WriteSetting(connection, "catalog.source", "demo");
    }

    private static void InsertDemo(
        SqliteConnection connection,
        string id,
        string sku,
        string barcode,
        string nameAr,
        string nameEn,
        string nameZh,
        string unitAr,
        long priceMinor,
        string now)
    {
        using var product = connection.CreateCommand();
        product.CommandText = """
            INSERT INTO product (
              id, sku, barcode, name_ar, name_en, name_zh, unit_ar, tax_rate_bps, is_active, updated_at
            ) VALUES (
              $id, $sku, $barcode, $nameAr, $nameEn, $nameZh, $unit, 0, 1, $now
            );
            """;
        product.Parameters.AddWithValue("$id", id);
        product.Parameters.AddWithValue("$sku", sku);
        product.Parameters.AddWithValue("$barcode", barcode);
        product.Parameters.AddWithValue("$nameAr", nameAr);
        product.Parameters.AddWithValue("$nameEn", nameEn);
        product.Parameters.AddWithValue("$nameZh", nameZh);
        product.Parameters.AddWithValue("$unit", unitAr);
        product.Parameters.AddWithValue("$now", now);
        product.ExecuteNonQuery();

        using var price = connection.CreateCommand();
        price.CommandText = """
            INSERT INTO product_price (product_id, currency, amount_minor)
            VALUES ($id, 'EGP', $price);
            """;
        price.Parameters.AddWithValue("$id", id);
        price.Parameters.AddWithValue("$price", priceMinor);
        price.ExecuteNonQuery();
    }

    public bool ToggleFavorite(string productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            throw new ArgumentException("Product id is required.", nameof(productId));

        lock (_gate)
        {
            using var connection = Open();
            using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT 1 FROM product_favorite WHERE product_id = $id";
            exists.Parameters.AddWithValue("$id", productId);
            var pinned = exists.ExecuteScalar() is not null;
            if (pinned)
            {
                using var remove = connection.CreateCommand();
                remove.CommandText = "DELETE FROM product_favorite WHERE product_id = $id";
                remove.Parameters.AddWithValue("$id", productId);
                remove.ExecuteNonQuery();
                return false;
            }

            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO product_favorite (product_id, pinned_at)
                VALUES ($id, $at);
                """;
            insert.Parameters.AddWithValue("$id", productId);
            insert.Parameters.AddWithValue("$at", Now());
            insert.ExecuteNonQuery();
            return true;
        }
    }

    public HeldCartSummary HoldCart(IReadOnlyList<HeldCartLinePayload> lines, string? cashierId = null)
    {
        if (lines is null || lines.Count == 0)
            throw new InvalidOperationException("لا يمكن تعليق سلة فارغة.");

        var payload = new HeldCartPayload(lines);
        var json = JsonSerializer.Serialize(payload);
        var total = lines.Sum(line => checked(line.UnitPriceMinor * line.Qty));
        var itemCount = lines.Sum(line => line.Qty);
        var now = DateTimeOffset.Now;
        var id = Guid.NewGuid().ToString("N");
        var label = now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        lock (_gate)
        {
            using var connection = Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO held_cart (id, label, held_at, item_count, total_minor, payload_json, cashier_id)
                VALUES ($id, $label, $heldAt, $count, $total, $json, $cashier);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$label", label);
            insert.Parameters.AddWithValue("$heldAt", now.ToString("O"));
            insert.Parameters.AddWithValue("$count", itemCount);
            insert.Parameters.AddWithValue("$total", total);
            insert.Parameters.AddWithValue("$json", json);
            insert.Parameters.AddWithValue("$cashier", (object?)cashierId ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        PosLog.Info($"held cart created id={id} lines={lines.Count} total={total}");
        return new HeldCartSummary(id, label, now.ToString("O"), itemCount, total);
    }

    public IReadOnlyList<HeldCartSummary> ListHeldCarts()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, label, held_at, item_count, total_minor
                FROM held_cart
                ORDER BY held_at DESC;
                """;
            using var reader = command.ExecuteReader();
            var list = new List<HeldCartSummary>();
            while (reader.Read())
            {
                list.Add(new HeldCartSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    (int)reader.GetInt64(3),
                    reader.GetInt64(4)));
            }

            return list;
        }
    }

    public IReadOnlyList<HeldCartLinePayload>? TakeHeldCart(string heldCartId)
    {
        if (string.IsNullOrWhiteSpace(heldCartId))
            throw new ArgumentException("Held cart id is required.", nameof(heldCartId));

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            using var select = connection.CreateCommand();
            select.Transaction = tx;
            select.CommandText = "SELECT payload_json FROM held_cart WHERE id = $id";
            select.Parameters.AddWithValue("$id", heldCartId);
            var json = select.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(json))
            {
                tx.Rollback();
                return null;
            }

            var payload = JsonSerializer.Deserialize<HeldCartPayload>(json)
                ?? throw new InvalidOperationException("تعذر قراءة السلة المعلّقة.");
            using var delete = connection.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM held_cart WHERE id = $id";
            delete.Parameters.AddWithValue("$id", heldCartId);
            delete.ExecuteNonQuery();
            tx.Commit();
            PosLog.Info($"held cart resumed id={heldCartId}");
            return payload.Lines;
        }
    }

    public bool DiscardHeldCart(string heldCartId)
    {
        if (string.IsNullOrWhiteSpace(heldCartId))
            throw new ArgumentException("Held cart id is required.", nameof(heldCartId));

        lock (_gate)
        {
            using var connection = Open();
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM held_cart WHERE id = $id";
            delete.Parameters.AddWithValue("$id", heldCartId);
            var removed = delete.ExecuteNonQuery() > 0;
            if (removed)
                PosLog.Info($"held cart discarded id={heldCartId}");
            return removed;
        }
    }

    public int CountHeldCarts()
    {
        lock (_gate)
        {
            using var connection = Open();
            return (int)ScalarLong(connection, "SELECT COUNT(*) FROM held_cart");
        }
    }

    private static CatalogProduct ReadProduct(SqliteDataReader reader)
    {
        // HasLocalImage stays false here on purpose: checking the disk for every row
        // on catalog reload freezes startup with thousands of products. The UI converter
        // resolves local files lazily for the few cards that are actually on screen.
        return new CatalogProduct(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt64(6),
            reader.FieldCount > 7 && !reader.IsDBNull(7) && reader.GetInt64(7) != 0,
            reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetString(8) : null,
            HasLocalImage: false,
            CategoryId: reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetString(9) : null,
            CategoryNameAr: reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetString(10) : null);
    }

    public IReadOnlyList<CatalogCategory> ListCategories()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, name_ar, name_en, name_zh, product_count
                FROM category
                ORDER BY product_count DESC, name_ar COLLATE NOCASE
                """;
            using var reader = command.ExecuteReader();
            var list = new List<CatalogCategory>();
            while (reader.Read())
            {
                list.Add(new CatalogCategory(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? 0 : (int)reader.GetInt64(4)));
            }

            return list;
        }
    }

    public int UpsertCategories(IReadOnlyList<CatalogCategoryDraft> categories)
    {
        if (categories.Count == 0)
            return 0;

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToString("O");
            var upserted = 0;
            foreach (var category in categories)
            {
                if (string.IsNullOrWhiteSpace(category.Id) || string.IsNullOrWhiteSpace(category.NameAr))
                    continue;

                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO category (id, name_ar, name_en, name_zh, slug, image_url, product_count, updated_at)
                    VALUES ($id, $nameAr, $nameEn, $nameZh, $slug, $image, $count, $now)
                    ON CONFLICT(id) DO UPDATE SET
                      name_ar = excluded.name_ar,
                      name_en = excluded.name_en,
                      name_zh = excluded.name_zh,
                      slug = excluded.slug,
                      image_url = COALESCE(excluded.image_url, category.image_url),
                      product_count = excluded.product_count,
                      updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$id", category.Id.Trim());
                command.Parameters.AddWithValue("$nameAr", category.NameAr.Trim());
                command.Parameters.AddWithValue("$nameEn", (object?)category.NameEn ?? DBNull.Value);
                command.Parameters.AddWithValue("$nameZh", (object?)category.NameZh ?? DBNull.Value);
                command.Parameters.AddWithValue("$slug", (object?)category.Slug ?? DBNull.Value);
                command.Parameters.AddWithValue("$image", (object?)category.ImageUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("$count", Math.Max(0, category.ProductCount));
                command.Parameters.AddWithValue("$now", now);
                command.ExecuteNonQuery();
                upserted++;
            }

            tx.Commit();
            return upserted;
        }
    }

    private static string? ReadSetting(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_setting WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void WriteSetting(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_setting (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static string? ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
}
