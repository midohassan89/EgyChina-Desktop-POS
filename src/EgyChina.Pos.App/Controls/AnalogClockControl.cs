using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using EgyChina.Pos.Core;

namespace EgyChina.Pos.App.Controls;

/// <summary>
/// Idle CFD analog clock: glassy dial, white hour/minute hands, orange second hand,
/// numerals rotate Arabic / Latin / Chinese with the idle greeting.
/// Forced LTR so the RTL customer window does not mirror the dial.
/// Owns a ~30fps timer so the second hand sweeps continuously.
/// </summary>
public sealed class AnalogClockControl : Control
{
    public static readonly StyledProperty<CustomerClockScript> ScriptProperty =
        AvaloniaProperty.Register<AnalogClockControl, CustomerClockScript>(nameof(Script), CustomerClockScript.Arabic);

    private static readonly IBrush FaceFill = new SolidColorBrush(Color.FromArgb(0x55, 0xEC, 0xFD, 0xF5));
    private static readonly IBrush FaceStroke = new SolidColorBrush(Color.FromArgb(0xCC, 0xCB, 0xD5, 0xE1));
    private static readonly IBrush TickBrush = Brushes.White;
    private static readonly IBrush HandBrush = Brushes.White;
    private static readonly IBrush SecondBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush NumeralBrush = Brushes.White;

    private readonly DispatcherTimer _sweep;
    private DateTime _now = DateTime.Now;

    static AnalogClockControl()
    {
        AffectsRender<AnalogClockControl>(ScriptProperty);
    }

    public AnalogClockControl()
    {
        // Parent CFD window is RTL; without this the dial is mirrored and hands run anti-clockwise.
        FlowDirection = FlowDirection.LeftToRight;
        _sweep = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _sweep.Tick += OnSweepTick;
        AttachedToVisualTree += (_, _) =>
        {
            _now = DateTime.Now;
            _sweep.Start();
            InvalidateVisual();
        };
        DetachedFromVisualTree += (_, _) => _sweep.Stop();
    }

    public CustomerClockScript Script
    {
        get => GetValue(ScriptProperty);
        set => SetValue(ScriptProperty, value);
    }

    private void OnSweepTick(object? sender, EventArgs e)
    {
        _now = DateTime.Now;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 8 || height < 8)
            return;

        var size = Math.Min(width, height);
        var center = new Point(width / 2d, height / 2d);
        var radius = size / 2d - 2d;

        context.DrawEllipse(FaceFill, new Pen(FaceStroke, Math.Max(2d, radius * 0.018)), center, radius, radius);

        var tickOuter = radius * 0.92;
        var tickInner = radius * 0.84;
        var numeralRadius = radius * 0.70;
        var tickPen = new Pen(TickBrush, Math.Max(1.5d, radius * 0.012), lineCap: PenLineCap.Round);

        for (var hour = 1; hour <= 12; hour++)
        {
            var angle = hour * 30d - 90d;
            var rad = angle * Math.PI / 180d;
            var cos = Math.Cos(rad);
            var sin = Math.Sin(rad);
            context.DrawLine(
                tickPen,
                new Point(center.X + cos * tickInner, center.Y + sin * tickInner),
                new Point(center.X + cos * tickOuter, center.Y + sin * tickOuter));

            var label = CustomerAnalogClock.HourLabel(hour, Script);
            var fontSize = Script == CustomerClockScript.Chinese
                ? radius * 0.13
                : radius * 0.15;
            var formatted = new FormattedText(
                label,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
                fontSize,
                NumeralBrush);
            var textPos = new Point(
                center.X + cos * numeralRadius - formatted.Width / 2d,
                center.Y + sin * numeralRadius - formatted.Height / 2d);
            context.DrawText(formatted, textPos);
        }

        var (hourDeg, minuteDeg, secondDeg) = CustomerAnalogClock.HandAngles(_now);
        DrawHand(context, center, hourDeg, radius * 0.48, Math.Max(4d, radius * 0.045), HandBrush);
        DrawHand(context, center, minuteDeg, radius * 0.68, Math.Max(3d, radius * 0.028), HandBrush);
        DrawHand(context, center, secondDeg, radius * 0.78, Math.Max(1.5d, radius * 0.012), SecondBrush, tail: radius * 0.14);

        var hub = Math.Max(4d, radius * 0.045);
        context.DrawEllipse(SecondBrush, null, center, hub, hub);
        context.DrawEllipse(HandBrush, null, center, hub * 0.35, hub * 0.35);
    }

    private static void DrawHand(
        DrawingContext context,
        Point center,
        double degreesFrom12,
        double length,
        double thickness,
        IBrush brush,
        double tail = 0)
    {
        var rad = (degreesFrom12 - 90d) * Math.PI / 180d;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var tip = new Point(center.X + cos * length, center.Y + sin * length);
        var back = tail > 0
            ? new Point(center.X - cos * tail, center.Y - sin * tail)
            : center;
        context.DrawLine(new Pen(brush, thickness, lineCap: PenLineCap.Round), back, tip);
    }
}
