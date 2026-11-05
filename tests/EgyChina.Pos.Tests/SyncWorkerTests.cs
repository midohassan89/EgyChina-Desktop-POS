using System.Net;
using EgyChina.Pos.Core;
using Xunit;
using EgyChina.Pos.Data;
using EgyChina.Pos.Sync;

namespace EgyChina.Pos.Tests;

public sealed class SyncWorkerTests : IDisposable
{
    private readonly string _directory;

    public SyncWorkerTests()
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
    public async Task Tick_without_an_endpoint_stays_local()
    {
        var database = NewDatabase();
        database.TryEnqueueOutbox("sale", "1", "SaleCompleted", "{}", "term:sale:1");
        var status = new SyncStatus();
        var worker = new SyncWorker(database, status, new SyncOptions
        {
            BaseAddress = null,
            Interval = TimeSpan.FromHours(1),
        });

        await worker.TickAsync(CancellationToken.None);

        var snap = status.Read();
        Assert.False(snap.IsOnline);
        Assert.Equal(1, snap.PendingOutbox);
        Assert.Contains("غير مضبوط", snap.Message, StringComparison.Ordinal);
        Assert.Equal(SyncWorker.NotConfiguredCursor, database.ReadCursor(SyncWorker.CursorStream)?.Cursor);
        await worker.DisposeAsync();
    }

    [Fact]
    public void Health_uri_keeps_the_version_prefix()
    {
        var uri = SyncWorker.HealthUri(new Uri("https://erp.echinamart.com/api/pos/v1"));
        Assert.Equal("https://erp.echinamart.com/api/pos/v1/health", uri.ToString());
    }

    [Fact]
    public async Task Tick_records_an_http_status_without_posting()
    {
        var listener = StartListener(out var port);
        var seen = string.Empty;
        var serve = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            seen = context.Request.HttpMethod + " " + context.Request.Url?.AbsolutePath;
            context.Response.StatusCode = 401;
            context.Response.Close();
        });

        var database = NewDatabase();
        var status = new SyncStatus();
        var worker = new SyncWorker(database, status, new SyncOptions
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/api/pos/v1/"),
            Interval = TimeSpan.FromHours(1),
        });

        try
        {
            await worker.TickAsync(CancellationToken.None);
            await serve.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Close();
            await worker.DisposeAsync();
        }

        Assert.Equal("GET /api/pos/v1/health", seen);
        Assert.Equal("http-401", database.ReadCursor(SyncWorker.CursorStream)?.Cursor);
        Assert.Contains("401", status.Read().Message, StringComparison.Ordinal);
        Assert.False(status.Read().IsOnline);
    }

    private PosDatabase NewDatabase()
    {
        var database = new PosDatabase(Path.Combine(_directory, "pos.db"));
        database.Initialize();
        return database;
    }

    private static HttpListener StartListener(out int port)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            port = Random.Shared.Next(20000, 45000);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }

        throw new InvalidOperationException("Could not bind a local port for the sync probe test.");
    }
}
