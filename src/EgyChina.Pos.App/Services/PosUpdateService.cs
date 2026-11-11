using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using Velopack;

namespace EgyChina.Pos.App.Services;

public enum UpdateCheckStatus
{
    NotApplicable,
    UpToDate,
    DownloadedDeferred,
    DownloadedApplied,
    Failed,
    Cancelled,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string Message,
    string? TargetVersion = null);

/// <summary>
/// Background Velopack check/download. Apply only when the till is idle (no open shift / tender).
/// Open shift blocks apply — not check/download.
/// </summary>
public sealed class PosUpdateService : IAsyncDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private readonly PosUpdateSettings _settings;
    private readonly Func<bool> _canApplyNow;
    private readonly Action<string> _announce;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private UpdateInfo? _downloaded;
    private int _lastAnnouncedProgress = -1;

    public PosUpdateService(
        PosUpdateSettings settings,
        Func<bool> canApplyNow,
        Action<string> announce)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _canApplyNow = canApplyNow ?? throw new ArgumentNullException(nameof(canApplyNow));
        _announce = announce ?? throw new ArgumentNullException(nameof(announce));
    }

    public string Channel => _settings.Channel;

    public Uri FeedUrl => _settings.FeedUrl;

    public bool HasPendingUpdate
    {
        get
        {
            lock (_gate)
                return _downloaded is not null;
        }
    }

    public string? PendingVersion
    {
        get
        {
            lock (_gate)
                return _downloaded?.TargetFullRelease?.Version?.ToString();
        }
    }

    public void Start()
    {
        if (_loop is not null)
            return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task<UpdateCheckResult> CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await CheckAndDownloadAsync(cancellationToken).ConfigureAwait(false);
            if (result.Status is UpdateCheckStatus.DownloadedDeferred or UpdateCheckStatus.DownloadedApplied)
            {
                if (TryApplyIfSafe(announce: true))
                {
                    return new UpdateCheckResult(
                        UpdateCheckStatus.DownloadedApplied,
                        "يتوفر تحديث — سيتم إعادة تشغيل البرنامج.",
                        result.TargetVersion);
                }

                return new UpdateCheckResult(
                    UpdateCheckStatus.DownloadedDeferred,
                    BuildDeferredMessage(result.TargetVersion),
                    result.TargetVersion);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Cancelled, "تم إلغاء التحقق من التحديثات.");
        }
        catch (Exception ex)
        {
            PosLog.Error("update check failed", ex);
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                "تعذر التحقق من التحديثات. تحقق من الاتصال.");
        }
    }

    /// <summary>Call after Z-close when the till is idle.</summary>
    public bool TryApplyAfterShiftClose() =>
        TryApplyIfSafe(announce: true);

    /// <summary>Manual apply from the open-shift gate (no need to open a new shift).</summary>
    public bool TryApplyNow() =>
        TryApplyIfSafe(announce: true);

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            var startup = await CheckAndDownloadAsync(cancellationToken).ConfigureAwait(false);
            if (startup.Status is UpdateCheckStatus.DownloadedDeferred or UpdateCheckStatus.DownloadedApplied)
                TryApplyIfSafe(announce: true);
            else if (startup.Status == UpdateCheckStatus.Failed)
                PosLog.Info($"startup update: {startup.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            PosLog.Error("startup update check failed", ex);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, cancellationToken).ConfigureAwait(false);
                var hourly = await CheckAndDownloadAsync(cancellationToken).ConfigureAwait(false);
                if (hourly.Status is UpdateCheckStatus.DownloadedDeferred or UpdateCheckStatus.DownloadedApplied)
                    TryApplyIfSafe(announce: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                PosLog.Error("hourly update check failed", ex);
            }
        }
    }

    private async Task<UpdateCheckResult> CheckAndDownloadAsync(CancellationToken cancellationToken)
    {
        if (!VelopackRuntimeInfo.IsWindows)
        {
            PosLog.Info("update skipped — not a Windows Velopack install");
            return new UpdateCheckResult(
                UpdateCheckStatus.NotApplicable,
                "التحديث التلقائي متاح على ويندوز فقط.");
        }

        UpdateManager mgr;
        try
        {
            mgr = CreateManager();
        }
        catch (Exception ex)
        {
            PosLog.Error("update manager create failed", ex);
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                "تعذر تهيئة خدمة التحديث.");
        }

        if (!mgr.IsInstalled)
        {
            PosLog.Info("update skipped — app is not an installed Velopack package (dev/portable)");
            return new UpdateCheckResult(
                UpdateCheckStatus.NotApplicable,
                "ثبّت البرنامج من Setup.exe عشان يشتغل التحديث التلقائي.");
        }

        UpdateInfo? info;
        try
        {
            PosLog.Info($"update check start channel={_settings.Channel} feed={_settings.FeedUrl}");
            _announce("جاري التحقق من وجود تحديثات…");
            info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLog.Error("update check transport failed", ex);
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                "تعذر الوصول لخادم التحديثات. تحقق من الإنترنت.");
        }

        if (info is null)
        {
            PosLog.Info("update check: no newer release");
            lock (_gate)
                _downloaded = null;
            return new UpdateCheckResult(
                UpdateCheckStatus.UpToDate,
                "أنت على أحدث نسخة حالياً.");
        }

        var target = info.TargetFullRelease?.Version?.ToString() ?? "(unknown)";
        PosLog.Info($"update available target={target}");
        _lastAnnouncedProgress = -1;
        _announce($"جاري تنزيل التحديث {target}…");

        try
        {
            await mgr.DownloadUpdatesAsync(
                    info,
                    progress =>
                    {
                        if (progress < 0 || progress > 100)
                            return;
                        if (progress == _lastAnnouncedProgress)
                            return;
                        if (progress is not (0 or 100) && progress - _lastAnnouncedProgress < 10)
                            return;
                        _lastAnnouncedProgress = progress;
                        _announce($"جاري تنزيل التحديث {target}… {progress}%");
                    },
                    cancelToken: cancellationToken)
                .ConfigureAwait(false);
            lock (_gate)
                _downloaded = info;
            PosLog.Info($"update downloaded target={target}");

            if (_canApplyNow())
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.DownloadedApplied,
                    $"تم تنزيل التحديث {target} — جاري التطبيق…",
                    target);
            }

            var deferred = BuildDeferredMessage(target);
            _announce(deferred);
            return new UpdateCheckResult(
                UpdateCheckStatus.DownloadedDeferred,
                deferred,
                target);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLog.Error("update download failed", ex);
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                $"تعذر تنزيل التحديث {target}. حاول مرة أخرى.");
        }
    }

    private static string BuildDeferredMessage(string? targetVersion)
    {
        var ver = string.IsNullOrWhiteSpace(targetVersion) ? "" : $" {targetVersion}";
        return $"تم تنزيل التحديث{ver}. أقفل الوردية ثم اضغط «تطبيق التحديث» — من غير ما تفتح وردية جديدة.";
    }

    private bool TryApplyIfSafe(bool announce)
    {
        UpdateInfo? pending;
        lock (_gate)
            pending = _downloaded;

        if (pending is null)
        {
            try
            {
                var mgrPeek = CreateManager();
                if (mgrPeek.UpdatePendingRestart is not null && _canApplyNow())
                {
                    if (announce)
                        _announce("يتوفر تحديث — سيتم إعادة تشغيل البرنامج.");
                    PosLog.Info("applying pending UpdatePendingRestart package");
                    mgrPeek.ApplyUpdatesAndRestart(mgrPeek.UpdatePendingRestart);
                    return true;
                }
            }
            catch (Exception ex)
            {
                PosLog.Error("pending restart apply failed", ex);
            }

            return false;
        }

        if (!_canApplyNow())
        {
            PosLog.Info("update ready but till busy — defer apply");
            if (announce)
                _announce(BuildDeferredMessage(pending.TargetFullRelease?.Version?.ToString()));
            return false;
        }

        try
        {
            if (announce)
                _announce("يتوفر تحديث — سيتم إعادة تشغيل البرنامج.");
            PosLog.Info(
                $"applying update target={pending.TargetFullRelease?.Version}");
            var mgr = CreateManager();
            mgr.ApplyUpdatesAndRestart(pending);
            return true;
        }
        catch (Exception ex)
        {
            PosLog.Error("update apply failed", ex);
            if (announce)
                _announce("تعذر تطبيق التحديث. أعد المحاولة بعد قفل الوردية.");
            return false;
        }
    }

    private UpdateManager CreateManager() =>
        new(
            _settings.FeedUrl.ToString(),
            new UpdateOptions
            {
                ExplicitChannel = _settings.Channel,
                AllowVersionDowngrade = false,
            });

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }
}
