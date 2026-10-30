using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using Valve.VR;

namespace ShockWave.Overlay.SteamVr;

internal sealed record VrInputFiles(string Manifest,string Actions,string Binary)
{
    public const string AppKey="szebs.shockwave.steamvr.input.v2";
    public static string DefaultDirectory
    {
        get
        {
            var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if(string.IsNullOrWhiteSpace(local)) local=Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if(string.IsNullOrWhiteSpace(local) || !Path.IsPathFullyQualified(local)) throw new IOException("Windows user-data directory is unavailable");
            return Path.Combine(local,"SzebsToys","SteamVRInput");
        }
    }
    public static string DestinationFor(string registeredActions)
    {
        if(Uri.TryCreate(registeredActions,UriKind.Absolute,out var uri) && uri.IsFile) registeredActions=uri.LocalPath;
        if(!string.IsNullOrWhiteSpace(registeredActions) && Path.IsPathFullyQualified(registeredActions))
        {
            var path=Path.GetFullPath(registeredActions);var profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if(string.IsNullOrWhiteSpace(profile)) profile=Environment.GetEnvironmentVariable("USERPROFILE");
            if(string.IsNullOrWhiteSpace(profile) || !Path.IsPathFullyQualified(profile)) return DefaultDirectory;
            profile=Path.GetFullPath(profile);
            if(path.StartsWith(profile+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) &&
                path.Replace('\\','/').EndsWith("/SzebsToys/SteamVRInput/bindings/actions.json",StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                return Path.GetDirectoryName(Path.GetDirectoryName(path)!)!;
        }
        return DefaultDirectory;
    }
    public static VrInputFiles Prepare(string package,string destination)
    {
        package=Path.GetFullPath(package);destination=Path.GetFullPath(destination);
        var source=Path.Combine(package,"bindings");var target=Path.Combine(destination,"bindings");
        var actions=Path.Combine(target,"actions.json");var binary=Path.Combine(package,"ShockWave.exe");
        if(!File.Exists(binary)) throw new IOException("ShockWave executable is missing");
        var actionText=File.ReadAllText(Path.Combine(source,"actions.json"));
        var document=JsonNode.Parse(actionText) ?? throw new InvalidDataException("Invalid action manifest");
        var bindings=document["default_bindings"]?.AsArray() ?? throw new InvalidDataException("No default controller bindings");
        var files=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {{"actions.json",actionText}};
        foreach(var binding in bindings)
        {
            var name=binding?["binding_url"]?.GetValue<string>();
            if(string.IsNullOrWhiteSpace(name) || name!=Path.GetFileName(name) || name.Contains('/') || name.Contains('\\') || !name.EndsWith(".json",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Controller binding must be a local JSON filename");
            var contents=File.ReadAllText(Path.Combine(source,name));
            if(JsonNode.Parse(contents)?["bindings"] is not JsonObject) throw new InvalidDataException("Invalid controller binding");
            files[name]=contents;
        }
        // Validate everything before updating the app-owned stable input files.
        Directory.CreateDirectory(target);
        foreach(var file in files) WriteIfChanged(Path.Combine(target,file.Key),file.Value);
        actions=NativeFilePath.Resolve(actions);binary=NativeFilePath.Resolve(binary);
        var manifest=Path.Combine(destination,"app.vrmanifest");
        var data=new {applications=new[]{new {app_key=AppKey,launch_type="binary",binary_path_windows=binary,working_directory=Path.GetDirectoryName(binary),
            is_dashboard_overlay=true,action_manifest_path=actions,strings=new {en_us=new {name="ShockWave",description="ShockWave wrist and floating panel"}}}}};
        WriteIfChanged(manifest,JsonSerializer.Serialize(data,new JsonSerializerOptions {WriteIndented=true}));
        return new(NativeFilePath.Resolve(manifest),actions,binary);
    }
    private static void WriteIfChanged(string path,string contents)
    {
        if(File.Exists(path) && File.ReadAllText(path)==contents) return;
        var temporary=path+".tmp-"+Guid.NewGuid().ToString("N");
        try {File.WriteAllText(temporary,contents);File.Move(temporary,path,true);}
        finally {if(File.Exists(temporary)) File.Delete(temporary);}
    }
    internal static bool SamePath(string a,string b)
    {
        static string Local(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri) && uri.IsFile ? uri.LocalPath : value;
        try {return Path.GetFullPath(Local(a)).Equals(Path.GetFullPath(Local(b)),StringComparison.OrdinalIgnoreCase);}
        catch(Exception e) when(e is ArgumentException or NotSupportedException) {return false;}
    }
}

// SteamVR is a separate process and cannot see another process's MSIX/UAC
// virtualized view. Pass the physical path reported by the open file handle.
internal static class NativeFilePath
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint GetFinalPathNameByHandle(IntPtr handle,StringBuilder path,uint size,uint flags);
    public static string Resolve(string path)
    {
        if(!OperatingSystem.IsWindows()) return Path.GetFullPath(path);
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var value=new StringBuilder(32768);var length=GetFinalPathNameByHandle(stream.SafeFileHandle.DangerousGetHandle(),value,32768,0);
        if(length==0 || length>=32768) throw new IOException("Cannot resolve SteamVR input path",Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        var result=value.ToString();
        if(result.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase)) return @"\\"+result[8..];
        return result.StartsWith(@"\\?\",StringComparison.Ordinal) ? result[4..] : result;
    }
}

internal interface IVrAppRegistration
{
    bool Installed {get;}
    uint ProcessId {get;}
    string ActionManifest {get;}
    string Binary {get;}
    EVRApplicationError Add(string path);
    EVRApplicationError Identify(uint process);
}

internal sealed class OpenVrAppRegistration(CVRApplications applications) : IVrAppRegistration
{
    public bool Installed=>applications.IsApplicationInstalled(VrInputFiles.AppKey);
    public uint ProcessId=>applications.GetApplicationProcessId(VrInputFiles.AppKey);
    private string Property(EVRApplicationProperty property)
    {
        var value=new StringBuilder(32768);var error=EVRApplicationError.None;
        applications.GetApplicationPropertyString(VrInputFiles.AppKey,property,value,32768,ref error);
        return error==EVRApplicationError.None ? value.ToString() : "";
    }
    public string ActionManifest=>Property(EVRApplicationProperty.ActionManifestURL_String);
    public string Binary=>Property(EVRApplicationProperty.BinaryPath_String);
    public EVRApplicationError Add(string path)=>applications.AddApplicationManifest(path,false);
    public EVRApplicationError Identify(uint process)=>applications.IdentifyApplication(process,VrInputFiles.AppKey);
}

internal static class VrInputRegistration
{
    public static void Register(VrInputFiles files,IVrAppRegistration app,uint process)
    {
        if(app.ProcessId is var owner && owner!=0 && owner!=process) throw new InvalidOperationException("Close the other ShockWave VR app first");
        var initialAdd=app.Add(files.Manifest);
        if(initialAdd==EVRApplicationError.None && VrInputFiles.SamePath(app.ActionManifest,files.Actions))
        {
            var identity=app.Identify(process);
            if(identity!=EVRApplicationError.None) throw new InvalidOperationException("SteamVR application identity: "+identity);
            return;
        }
        if(initialAdd is not (EVRApplicationError.None or EVRApplicationError.AppKeyAlreadyExists))
            throw new InvalidOperationException("SteamVR stable manifest load: "+initialAdd+" · "+files.Manifest);
        throw new InvalidOperationException("SteamVR input registration points to a different ShockWave installation; close the other VR app and try again");
    }
}
