namespace EgyChina.Pos.Core;

/// <summary>Mobile waiting tickets <c>Q-xxxx</c> (ERP <c>queueTicket.ts</c>).</summary>
public static class QueueTicketCodes
{
    public static string Normalize(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? string.Empty
            : string.Concat(raw.Where(ch => !char.IsWhiteSpace(ch))).ToUpperInvariant();

    public static bool IsQueueCode(string? raw)
    {
        var code = Normalize(raw);
        if (code.Length < 3 || !code.StartsWith("Q-", StringComparison.Ordinal))
            return false;
        return code.AsSpan(2).ToString().All(char.IsAsciiDigit) && code.Length > 2;
    }
}

public sealed record QueueTicketItem(
    string ProductId,
    string? PackageId,
    string Name,
    string? NameZh,
    string? Barcode,
    decimal Quantity,
    decimal UnitPriceMajor,
    bool IsWeighted);

public sealed record QueueTicketSnapshot(
    string Code,
    string Status,
    decimal TotalMajor,
    string? CustomerId,
    string? CustomerPhone,
    string? CustomerName,
    IReadOnlyList<QueueTicketItem> Items);

public sealed record QueueTicketClaimResult(
    bool Ok,
    QueueTicketSnapshot? Ticket,
    string? ErrorMessage,
    int? StatusCode);
