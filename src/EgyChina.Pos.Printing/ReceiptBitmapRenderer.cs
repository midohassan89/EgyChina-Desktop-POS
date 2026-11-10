using System.Globalization;
using System.Text;
using EgyChina.Pos.Core;
using QRCoder;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace EgyChina.Pos.Printing;

/// <summary>
/// Renders a compact Arabic/Chinese thermal receipt as a 576-dot bitmap for XP-P323B.
/// Mixed lines use per-script fonts so Arabic never falls back to □ inside a CJK face,
/// and Latin is never mirrored by an RTL paragraph.
/// </summary>
public static class ReceiptBitmapRenderer
{
    public const int Width = EscPosRasterEncoder.PageWidthDots;
    private const float Right = 556f;
    private const float Left = 24f;
    private const float Center = Width / 2f;

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
        var height = Math.Max(140, (int)Math.Ceiling(layout.TotalHeight) + 16);
        var bitmap = new SKBitmap(Width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.Black, IsStroke = false };
        using var arabicFont = new SKFont(arabic, 20f);
        using var cjkFont = new SKFont(cjk, 20f);

        var y = 8f;
        foreach (var op in layout.Ops)
        {
            switch (op.Kind)
            {
                case LayoutKind.Rule:
                {
                    using var rule = new SKPaint { Color = SKColors.Black, StrokeWidth = 1.6f, IsAntialias = true };
                    canvas.DrawLine(Left, y + 3f, Right, y + 3f, rule);
                    y += op.Advance;
                    break;
                }
                case LayoutKind.Dotted:
                {
                    using var rule = new SKPaint
                    {
                        Color = SKColors.Black,
                        StrokeWidth = 1.2f,
                        IsAntialias = true,
                        PathEffect = SKPathEffect.CreateDash([3f, 3f], 0f),
                    };
                    canvas.DrawLine(Left, y + 2f, Right, y + 2f, rule);
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
                    DrawMixed(canvas, op.Text, Right, y + op.Size, SKTextAlign.Right, arabicFont, cjkFont, paint, op.Size, op.Bold);
                    if (!string.IsNullOrEmpty(op.Left))
                    {
                        DrawMixed(canvas, op.Left, Left, y + op.Size, SKTextAlign.Left, arabicFont, cjkFont, paint, op.Size, op.Bold);
                    }

                    y += op.Advance;
                    break;
                }
                case LayoutKind.Center:
                {
                    DrawMixed(canvas, op.Text, Center, y + op.Size, SKTextAlign.Center, arabicFont, cjkFont, paint, op.Size, op.Bold);
                    y += op.Advance;
                    break;
                }
                default:
                {
                    DrawMixed(canvas, op.Text, Right, y + op.Size, SKTextAlign.Right, arabicFont, cjkFont, paint, op.Size, op.Bold);
                    y += op.Advance;
                    break;
                }
            }
        }

        return bitmap;
    }

    private enum ScriptKind
    {
        Arabic,
        Cjk,
        Latin,
    }

    private readonly record struct ScriptRun(ScriptKind Kind, string Text);

    private static ScriptKind Classify(char ch)
    {
        if (ch is >= '\u0600' and <= '\u06FF'
            or >= '\u0750' and <= '\u077F'
            or >= '\u08A0' and <= '\u08FF'
            or >= '\uFB50' and <= '\uFDFF'
            or >= '\uFE70' and <= '\uFEFF')
            return ScriptKind.Arabic;
        if (ch is >= '\u3400' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF'
            or >= '\u3000' and <= '\u303F')
            return ScriptKind.Cjk;
        return ScriptKind.Latin;
    }

    private static List<ScriptRun> SegmentScripts(string text)
    {
        var runs = new List<ScriptRun>();
        if (string.IsNullOrEmpty(text))
            return runs;

        var kind = Classify(text[0]);
        var buffer = new StringBuilder();
        buffer.Append(text[0]);
        for (var i = 1; i < text.Length; i++)
        {
            var ch = text[i];
            // Keep spaces/punctuation with the previous run so " | " stays attached.
            var next = char.IsWhiteSpace(ch) || char.IsPunctuation(ch)
                ? kind
                : Classify(ch);
            if (next != kind)
            {
                runs.Add(new ScriptRun(kind, buffer.ToString()));
                buffer.Clear();
                kind = next;
            }

            buffer.Append(ch);
        }

        if (buffer.Length > 0)
            runs.Add(new ScriptRun(kind, buffer.ToString()));
        return runs;
    }

    private static void DrawMixed(
        SKCanvas canvas,
        string text,
        float x,
        float baseline,
        SKTextAlign align,
        SKFont arabicFont,
        SKFont cjkFont,
        SKPaint paint,
        float size,
        bool bold)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var runs = SegmentScripts(text);
        if (runs.Count == 1 && runs[0].Kind != ScriptKind.Arabic)
        {
            // Pure CJK / Latin: draw LTR with the matching face — never mirror Latin.
            var font = FontFor(runs[0].Kind, arabicFont, cjkFont);
            font.Size = size;
            font.Embolden = bold;
            canvas.DrawShapedText(text, x, baseline, align, font, paint);
            return;
        }

        if (runs.Count == 1)
        {
            arabicFont.Size = size;
            arabicFont.Embolden = bold;
            canvas.DrawShapedText(text, x, baseline, align, arabicFont, paint);
            return;
        }

        var measured = new List<(ScriptRun Run, float Width, SKFont Font)>(runs.Count);
        var total = 0f;
        foreach (var run in runs)
        {
            var font = FontFor(run.Kind, arabicFont, cjkFont);
            font.Size = size;
            font.Embolden = bold;
            var width = MeasureRun(run, font, paint);
            measured.Add((run, width, font));
            total += width;
        }

        float cursor = align switch
        {
            SKTextAlign.Center => x - total / 2f,
            SKTextAlign.Right => x - total,
            _ => x,
        };

        // Paint runs left→right in logical order with the correct face.
        // Arabic runs still go through HarfBuzz so letters join; Latin/CJK stay upright.
        foreach (var (run, width, font) in measured)
        {
            font.Size = size;
            font.Embolden = bold;
            if (run.Kind == ScriptKind.Arabic)
                canvas.DrawShapedText(run.Text, cursor + width, baseline, SKTextAlign.Right, font, paint);
            else
                canvas.DrawShapedText(run.Text, cursor, baseline, SKTextAlign.Left, font, paint);
            cursor += width;
        }
    }

    private static float MeasureRun(ScriptRun run, SKFont font, SKPaint paint)
    {
        using var shaper = new SKShaper(font.Typeface);
        var result = shaper.Shape(run.Text, font);
        if (result.Width > 0.1f)
            return result.Width;
        return font.MeasureText(run.Text, paint);
    }

    private static SKFont FontFor(ScriptKind kind, SKFont arabic, SKFont cjk) =>
        kind == ScriptKind.Cjk ? cjk : arabic;

    private static float DrawLogo(SKCanvas canvas, string? path, float y, float reserved)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Math.Min(reserved, 8f);

        try
        {
            using var logo = SKBitmap.Decode(path);
            if (logo is null || logo.Width < 2 || logo.Height < 2)
                return Math.Min(reserved, 8f);

            var maxW = 220f;
            var maxH = Math.Max(40f, reserved - 4f);
            var scale = Math.Min(maxW / logo.Width, maxH / logo.Height);
            var w = logo.Width * scale;
            var h = logo.Height * scale;
            var dest = SKRect.Create(Center - w / 2f, y, w, h);
            canvas.DrawBitmap(logo, dest);
            return h + 4f;
        }
        catch (Exception)
        {
            return Math.Min(reserved, 8f);
        }
    }

    private static float DrawQr(SKCanvas canvas, string payload, int size, float y)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return 4f;

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload.Trim(), QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(5, [0, 0, 0], [255, 255, 255], drawQuietZones: false);
            using var qr = SKBitmap.Decode(png);
            if (qr is null)
                return 4f;

            var box = Math.Min(size, 150);
            var dest = SKRect.Create(Center - box / 2f, y + 2f, box, box);
            canvas.DrawBitmap(qr, dest);
            return box + 6f;
        }
        catch (Exception)
        {
            return 4f;
        }
    }

    private static Layout BuildLayout(ReceiptDocument document)
    {
        var ops = new List<LayoutOp>();
        void Line(string text, float size = 20f, bool bold = false, float gap = 5f) =>
            ops.Add(new LayoutOp(LayoutKind.Text, text, null, size, bold, size + gap));
        void Center(string text, float size = 20f, bool bold = false, float gap = 5f) =>
            ops.Add(new LayoutOp(LayoutKind.Center, text, null, size, bold, size + gap));
        void Pair(string right, string left, float size = 19f, bool bold = false, float gap = 5f) =>
            ops.Add(new LayoutOp(LayoutKind.LeftRight, right, left, size, bold, size + gap));
        void Bi(string ar, string zh, float size = 19f, bool bold = false) =>
            Center($"{ar}  |  {zh}", size, bold, 4f);
        void Rule() => ops.Add(new LayoutOp(LayoutKind.Rule, string.Empty, null, 0, false, 8f));
        void Dot() => ops.Add(new LayoutOp(LayoutKind.Dotted, string.Empty, null, 0, false, 6f));

        var hasLogo = !string.IsNullOrWhiteSpace(document.LogoPath) && File.Exists(document.LogoPath);
        if (hasLogo)
            ops.Add(new LayoutOp(LayoutKind.Logo, string.Empty, null, 0, false, 64f, document.LogoPath));
        else
        {
            // No logo file — print the Arabic store name once.
            Center(document.StoreNameAr, 26f, true, 4f);
        }

        // Chinese brand line once (logo already carries Arabic name).
        var storeZh = string.IsNullOrWhiteSpace(document.StoreNameZh)
            ? "埃及中国超市"
            : document.StoreNameZh.Trim();
        Center(storeZh, 16f, false, 3f);

        if (document.IsReprint)
            Center("*** إعادة طباعة / 重打印 ***", 18f, true, 3f);

        Bi("فاتورة مبيعات", "销售小票", 20f, true);
        Rule();

        var receiptLabel = $"إيصال {document.ReceiptNo.ToString(CultureInfo.InvariantCulture)}";
        var when = document.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Pair(receiptLabel, when, 17f);
        Pair("الكاشير  |  收银员", document.CashierName, 17f);
        if (!string.IsNullOrWhiteSpace(document.PaymentLabelAr))
        {
            var pay = string.IsNullOrWhiteSpace(document.PaymentLabelZh)
                ? document.PaymentLabelAr
                : $"{document.PaymentLabelAr}  |  {document.PaymentLabelZh}";
            Pair("طريقة الدفع  |  支付方式", pay, 16f);
        }

        Rule();
        Bi("الأصناف", "商品明细", 18f, true);

        long pieces = 0;
        foreach (var line in document.Lines)
        {
            pieces += line.QtyMilli / 1000;
            var qty = (line.QtyMilli / 1000m).ToString("0.###", CultureInfo.InvariantCulture);
            Line(line.NameAr, 19f, true, 3f);
            if (!string.IsNullOrWhiteSpace(line.NameZh))
                Line(line.NameZh!, 15f, false, 2f);
            Pair(
                $"{qty} × {Money.FormatAmount(line.UnitPriceMinor)}",
                $"{Money.FormatAmount(line.LineTotalMinor)} ج.م",
                17f,
                false,
                3f);
            Dot();
        }

        Rule();
        Pair("عدد القطع  |  商品件数", pieces.ToString(CultureInfo.InvariantCulture), 17f, true, 3f);
        if (document.DiscountMinor > 0)
            Pair("الخصم  |  折扣", $"{Money.FormatAmount(document.DiscountMinor)} ج.م", 16f);
        Pair("الصافي  |  应付", $"{Money.FormatAmount(document.TotalMinor)} ج.م", 21f, true, 4f);
        Pair("المدفوع  |  实收", $"{Money.FormatAmount(document.TenderMinor)} ج.م", 16f);
        Pair("الباقي  |  找零", $"{Money.FormatAmount(document.ChangeMinor)} ج.م", 16f);
        Rule();

        var qr = document.ReturnQrPayload;
        ops.Add(new LayoutOp(LayoutKind.Qr, qr, null, 132f, false, 140f));
        Center(qr, 16f, true, 2f);
        Bi("امسح للمرتجع", "扫码退货", 14f);
        Rule();

        Center("سوق العبور  ·  Souq El Obour", 14f, false, 2f);
        Center("奥布尔市场", 13f, false, 2f);
        Center("شكراً لتسوقكم في إيجي شاينا", 15f, true, 2f);
        Center("感谢光临埃及中国超市，欢迎再次光临！", 13f, false, 2f);

        var total = ops.Sum(op => op.Advance) + 10f;
        return new Layout(ops, total);
    }

    private static Layout BuildZReportLayout(ZReportDocument document)
    {
        var ops = new List<LayoutOp>();
        void Title(string text, float size = 24f, bool bold = true) =>
            ops.Add(new LayoutOp(LayoutKind.Text, text, null, size, bold, size + 6f));
        void Body(string text, float size = 18f) =>
            ops.Add(new LayoutOp(LayoutKind.Text, text, null, size, false, size + 5f));
        void Pair(string right, string left, float size = 18f, bool bold = false) =>
            ops.Add(new LayoutOp(LayoutKind.LeftRight, right, left, size, bold, size + 5f));
        void Rule() => ops.Add(new LayoutOp(LayoutKind.Rule, string.Empty, null, 0, false, 8f));

        Title(document.StoreNameAr, 24f);
        if (!string.IsNullOrWhiteSpace(document.StoreNameZh))
            Body(document.StoreNameZh, 16f);
        Title("تقرير Z · إغلاق وردية", 22f);
        Body("Z报表 · 交班", 16f);
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

        Pair("الكاشير / 收银员", document.CashierName, 17f);
        Pair("فتح الوردية", openedLocal, 16f);
        Pair("إغلاق الوردية", closedLocal, 16f);
        Rule();

        Pair("عدد التذاكر", document.TicketCount.ToString(CultureInfo.InvariantCulture));
        Pair("استرجاع", document.ReturnTicketCount.ToString(CultureInfo.InvariantCulture));
        Rule();

        Title("المبيعات حسب الدفع", 18f);
        Pair("نقد", Money.FormatAmount(document.CashSalesMinor));
        Pair("فيزا", Money.FormatAmount(document.VisaSalesMinor));
        Pair("محفظة", Money.FormatAmount(document.WalletSalesMinor));
        Pair("انستا باي", Money.FormatAmount(document.InstaPaySalesMinor));
        Pair("وي شات", Money.FormatAmount(document.WeChatSalesMinor));
        Pair("مردود نقدي", Money.FormatAmount(document.CashRefundsMinor));
        Rule();
        Title($"إجمالي المبيعات  {Money.FormatAmount(document.TotalSalesMinor)}", 20f);
        Rule();

        Pair("عهدة الافتتاح", Money.FormatAmount(document.OpeningFloatMinor));
        Pair("النقد المتوقع", Money.FormatAmount(document.ExpectedCashMinor), 18f, true);
        Pair("نقدية الإغلاق", Money.FormatAmount(document.ActualClosingCashMinor), 18f, true);
        Pair("الفرق", Money.FormatAmount(document.VarianceMinor), 20f, true);
        Rule();
        Body("نهاية تقرير الوردية", 15f);

        var total = ops.Sum(op => op.Advance) + 6f;
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
