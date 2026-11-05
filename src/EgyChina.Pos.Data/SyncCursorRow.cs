namespace EgyChina.Pos.Data;

public sealed record SyncCursorRow(string Stream, string? Cursor, string? LastSuccessAt, string? LastError);
