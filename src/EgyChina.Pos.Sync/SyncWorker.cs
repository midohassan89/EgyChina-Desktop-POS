using EgyChina.Pos.Core;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.Sync;

/// <summary>
/// Background tick. Posts pending sales to the live <c>/api/checkout</c> route only when the
/// verified gates are set: Auth.js cookie, money scale, and payment-method string.
/// Anything less stays local. A sale is marked synced only after a full 2xx response.
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
        var block = _options.TransmissionBlock();
        if (block is not null || _http is null || _options.BaseAddress is null || _options.SessionCookie is null)
        {
            var pending = _database.CountPendingOutbox();
            var (cursor, message) = block switch
            {
                "auth-missing" => ("auth-missing", "المزامنة تنتظر جلسة Auth.js. لا يُرسل بيع."),
                "money-scale-unverified" => ("money-scale-unverified", "وحدة المبلغ في /api/checkout غير مؤكدة. لا يُرسل بيع."),
                "payment-method-unverified" => ("payment-method-unverified", "اسم طريقة الدفع عند الـ ERP غير معروف. لا يُرسل بيع."),
                _ => (NotConfiguredCursor, "المزامنة المحلية تعمل. عنوان الخادم غير مضبوط."),
            };
            _database.WriteCursor(CursorStream, cursor, null);
            _status.Update(false, message, pending);
            return;
        }

        var sync = new SaleOutboxSync(_database, new ErpPosClient(_options.BaseAddress, _options.SessionCookie, _http), _options);
        SyncTickResult result;
        try
        {
            result = await sync.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        var left = result.Pending >= 0 ? result.Pending : _database.CountPendingOutbox();
        var online = result.Code is "idle";
        var text = result.Code switch
        {
            "unauthorized" => "الخادم رد 401. المبيعات ما زالت على هذا الجهاز.",
            "transport" => "انقطع الرد قبل تأكيد القبول. البيع ما زال في صندوق الصادر.",
            "catalog-rejected" => "تعذر قراءة الأصناف من /api/products. لم يُقبل بيع.",
            "idle" when result.Accepted > 0 => "قُبلت مبيعات على الخادم دون تكرار محلي.",
            "idle" when left == 0 => "لا توجد مبيعات بانتظار الإرسال.",
            _ => "صندوق الصادر ينتظر رقم صنف من الـ ERP أو رداً مكتملاً.",
        };
        _database.WriteCursor(CursorStream, result.Code, result.Detail);
        _status.Update(online, text, left);
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

    public static Uri CheckoutUri(Uri baseAddress)
    {
        var text = baseAddress.ToString();
        if (!text.EndsWith('/'))
            text += "/";
        return new Uri(new Uri(text, UriKind.Absolute), "api/checkout");
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
