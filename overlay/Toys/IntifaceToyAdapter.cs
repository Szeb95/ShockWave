namespace ShockWave.Overlay.Toys;

// The existing remembered coordinator performs bounded reconnect attempts. Each attempt
// gets a fresh socket/adapter; old commands and inventory indexes never move to the new socket.
internal sealed class IntifaceToyAdapter(IntifaceConnection settings, Func<LocalToyKeys> createKeys,
    Func<Uri, IButtplugSession>? createSession = null,
    Func<CancellationToken, Task<IReadOnlyList<IntifaceServer>>>? discover = null)
    : ILocalToyAdapter, ILocalToyOutput, ILocalToyExit, ILocalToyConnectionFilter
{
    private readonly SemaphoreSlim connection = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private ButtplugToyAdapter? current;
    private int attempts, closed;
    private string message = "Open Intiface on your phone. Saved connections recover automatically.";
    public Func<string, bool> AllowConnection { private get; set; } = _ => true;
    public event Action? Changed;
    public event Action<string>? OutputFailed;
    public ToyAdapterSnapshot Snapshot => current?.Snapshot ?? new(ToyAdapterAvailability.Ready, false, message, Array.Empty<LocalToyDescriptor>()) { SupportsOutputWhileScanning = true };
    public async Task StartScanAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        await connection.WaitAsync(linked.Token);
        try
        {
            if (current is null || current.Snapshot.Availability == ToyAdapterAvailability.Error)
            {
                var previous = current;
                if (previous is not null) { previous.Changed -= Publish; previous.OutputFailed -= Failed; await previous.DisposeAsync(); }
                current = null;
                if (string.IsNullOrWhiteSpace(settings.Address))
                { message = "Choose Find phone or enter its Intiface server address in Settings → Bluetooth toys."; Publish(); return; }
                var endpoint = IntifaceConnectionSettings.ParseAddress(settings.Address);
                if (attempts++ > 0 && settings.DiscoveryHost.Length > 0)
                {
                    var found = await (discover?.Invoke(linked.Token) ?? IntifaceDiscovery.FindAsync(linked.Token));
                    var match = found.Where(server => server.Host.Equals(settings.DiscoveryHost, StringComparison.OrdinalIgnoreCase) && server.Address.Port == endpoint.Port).ToArray();
                    if (match.Length == 1) endpoint = match[0].Address;
                }
                linked.Token.ThrowIfCancellationRequested();
                current = new(createKeys, () => createSession?.Invoke(endpoint) ?? new IntifaceWebSocketSession(endpoint, settings.Scope), external: true);
                current.AllowConnection = key => AllowConnection(key);
                current.Changed += Publish; current.OutputFailed += Failed;
            }
            await current.StartScanAsync(linked.Token);
        }
        finally { connection.Release(); }
    }
    private void Publish() { if (Volatile.Read(ref closed) == 0) Changed?.Invoke(); }
    private void Failed(string key) => OutputFailed?.Invoke(key);
    public Task StopScanAsync(CancellationToken cancellationToken = default) => current?.StopScanAsync(cancellationToken) ?? Task.CompletedTask;
    public Task ConnectAsync(string key, CancellationToken cancellationToken = default) => current?.ConnectAsync(key, cancellationToken) ?? Task.CompletedTask;
    public Task DisconnectAsync(string key, CancellationToken cancellationToken = default) => current?.DisconnectAsync(key, cancellationToken) ?? Task.CompletedTask;
    public Task ReadBatteryAsync(string key, CancellationToken cancellationToken = default) => current?.ReadBatteryAsync(key, cancellationToken) ?? Task.CompletedTask;
    public void SendLevel(string key, string channel, int value) => TrySendLevel(key, channel, value, () => true);
    public bool TrySendLevel(string key, string channel, int value, Func<bool> isCurrent) =>
        current?.TrySendLevel(key, channel, value, () => Volatile.Read(ref closed) == 0 && isCurrent()) ?? false;
    public Task CloseTransportForExitAsync() { lifetime.Cancel(); return current?.CloseTransportForExitAsync() ?? Task.CompletedTask; }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        lifetime.Cancel();
        await connection.WaitAsync();
        try { if (current is not null) { current.Changed -= Publish; current.OutputFailed -= Failed; await current.DisposeAsync(); } }
        finally { connection.Release(); }
    }
}
