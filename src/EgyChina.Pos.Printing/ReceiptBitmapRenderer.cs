using System.Globalization;
using EgyChina.Pos.Core;
using SkiaSharp;

namespace EgyChina.Pos.Printing;

/// <summary>
/// Renders an Arabic-first receipt as a 576-dot monochrome bitmap for XP-P323B.
/// </summary>
public static class ReceiptBitmapRenderer
{
    public const int Width = EscPosRasterEncoder.PageWidthDots;
    private const float Right = 556f;
    private const float Left = 24f;

    public static SKBitmap Render(ReceiptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Draw(BuildLayout(document));
    }

    public static SKBitmap RenderZReport(ZReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Draw(BuildZReportLayout(document));
    }

    private static SKBitmap Draw(Layout lines)
    {
        var typeface = ResolveTypeface();
        var height = Math.Max(120, (int)Math.Ceiling(lines.TotalHeight) + 24);
        var bitmap = new SKBitmap(Width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black,
        };
        using var font = new SKFont(typeface, 22f);

        var y = 20f;
        foreach (var op in lines.Ops)
        {
            font.Size = op.Size;
            font.Embolden = op.Bold;
            if (op.Kind == LayoutKind.Rule)
            {
                using var rule = new SKPaint { Color = SKColors.Black, StrokeWidth = 1.5f, IsAntialias = true };
                canvas.DrawLine(Left, y + 4f, Right, y + 4f, rule);
                y += op.Advance;
                continue;
            }

            if (op.Kind == LayoutKind.LeftRight)
            {
                canvas.DrawText(op.Left ?? string.Empty, Left, y + op.Size, SKTextAlign.Left, font, paint);
                canvas.DrawText(op.Text, Right, y + op.Size, SKTextAlign.Right, font, paint);
                y += op.Advance;
                continue;
            }

            canvas.DrawText(op.Text, Right, y + op.Size, SKTextAlign.Right, font, paint);
            y += op.Advance;
        }

        return bitmap;
    }

    private static Layout BuildLayout(ReceiptDocument document)
    {
        var ops = new List<LayoutOp>();
        void Title(string text, float size = 28f, bool bold = true) =>
            ops.Add(new LayoutOp(LayoutKind.Text, text, null, size, bold, size + 10f));
        void Body(string text, float size = 22f) =>
            ops.Add(new LayoutOp(LayoutKind.Text, text, null, size, false, size + 8f));
        void Pair(string right, string left, float size = 22f) =>
            ops.Add(new LayoutOp(LayoutKind.LeftRight, right, left, size, false, size + 8f));
        void Rule() => ops.Add(new LayoutOp(LayoutKind.Rule, string.Empty, null, 0, false, 14f));

        if (document.IsReprint)
            Title("*** إعادة طباعة ***", 24f);

        Title(document.StoreNameAr, 30f);
        if (!string.IsNullOrWhiteSpace(document.StoreNameZh))
            Body(document.StoreNameZh, 20f);

        Rule();
        Pair($"إيصال {document.ReceiptNo.ToString(CultureInfo.InvariantCulture)}",
            document.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        Body(document.CashierName, 20f);
        Rule();

        foreach (var line in document.Lines)
        {
            var qty = (line.QtyMilli / 1000m).ToString("0.###", CultureInfo.InvariantCulture);
            Body(line.NameAr, 22f);
            if (!string.IsNullOrWhiteSpace(line.NameZh))
                Body(line.NameZh!, 18f);
            Pair(
                Money.FormatAmount(line.LineTotalMinor),
                $"{qty} × {Money.FormatAmount(line.UnitPriceMinor)}");
        }

        Rule();
        Title($"الإجمالي  {Money.FormatAmount(document.TotalMinor)}", 26f);
        Pair("المدفوع", Money.FormatAmount(document.TenderMinor));
        Pair("الباقي", Money.FormatAmount(document.ChangeMinor));
        Rule();
        Body(document.FooterAr, 18f);
        Body("شكراً لتسوقكم", 20f);

        var total = ops.Sum(op => op.Advance) + 8f;
        return new Layout(ops, total);
    }

    private static Layout BuildZReportLayout(ZReportDocument document)
    {
        var ops = new List<LayoutOp>();
        void Title(string text, float size = 28f, bool bold = true) =>
            ops.Add(new LayoutOp(LayoutKind.Text, text, null, size, bold, size + 10f));
        void Body(string text, float size = 22f) =>
            ops.Add(new LayoutOp(LayoutKind.Text, text, null, size, false, size + 8f));
        void Pair(string right, string left, float size = 22f, bool bold = false) =>
            ops.Add(new LayoutOp(LayoutKind.LeftRight, right, left, size, bold, size + 8f));
        void Rule() => ops.Add(new LayoutOp(LayoutKind.Rule, string.Empty, null, 0, false, 14f));

        Title(document.StoreNameAr, 30f);
        if (!string.IsNullOrWhiteSpace(document.StoreNameZh))
            Body(document.StoreNameZh, 20f);
        Title("تقرير Z · إغلاق وردية", 26f);
        Rule();

        var openedLocal = DateTimeOffset.TryParse(
            document.ShiftOpenedAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var opened)
            ? opened.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : document.ShiftOpenedAt;
        var closedLocal = document.ClosedAt.ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        Pair("الكاشير", document.CashierName, 20f);
        Pair("فتح الوردية", openedLocal, 18f);
        Pair("إغلاق الوردية", closedLocal, 18f);
        Rule();

        Pair("عدد التذاكر", document.TicketCount.ToString(CultureInfo.InvariantCulture));
        Pair("استرجاع", document.ReturnTicketCount.ToString(CultureInfo.InvariantCulture));
        Rule();

        Title("المبيعات حسب الدفع", 22f);
        Pair("نقد", Money.FormatAmount(document.CashSalesMinor));
        Pair("فيزا", Money.FormatAmount(document.VisaSalesMinor));
        Pair("محفظة", Money.FormatAmount(document.WalletSalesMinor));
        Pair("انستا باي", Money.FormatAmount(document.InstaPaySalesMinor));
        Pair("وي شات", Money.FormatAmount(document.WeChatSalesMinor));
        Pair("مردود نقدي", Money.FormatAmount(document.CashRefundsMinor));
        Rule();
        Title($"إجمالي المبيعات  {Money.FormatAmount(document.TotalSalesMinor)}", 24f);
        Rule();

        Pair("عهدة الافتتاح", Money.FormatAmount(document.OpeningFloatMinor));
        Pair("النقد المتوقع", Money.FormatAmount(document.ExpectedCashMinor), 22f, true);
        Pair("نقدية الإغلاق", Money.FormatAmount(document.ActualClosingCashMinor), 22f, true);
        Pair("الفرق", Money.FormatAmount(document.VarianceMinor), 24f, true);
        Rule();
        Body("نهاية تقرير الوردية", 18f);

        var total = ops.Sum(op => op.Advance) + 8f;
        return new Layout(ops, total);
    }

    private static SKTypeface ResolveTypeface()
    {
        foreach (var family in new[] { "Segoe UI", "Tahoma", "Arial", "Microsoft YaHei", "Noto Sans Arabic" })
        {
            var face = SKTypeface.FromFamilyName(family, SKFontStyle.Normal);
            if (face is not null && !string.Equals(face.FamilyName, "sans-serif", StringComparison.OrdinalIgnoreCase))
                return face;
        }

        return SKTypeface.Default;
    }

    private enum LayoutKind
    {
        Text,
        LeftRight,
        Rule,
    }

    private sealed record LayoutOp(
        LayoutKind Kind,
        string Text,
        string? Left,
        float Size,
        bool Bold,
        float Advance);

    private sealed record Layout(IReadOnlyList<LayoutOp> Ops, float TotalHeight);
}
