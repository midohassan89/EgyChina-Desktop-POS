using System.Globalization;
using EgyChina.Pos.Core;
using QRCoder;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace EgyChina.Pos.Printing;

/// <summary>
/// Renders a professional Arabic/Chinese thermal receipt as a 576-dot bitmap for XP-P323B.
/// Uses HarfBuzz shaping so Arabic joins correctly (never code-page text).
/// </summary>
public static class ReceiptBitmapRenderer
{
    public const int Width = EscPosRasterEncoder.PageWidthDots;
    private const float Right = 556f;
    private const float Left = 24f;
    private const float Center = Width / 2f;
    private const float MaxTextWidth = Right - Left;

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

    private static SKBitmap Draw(Layout layout)
    {
        var arabic = ResolveArabicTypeface();
        var cjk = ResolveCjkTypeface();
        var height = Math.Max(160, (int)Math.Ceiling(layout.TotalHeight) + 28);
        var bitmap = new SKBitmap(Width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.Black, IsStroke = false };
        using var arabicFont = new SKFont(arabic, 22f);
        using var cjkFont = new SKFont(cjk, 22f);

        var y = 16f;
        foreach (var op in layout.Ops)
        {
            switch (op.Kind)
            {
                case LayoutKind.Rule:
                {
                    using var rule = new SKPaint { Color = SKColors.Black, StrokeWidth = 1.8f, IsAntialias = true };
                    canvas.DrawLine(Left, y + 5f, Right, y + 5f, rule);
                    y += op.Advance;
                    break;
                }
                case LayoutKind.Dotted:
                {
                    using var rule = new SKPaint
                    {
                        Color = SKColors.Black,
                        StrokeWidth = 1.4f,
                        IsAntialias = true,
                        PathEffect = SKPathEffect.CreateDash([4f, 4f], 0f),
                    };
                    canvas.DrawLine(Left, y + 4f, Right, y + 4f, rule);
                    y += op.Advance;
                    break;
                }
                case LayoutKind.Logo:
                {
                    y += DrawLogo(canvas, op.LogoPath, y, op.Advance);
                    break;
                }
                case LayoutKind.Qr:
                {
                    y += DrawQr(canvas, op.Text, (int)op.Size, y);
                    break;
                }
                case LayoutKind.LeftRight:
                {
                    var font = PickFont(op.Text, arabicFont, cjkFont);
                    font.Size = op.Size;
                    font.Embolden = op.Bold;
                    DrawShaped(canvas, op.Text, Right, y + op.Size, SKTextAlign.Right, font, paint);
                    if (!string.IsNullOrEmpty(op.Left))
                    {
                        var leftFont = PickFont(op.Left, arabicFont, cjkFont);
                        leftFont.Size = op.Size;
                        leftFont.Embolden = op.Bold;
                        DrawShaped(canvas, op.Left, Left, y + op.Size, SKTextAlign.Left, leftFont, paint);
                    }

                    y += op.Advance;
                    break;
                }
                case LayoutKind.Center:
                {
                    var font = PickFont(op.Text, arabicFont, cjkFont);
                    font.Size = op.Size;
                    font.Embolden = op.Bold;
                    DrawShaped(canvas, op.Text, Center, y + op.Size, SKTextAlign.Center, font, paint);
                    y += op.Advance;
                    break;
                }
                default:
                {
                    var font = PickFont(op.Text, arabicFont, cjkFont);
                    font.Size = op.Size;
                    font.Embolden = op.Bold;
                    DrawShaped(canvas, op.Text, Right, y + op.Size, SKTextAlign.Right, font, paint);
                    y += op.Advance;
                    break;
                }
            }
        }

        return bitmap;
    }

    private static void DrawShaped(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        SKTextAlign align,
        SKFont font,
        SKPaint paint)
    {
        if (string.IsNullOrEmpty(text))
            return;
        // HarfBuzz shapes Arabic joining + RTL; CJK/Latin pass through cleanly.
        canvas.DrawShapedText(text, x, y, align, font, paint);
    }

    private static SKFont PickFont(string text, SKFont arabic, SKFont cjk) =>
        ContainsCjk(text) ? cjk : arabic;

    private static bool ContainsCjk(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= '\u3400' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF')
                return true;
        }

        return false;
    }

    private static float DrawLogo(SKCanvas canvas, string? path, float y, float reserved)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return reserved;

        try
        {
            using var logo = SKBitmap.Decode(path);
            if (logo is null || logo.Width < 2 || logo.Height < 2)
                return reserved;

            var maxW = 280f;
            var maxH = Math.Max(48f, reserved - 8f);
            var scale = Math.Min(maxW / logo.Width, maxH / logo.Height);
            var w = logo.Width * scale;
            var h = logo.Height * scale;
            var dest = SKRect.Create(Center - w / 2f, y, w, h);
            canvas.DrawBitmap(logo, dest);
            return h + 10f;
        }
        catch (Exception)
        {
            return reserved;
        }
    }

    private static float DrawQr(SKCanvas canvas, string payload, int size, float y)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return 8f;

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload.Trim(), QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(6, [0, 0, 0], [255, 255, 255], drawQuietZones: true);
            using var qr = SKBitmap.Decode(png);
            if (qr is null)
                return 8f;

            var box = Math.Min(size, 220);
            var dest = SKRect.Create(Center - box / 2f, y + 6f, box, box);
            canvas.DrawBitmap(qr, dest);
            return box + 16f;
        }
        catch (Exception)
        {
            return 8f;
        }
    }

    private static Layout BuildLayout(ReceiptDocument document)
    {
        var ops = new List<LayoutOp>();
        void Title(string text, float size = 28f, bool bold = true) =>
            ops.Add(new LayoutOp(LayoutKind.Text, text, null, size, bold, size + 10f));
        void Body(string text, float size = 22f, bool bold = false) =>
            ops.Add(new LayoutOp(LayoutKind.Text, text, null, size, bold, size + 8f));
        void Center(string text, float size = 22f, bool bold = false) =>
            ops.Add(new LayoutOp(LayoutKind.Center, text, null, size, bold, size + 10f));
        void Pair(string right, string left, float size = 22f, bool bold = false) =>
            ops.Add(new LayoutOp(LayoutKind.LeftRight, right, left, size, bold, size + 9f));
        void Rule() => ops.Add(new LayoutOp(LayoutKind.Rule, string.Empty, null, 0, false, 14f));
        void Dot() => ops.Add(new LayoutOp(LayoutKind.Dotted, string.Empty, null, 0, false, 12f));

        if (!string.IsNullOrWhiteSpace(document.LogoPath) && File.Exists(document.LogoPath))
            ops.Add(new LayoutOp(LayoutKind.Logo, string.Empty, null, 0, false, 96f, document.LogoPath));

        if (document.IsReprint)
            Center("*** إعادة طباعة / 重打印 ***", 22f, true);

        Title("إيجي شاينا هايبر ماركت", 30f);
        Body("埃及中国超市 E-CHINA", 22f, true);
        Center("فاتورة مبيعات | 销售小票", 24f, true);
        Rule();

        var receiptLabel = $"إيصال {document.ReceiptNo.ToString(CultureInfo.InvariantCulture)}";
        var when = document.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Pair(receiptLabel, when, 20f);
        Pair("الكاشير | 收银员", document.CashierName, 20f);
        if (!string.IsNullOrWhiteSpace(document.PaymentLabelAr))
        {
            var pay = string.IsNullOrWhiteSpace(document.PaymentLabelZh)
                ? document.PaymentLabelAr
                : $"{document.PaymentLabelAr} | {document.PaymentLabelZh}";
            Pair("طريقة الدفع | 支付方式", pay, 20f);
        }

        Rule();
        Body("الأصناف | 商品明细", 22f, true);

        long pieces = 0;
        foreach (var line in document.Lines)
        {
            pieces += line.QtyMilli / 1000;
            var qty = (line.QtyMilli / 1000m).ToString("0.###", CultureInfo.InvariantCulture);
            Body(line.NameAr, 22f, true);
            if (!string.IsNullOrWhiteSpace(line.NameZh))
                Body(line.NameZh!, 18f);
            Pair(
                $"{qty} × {Money.FormatAmount(line.UnitPriceMinor)}",
                $"{Money.FormatAmount(line.LineTotalMinor)} ج.م",
                20f);
            Dot();
        }

        Rule();
        Pair("عدد القطع | 商品件数", pieces.ToString(CultureInfo.InvariantCulture), 22f, true);
        if (document.DiscountMinor > 0)
            Pair("الخصم | 折扣", $"{Money.FormatAmount(document.DiscountMinor)} ج.م", 20f);
        Pair("الصافي | 应付总额", $"{Money.FormatAmount(document.TotalMinor)} ج.م", 26f, true);
        Pair("المدفوع | 实收", $"{Money.FormatAmount(document.TenderMinor)} ج.م", 20f);
        Pair("الباقي | 找零", $"{Money.FormatAmount(document.ChangeMinor)} ج.م", 20f);
        Rule();

        var qr = document.ReturnQrPayload;
        ops.Add(new LayoutOp(LayoutKind.Qr, qr, null, 200f, false, 216f));
        Center(qr, 24f, true);
        Body("امسح للمرتجع | 扫码退货", 18f);
        Rule();
        Body(document.FooterAr, 18f);
        Body("شكراً لتسوقكم في إيجي شاينا هايبر ماركت", 20f, true);
        Body("感谢光临埃及中国超市，欢迎再次光临！", 18f);

        var total = ops.Sum(op => op.Advance) + 20f;
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

    private static SKTypeface ResolveArabicTypeface()
    {
        foreach (var family in new[]
                 {
                     "Segoe UI",
                     "Tahoma",
                     "Arial",
                     "Noto Naskh Arabic",
                     "Noto Sans Arabic",
                     "Microsoft Sans Serif",
                 })
        {
            var face = SKTypeface.FromFamilyName(family, SKFontStyle.Normal);
            if (face is not null
                && !string.Equals(face.FamilyName, "sans-serif", StringComparison.OrdinalIgnoreCase)
                && face.GlyphCount > 200)
            {
                return face;
            }
        }

        return SKTypeface.Default;
    }

    private static SKTypeface ResolveCjkTypeface()
    {
        foreach (var family in new[]
                 {
                     "Microsoft YaHei",
                     "Microsoft YaHei UI",
                     "SimSun",
                     "Noto Sans CJK SC",
                     "Segoe UI",
                     "Arial",
                 })
        {
            var face = SKTypeface.FromFamilyName(family, SKFontStyle.Normal);
            if (face is not null
                && !string.Equals(face.FamilyName, "sans-serif", StringComparison.OrdinalIgnoreCase))
            {
                return face;
            }
        }

        return ResolveArabicTypeface();
    }

    private enum LayoutKind
    {
        Text,
        Center,
        LeftRight,
        Rule,
        Dotted,
        Logo,
        Qr,
    }

    private sealed record LayoutOp(
        LayoutKind Kind,
        string Text,
        string? Left,
        float Size,
        bool Bold,
        float Advance,
        string? LogoPath = null);

    private sealed record Layout(IReadOnlyList<LayoutOp> Ops, float TotalHeight);
}
