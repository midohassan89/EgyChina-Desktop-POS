using EgyChina.Pos.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EgyChina.Pos.Tests;

[Collection("PosLog")]
public sealed class SchemaUpgradeTests : IDisposable
{
    private readonly string _directory;

    public SchemaUpgradeTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "egychina-pos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        PosLog.SetDirectory(Path.Combine(_directory, "logs"));
    }

    public void Dispose()
    {
        PosDatabase.DebugFailAfterMigrationFile = null;
        PosDatabase.DebugFailAfterMigrationPath = null;
        PosLog.Reset();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Schema9_database_is_backed_up_then_migrated_idempotently()
    {
        var database = NewDatabase();
        database.Initialize();
        var shift = TestShift.Open(database);
        RewindToSchema9(database, dropIndexes: true);
        Assert.Equal(9, database.GetSchemaVersion());
        Assert.True(database.HasPendingMigrations());
        Assert.NotNull(database.GetOpenShift());

        var backups = Path.Combine(_directory, "backups");
        SchemaUpgrade.ApplyKeepingPreviousSchema(database, backups);

        Assert.Equal(10, database.GetSchemaVersion());
        Assert.False(database.HasPendingMigrations());
        Assert.Equal(shift.Id, database.GetOpenShift()!.Id);
        Assert.True(IndexExists(database, "ix_sale_completed_recent"));
        Assert.True(IndexExists(database, "ix_outbox_aggregate"));
        Assert.True(IndexExists(database, "ix_sale_shift_status"));
        Assert.True(IndexExists(database, "ix_sale_line_product"));

        var snapshot = Directory.GetFiles(backups, "pos-*.db").Single();
        Assert.Equal(9, PosDatabase.PeekSchemaVersion(snapshot));

        var log = File.ReadAllText(Path.Combine(_directory, "logs", "pos.log"));
        Assert.Contains("pre-migration backup path=", log, StringComparison.Ordinal);
        Assert.Contains("schema ready version=10", log, StringComparison.Ordinal);

        SchemaUpgrade.ApplyKeepingPreviousSchema(database, backups);
        Assert.Equal(10, database.GetSchemaVersion());

        RewindToSchema9(database, dropIndexes: false);
        database.Initialize();
        Assert.Equal(10, database.GetSchemaVersion());
        Assert.True(IndexExists(database, "ix_sale_completed_recent"));
    }

    [Fact]
    public void Failed_migration_010_restores_schema9_and_still_opens()
    {
        var database = NewDatabase();
        database.Initialize();
        var shift = TestShift.Open(database);
        RewindToSchema9(database, dropIndexes: true);

        PosDatabase.DebugFailAfterMigrationFile = "010_query_indexes.sql";
        PosDatabase.DebugFailAfterMigrationPath = database.DatabasePath;
        try
        {
            SchemaUpgrade.ApplyKeepingPreviousSchema(database, Path.Combine(_directory, "backups"));
        }
        finally
        {
            PosDatabase.DebugFailAfterMigrationFile = null;
            PosDatabase.DebugFailAfterMigrationPath = null;
        }

        Assert.Equal(9, database.GetSchemaVersion());
        Assert.False(IndexExists(database, "ix_sale_completed_recent"));
        Assert.False(IndexExists(database, "ix_outbox_aggregate"));
        Assert.False(IndexExists(database, "ix_sale_shift_status"));
        Assert.False(IndexExists(database, "ix_sale_line_product"));
        Assert.Equal(shift.Id, database.GetOpenShift()!.Id);
        Assert.NotEmpty(database.ListActiveProducts());

        var log = File.ReadAllText(Path.Combine(_directory, "logs", "pos.log"));
        Assert.Contains("pre-migration backup path=", log, StringComparison.Ordinal);
        Assert.Contains("migration failed", log, StringComparison.Ordinal);
        Assert.Contains("migration restored backup path=", log, StringComparison.Ordinal);
        Assert.Contains("migration kept previous schema version=9", log, StringComparison.Ordinal);
        Assert.Contains("schema ready version=9", log, StringComparison.Ordinal);
    }

    private PosDatabase NewDatabase()
    {
        var database = new PosDatabase(Path.Combine(_directory, "pos.db"));
        return database;
    }

    private static void RewindToSchema9(PosDatabase database, bool dropIndexes)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ConnectionString);
        connection.Open();
        var statements = new List<string>
        {
            "DELETE FROM schema_migration WHERE version >= 10;",
        };
        if (dropIndexes)
        {
            statements.Insert(0, "DROP INDEX IF EXISTS ix_sale_line_product;");
            statements.Insert(0, "DROP INDEX IF EXISTS ix_sale_shift_status;");
            statements.Insert(0, "DROP INDEX IF EXISTS ix_sale_completed_recent;");
            statements.Insert(0, "DROP INDEX IF EXISTS ix_outbox_aggregate;");
        }

        foreach (var sql in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private static bool IndexExists(PosDatabase database, string name)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return (long)command.ExecuteScalar()! == 1;
    }
}
