using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace TurboTick.Networking;

public sealed partial class DedicatedServer
{
    private readonly string _simulationKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private Peer? _simulationPeer;
    private SimulationProgress? _simulationProgress;
    private string? _requiredSimulationWorldId;
    public string? RequiredSimulationWorldId => Volatile.Read(ref _requiredSimulationWorldId);
    private sealed record SimulationProgress(Peer? Peer, long Epoch, int AppliedTick, bool Ready);
    private const int SimulationLeadTicks = 6;

    /// <summary>Admits the resident lockstep simulation through the same TCP and compatibility handshake
    /// as players. Its credential never leaves this process except on the loopback connection.</summary>
    public async Task ConnectSimulationAsync(DedicatedClient client, ClientHello hello, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(hello);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_run is null || _run.IsCancellationRequested || _listener is null)
                throw new InvalidOperationException("Start the server before connecting its simulation.");
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            var address = endpoint.Address;
            if (address.Equals(IPAddress.Any)) address = IPAddress.Loopback;
            else if (address.Equals(IPAddress.IPv6Any)) address = IPAddress.IPv6Loopback;
            if (!IPAddress.IsLoopback(address))
                throw new InvalidOperationException("The simulation requires a loopback or wildcard listener address.");
            await client.ConnectAsync(address.ToString(), endpoint.Port, hello with { SimulationKey = _simulationKey }, ct)
                .ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Publishes an immutable frame sample from the process's game thread. This grants no
    /// network role and an old epoch or disconnected simulation sample never opens the tick gate.</summary>
    public void ReportSimulationProgress(long epoch, int appliedTick, bool ready)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(epoch, -1);
        ArgumentOutOfRangeException.ThrowIfNegative(appliedTick);
        Volatile.Write(ref _simulationProgress, new SimulationProgress(Volatile.Read(ref _simulationPeer), epoch, appliedTick, ready));
    }

    /// <summary>Persists the lockstep epoch, tick, snapshot and command history for periodic or exit storage.</summary>
    public Task PersistAsync(CancellationToken ct = default) => PersistSessionAsync(ct);

    // Caller holds _stateGate. A name, client-supplied role or loopback address alone grants no privilege.
    private bool ValidateSimulationAdmission(Peer peer, ClientHello hello)
    {
        if (hello.SimulationKey is null) return false;
        var supplied = hello.SimulationKey;
        if (!peer.IsLoopback || supplied.Length != _simulationKey.Length ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(_simulationKey)))
            throw new NetworkException("SimulationAuthenticationFailed", "The process simulation credential or loopback connection is invalid.");
        if (_simulationPeer is { Joined: true } existing && !existing.Token.IsCancellationRequested)
            throw new NetworkException("SimulationOccupied", "The process simulation is already connected.");
        return true;
    }

    // Called under _stateGate (or during startup, before the listener exists).
    private void ClearSimulationState()
    {
        Volatile.Write(ref _simulationPeer, null);
        Volatile.Write(ref _simulationProgress, null);
        Volatile.Write(ref _requiredSimulationWorldId, null);
    }

    private int SimulationTickBudget()
    {
        var peer = Volatile.Read(ref _simulationPeer);
        var progress = Volatile.Read(ref _simulationProgress);
        if (peer is null || !peer.Joined || peer.Token.IsCancellationRequested || peer.RoomGeneration != _roomGeneration ||
            progress is null || !ReferenceEquals(progress.Peer, peer) || !progress.Ready || progress.Epoch != Epoch)
            return 0;
        return (int)Math.Clamp((long)progress.AppliedTick + SimulationLeadTicks - Tick, 0L, int.MaxValue);
    }

    /// <summary>Diagnostic mirror of the budget that gates the published timeline.</summary>
    public int SimulationTickBudgetForDiagnostics => SimulationTickBudget();
}
