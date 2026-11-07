namespace EgyChina.Pos.Core;

/// <summary>
/// Interactive ERP calls for the till (queue tickets + loyalty). Separate from outbox sync.
/// </summary>
public interface IPosErpGateway
{
    bool IsConfigured { get; }

    Task<QueueTicketClaimResult> ClaimQueueTicketAsync(string code, CancellationToken cancellationToken);

    Task<bool> CompleteQueueTicketAsync(string code, string? saleId, CancellationToken cancellationToken);

    Task<(LoyaltyCustomer? Customer, string? Error)> LookupLoyaltyPhoneAsync(
        string phone,
        CancellationToken cancellationToken);
}

public sealed class NullPosErpGateway : IPosErpGateway
{
    public static NullPosErpGateway Instance { get; } = new();

    public bool IsConfigured => false;

    public Task<QueueTicketClaimResult> ClaimQueueTicketAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult(new QueueTicketClaimResult(
            false,
            null,
            "مزامنة ERP غير مضبوطة — لا يمكن سحب قسائم الانتظار.",
            null));

    public Task<bool> CompleteQueueTicketAsync(string code, string? saleId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<(LoyaltyCustomer? Customer, string? Error)> LookupLoyaltyPhoneAsync(
        string phone,
        CancellationToken cancellationToken) =>
        Task.FromResult<(LoyaltyCustomer?, string?)>((null, "مزامنة ERP غير مضبوطة — لا يمكن البحث عن الولاء."));
}
