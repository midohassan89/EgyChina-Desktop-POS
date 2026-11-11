using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using Velopack;

namespace EgyChina.Pos.App.Services;

/// <summary>
/// Background Velopack check/download. Apply only when the till is idle (no open shift / tender).
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

    public void Start()
    {
        if (_loop is not null)
            return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await CheckAndDownloadAsync(cancellationToken).ConfigureAwait(false);
            TryApplyIfSafe(announce: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLog.Error("update check failed", ex);
        }
    }

    /// <summary>Call after Z-close when the till is idle.</summary>
    public bool TryApplyAfterShiftClose() =>
        TryApplyIfSafe(announce: true);

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        // First check shortly after startup (UI already up).
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            await CheckAndDownloadAsync(cancellationToken).ConfigureAwait(false);
            TryApplyIfSafe(announce: true);
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
                await CheckAndDownloadAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task CheckAndDownloadAsync(CancellationToken cancellationToken)
    {
        if (!VelopackRuntimeInfo.IsWindows)
        {
            PosLog.Info("update skipped — not a Windows Velopack install");
            return;
        }

        UpdateManager mgr;
        try
        {
            mgr = CreateManager();
        }
        catch (Exception ex)
        {
            PosLog.Error("update manager create failed", ex);
            return;
        }

        if (!mgr.IsInstalled)
        {
            PosLog.Info("update skipped — app is not an installed Velopack package (dev/portable)");
            return;
        }

        UpdateInfo? info;
        try
        {
            PosLog.Info($"update check start channel={_settings.Channel} feed={_settings.FeedUrl}");
            info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLog.Error("update check transport failed", ex);
            return;
        }

        if (info is null)
        {
            PosLog.Info("update check: no newer release");
            lock (_gate)
                _downloaded = null;
            return;
        }

        var target = info.TargetFullRelease?.Version?.ToString() ?? "(unknown)";
        PosLog.Info($"update available target={target}");

        try
        {
            await mgr.DownloadUpdatesAsync(info, cancelToken: cancellationToken).ConfigureAwait(false);
            lock (_gate)
                _downloaded = info;
            PosLog.Info($"update downloaded target={target}");
            _announce($"تم تنزيل تحديث {target} — سيُطبَّق بعد قفل الوردية.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLog.Error("update download failed", ex);
        }
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
            return false;
        }

        try
        {
            if (announce)
                _announce("يتوفر تحديث — سيتم إعادة تشغيل البرنامج.");
            PosLog.Info(
                $"applying update target={pending.TargetFullRelease?.Version}");
            var mgr = CreateManager();
            mgr.ApplyUpdatesAndRestart(pending.TargetFullRelease);
            return true;
        }
        catch (Exception ex)
        {
            PosLog.Error("update apply failed", ex);
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
