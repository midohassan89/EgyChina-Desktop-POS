using EgyChina.Pos.Core;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class ArabicUiTests
{
    [Fact]
    public void Keeps_arabic_messages_and_maps_known_english()
    {
        Assert.Equal(
            "يجب فتح وردية قبل إتمام البيع.",
            ArabicUi.FromException(new InvalidOperationException("يجب فتح وردية قبل إتمام البيع."), "fallback"));

        Assert.Equal(
            "المبلغ المستلم أقل من الإجمالي.",
            ArabicUi.FromException(new InvalidOperationException("Tender is below the sale total."), "fallback"));

        Assert.StartsWith(
            "fallback (",
            ArabicUi.FromException(new InvalidOperationException("weird boom"), "fallback"),
            StringComparison.Ordinal);
    }
}
