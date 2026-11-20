using System.Diagnostics;
using System.Globalization;
using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EgyChina.Pos.Tests;

/// <summary>
/// Migration 010. The budget defaults to 50ms on the median of several runs.
/// A slow CI runner can raise it with EGYCHINA_POS_RECENT_SALES_BUDGET_MS (milliseconds, test-only).
/// This file does not set sync environment variables and does not talk to ERP.
/// </summary>
public sealed class RecentSaleQueryTests : IDisposable
{
    private const int SampleCount = 5;
    private readonly string _directory;

    public RecentSaleQueryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "egychina-pos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Hot_reads_use_migration_010_indexes()
    {
        var database = NewDatabase();
        var shift = TestShift.Open(database);
        SeedCompletedSales(database, shift, 32);
        SeedOtherShifts(database, shift.CashierId, shifts: 80, salesPerShift: 4);
        Optimize(database);

        var names = ReadIndexNames(database);
        Assert.Contains("ix_outbox_aggregate", names);
        Assert.Contains("ix_sale_completed_recent", names);
        Assert.Contains("ix_sale_shift_status", names);
        Assert.Contains("ix_sale_line_product", names);
        Assert.Equal(10, database.GetSchemaVersion());

        var plans = database.ExplainHotReadPlans().ToDictionary(plan => plan.Name, plan => plan.Text);
        AssertIndexed(plans["recent-sales"], "sale", "ix_sale_completed_recent");
        AssertIndexed(plans["recent-sales"], "outbox", "ix_outbox_aggregate");
        AssertIndexed(plans["last-sale"], "sale", "ix_sale_completed_recent");
        AssertIndexed(plans["z-report"], "sale", "ix_sale_shift_status");
    }

    [Fact]
    public void ListRecentCompletedSales_median_stays_within_budget_at_10k()
    {
        var database = NewDatabase();
        var shift = TestShift.Open(database);
        SeedCompletedSales(database, shift, 10_000);

        var warmup = database.ListRecentCompletedSales(3);
        Assert.Equal(3, warmup.Count);

        var samples = new long[SampleCount];
        for (var i = 0; i < samples.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var rows = database.ListRecentCompletedSales(3);
            samples[i] = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.Equal(3, rows.Count);
        }

        Array.Sort(samples);
        var median = samples[samples.Length / 2];
        var budget = ReadBudgetMs();
        Assert.True(
            median <= budget,
            $"median {median.ToString(CultureInfo.InvariantCulture)}ms of [{string.Join(',', samples)}] exceeded {budget.ToString(CultureInfo.InvariantCulture)}ms");
    }

    private PosDatabase NewDatabase()
    {
        var database = new PosDatabase(Path.Combine(_directory, "pos.db"));
        database.Initialize();
        return database;
    }

    private static int ReadBudgetMs()
    {
        var raw = Environment.GetEnvironmentVariable("EGYCHINA_POS_RECENT_SALES_BUDGET_MS");
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
            && ms > 0)
        {
            return ms;
        }

        return 50;
    }

    private static void AssertIndexed(string plan, string table, string index)
    {
        Assert.Contains(index, plan, StringComparison.Ordinal);
        foreach (var raw in plan.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var scansTable = line.Contains($"SCAN {table}", StringComparison.OrdinalIgnoreCase)
                || line.Contains($"SCAN TABLE {table}", StringComparison.OrdinalIgnoreCase);
            if (!scansTable)
                continue;

            Assert.True(
                line.Contains("USING INDEX", StringComparison.OrdinalIgnoreCase)
                || line.Contains("USING COVERING INDEX", StringComparison.OrdinalIgnoreCase),
                line);
        }
    }

    private static HashSet<string> ReadIndexNames(PosDatabase database)
    {
        using var connection = Open(database);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index'";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static void SeedCompletedSales(PosDatabase database, ShiftRecord shift, int count)
    {
        using var connection = Open(database);
        using var tx = connection.BeginTransaction();
        using var sale = connection.CreateCommand();
        sale.Transaction = tx;
        sale.CommandText = """
            INSERT INTO sale (
              id, server_id, shift_id, cashier_id, receipt_no, status, currency,
              subtotal_minor, discount_minor, tax_minor, total_minor,
              note, created_at, completed_at, sync_status
            ) VALUES (
              $id, NULL, $shift, $cashier, $receipt, 'completed', 'EGP',
              1000, 0, 0, 1000,
              NULL, $at, $at, 'pending'
            );
            """;
        var saleId = sale.Parameters.Add("$id", SqliteType.Text);
        var shiftId = sale.Parameters.Add("$shift", SqliteType.Text);
        var cashierId = sale.Parameters.Add("$cashier", SqliteType.Text);
        var receipt = sale.Parameters.Add("$receipt", SqliteType.Integer);
        var at = sale.Parameters.Add("$at", SqliteType.Text);
        shiftId.Value = shift.Id;
        cashierId.Value = shift.CashierId;

        using var outbox = connection.CreateCommand();
        outbox.Transaction = tx;
        outbox.CommandText = """
            INSERT INTO outbox (
              id, aggregate_type, aggregate_id, event_type, payload_json,
              idempotency_key, created_at, attempt_count, next_attempt_at, status
            ) VALUES (
              $id, 'sale', $saleId, 'SaleCompleted', '{}',
              $key, $at, 0, $at, 'pending'
            );
            """;
        var outboxId = outbox.Parameters.Add("$id", SqliteType.Text);
        var aggregateId = outbox.Parameters.Add("$saleId", SqliteType.Text);
        var key = outbox.Parameters.Add("$key", SqliteType.Text);
        var outboxAt = outbox.Parameters.Add("$at", SqliteType.Text);

        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid().ToString("D");
            var stamp = DateTimeOffset.UnixEpoch.AddSeconds(i).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            saleId.Value = id;
            receipt.Value = 1_000_000 + i;
            at.Value = stamp;
            sale.ExecuteNonQuery();

            outboxId.Value = Guid.NewGuid().ToString("D");
            aggregateId.Value = id;
            key.Value = "seed-" + i.ToString(CultureInfo.InvariantCulture);
            outboxAt.Value = stamp;
            outbox.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void SeedOtherShifts(PosDatabase database, string cashierId, int shifts, int salesPerShift)
    {
        using var connection = Open(database);
        using var tx = connection.BeginTransaction();
        using var shift = connection.CreateCommand();
        shift.Transaction = tx;
        shift.CommandText = """
            INSERT INTO shift (
              id, server_id, cashier_id, opened_at, closed_at,
              opening_float_minor, closing_cash_minor, status, sync_status
            ) VALUES (
              $id, NULL, $cashier, $at, $at,
              0, 0, 'closed', 'synced'
            );
            """;
        var shiftId = shift.Parameters.Add("$id", SqliteType.Text);
        var cashier = shift.Parameters.Add("$cashier", SqliteType.Text);
        var shiftAt = shift.Parameters.Add("$at", SqliteType.Text);
        cashier.Value = cashierId;

        using var sale = connection.CreateCommand();
        sale.Transaction = tx;
        sale.CommandText = """
            INSERT INTO sale (
              id, server_id, shift_id, cashier_id, receipt_no, status, currency,
              subtotal_minor, discount_minor, tax_minor, total_minor,
              note, created_at, completed_at, sync_status
            ) VALUES (
              $id, NULL, $shift, $cashier, $receipt, 'completed', 'EGP',
              1000, 0, 0, 1000,
              NULL, $at, $at, 'synced'
            );
            """;
        var saleId = sale.Parameters.Add("$id", SqliteType.Text);
        var saleShift = sale.Parameters.Add("$shift", SqliteType.Text);
        var saleCashier = sale.Parameters.Add("$cashier", SqliteType.Text);
        var receipt = sale.Parameters.Add("$receipt", SqliteType.Integer);
        var saleAt = sale.Parameters.Add("$at", SqliteType.Text);
        saleCashier.Value = cashierId;

        var receiptNo = 2_000_000;
        for (var s = 0; s < shifts; s++)
        {
            var id = "shift-" + s.ToString(CultureInfo.InvariantCulture);
            var stamp = DateTimeOffset.UnixEpoch.AddDays(s + 1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            shiftId.Value = id;
            shiftAt.Value = stamp;
            shift.ExecuteNonQuery();

            for (var n = 0; n < salesPerShift; n++)
            {
                saleId.Value = Guid.NewGuid().ToString("D");
                saleShift.Value = id;
                receipt.Value = receiptNo++;
                saleAt.Value = stamp;
                sale.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    private static void Optimize(PosDatabase database)
    {
        using var connection = Open(database);
        using var command = connection.CreateCommand();
        command.CommandText = "ANALYZE;";
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(PosDatabase database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ConnectionString);
        connection.Open();
        using var fk = connection.CreateCommand();
        fk.CommandText = "PRAGMA foreign_keys=ON;";
        fk.ExecuteNonQuery();
        return connection;
    }
}
