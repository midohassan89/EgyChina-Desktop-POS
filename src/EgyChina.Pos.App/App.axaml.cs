using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
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

        _database = new PosDatabase(PosPaths.DatabaseFile());
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

        _session = new PosSession(_database, products, status, demo, printer);
        _session.RequestCustomerDisplay = ShowCustomer;
        var disk = DiskSpaceGuard.Check(_database.DatabasePath);
        if (disk.HasWarning)
        {
            _session.ApplyDiskStatus(disk.BlockSales, disk.Message);
            PosLog.Info($"disk free={disk.FreeBytes} blockSales={disk.BlockSales}");
        }

        _worker = new SyncWorker(_database, status, SyncOptions.FromEnvironment());
        _worker.Start();

        var cashier = new CashierWindow { DataContext = _session };
        cashier.Opened += (_, _) => PlaceWindows(moveCashier: !_cashierPlaced);

        desktop.MainWindow = cashier;
        ShowCustomer();
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

        _session.ScreenModeText = placement.CustomerOnSecondary
            ? "شاشتان — شاشة العميل على الشاشة الثانية"
            : "شاشة واحدة — نافذة العميل بجانب الكاشير";
        _session.DisplayBanner = placement.CustomerOnSecondary
            ? string.Empty
            : "وضع التطوير: شاشة واحدة. على جهاز الكاشير تُعرض هذه النافذة على الشاشة الثانية.";
    }
}
