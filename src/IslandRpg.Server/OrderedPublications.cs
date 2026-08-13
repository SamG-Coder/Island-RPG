namespace IslandRpg.Server;

/// <summary>
/// Serializes public replication in authoritative commit order. A command may
/// reserve the head while its requester-private state and receipt are queued;
/// ready autonomous publications behind it cannot overtake that boundary.
/// </summary>
internal sealed class OrderedPublications
{
    private readonly object _sync = new();
    private readonly Queue<Entry> _pending = [];
    private long _nextId;
    private bool _draining;

    public Ticket Reserve()
    {
        lock (_sync)
        {
            var ticket = new Ticket(checked(++_nextId));
            _pending.Enqueue(new Entry(ticket));
            return ticket;
        }
    }

    public void Publish(Action publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var ticket = Reserve();
        Release(ticket, publication);
    }

    public void Release(Ticket ticket, Action? publication = null)
    {
        lock (_sync)
        {
            var entry = _pending.FirstOrDefault(value => value.Ticket == ticket) ??
                throw new InvalidOperationException(
                    "The public publication ticket is unknown or already released.");
            if (entry.Ready)
                throw new InvalidOperationException(
                    "The public publication ticket was released twice.");
            entry.Publication = publication;
            entry.Ready = true;
            if (_draining) return;
            _draining = true;
        }

        Drain();
    }

    private void Drain()
    {
        try
        {
            while (true)
            {
                Action? publication;
                lock (_sync)
                {
                    if (_pending.Count == 0 || !_pending.Peek().Ready)
                    {
                        _draining = false;
                        return;
                    }
                    publication = _pending.Dequeue().Publication;
                }
                publication?.Invoke();
            }
        }
        catch
        {
            lock (_sync) _draining = false;
            throw;
        }
    }

    internal int PendingCount
    {
        get { lock (_sync) return _pending.Count; }
    }

    internal readonly record struct Ticket(long Id);

    private sealed class Entry(Ticket ticket)
    {
        public Ticket Ticket { get; } = ticket;
        public Action? Publication { get; set; }
        public bool Ready { get; set; }
    }
}
