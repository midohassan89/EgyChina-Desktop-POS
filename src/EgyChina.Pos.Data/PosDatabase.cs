using EgyChina.Pos.Core;
using Microsoft.Data.Sqlite;

namespace EgyChina.Pos.Data;

/// <summary>
/// One cashier terminal database. Callers share a process lock; do not open the file elsewhere.
/// </summary>
public sealed class PosDatabase
{
    public const string DefaultStoreCode = "OBOUR";
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
                ApplyMigrationScript(connection);
                using var mark = connection.CreateCommand();
                mark.CommandText = """
                    INSERT INTO schema_migration (version, name, applied_at)
                    VALUES (1, 'init', $at);
                    """;
                mark.Parameters.AddWithValue("$at", Now());
                mark.ExecuteNonQuery();
            }

            EnsureTerminal(connection);
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
            return (int)ScalarLong(connection, "SELECT COUNT(*) FROM outbox WHERE status = 'pending'");
        }
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

    private static void ApplyMigrationScript(SqliteConnection connection)
    {
        var sql = ReadMigration("001_init.sql");
        foreach (var statement in SplitStatements(sql))
            Exec(connection, statement);
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
