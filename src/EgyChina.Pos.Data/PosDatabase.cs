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
    public const string DemoRiceBarcode = "2000000000018";
    public const string DemoOilBarcode = "2000000000025";
    public const string DemoTeaBarcode = "2000000000032";

    private const string ProductSelect = """
        SELECT p.id, p.sku, p.barcode, p.name_ar, p.name_en, p.name_zh, pr.amount_minor
        FROM product p
        INNER JOIN product_price pr ON pr.product_id = p.id AND pr.currency = 'EGP'
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
            }

            EnsureTerminal(connection);
            EnsureLocalCashier(connection);
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

    public void RecoverInflightOutbox()
    {
        lock (_gate)
        {
            using var connection = Open();
            Exec(connection, "UPDATE outbox SET status = 'pending' WHERE status = 'inflight'");
        }
    }

    public IReadOnlyList<PendingOutboxRow> ListPendingSales(int limit)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, aggregate_id, event_type, payload_json, attempt_count
                FROM outbox
                WHERE status = 'pending' AND event_type = 'SaleCompleted'
                ORDER BY created_at
                LIMIT $limit
                """;
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

    public void RecordOutboxFailure(string outboxId, string error)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox
                SET status = 'pending',
                    attempt_count = attempt_count + 1,
                    last_error = $error,
                    next_attempt_at = $now
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", outboxId);
            command.Parameters.AddWithValue("$error", error);
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

    public long? TryGetErpNumericId(string localProductId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT erp_numeric_id FROM product WHERE id = $id";
            command.Parameters.AddWithValue("$id", localProductId);
            var value = command.ExecuteScalar();
            return value is long number ? number : null;
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

    /// <summary>
    /// Applies a GET /api/products body. Links sku to the numeric id. Does not change shelf prices.
    /// </summary>
    public int LinkCatalogFromErpJson(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        var linked = 0;
        if (root.TryGetProperty("products", out var products) && products.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var product in products.EnumerateArray())
            {
                if (!product.TryGetProperty("sku", out var skuNode) || skuNode.ValueKind != System.Text.Json.JsonValueKind.String)
                    continue;
                if (!product.TryGetProperty("id", out var idNode) || idNode.ValueKind != System.Text.Json.JsonValueKind.Number || !idNode.TryGetInt64(out var numericId) || numericId <= 0)
                    continue;
                var prisma = product.TryGetProperty("prismaId", out var prismaNode) && prismaNode.ValueKind == System.Text.Json.JsonValueKind.String
                    ? prismaNode.GetString()
                    : null;
                lock (_gate)
                {
                    using var connection = Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        UPDATE product
                        SET erp_numeric_id = $erp, prisma_id = COALESCE($prisma, prisma_id)
                        WHERE sku = $sku
                        """;
                    command.Parameters.AddWithValue("$erp", numericId);
                    command.Parameters.AddWithValue("$prisma", (object?)prisma ?? DBNull.Value);
                    command.Parameters.AddWithValue("$sku", skuNode.GetString());
                    linked += command.ExecuteNonQuery();
                }
            }
        }

        if (root.TryGetProperty("removedPrismaIds", out var removed) && removed.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in removed.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.String)
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

    /// <summary>
    /// Commits one cash EGP sale, its lines, its payment, and the outbox row in a single transaction.
    /// Does not talk to the network. The payload matches the proposed SaleCompleted shape; posting it is still an ASSUMPTION.
    /// </summary>
    public CompletedCashSale CompleteCashSale(IReadOnlyList<CashSaleLine> lines, long tenderMinor)
    {
        if (lines is null || lines.Count == 0)
            throw new ArgumentException("A cash sale needs at least one line.", nameof(lines));
        if (tenderMinor < 0)
            throw new ArgumentOutOfRangeException(nameof(tenderMinor));

        long total = 0;
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
            total = checked(total + line.LineTotalMinor);
        }

        if (tenderMinor < total)
            throw new InvalidOperationException("Tender is below the sale total.");

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            EnsureLocalCashier(connection);

            var terminalId = ScalarString(connection, "SELECT id FROM terminal LIMIT 1");
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
            using (var sale = connection.CreateCommand())
            {
                sale.CommandText = """
                    INSERT INTO sale (
                      id, server_id, shift_id, cashier_id, receipt_no, status, currency,
                      subtotal_minor, discount_minor, tax_minor, total_minor,
                      created_at, completed_at, sync_status
                    ) VALUES (
                      $id, NULL, NULL, $cashier, $receipt, 'completed', 'EGP',
                      $total, 0, 0, $total,
                      $now, $now, 'pending'
                    );
                    """;
                sale.Parameters.AddWithValue("$id", saleId);
                sale.Parameters.AddWithValue("$cashier", LocalCashierId);
                sale.Parameters.AddWithValue("$receipt", receiptNo);
                sale.Parameters.AddWithValue("$total", total);
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
                      $id, $sale, 'cash_egp', 'EGP', $tender, $tender, NULL, $now
                    );
                    """;
                payment.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                payment.Parameters.AddWithValue("$sale", saleId);
                payment.Parameters.AddWithValue("$tender", tenderMinor);
                payment.Parameters.AddWithValue("$now", now);
                payment.ExecuteNonQuery();
            }

            var idempotencyKey = $"{terminalId}:sale:{saleId}";
            var payload = BuildSalePayload(saleId, receiptNo, total, tenderMinor, now, lines);
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
                LocalCashierId,
                receiptNo,
                total,
                tenderMinor,
                tenderMinor - total,
                idempotencyKey);
            PosLog.Info($"cash sale committed receipt={receiptNo} total={total} tender={tenderMinor}");
            return completed;
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
        int receiptNo,
        long total,
        long tenderMinor,
        string createdAt,
        IReadOnlyList<CashSaleLine> lines)
    {
        // Local outbox document. ErpCheckoutMapper turns it into the live POST /api/checkout body.
        var payload = new
        {
            saleId,
            shiftId = (string?)null,
            cashierId = LocalCashierId,
            receiptNo,
            currency = "EGP",
            subtotalMinor = total,
            discountMinor = 0L,
            taxMinor = 0L,
            totalMinor = total,
            createdAt,
            fx = (object?)null,
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
                    method = "cash_egp",
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
        // Placeholder until PIN sync. Not a verifier and not a person.
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

    private static CatalogProduct ReadProduct(SqliteDataReader reader)
    {
        return new CatalogProduct(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt64(6));
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
