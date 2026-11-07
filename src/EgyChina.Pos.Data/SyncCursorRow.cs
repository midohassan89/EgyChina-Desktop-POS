namespace EgyChina.Pos.Data;

public sealed record SyncCursorRow(string Stream, string? Cursor, string? LastSuccessAt, string? LastError);

public sealed record PendingOutboxRow(
    string Id,
    string AggregateId,
    string EventType,
    string PayloadJson,
    int AttemptCount);

/// <summary>Cashier-facing outbox counters. Never implies a completed sale was deleted.</summary>
public sealed record OutboxSnapshot(
    int Pending,
    int Inflight,
    int Dead,
    string? LastError);

/// <summary>
/// Result of applying a <c>GET /api/products</c> body. Never rewrites <c>sale_line</c> snapshots.
/// </summary>
public sealed record CatalogApplyResult(
    int Upserted,
    int Deactivated,
    int DemoRemoved,
    string? ServerTime);

/// <summary>Normalized ERP catalog row ready for local upsert.</summary>
public sealed record CatalogProductDraft(
    string LocalId,
    string Sku,
    string? Barcode,
    string NameAr,
    string? NameEn,
    string? NameZh,
    string UnitAr,
    long ErpNumericId,
    string? PrismaId,
    long PriceEgpMinor,
    bool IsPackage,
    string? ImageUrl = null);
