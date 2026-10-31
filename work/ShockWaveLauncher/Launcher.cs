using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Forms;

[DataContract]
internal sealed class LatestManifest
{
    [DataMember(IsRequired=true)] public string Revision {get;set;}
    [DataMember(IsRequired=true)] public string Sha256 {get;set;}
    [DataMember(IsRequired=true)] public long Length {get;set;}
    [DataMember(IsRequired=true)] public bool Complete {get;set;}
}
[DataContract]
internal sealed class ResolvedPanel
{
    [DataMember] public string Revision;
    [DataMember] public string AppPath;
    [DataMember] public string WorkingDirectory;
}
internal static class Launcher
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window,int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);

    [STAThread]
    private static int Main(string[] args)
    {
        bool resolveOnly=args.Length==2 && args[0]=="--resolve-only";
        try
        {
            ResolvedPanel panel=Resolve();
            if(resolveOnly)
            {
                using(FileStream file=File.Create(args[1])) new DataContractJsonSerializer(typeof(ResolvedPanel)).WriteObject(file,panel);
                return 0;
            }
            if(ActivateExisting(panel.AppPath)) return 0;
            Process.Start(new ProcessStartInfo(panel.AppPath) {UseShellExecute=true,WorkingDirectory=panel.WorkingDirectory});
            return 0;
        }
        catch(Exception error)
        {
            if(resolveOnly) File.WriteAllText(args[1],"ERROR: "+error.Message);
            else MessageBox.Show("The newest ShockWave app could not be opened.\n\n"+error.Message+"\n\nKeep 00-Latest beside the ShockWave version folders.","ShockWave — Latest",MessageBoxButtons.OK,MessageBoxIcon.Information);
            return 1;
        }
    }
    private static ResolvedPanel Resolve()
    {
        string launcherDirectory=AppDomain.CurrentDomain.BaseDirectory;
        LatestManifest latest;
        using(FileStream file=new FileStream(Path.Combine(launcherDirectory,"latest.json"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite | FileShare.Delete))
        {
            if(file.Length>4096) throw new InvalidDataException("The latest-version record is invalid.");
            latest=(LatestManifest)new DataContractJsonSerializer(typeof(LatestManifest)).ReadObject(file);
        }
        if(latest==null || !latest.Complete || latest.Revision==null || !Regex.IsMatch(latest.Revision,@"\A[0-9]+[.][0-9]+[.][0-9]+\z") ||
            latest.Sha256==null || !Regex.IsMatch(latest.Sha256,@"\A[0-9a-fA-F]{64}\z") || latest.Length<=0)
            throw new InvalidDataException("The newest version is not marked complete.");
        string root=Path.GetFullPath(Path.Combine(launcherDirectory,".."));
        string name="ShockWave-"+latest.Revision+"-win-x64";
        string folder=Path.Combine(root,name);string app=Path.Combine(folder,"ShockWave.exe");
        bool nested=Directory.Exists(Path.Combine(folder,"ToyControl","SteamVR"));
        string[] required=nested
            ? new[]{"ShockWave.exe",Path.Combine("docs","build-info.json"),Path.Combine("ToyControl","SteamVR","openvr_api.dll"),Path.Combine("ToyControl","SteamVR","bindings","actions.json")}
            : new[]{"ShockWave.exe","build-info.json","openvr_api.dll","app.vrmanifest",Path.Combine("bindings","actions.json")};
        foreach(string relative in required)
            if(!File.Exists(Path.Combine(folder,relative))) throw new FileNotFoundException("The newest app folder is incomplete: "+relative);
        using(FileStream file=File.OpenRead(app))
        using(SHA256 sha=SHA256.Create())
        {
            if(file.Length!=latest.Length || !String.Equals(BitConverter.ToString(sha.ComputeHash(file)).Replace("-",""),latest.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The newest app does not match its completed build. Build it again with a new revision.");
        }
        return new ResolvedPanel {Revision=latest.Revision,AppPath=app,WorkingDirectory=folder};
    }
    private static bool ActivateExisting(string app)
    {
        foreach(Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app)))
        using(process)
        {
            try
            {
                if(!String.Equals(process.MainModule.FileName,app,StringComparison.OrdinalIgnoreCase)) continue;
                IntPtr window=process.MainWindowHandle;
                if(window==IntPtr.Zero) continue;
                if(IsIconic(window)) ShowWindowAsync(window,9);
                SetForegroundWindow(window);return true;
            }
            catch(System.ComponentModel.Win32Exception) { }
            catch(InvalidOperationException) { }
        }
        return false;
    }
}
