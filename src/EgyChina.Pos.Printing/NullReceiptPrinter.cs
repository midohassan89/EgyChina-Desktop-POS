using EgyChina.Pos.Core;

namespace EgyChina.Pos.Printing;

/// <summary>No hardware configured — sales still commit; reprint stays available later.</summary>
public sealed class NullReceiptPrinter : IReceiptPrinter
{
    public bool IsConfigured => false;

    public Task PrintAsync(ReceiptDocument document, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
