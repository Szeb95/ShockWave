using System.Reflection;
using System.Runtime.InteropServices;

namespace ShockWave.Overlay.Toys;

internal interface ILovenseBleApi : IDisposable
{
    event Action<int>? BluetoothStatus;
    event Action<string, string, string>? ToyFound; // private vendor ID, private address, model symbol
    event Action<string, bool>? ConnectionChanged;
    event Action<string, string>? Battery;
    event Action<int, string>? Notification;
    void CheckBluetooth();
    void StartScan();
    void StopScan();
    void Connect(string privateId);
    void Disconnect(string privateId);
    void ReadBattery(string privateId);
    void SendLevel(string privateId, int command, int value);
    bool TrySendLevel(string privateId, int command, int value, Func<bool> isCurrent)
    {
        if (!isCurrent()) return false;
        SendLevel(privateId, command, value);
        return true;
    }
}

internal sealed class LovenseLibraryException(string message) : Exception(message);

// Independent wrapper of the official BLE v1.2 C# demo ABI. Windows x64 only.
internal sealed class LovenseBleApi : ILovenseBleApi
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void VoidCall();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void IdCall([MarshalAs(UnmanagedType.LPWStr)] string id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BatteryRequest([MarshalAs(UnmanagedType.LPWStr)] string id, int command, int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void StatusCallback(int status);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void NotifyCallback(int status, IntPtr id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ToyCallback(IntPtr id, IntPtr name, IntPtr address, byte symbol0, byte symbol1);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ConnectionCallback(IntPtr id, [MarshalAs(UnmanagedType.I1)] bool connected);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BatteryCallback(IntPtr id, IntPtr battery);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void RegisterStatus(StatusCallback callback);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void RegisterNotify(NotifyCallback callback);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void RegisterToy(ToyCallback callback);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void RegisterConnection(ConnectionCallback callback);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void RegisterBattery(BatteryCallback callback);

    private static readonly object OwnerLock = new();
    private static LovenseBleApi? owner;
    // _Quit's callback drain/unload guarantee is undocumented. Keep callbacks and DLL resident until process exit.
    private static readonly List<LovenseBleApi> CallbackRoots = [];
    private readonly IntPtr library;
    private readonly VoidCall check, start, stop, quit;
    private readonly IdCall connect, disconnect;
    private readonly BatteryRequest batteryRequest;
    private readonly StatusCallback statusCallback;
    private readonly NotifyCallback notifyCallback;
    private readonly ToyCallback toyCallback;
    private readonly ConnectionCallback connectionCallback;
    private readonly BatteryCallback batteryCallback;
    private volatile bool disposed;

    public event Action<int>? BluetoothStatus;
    public event Action<string, string, string>? ToyFound;
    public event Action<string, bool>? ConnectionChanged;
    public event Action<string, string>? Battery;
    public event Action<int, string>? Notification;

    public LovenseBleApi(string path)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new LovenseLibraryException("Lovense discovery requires Windows x64.");
        if (!File.Exists(path))
            throw new LovenseLibraryException("Lovense library missing. Follow the local-toy setup guide or choose simulated toys.");
        lock (OwnerLock)
        {
            if (owner is not null) throw new LovenseLibraryException("The Lovense adapter is already in use.");
            try
            {
                library = NativeLibrary.Load(Path.GetFullPath(path), Assembly.GetExecutingAssembly(),
                    DllImportSearchPath.UseDllDirectoryForDependencies);
                T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
                check = Export<VoidCall>("_CheckBLEStatus");
                start = Export<VoidCall>("_StartBLEScan");
                stop = Export<VoidCall>("_StopBLEScan");
                quit = Export<VoidCall>("_Quit");
                connect = Export<IdCall>("_ConnectToy");
                disconnect = Export<IdCall>("_DisConnectToy");
                batteryRequest = Export<BatteryRequest>("_SendCommand");
                statusCallback = status => Guard(() => BluetoothStatus?.Invoke(status));
                notifyCallback = (status, id) => Guard(() => Notification?.Invoke(status, Text(id)));
                toyCallback = (id, name, address, c0, c1) => Guard(() => ToyFound?.Invoke(Text(id), Text(address),
                    new string([(char)c0, (char)c1]).Trim('\0', '0')));
                connectionCallback = (id, connected) => Guard(() => ConnectionChanged?.Invoke(Text(id), connected));
                batteryCallback = (id, value) => Guard(() => Battery?.Invoke(Text(id), Text(value)));
                // Resolve all registrations before registering any callback.
                var registerStatus = Export<RegisterStatus>("_RegisterCheckBLECallback");
                var registerNotify = Export<RegisterNotify>("_RegisterNotifyCallback");
                var registerToy = Export<RegisterToy>("_RegisterToyAddCallback");
                var registerConnection = Export<RegisterConnection>("_RegisterConnectChangedCallback");
                var registerBattery = Export<RegisterBattery>("_RegisterBatteryCallback");
                CallbackRoots.Add(this);
                owner = this;
                registerStatus(statusCallback);
                registerNotify(notifyCallback);
                registerToy(toyCallback);
                registerConnection(connectionCallback);
                registerBattery(batteryCallback);
            }
            catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                disposed = true;
                if (owner == this) owner = null;
                throw new LovenseLibraryException("Lovense library could not load. Use the official Windows x64 BLE v1.2 library and its dependencies.");
            }
        }
    }

    private static string Text(IntPtr pointer) => Marshal.PtrToStringUni(pointer) ?? "";
    private void Guard(Action callback)
    {
        if (disposed) return;
        try { callback(); }
        catch (Exception) { /* Managed failures must never unwind through native callbacks. */ }
    }
    public void CheckBluetooth() => check();
    public void StartScan() => start();
    public void StopScan() => stop();
    public void Connect(string id) => connect(id);
    public void Disconnect(string id) => disconnect(id);
    public void ReadBattery(string id) => batteryRequest(id, 12, 0);
    public void SendLevel(string id, int command, int value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (command is < 0 or > 11 || value < 0 || value > (command == 10 ? 100 : command is 5 or 9 ? 3 : 20))
            throw new ArgumentOutOfRangeException(nameof(value));
        batteryRequest(id, command, value);
    }
    public void Dispose()
    {
        lock (OwnerLock)
        {
            if (disposed) return;
            disposed = true;
            try { quit(); }
            finally
            {
                BluetoothStatus = null; ToyFound = null; ConnectionChanged = null; Battery = null; Notification = null;
                if (owner == this) owner = null;
            }
        }
    }
}
