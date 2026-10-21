using ShockWave.Overlay.Toys;

namespace ShockWave.Overlay;

internal sealed partial class OverlayForm
{
    private void RefreshToyControlStatus()
    {
        curveStatus.Text="●  "+(toyControlPage.Connected ? "Connected" : toyControlPage.Listening ? "Waiting" : "Inactive");
        curveStatus.ForeColor=toyControlPage.Connected ? Accent : toyControlPage.Listening ? Warning : Faint;
        statusHelp.SetToolTip(curveStatus,toyControlPage.Status+"\nOpen Toy control, then show the avatar panel with OSC enabled.");
        SetOscStatus(toyControlPage.Listening ? "Listening" : "Stopped",toyControlPage.Listening ? Accent : Faint);
    }
    private void ForwardOscToBridge(byte[] packet, ShockWave.ShaderLab.OscMessage[] messages)
    {
        if(bridge is not { HasExited:false } || closing || stopRequested) return;
        var prefix="/avatar/parameters/"+config.SelfTestAvatarGroup+"_";
        bool Matches(string address) => config.ActiveSelfTestEnabled ? address==prefix+"Intensity" || address==prefix+"Duration" || address==prefix+"Action" :
            config.ActiveShockers.Any(s=>s.MatchesControlAddress(address));
        if(!messages.Any(m=>Matches(m.Address))) return;
        try { bridge.StandardInput.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {kind="osc",packet=Convert.ToBase64String(packet)})); }
        catch(Exception error) when(error is IOException or InvalidOperationException) { AppendLog("error",$"OSC forwarding to {config.Provider} stopped."); }
    }

    private void RefreshLocalStatus()
    {
        var snapshot = localToysPage.Snapshot;
        var (text, color) = snapshot?.Availability switch
        {
            ToyAdapterAvailability.Ready => localToysPage.Simulated ? ("Simulated", Warning) : ("Active", Accent),
            ToyAdapterAvailability.Error => ("Error", ErrorColor),
            ToyAdapterAvailability.LibraryMissing or ToyAdapterAvailability.BluetoothUnavailable => ("Unavailable", Warning),
            _ => ("Inactive", Faint),
        };
        bluetoothStatus.Text = "●  " + text; bluetoothStatus.ForeColor = color;
        statusHelp.SetToolTip(bluetoothStatus, localToysPage.StatusMessage);
        scanStatus.Text = "●  " + (localToysPage.IsScanning ? "Scanning" : "Inactive");
        scanStatus.ForeColor = localToysPage.IsScanning ? Accent : Faint;
        scanStatus.Cursor = localToysPage.CanToggleScan ? Cursors.Hand : Cursors.WaitCursor;
        statusHelp.SetToolTip(scanStatus, localToysPage.StatusMessage + "\n\nClick to start or stop scanning.");
        resetBluetoothStatus.Text = "●  " + (localToysPage.CanResetBluetooth ? "Reset" : "Busy");
        resetBluetoothStatus.ForeColor = localToysPage.CanResetBluetooth ? Accent : Faint;
        resetBluetoothStatus.Cursor = localToysPage.CanResetBluetooth ? Cursors.Hand : Cursors.WaitCursor;
        statusHelp.SetToolTip(resetBluetoothStatus, "Click to stop toy activity and reconnect with a fresh Bluetooth session.\n" +
            "Saved sensitivity and maximums stay; Input returns to zero.\n\n" + localToysPage.StatusMessage);
    }
}
