using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using EgyChina.Pos.App.Services;
using EgyChina.Pos.App.ViewModels;
using EgyChina.Pos.App.Views;
using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using EgyChina.Pos.Printing;
using EgyChina.Pos.Sync;

namespace EgyChina.Pos.App;

public partial class App : Application
{
    private PosDatabase? _database;
    private SyncWorker? _worker;
    private PosUpdateService? _updates;
    private PosSession? _session;
    private CustomerWindow? _customer;
    private bool _cashierPlaced;
    private int _placeAttempts;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            try
            {
                StartShell(desktop);
            }
            catch (Exception ex)
            {
                PosLog.Error("startup failed", ex);
                desktop.MainWindow = new Window
                {
                    Title = "ايجي شاينا — تعذر التشغيل",
                    Width = 720,
                    Height = 420,
                    FlowDirection = FlowDirection.RightToLeft,
                    Content = new TextBlock
                    {
                        Margin = new Thickness(28),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 18,
                        Text = "تعذر تجهيز قاعدة البيانات المحلية.\n\n" + ex.Message,
                    },
                };
            }

            desktop.Exit += (_, _) =>
            {
                if (_updates is not null)
                    _updates.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (_worker is not null)
                    _worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void StartShell(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var root = PosPaths.Root();
        Directory.CreateDirectory(root);
        PosLog.SetDirectory(PosPaths.LogDirectory());
        PosLog.Info($"problems log path={PosPaths.ProblemsLogFile()}");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                PosLog.Error("unhandled exception", ex);
            else
                PosLog.Error("unhandled exception: " + args.ExceptionObject);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            PosLog.Error("unobserved task exception", args.Exception);
            args.SetObserved();
        };
        try
        {
            PosPaths.EnsureUiAssets();
            var logo = BrandLogo.Bitmap;
            if (logo is not null)
                Resources["BrandLogo"] = logo;
            PosLog.Info(
                $"ui assets logo={(logo is null ? "missing" : "ok")} wechat={(File.Exists(PosPaths.WeChatQrFile()) ? "ok" : "missing")} instapay={(File.Exists(PosPaths.InstaPayQrFile()) ? "ok" : "missing")}");
        }
        catch (Exception ex)
        {
            PosLog.Error("ui asset seed failed", ex);
        }

        _database = new PosDatabase(PosPaths.DatabaseFile());
        // Migrations must never run without a snapshot of the previous schema.
        if (File.Exists(_database.DatabasePath) && _database.HasPendingMigrations())
        {
            var preMigrate = DatabaseBackup.Create(_database);
            if (preMigrate.Created)
                PosLog.Info($"pre-migration backup path={preMigrate.Path}");
            else
                PosLog.Error($"pre-migration backup skipped: {LogRedactor.Redact(preMigrate.Error)}");
        }

        _database.Initialize();
        var products = _database.ListActiveProducts();
        var demo = _database.GetSetting("catalog.source") == "demo";
        PosLog.Info(
            $"database ready path={_database.DatabasePath} schema={_database.GetSchemaVersion()} products={products.Count} source={(demo ? "demo" : "other")}");

        var backup = DatabaseBackup.Create(_database);
        if (!backup.Created)
            PosLog.Error($"startup backup skipped: {LogRedactor.Redact(backup.Error)}");

        var status = new SyncStatus();
        var printerOptions = PrinterOptions.FromEnvironment();
        IReceiptPrinter printer;
        try
        {
            printer = EscPosReceiptPrinter.Create(printerOptions);
            PosLog.Info($"printer transport={printerOptions.Transport} configured={printer.IsConfigured}");
        }
        catch (Exception ex)
        {
            PosLog.Error("printer setup failed — sales still work offline", ex);
            printer = new NullReceiptPrinter();
        }

        var syncOptions = SyncOptions.FromEnvironment();
        var erpGateway = PosErpGateway.FromOptions(syncOptions);
        _session = new PosSession(_database, products, status, demo, printer, erpGateway, syncOptions);
        _session.RequestCustomerDisplay = ShowCustomer;
        _session.RequestToggleCashierKiosk = ToggleCashierKiosk;
        var disk = DiskSpaceGuard.Check(_database.DatabasePath);
        if (disk.HasWarning)
        {
            _session.ApplyDiskStatus(disk.BlockSales, disk.Message);
            PosLog.Info($"disk free={disk.FreeBytes} blockSales={disk.BlockSales}");
        }

        _worker = new SyncWorker(_database, status, syncOptions);
        _session.BindSyncWorker(_worker);
        _worker.Start();
        StartProductImageWarmup(_database, _session, syncOptions.BaseAddress);

        var updateSettings = PosUpdateSettings.Load(root);
        PosLog.Info($"update feed channel={updateSettings.Channel} url={updateSettings.FeedUrl}");
        _updates = new PosUpdateService(
            updateSettings,
            canApplyNow: () => _session?.CanApplyAppUpdate == true,
            announce: message =>
            {
                try
                {
                    Dispatcher.UIThread.Post(() => _session?.AnnounceUpdateHint(message));
                }
                catch (Exception ex)
                {
                    PosLog.Error("update announce failed", ex);
                }
            });
        _session.BindUpdateService(_updates);
        _updates.Start();

        var cashier = new CashierWindow { DataContext = _session };
        cashier.Opened += (_, _) => PlaceWindows(moveCashier: !_cashierPlaced);

        desktop.MainWindow = cashier;
        ShowCustomer();

        var version = ReadAppVersion();
        _session.SetAppVersion(version);
        try
        {
            AppHeartbeat.Write(root, version);
            PosLog.Info($"heartbeat written version={version}");
        }
        catch (Exception ex)
        {
            PosLog.Error("heartbeat write failed", ex);
        }
    }

    private static void StartProductImageWarmup(PosDatabase database, PosSession session, Uri? mediaBase)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var origin = mediaBase ?? ProductImageCache.DefaultMediaBase();
                using (var liveHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                {
                    liveHttp.DefaultRequestHeaders.UserAgent.ParseAdd("EgyChina-POS/0.1-live-catalog");
                    var live = await LiveStoreCatalogSync.ApplyAsync(database, origin, liveHttp, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (live.Ok)
                        database.WriteCursor(SyncWorker.LiveCatalogCursorStream, "ok", null);
                    PosLog.Info(
                        live.Ok
                            ? $"live catalog pages={live.Pages} seen={live.Seen} upserted={live.Upserted} deactivated={live.Deactivated}"
                            : $"live catalog failed error={live.Error}");

                    var staff = await LiveStaffSync.ApplyAsync(database, origin, liveHttp, CancellationToken.None)
                        .ConfigureAwait(false);
                    PosLog.Info(
                        staff.Ok
                            ? $"live staff upserted={staff.Upserted} deactivated={staff.Deactivated}"
                            : $"live staff failed error={staff.Error}");

                    var enroll = await DeviceEnrollmentClient.AutoEnrollAsync(
                        origin,
                        database.GetTerminalKey(),
                        "EgyChina Desktop POS",
                        CancellationToken.None).ConfigureAwait(false);
                    PosLog.Info(
                        enroll.Ok
                            ? "device auto-enroll ok"
                            : $"device auto-enroll failed error={enroll.Error}");
                }

                await Dispatcher.UIThread.InvokeAsync(session.ReloadCatalogFromDatabase);
                await Dispatcher.UIThread.InvokeAsync(session.NotifyStaffSyncCompleted);

                var items = database.ListProductsWithImageUrls();
                var pending = items
                    .Where(item => ProductImageCache.TryLocalPath(item.ProductId) is null)
                    .ToList();
                if (pending.Count == 0)
                {
                    PosLog.Info("product images already cached");
                    await Dispatcher.UIThread.InvokeAsync(session.NotifyCatalogVisualRefresh);
                    return;
                }

                PosLog.Info($"product image warmup start pending={pending.Count}");
                var lastUi = 0;
                var progress = new Progress<int>(count =>
                {
                    var previous = Volatile.Read(ref lastUi);
                    if (count - previous < 40)
                        return;
                    Interlocked.Exchange(ref lastUi, count);
                    Dispatcher.UIThread.Post(session.NotifyCatalogVisualRefresh, DispatcherPriority.Background);
                });

                var result = await ProductImageCache.WarmAsync(
                    pending,
                    origin,
                    maxDownloads: pending.Count,
                    parallelism: 8,
                    progress,
                    CancellationToken.None).ConfigureAwait(false);
                PosLog.Info(
                    $"product image warmup done attempted={result.Attempted} warmed={result.Warmed} failed={result.Failed}");
                await Dispatcher.UIThread.InvokeAsync(session.NotifyCatalogVisualRefresh);
            }
            catch (Exception ex)
            {
                PosLog.Error("product image warmup failed", ex);
            }
        });
    }

    private static string ReadAppVersion()
    {
        var assembly = typeof(App).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational.Trim();
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private void ShowCustomer()
    {
        if (_session is null)
            return;

        if (_customer is { IsVisible: true })
        {
            _customer.Activate();
            return;
        }

        _customer = new CustomerWindow
        {
            DataContext = _session,
            ShowActivated = false,
        };
        _customer.Opened += (_, _) => PlaceWindows(moveCashier: false);
        _customer.Closed += (_, _) => _customer = null;
        _customer.Show();
    }

    private void ToggleCashierKiosk()
    {
        var cashier = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (cashier is null)
            return;
        ScreenLayout.ToggleCashierKiosk(cashier);
        if (_session is not null)
        {
            _session.ScreenModeText = ScreenLayout.CashierKiosk
                ? "كشك — ملء الشاشة (بدون شريط ويندوز)"
                : "نافذة — شريط ويندوز ظاهر";
        }
    }

    private void PlaceWindows(bool moveCashier)
    {
        if (_session is null)
            return;

        var cashier = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (cashier is null || _customer is null)
            return;

        var placement = ScreenLayout.Place(cashier, _customer, moveCashier);
        if (placement.ScreenCount == 0)
        {
            if (++_placeAttempts > 8)
            {
                _session.ScreenModeText = "تعذر قراءة الشاشات";
                return;
            }

            Dispatcher.UIThread.Post(() => PlaceWindows(moveCashier), DispatcherPriority.Background);
            return;
        }

        if (moveCashier)
            _cashierPlaced = true;

        var kioskLabel = ScreenLayout.CashierKiosk ? "كشك ملء الشاشة" : "نافذة (شريط ويندوز ظاهر)";
        _session.ScreenModeText = placement.CustomerOnSecondary
            ? $"شاشتان — شاشة العميل على الثانية · {kioskLabel}"
            : $"شاشة واحدة — نافذة العميل بجانب الكاشير · {kioskLabel}";
        // Never show placement/debug banners on the customer-facing display.
        _session.DisplayBanner = string.Empty;
    }
}
