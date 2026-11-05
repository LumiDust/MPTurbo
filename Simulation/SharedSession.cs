using TurboTick.Networking;

namespace TurboTick.Simulation;

/// <summary>Where a shared-colony session is in its lifecycle. Only Playing permits local input.</summary>
public enum SharedPhase
{
    Offline,
    LoadingWorld,
    Synchronizing,
    Playing,
    Faulted
}

/// <summary>
/// Ordered command intake for a shared colony. Every peer must apply the same sequence at the same tick,
/// so ordering is by (Tick, Sequence) and duplicates/regressions are rejected rather than re-applied.
/// </summary>
public sealed class CommandLog
{
    private readonly List<GameCommand> _pending = [];
    private long _lastSequence = -1;

    public int Count => _pending.Count;
    public long LastSequence => _lastSequence;

    /// <summary>Queue a server-issued command. Returns false for duplicates or non-monotonic input.</summary>
    public bool Accept(GameCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Sequence <= _lastSequence) return false;
        if (command.Tick < 0) return false;
        _lastSequence = command.Sequence;
        // Keep deterministic order even if the transport delivers slightly out of order.
        var index = _pending.Count;
        while (index > 0 && Compare(_pending[index - 1], command) > 0) index--;
        _pending.Insert(index, command);
        return true;
    }

    /// <summary>Take every command scheduled at or before the given tick, in application order.</summary>
    public List<GameCommand> TakeThrough(int tick)
    {
        var ready = new List<GameCommand>();
        var remaining = new List<GameCommand>();
        foreach (var command in _pending)
            (command.Tick <= tick ? ready : remaining).Add(command);
        _pending.Clear();
        _pending.AddRange(remaining);
        return ready;
    }

    public void Clear() { _pending.Clear(); _lastSequence = -1; }

    private static int Compare(GameCommand a, GameCommand b)
    {
        var byTick = a.Tick.CompareTo(b.Tick);
        return byTick != 0 ? byTick : a.Sequence.CompareTo(b.Sequence);
    }
}

/// <summary>
/// Server-paced simulation clock. Peers may only simulate up to the last tick the server published,
/// which is what keeps two clients from drifting while one machine is slower.
/// </summary>
public sealed class TickBarrier
{
    private int _serverTick;
    private int _appliedTick;

    public int ServerTick => Volatile.Read(ref _serverTick);
    public int AppliedTick => Volatile.Read(ref _appliedTick);
    public bool CanAdvance => AppliedTick < ServerTick;
    public int Backlog => Math.Max(0, ServerTick - AppliedTick);

    public void Publish(int tick)
    {
        if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));
        int current;
        while ((current = Volatile.Read(ref _serverTick)) < tick)
            if (Interlocked.CompareExchange(ref _serverTick, tick, current) == current) break;
    }

    public bool TryAdvance()
    {
        if (!CanAdvance) return false;
        Interlocked.Increment(ref _appliedTick);
        return true;
    }

    public void Reset() => Reset(0);

    /// <summary>Jump to a checkpoint tick: the world snapshot already includes everything before it.</summary>
    public void Reset(int startTick)
    {
        if (startTick < 0) throw new ArgumentOutOfRangeException(nameof(startTick));
        Volatile.Write(ref _appliedTick, startTick);
        Volatile.Write(ref _serverTick, startTick);
    }
}

/// <summary>Shared-colony session state owned by the game thread.</summary>
public sealed class SharedSession
{
    public SharedPhase Phase { get; private set; } = SharedPhase.Offline;
    public long Epoch { get; private set; } = -1;
    public string? WorldId { get; private set; }
    public TickBarrier Barrier { get; } = new();
    public CommandLog Commands { get; } = new();
    public string LastError { get; private set; } = "";

    public bool IsActive => Phase is SharedPhase.LoadingWorld or SharedPhase.Synchronizing or SharedPhase.Playing;
    public bool AcceptsInput => Phase == SharedPhase.Playing;

    public void BeginLoading(long epoch, string worldId)
    {
        Reset();
        Epoch = epoch;
        WorldId = worldId;
        Phase = SharedPhase.LoadingWorld;
    }

    public void WorldLoaded() => Phase = SharedPhase.Synchronizing;

    public void Playing() => Phase = SharedPhase.Playing;

    public void Fault(string error)
    {
        LastError = error;
        Phase = SharedPhase.Faulted;
        Commands.Clear();
    }

    public void Leave()
    {
        Reset();
        Phase = SharedPhase.Offline;
    }

    private void Reset()
    {
        Epoch = -1;
        WorldId = null;
        LastError = "";
        Barrier.Reset();
        Commands.Clear();
    }
}
