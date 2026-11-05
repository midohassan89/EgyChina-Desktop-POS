using EgyChina.Pos.Core;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.Sync;

/// <summary>
/// Background tick for the outbox. This build never POSTs a sale.
/// With no <see cref="SyncOptions.BaseAddress"/> it only records that the device API is not configured.
/// With a base address it GETs <c>health</c> and stores the status code.
/// </summary>
public sealed class SyncWorker : IAsyncDisposable
{
    public const string CursorStream = "worker";
    public const string NotConfiguredCursor = "not-configured";

    private readonly PosDatabase _database;
    private readonly SyncStatus _status;
    private readonly SyncOptions _options;
    private readonly PeriodicTimer _timer;
    private readonly HttpClient? _http;
    private readonly bool _ownsHttpClient;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _started;

    public SyncWorker(PosDatabase database, SyncStatus status, SyncOptions? options = null, HttpClient? httpClient = null)
    {
        _database = database;
        _status = status;
        _options = options ?? SyncOptions.FromEnvironment();
        if (_options.Interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Sync interval must be positive.");

        _timer = new PeriodicTimer(_options.Interval);
        if (_options.BaseAddress is null)
        {
            _http = null;
            _ownsHttpClient = false;
            return;
        }

        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttpClient = false;
            return;
        }

        var client = new HttpClient { Timeout = _options.HttpTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EgyChina-POS/0.1");
        _http = client;
        _ownsHttpClient = true;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => Loop(_cts.Token));
    }

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var pending = _database.CountPendingOutbox();
        if (_options.BaseAddress is null || _http is null)
        {
            _database.WriteCursor(CursorStream, NotConfiguredCursor, null);
            _status.Update(false, "المزامنة المحلية تعمل. عنوان الخادم غير مضبوط.", pending);
            return;
        }

        try
        {
            using var response = await _http.GetAsync(HealthUri(_options.BaseAddress), cancellationToken)
                .ConfigureAwait(false);
            var code = (int)response.StatusCode;
            _database.WriteCursor(CursorStream, $"http-{code}", response.IsSuccessStatusCode ? null : $"HTTP {code}");
            var message = response.IsSuccessStatusCode
                ? "الخادم يستجيب."
                : $"الخادم رد برقم {code}. لا يُرسل بيع في هذه النسخة.";
            _status.Update(response.IsSuccessStatusCode, message, pending);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            PosLog.Error("sync health probe failed", ex);
            _database.WriteCursor(CursorStream, "unreachable", ex.Message);
            _status.Update(false, "تعذر الوصول إلى خادم المزامنة.", pending);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _timer.Dispose();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The process is leaving. A tick still inside SQLite can finish on its own.
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_ownsHttpClient)
            _http?.Dispose();
        _cts?.Dispose();
    }

    public static Uri HealthUri(Uri baseAddress)
    {
        var text = baseAddress.ToString();
        if (!text.EndsWith('/'))
            text += "/";
        return new Uri(new Uri(text, UriKind.Absolute), "health");
    }

    private async Task Loop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PosLog.Error("sync tick failed", ex);
                try
                {
                    _status.Update(false, "توقفت دورة المزامنة بسبب خطأ داخلي.", _database.CountPendingOutbox());
                }
                catch (Exception statusError)
                {
                    PosLog.Error("sync status update failed", statusError);
                }
            }

            try
            {
                if (!await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
