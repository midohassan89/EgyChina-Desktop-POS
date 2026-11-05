namespace EgyChina.Pos.Core;

/// <summary>
/// Latest sync-worker observation. The UI copies a snapshot onto the dispatcher thread.
/// </summary>
public sealed class SyncStatus
{
    private readonly object _gate = new();

    public bool IsOnline { get; private set; }

    public string Message { get; private set; } = "بدء المزامنة…";

    public int PendingOutbox { get; private set; }

    public DateTimeOffset LastTickAt { get; private set; }

    public event Action? Changed;

    public void Update(bool online, string message, int pendingOutbox)
    {
        lock (_gate)
        {
            IsOnline = online;
            Message = message;
            PendingOutbox = pendingOutbox;
            LastTickAt = DateTimeOffset.Now;
        }

        Changed?.Invoke();
    }

    public Snapshot Read()
    {
        lock (_gate)
        {
            return new Snapshot(IsOnline, Message, PendingOutbox, LastTickAt);
        }
    }

    public readonly record struct Snapshot(bool IsOnline, string Message, int PendingOutbox, DateTimeOffset LastTickAt);
}
