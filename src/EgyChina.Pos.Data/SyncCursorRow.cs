namespace EgyChina.Pos.Data;

public sealed record SyncCursorRow(string Stream, string? Cursor, string? LastSuccessAt, string? LastError);

public sealed record PendingOutboxRow(
    string Id,
    string AggregateId,
    string EventType,
    string PayloadJson,
    int AttemptCount);
