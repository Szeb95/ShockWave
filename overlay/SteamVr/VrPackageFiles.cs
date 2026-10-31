using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Valve.VR;

namespace ShockWave.Overlay.SteamVr;

// ToyControl was an allowed package directory in the first public updater.
// Prefer the complete new layout, including when an in-place update leaves an
// obsolete root DLL behind. The root fallback supports older development copies.
internal static class VrPackageFiles
{
    public static string ResourceDirectory(string appDirectory)
    {
        return VrInputFiles.ResourceDirectory(appDirectory);
    }
    public static string LibraryPath=>Path.Combine(ResourceDirectory(AppContext.BaseDirectory),"openvr_api.dll");
    private static readonly Lazy<nint> library=new(()=>NativeLibrary.Load(LibraryPath));
    [ModuleInitializer]
    internal static void RegisterResolver()=>NativeLibrary.SetDllImportResolver(typeof(OpenVR).Assembly,Resolve);
    private static nint Resolve(string name,Assembly assembly,DllImportSearchPath? searchPath)
        => name is "openvr_api" or "openvr_api.dll" ? library.Value : 0;

    // Explicit, read-only packaging diagnostic. It neither initializes SteamVR
    // nor starts the app, registers input, opens hardware or sends commands.
    internal static int CheckLibrary(string output)
    {
        Directory.CreateDirectory(output);
        try
        {
            var path=LibraryPath;
            var metadata=JsonDocument.Parse(File.ReadAllText(Path.Combine(ResourceDirectory(AppContext.BaseDirectory),"openvr-version.json")));
            using(metadata)
            {
                var expected=metadata.RootElement.GetProperty("Files").EnumerateArray().Single(x=>x.GetProperty("File").GetString()=="openvr_api.dll").GetProperty("Sha256").GetString();
                using var stream=File.OpenRead(path);var hash=Convert.ToHexString(SHA256.HashData(stream));
                if(!string.Equals(hash,expected,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Packaged OpenVR checksum differs from its pin");
                var installed=OpenVR.IsRuntimeInstalled();
                using var process=Process.GetCurrentProcess();
                var module=process.Modules.Cast<ProcessModule>().Single(x=>string.Equals(x.ModuleName,"openvr_api.dll",StringComparison.OrdinalIgnoreCase));
                if(!VrInputFiles.SamePath(module.FileName,path)) throw new InvalidOperationException("OpenVR was loaded from an obsolete directory");
                var inputs=VrInputFiles.Prepare(AppContext.BaseDirectory,Path.Combine(output,"input-file-fixture"));
                var bindingFiles=Directory.GetFiles(Path.GetDirectoryName(inputs.Actions)!,"*.json").Length;
                if(bindingFiles<2 || !VrInputFiles.SamePath(inputs.Binary,Path.Combine(AppContext.BaseDirectory,"ShockWave.exe"))) throw new InvalidDataException("Packaged input files did not install correctly");
                File.WriteAllText(Path.Combine(output,"native-library.json"),JsonSerializer.Serialize(new {passed=true,library=module.FileName,sha256=hash,runtimeInstalled=installed,bindingFiles,inputFilesVerified=true,inputRegistered=false,steamVrInitialized=false,physicalOutput=false},new JsonSerializerOptions {WriteIndented=true}));
                return 0;
            }
        }
        catch(Exception error) {File.WriteAllText(Path.Combine(output,"failure.txt"),error.ToString());return 1;}
    }
}
