using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ShockWave.Overlay;
using ShockWave.Overlay.Toys;

internal static partial class Program
{
    private static void FriendStorage()
    {
        var root = Path.Combine(artifacts, "friends-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "remote-friends.bin"); var store = new RemoteFriendStore(path);
        var empty = store.Load(); Check(!File.Exists(path), "reading a new friend store creates no file or connection");
        var id = "sw-" + new string('a',32); var key = new string('b',64);
        var data = empty with { Friends = [new(id,"private-fixture-friend",key)] }; store.Save(data);
        var bytes = File.ReadAllBytes(path); var text = Encoding.UTF8.GetString(bytes);
        Check(!text.Contains(id) && !text.Contains(key) && !text.Contains("private-fixture-friend"), "Windows-protected friend storage contains no plaintext identity, name or key");
        var restored = store.Load(); Check(restored.Id==empty.Id && restored.Friends.SequenceEqual(data.Friends), "saved identity and per-friend key survive an isolated store reload");
        Check(data.Friends[0].ToString()=="private-fixture-friend" && !data.ToString().Contains(key), "friend display strings cannot print pairing keys");
        var book = new RemoteFriendBook(store);
        Check(!book.Remember(id,new string('c',64),"replacement") && store.Load().Friends[0].Secret==key, "a new pairing cannot silently replace an already saved friend's key");
        Check(!book.Rename(id,"bad\nname") && store.Load().Friends[0].Name=="private-fixture-friend", "invalid friend names leave the previous encrypted data intact");
        Check(book.Rename(id,"Renamed fixture") && store.Load().Friends[0].Name=="Renamed fixture", "friend renaming preserves identity and key");
        File.WriteAllBytes(path,[1,2,3,4]); var damaged=File.ReadAllBytes(path); var corrupt=new RemoteFriendBook(store);
        Check(!corrupt.Available && !corrupt.Prepare() && File.ReadAllBytes(path).SequenceEqual(damaged), "damaged friend storage blocks saved connections without overwriting the file");
        var failing = new FailingFriendStore(data); var loaded = new RemoteFriendBook(failing); failing.Fail=true;
        Check(!loaded.Forget(id) && loaded.Friends.Count==1, "failed deletion keeps the remembered entry and reports failure");
        using var fixture = new Fixture(friends:failing); var session=fixture.Session;
        var toy=session.PublishInventory().Toys[0]; session.Receive("state",Element(new[]{new RemoteLevel(toy.Id,toy.Functions[0].Id,65,1)}));
        typeof(RemoteSession).GetProperty("Connected")!.SetValue(session,true);
        session.ForgetFriend(id);
        Check(!session.Connected && fixture.Controller.State("fixture","v1").RequestedPercent==0 && session.Friends.Count==1,
            "Forget closes the active input gate even when encrypted file deletion fails");
    }
    private static void FriendPage()
    {
        var store=new MemoryRemoteFriendStore(); var data=store.Load();
        store.Save(data with {Friends=[new("sw-"+new string('a',32),"Fixture friend",new string('b',64))]});
        using var fixture=new Fixture(friends:store); using var page=new RemotePage(fixture.Session,()=>"node.exe",Path.GetFullPath("bridge"));
        using var preview=new Form {Width=620,Height=650,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-32000,-32000)};
        preview.Controls.Add(page); preview.Show(); Application.DoEvents();
        T Field<T>(string name)=>(T)typeof(RemotePage).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(page)!;
        Check(Field<ComboBox>("friends").Items.Count==2 && Field<ComboBox>("friends").SelectedIndex==1 && !fixture.Session.Enabled,
            "a saved friend is offered after restart with Connect off");
        Check(!Field<FlowLayoutPanel>("pairing").Visible && Field<Button>("connect").Enabled && Field<Button>("forgetFriend").Enabled,
            "saved-friend mode hides code entry and retains Connect and Forget");
        Field<TextBox>("friendName").Text="New fixture name"; Field<Button>("saveFriend").PerformClick();
        Check(store.Load().Friends[0].Name=="New fixture name", "Rename friend updates only the local encrypted-book model");
        using(var image=new Bitmap(preview.ClientSize.Width,preview.ClientSize.Height)) {preview.DrawToBitmap(image,preview.ClientRectangle); image.Save(Path.Combine(artifacts,"saved-friends-page.png"));}
        Field<Button>("forgetFriend").PerformClick();
        Check(store.Load().Friends.Length==0 && Field<ComboBox>("friends").SelectedIndex==0 && !fixture.Session.Enabled,
            "Forget removes the pairing and returns to new-code mode without starting output");
        Check(fixture.Adapter.Writes.IsEmpty, "friend UI, rename and forgetting do not submit physical commands");
        preview.Hide();
    }
    private static void FriendInternet()
    {
        var aStore=new MemoryRemoteFriendStore(); var bStore=new MemoryRemoteFriendStore();
        aStore.Save(aStore.Load() with {Id="sw-8"+Guid.NewGuid().ToString("N")[1..]});
        bStore.Save(bStore.Load() with {Id="sw-4"+Guid.NewGuid().ToString("N")[1..]});
        var node=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"nodejs","node.exe"); var directory=Path.GetFullPath("bridge");
        string oldCode, oldSession;
        using(var a=new Fixture(friends:aStore)) using(var b=new Fixture(friends:bStore))
        {
            var logs=new List<string>(); a.Session.Log+=logs.Add; b.Session.Log+=logs.Add;
            a.Session.Connect("",node,directory);
            Wait(()=>{a.Session.Tick();return a.Session.Invitation.Length>0;},"temporary pairing code is generated",15000);
            oldCode=a.Session.Invitation;
            b.Session.Connect(oldCode,node,directory);
            Wait(()=>{a.Session.Tick();b.Session.Tick();return a.Session.Connected && b.Session.Connected && a.Session.CanRememberFriend && b.Session.CanRememberFriend;},"both updated apps negotiate encrypted friend pairing",55000);
            oldSession=a.Session.SessionId; a.Session.RememberFriend("Saved B");
            Wait(()=>{a.Session.Tick();b.Session.Tick();return a.Session.SelectedFriendId.Length>0 && b.Session.SelectedFriendId.Length>0 && !a.Session.RememberingFriend;},"one Save friend confirms durable pairing on both apps",15000);
            Check(aStore.Load().Friends.Single().Secret==bStore.Load().Friends.Single().Secret && aStore.Load().Friends.Single().Name=="Saved B",
                "both isolated books contain the same private pairing key and local nickname");
            Check(a.Session.Invitation.Length==0 && b.Session.Invitation.Length==0 && a.Adapter.Writes.IsEmpty && b.Adapter.Writes.IsEmpty,
                "saving retires temporary codes without toy output");
            var privateValues=new[]{oldCode,aStore.Load().Id,bStore.Load().Id,aStore.Load().Friends[0].Secret};
            Check(logs.All(line=>privateValues.All(value=>!line.Contains(value))),"pairing logs omit invitations, persistent identities and saved keys");
        }
        using var aa=new Fixture(friends:aStore); using var bb=new Fixture(friends:bStore);
        Check(!aa.Session.Enabled && !bb.Session.Enabled,"new app sessions retain friends while Connect defaults off");
        aa.Session.ConnectFriend(aStore.Load().Friends[0].Id,node,directory);
        var until=Environment.TickCount64+500;
        while(Environment.TickCount64<until){aa.Session.Tick();bb.Session.Tick();Thread.Sleep(10);}
        Check(!aa.Session.Connected && !bb.Session.Connected,"a saved friend cannot connect while the other Connect switch is off");
        bb.Session.ConnectFriend(bStore.Load().Friends[0].Id,node,directory);
        Wait(()=>{aa.Session.Tick();bb.Session.Tick();return aa.Session.Connected && bb.Session.Connected && aa.Session.Toys.Snapshot.Devices.Count>0;},"both saved friends reconnect after complete worker restart without a code",55000);
        Check(aa.Session.SessionId!=oldSession && aa.Adapter.Writes.IsEmpty && bb.Adapter.Writes.IsEmpty,
            "saved reconnect uses a fresh session with zero activity and no replay");
        var toy=aa.Session.Toys.Snapshot.Devices[0]; var lease=aa.Session.Toys.BeginInput(toy.DeviceKey,toy.Capabilities[0].ChannelId,Guid.NewGuid(),65,TimeSpan.FromSeconds(2));
        Wait(()=>{aa.Session.Toys.UpdateInput(lease,65,TimeSpan.FromSeconds(2));aa.Session.Tick();bb.Session.Tick();return bb.Controller.State("fixture","v1").RequestedPercent==65;},"fresh input after saved reconnect reaches the simulated receiving gate",6000);
        aa.Session.ForgetFriend(aStore.Load().Friends[0].Id);
        Wait(()=>{aa.Session.Tick();bb.Session.Tick();return !bb.Session.Connected && bb.Controller.State("fixture","v1").RequestedPercent==0;},"Forget ends both remote directions and receiver input",6000);
        Check(aStore.Load().Friends.Length==0 && !aa.Session.Enabled,"forgotten pairing is removed and Connect switches off");
        aa.Session.Connect("",node,directory);
        Wait(()=>{aa.Session.Tick();bb.Session.Tick();return aa.Session.Invitation.Length>0;},"owner can open new pairing after Forget",15000);
        var freshCode=aa.Session.Invitation;
        until=Environment.TickCount64+6000;
        while(Environment.TickCount64<until){aa.Session.Tick();bb.Session.Tick();Thread.Sleep(10);}
        Check(!aa.Session.Connected && !bb.Session.Connected,"the other person's retained old friend key cannot reconnect after revocation");
        bb.Session.Disconnect(); bb.Session.Connect(oldCode,node,directory);
        until=Environment.TickCount64+6000;
        while(Environment.TickCount64<until){aa.Session.Tick();bb.Session.Tick();Thread.Sleep(10);}
        Check(!aa.Session.Connected && !bb.Session.Connected,"the retired original invitation cannot restore a forgotten pairing");
        bb.Session.ForgetFriend(bStore.Load().Friends[0].Id); bb.Session.Connect(freshCode,node,directory);
        Wait(()=>{aa.Session.Tick();bb.Session.Tick();return aa.Session.Connected && bb.Session.Connected && aa.Session.CanRememberFriend;},"fresh code pairing still works after both sides forget",55000);
    }
    private sealed class FailingFriendStore(RemoteFriendsData value) : IRemoteFriendStore
    {
        public bool Fail;
        public RemoteFriendsData Load()=>value;
        public void Save(RemoteFriendsData candidate){if(Fail)throw new IOException("fixture storage failure");value=candidate;}
    }
}
