using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ShockWave.Overlay.Updates;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition,string description) {if(!condition) throw new InvalidOperationException(description);checks++;Console.WriteLine("PASS "+description);}
    [STAThread]
    private static int Main(string[] args)
    {
        string? Option(string key) {var i=Array.IndexOf(args,key);return i>=0 && i+1<args.Length ? args[i+1] : null;}
        var output=Path.GetFullPath(Option("--output") ?? "work/legacy-updater-checks");Directory.CreateDirectory(output);
        var root=Path.Combine(output,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var success=false;
        try
        {
            using var pin=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"legacy-provenance.json")));
            var oldText=pin.RootElement.GetProperty("version").GetString()!;var current=Version.Parse(oldText);
            foreach(var file in pin.RootElement.GetProperty("files").EnumerateArray())
                Check(UpdatePackage.Hash(Path.Combine(AppContext.BaseDirectory,"legacy-source",file.GetProperty("file").GetString()!)).Equals(file.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase),"legacy updater source matches its published commit");
            var supplied=Option("--package");var online=args.Contains("--online");var oldHelper=Option("--old-helper");
            var next=new Version(0,8,1);byte[] zip;
            if(supplied is null)
            {
                var fixture=Path.Combine(root,"fixture-package");Directory.CreateDirectory(fixture);
                MakePackage(fixture,next.ToString(3),Assembly.GetExecutingAssembly().Location);
                foreach(var path in new[]{"ToyControl/SteamVR/openvr_api.dll","ToyControl/SteamVR/openvr-version.json","ToyControl/SteamVR/bindings/actions.json","docs/STEAMVR.md","docs/INTIFACE-PHONE.md","docs/build-info.json"}) Write(fixture,path,"fixture-only");
                SaveManifest(fixture,next.ToString(3));var archive=Path.Combine(root,"fixture.zip");ZipFile.CreateFromDirectory(fixture,archive);zip=File.ReadAllBytes(archive);
            }
            else
            {
                zip=File.ReadAllBytes(supplied);using var archive=new ZipArchive(new MemoryStream(zip));
                using var reader=new StreamReader(archive.GetEntry(UpdatePackage.ManifestName)!.Open());
                next=Version.Parse(JsonSerializer.Deserialize<UpdateManifest>(reader.ReadToEnd(),AppUpdater.Json)!.Version);
            }
            var install=Path.Combine(root,"installation");Directory.CreateDirectory(install);
            MakePackage(install,oldText,oldHelper ?? Assembly.GetExecutingAssembly().Location);
            if(oldHelper is not null) Check(AppUpdater.Normalize(Version.Parse(FileVersionInfo.GetVersionInfo(oldHelper).FileVersion!))==AppUpdater.Normalize(current),"real old helper has the expected published version");
            var userFiles=new[]{"bridge/config.json","api-token.bin","remote-friends.bin","my-file.txt","openvr_api.dll"};
            foreach(var path in userFiles) Write(install,path,"isolated-owner-data-keep");
            var saved=userFiles.ToDictionary(path=>path,path=>UpdatePackage.Hash(Path.Combine(install,path)));
            var oldExeHash=UpdatePackage.Hash(Path.Combine(install,"ShockWave.exe"));
            var nextText=AppUpdater.Display(next);var digest=Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
            var release=JsonSerializer.SerializeToUtf8Bytes(new {draft=false,prerelease=false,tag_name="v"+nextText,html_url=AppUpdater.Repository+"/releases/tag/v"+nextText,assets=new[]{new {name=$"ShockWave-{nextText}-win-x64.zip",state="uploaded",size=zip.Length,digest="sha256:"+digest,browser_download_url=AppUpdater.Repository+$"/releases/download/v{nextText}/ShockWave-{nextText}-win-x64.zip"}}});
            using(var client=online ? new HttpClient(new HttpClientHandler {AllowAutoRedirect=false}) : new HttpClient(new FixtureHttp(release,zip)))
            using(var updater=new AppUpdater(client,current,install,Path.Combine(root,"staging")))
            {
                updater.CheckAsync().GetAwaiter().GetResult();
                Check(updater.Available?.Version==AppUpdater.Normalize(next),"old updater recognizes the newer release");
                Check(updater.DownloadAsync().GetAwaiter().GetResult(),"old updater downloads, extracts and prepares every new package file: "+updater.Status);
                var prepared=updater.Prepared ?? throw new InvalidOperationException("No prepared update");
                Check(UpdatePackage.Hash(Path.Combine(install,"ShockWave.exe"))==oldExeHash,"download leaves the running installation untouched");
                var manifest=UpdatePackage.ReadManifest(prepared.PayloadDirectory);
                Check(manifest.Files.All(file=>UpdatePackage.AllowedFile(file.Path)),"all new files pass the original updater allowlist");
                Check(manifest.Files.Any(file=>file.Path=="ToyControl/SteamVR/openvr_api.dll") && manifest.Files.Any(file=>file.Path=="docs/build-info.json"),"SteamVR files and build metadata use backward-compatible directories");
                var request=new UpdateRequest(install,oldText,nextText,UpdatePackage.Hash(Path.Combine(prepared.PayloadDirectory,UpdatePackage.ManifestName)),-1,0,false);
                File.WriteAllText(Path.Combine(prepared.StageDirectory,"request.json"),JsonSerializer.Serialize(request,AppUpdater.Json));
                if(oldHelper is null) UpdateInstaller.Apply(prepared.StageDirectory,request);
                else
                {
                    var helper=Path.Combine(prepared.StageDirectory,"ShockWave.Update.exe");File.Copy(oldHelper,helper);
                    var start=new ProcessStartInfo(helper) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=prepared.StageDirectory};
                    start.ArgumentList.Add("--apply-update");start.ArgumentList.Add(Path.Combine(prepared.StageDirectory,"request.json"));
                    using var process=Process.Start(start)!;
                    if(!process.WaitForExit(45000)) {process.Kill(true);throw new TimeoutException("Owned legacy helper timed out");}
                    Check(process.ExitCode==0,"real old update helper installs the package without starting the app");
                }
                Check(manifest.Files.All(file=>UpdatePackage.Hash(Path.Combine(install,file.Path)).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase)),"every installed package file matches its verified hash");
                Check(AppUpdater.Normalize(Version.Parse(UpdatePackage.ReadManifest(install).Version))==AppUpdater.Normalize(next),"installed ownership manifest has the new version");
                Check(userFiles.All(path=>UpdatePackage.Hash(Path.Combine(install,path))==saved[path]),"owner settings, protected-store sentinels and old root DLL remain unchanged");
                Check(AppUpdater.Normalize(Version.Parse(FileVersionInfo.GetVersionInfo(Path.Combine(install,"ShockWave.exe")).FileVersion!))==AppUpdater.Normalize(next),"installed application executable has the new version");
                if(supplied is not null)
                {
                    var nativeOutput=Path.Combine(root,"native-check");var start=new ProcessStartInfo(Path.Combine(install,"ShockWave.exe")) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=install};
                    start.ArgumentList.Add("--steamvr-library-check");start.ArgumentList.Add(nativeOutput);
                    using var process=Process.Start(start)!;if(!process.WaitForExit(15000)) {process.Kill(true);throw new TimeoutException("Owned native check timed out");}
                    if(process.ExitCode!=0) throw new InvalidOperationException("Upgraded native library check failed: "+File.ReadAllText(Path.Combine(nativeOutput,"failure.txt")));
                    using var native=JsonDocument.Parse(File.ReadAllText(Path.Combine(nativeOutput,"native-library.json")));
                    Check(native.RootElement.GetProperty("passed").GetBoolean() && !native.RootElement.GetProperty("steamVrInitialized").GetBoolean(),"upgraded app loads the nested pinned DLL despite the stale root DLL, without initializing SteamVR");
                }
            }
            var report=new {passed=true,checks,from=oldText,to=nextText,legacyCommit=pin.RootElement.GetProperty("commit").GetString(),realOldHelper=oldHelper is not null,realPackage=supplied is not null,publicNetwork=online,normalAppStarted=false,settingsChanged=false,physicalOutput=false};
            File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true}));Console.WriteLine($"Legacy {oldText} -> {nextText}: {checks} checks passed");success=true;return 0;
        }
        catch(Exception error) {File.WriteAllText(Path.Combine(output,"failure.txt"),error.ToString());Console.Error.WriteLine(error);return 1;}
        finally
        {
            if(success && Path.GetFullPath(root).StartsWith(output+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(Path.GetFileName(root),"N",out _)) Directory.Delete(root,true);
        }
    }
    private static void Write(string root,string relative,string text) {var path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,text);}
    private static void MakePackage(string root,string version,string exe)
    {
        File.Copy(exe,Path.Combine(root,"ShockWave.exe"));foreach(var path in new[]{"node.exe","bridge/bridge.mjs","bridge/remote-worker.mjs","native/buttplug/intiface-engine.exe","LICENSE"}) Write(root,path,"fixture-only");SaveManifest(root,version);
    }
    private static void SaveManifest(string root,string version)
    {
        var files=Directory.GetFiles(root,"*",SearchOption.AllDirectories).Where(path=>Path.GetFileName(path)!=UpdatePackage.ManifestName).Select(path=>new UpdateFile(Path.GetRelativePath(root,path).Replace('\\','/'),UpdatePackage.Hash(path))).ToList();
        File.WriteAllText(Path.Combine(root,UpdatePackage.ManifestName),JsonSerializer.Serialize(new UpdateManifest(1,version,"win-x64",files),AppUpdater.Json));
    }
    private sealed class FixtureHttp(byte[] release,byte[] zip) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new ByteArrayContent(request.RequestUri!.Host=="api.github.com" ? release : zip)});
    }
}
