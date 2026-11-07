using EgyChina.Pos.Core;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.Sync;

/// <summary>
/// Background tick. Posts pending sales when device auth gates are set.
/// On start, requeues any <c>inflight</c> rows left by a crash. Never deletes a committed sale.
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

        // Crash mid-POST left rows as inflight — make them pending before the first tick.
        var recovered = _database.RecoverInflightOutbox();
        if (recovered > 0)
            PosLog.Info($"recovered {recovered} inflight outbox row(s) at startup");

        PublishLocalStatus();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => Loop(_cts.Token));
    }

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        // Catalog pull needs API + device token only. Sale transmission still needs money/payment gates.
        if (_http is null || _options.BaseAddress is null)
        {
            _database.WriteCursor(CursorStream, NotConfiguredCursor, null);
            PublishLocalStatus("البيع يعمل على الجهاز. لم يُضبط رابط سيرفر ERP (EGYCHINA_POS_API) لرفع المبيعات.", online: false);
            return;
        }

        // Staff PINs — public store route, throttled so checkout stubs/tests stay quiet.
        var staffCursor = _database.ReadCursor("staff");
        var staffDue = staffCursor?.LastSuccessAt is null
            || !DateTimeOffset.TryParse(staffCursor.LastSuccessAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var staffAt)
            || DateTimeOffset.UtcNow - staffAt > TimeSpan.FromMinutes(5);
        if (staffDue)
        {
            try
            {
                var staff = await LiveStaffSync.ApplyAsync(_database, _options.BaseAddress, _http, cancellationToken)
                    .ConfigureAwait(false);
                _database.WriteCursor("staff", staff.Ok ? "ok" : "error", staff.Error);
                if (staff.Ok && staff.Upserted > 0)
                    PosLog.Info($"staff sync upserted={staff.Upserted} deactivated={staff.Deactivated}");
            }
            catch (Exception ex)
            {
                PosLog.Error("staff sync tick failed", ex);
                _database.WriteCursor("staff", "error", ex.Message);
            }
        }

        // Optional device bearer (auto-enroll). Sales still upload via /api/store/pos-checkout without it.
        var deviceToken = !string.IsNullOrWhiteSpace(_options.DeviceToken)
            ? _options.DeviceToken
            : DeviceCredentialStore.Load();
        if (string.IsNullOrWhiteSpace(deviceToken))
        {
            try
            {
                var enroll = await DeviceEnrollmentClient.AutoEnrollAsync(
                    _options.BaseAddress,
                    _database.GetTerminalKey(),
                    "EgyChina Desktop POS",
                    cancellationToken).ConfigureAwait(false);
                deviceToken = enroll.Token;
            }
            catch (Exception ex)
            {
                PosLog.Error("device auto-enroll failed", ex);
            }
        }

        var effectiveOptions = new SyncOptions
        {
            BaseAddress = _options.BaseAddress,
            DeviceToken = deviceToken,
            PaymentMethod = _options.PaymentMethod,
            MoneyScale = _options.MoneyScale,
            CatalogPull = _options.CatalogPull,
            Interval = _options.Interval,
            HttpTimeout = _options.HttpTimeout,
        };

        var saleBlock = effectiveOptions.TransmissionBlock();
        var canTransmitSales = saleBlock is null;
        if (!canTransmitSales && !_options.CatalogPull)
        {
            var (cursor, message) = saleBlock switch
            {
                "money-scale-unverified" => ("money-scale-unverified", "وحدة المبلغ في /api/checkout غير مؤكدة. لا يُرسل بيع."),
                "payment-method-unverified" => ("payment-method-unverified", "اسم طريقة الدفع عند الـ ERP غير معروف. لا يُرسل بيع."),
                "demo-product-not-on-erp" => ("demo-product-not-on-erp", "بيع بأصناف تجريبية محلية لا تُرفع لسيرفر ERP."),
                _ => (NotConfiguredCursor, "البيع يعمل على الجهاز. لم يُضبط رابط سيرفر ERP لرفع المبيعات."),
            };
            _database.WriteCursor(CursorStream, cursor, null);
            PublishLocalStatus(message, online: false);
            return;
        }

        var sync = new SaleOutboxSync(
            _database,
            new ErpPosClient(_options.BaseAddress, deviceToken, _http),
            effectiveOptions,
            postSales: canTransmitSales);
        SyncTickResult result;
        try
        {
            result = await sync.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        var snap = result.Snapshot ?? _database.ReadOutboxSnapshot();
        var left = result.Pending >= 0 ? result.Pending : snap.Pending + snap.Inflight;
        var online = result.Code is "idle";
        var text = result.Code switch
        {
            "unauthorized" => "الخادم رد 401. المبيعات ما زالت على هذا الجهاز.",
            "transport" => "انقطع الرد قبل تأكيد القبول. البيع ما زال في صندوق الصادر.",
            "catalog-rejected" => "تعذر قراءة الأصناف من /api/products. لم يُقبل بيع.",
            "idle" when result.Accepted > 0 => "قُبلت مبيعات على الخادم دون تكرار محلي.",
            "idle" when result.MarkedDead > 0 => "بعض المبيعات معلّمة كتالفة محلياً؛ الإيصال محفوظ.",
            "idle" when !canTransmitSales && _options.CatalogPull =>
                "الكتالوج محدَّث. إرسال المبيعات ينتظر وحدة المبلغ وطريقة الدفع.",
            "idle" when left == 0 && snap.Dead == 0 =>
                _options.CatalogPull
                    ? "الكتالوج والمبيعات المحلية محدَّثان."
                    : "لا توجد مبيعات بانتظار الإرسال.",
            "idle" when left == 0 => "لا بانتظار إرسال. توجد قيود تالفة تحتاج مراجعة.",
            _ => "صندوق الصادر ينتظر رقم صنف من الـ ERP أو رداً مكتملاً.",
        };
        _database.WriteCursor(CursorStream, result.Code, result.Detail);
        _status.Update(online, text, left, snap.Dead, snap.LastError);
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

    private void PublishLocalStatus(string? message = null, bool online = false)
    {
        var snap = _database.ReadOutboxSnapshot();
        var pending = snap.Pending + snap.Inflight;
        var text = message
            ?? (pending > 0
                ? "صندوق الصادر يحتفظ بالمبيعات المكتملة محلياً."
                : "المزامنة المحلية جاهزة.");
        _status.Update(online, text, pending, snap.Dead, snap.LastError);
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
                    var snap = _database.ReadOutboxSnapshot();
                    _status.Update(
                        false,
                        "توقفت دورة المزامنة بسبب خطأ داخلي.",
                        snap.Pending + snap.Inflight,
                        snap.Dead,
                        snap.LastError ?? ex.Message);
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
