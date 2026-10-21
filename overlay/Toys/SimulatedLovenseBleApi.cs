namespace ShockWave.Overlay.Toys;

internal sealed class SimulatedLovenseBleApi(int bluetoothStatus = 0) : ILovenseBleApi
{
    private CancellationTokenSource? scan;
    private int scanNumber;
    private bool disposed;
    public event Action<int>? BluetoothStatus;
    public event Action<string, string, string>? ToyFound;
    public event Action<string, bool>? ConnectionChanged;
    public event Action<string, string>? Battery;
    public event Action<int, string>? Notification;
    public void CheckBluetooth() => BluetoothStatus?.Invoke(bluetoothStatus);
    public void StartScan()
    {
        scan?.Cancel();
        scan?.Dispose();
        scan = new CancellationTokenSource();
        var token = scan.Token;
        var number = ++scanNumber;
        Notification?.Invoke(0, "");
        _ = EmitAsync(token, number);
    }
    private async Task EmitAsync(CancellationToken token, int number)
    {
        try
        {
            foreach (var (id, model) in new[] { ("sim-edge", "p"), ("sim-lapis", "u"), ("sim-nora", "a"), ("sim-unknown", "??") })
            {
                await Task.Delay(80, token);
                if (number > 1 && id == "sim-unknown") continue;
                if (disposed || token.IsCancellationRequested) return;
                ToyFound?.Invoke(id, "", model);
                ToyFound?.Invoke(id, "", model); // Deliberate duplicate advertisement.
            }
        }
        catch (OperationCanceledException) { }
    }
    public void StopScan() { scan?.Cancel(); Notification?.Invoke(1, ""); }
    public void Connect(string id) => ConnectionChanged?.Invoke(id, true);
    public void Disconnect(string id) => ConnectionChanged?.Invoke(id, false);
    public void ReadBattery(string id) => Battery?.Invoke(id, id == "sim-unknown" ? "unknown" : "72");
    public void SendLevel(string id, int command, int value) => Notification?.Invoke(2, id);
    public void Dispose()
    {
        disposed = true;
        scan?.Cancel(); scan?.Dispose();
        BluetoothStatus = null; ToyFound = null; ConnectionChanged = null; Battery = null; Notification = null;
    }
}
