namespace ShockWave.Overlay;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--apply-update")
        {
            Environment.ExitCode = Updates.UpdateInstaller.Run(args[1]);
            return;
        }
        if (args.SequenceEqual(new[] { "--lovense-worker" }))
        {
            Environment.ExitCode = Toys.LovenseWorkerHost.Run(() => new Toys.LovenseBleApi(
                Path.Combine(AppContext.BaseDirectory, "native", "lovense", "LovenseBLE_Lib.dll")));
            return;
        }
        ApplicationConfiguration.Initialize();
        if(args.Length==2 && args[0]=="--panel-self-test")
        {
            Environment.ExitCode=SteamVr.PanelIntegrationChecks.Run(Path.GetFullPath(args[1]));return;
        }
        if(args.Length==2 && args[0]=="--panel-hidden-vr-check")
        {
            Environment.ExitCode=SteamVr.VrHiddenCheck.Run(Path.GetFullPath(args[1]));return;
        }
        if (Updates.UpdateInstaller.IsInstalling(AppContext.BaseDirectory))
        {
            MessageBox.Show("An update is finishing. Wait a moment, then open ShockWave again.", "ShockWave update", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Application.Run(new OverlayForm());
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "ShockWave could not start",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
