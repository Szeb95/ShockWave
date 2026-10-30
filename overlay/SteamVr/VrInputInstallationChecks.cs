using System.Text.Json.Nodes;
using Valve.VR;

namespace ShockWave.Overlay.SteamVr;

internal static class VrInputInstallationChecks
{
    public static void Run(Action<bool,string> check,string output)
    {
        var root=Path.GetFullPath(Path.Combine(output,"input-installation-fixture"));
        Directory.CreateDirectory(root);
        string Package(string name)
        {
            var path=Path.Combine(root,name);Directory.CreateDirectory(Path.Combine(path,"bindings"));
            File.WriteAllBytes(Path.Combine(path,"ShockWave.exe"),[1,2,3]);
            File.WriteAllText(Path.Combine(path,"bindings","actions.json"),"{\"default_bindings\":[{\"controller_type\":\"fixture\",\"binding_url\":\"fixture.json\"}],\"actions\":[],\"action_sets\":[]}");
            File.WriteAllText(Path.Combine(path,"bindings","fixture.json"),"{\"bindings\":{\"/actions/panel\":{}}}");
            File.WriteAllText(Path.Combine(path,"api-token.bin"),"fixture-must-not-be-copied");return path;
        }
        var first=Package("ShockWave-old-win-x64");var stable=Path.Combine(root,"SzebsToys","SteamVRInput");
        var installed=VrInputFiles.Prepare(first,stable);
        check(File.Exists(installed.Manifest) && File.Exists(installed.Actions) && Path.IsPathFullyQualified(installed.Actions),"input files install at an absolute stable path outside the versioned package");
        var app=JsonNode.Parse(File.ReadAllText(installed.Manifest))!["applications"]![0]!;
        check(app["app_key"]!.GetValue<string>()==VrInputFiles.AppKey && app["action_manifest_path"]!.GetValue<string>()==installed.Actions && app["binary_path_windows"]!.GetValue<string>()==installed.Binary,
            "stable registration contains the exact physical action and executable paths");
        check(!File.Exists(Path.Combine(stable,"api-token.bin")),"input installation copies only declared bindings and no credentials");
        var written=File.GetLastWriteTimeUtc(installed.Actions);VrInputFiles.Prepare(first,stable);
        check(File.GetLastWriteTimeUtc(installed.Actions)==written,"reopening unchanged input files does not rewrite their contents or timestamp");
        if(!first.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture deletion");
        Directory.Delete(first,true);
        check(File.Exists(installed.Actions) && File.Exists(Path.Combine(Path.GetDirectoryName(installed.Actions)!,"fixture.json")),"deleting an obsolete app package leaves registered actions and controller bindings available");
        var next=Package("ShockWave-new-win-x64");var newer=VrInputFiles.Prepare(next,stable);
        var updated=JsonNode.Parse(File.ReadAllText(newer.Manifest))!["applications"]![0]!;
        check(newer.Actions==installed.Actions && newer.Binary!=installed.Binary && updated["binary_path_windows"]!.GetValue<string>()==newer.Binary,"a new app version keeps the input path and updates its launch executable");
        check(VrInputFiles.SamePath(new Uri(newer.Actions).AbsoluteUri,newer.Actions),"SteamVR file URLs and physical paths compare correctly with spaces");
        check(VrInputFiles.DestinationFor(new Uri(newer.Actions).AbsoluteUri)==Path.GetDirectoryName(newer.Manifest),"later launch contexts reuse the already-registered physical input directory");
        var text=File.ReadAllText(newer.Actions);
        File.WriteAllText(Path.Combine(next,"bindings","actions.json"),"{\"default_bindings\":[{\"binding_url\":\"../escape.json\"}]}");
        var rejected=false;try {VrInputFiles.Prepare(next,stable);}catch(InvalidDataException) {rejected=true;}
        check(rejected && File.ReadAllText(newer.Actions)==text,"invalid binding paths are rejected before changing working stable files");
        var other=new FakeRegistration {ProcessId=99};rejected=false;
        try {VrInputRegistration.Register(newer,other,42);}catch(InvalidOperationException) {rejected=true;}
        check(rejected && other.Adds==0,"another running ShockWave input owner cannot be replaced");
        var runtime=new FakeRegistration();VrInputRegistration.Register(newer,runtime,42);
        check(runtime.ProcessId==42 && runtime.Adds==1 && VrInputFiles.SamePath(runtime.ActionManifest,newer.Actions),"registration verifies the loaded action path before identifying its process");
        var failed=new FakeRegistration {AddResult=EVRApplicationError.InvalidManifest};rejected=false;
        try {VrInputRegistration.Register(newer,failed,42);}catch(InvalidOperationException) {rejected=true;}
        check(rejected && failed.ProcessId==0,"a native manifest error cannot be silently reported as connected input");
        var stale=new FakeRegistration {KeepStale=true};rejected=false;
        try {VrInputRegistration.Register(newer,stale,42);}catch(InvalidOperationException) {rejected=true;}
        check(rejected && stale.ProcessId==0,"successful registration with stale readback still fails before input is enabled");
    }
    private sealed class FakeRegistration : IVrAppRegistration
    {
        public bool Installed=>Adds>0;
        public uint ProcessId {get;set;}
        public string ActionManifest {get;private set;}="";
        public string Binary {get;private set;}="";
        public int Adds;
        public bool KeepStale;
        public EVRApplicationError AddResult=EVRApplicationError.None;
        public EVRApplicationError Add(string path)
        {
            Adds++;
            if(AddResult==EVRApplicationError.None && !KeepStale)
            {var app=JsonNode.Parse(File.ReadAllText(path))!["applications"]![0]!;ActionManifest=app["action_manifest_path"]!.GetValue<string>();Binary=app["binary_path_windows"]!.GetValue<string>();}
            return AddResult;
        }
        public EVRApplicationError Identify(uint process) {ProcessId=process;return EVRApplicationError.None;}
    }
}
