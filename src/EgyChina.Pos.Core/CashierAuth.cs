namespace EgyChina.Pos.Core;

public enum CashierRole
{
    Cashier,
    Supervisor,
    Manager,
}

public sealed record CashierRecord(
    string Id,
    string DisplayName,
    CashierRole Role,
    bool IsActive);

public sealed record ShiftRecord(
    string Id,
    string CashierId,
    string OpenedAt,
    string? ClosedAt,
    long OpeningFloatMinor,
    long? ClosingCashMinor,
    string Status,
    string SyncStatus,
    string? ServerId = null);

public enum PinAuthStatus
{
    Ok,
    InvalidFormat,
    NotFound,
    Inactive,
    Locked,
    WrongPin,
}

public sealed record PinAuthResult(
    PinAuthStatus Status,
    CashierRecord? Cashier,
    string? Message,
    DateTimeOffset? LockedUntil = null);
