namespace ShockWave.Overlay;

internal sealed class RemotePage : UserControl
{
    private readonly RemoteSession session;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    private readonly TextBox own = Box(true), friend = Box(false);
    private readonly Button connect = Button("Connect"), disconnect = Button("Disconnect"), copy = Button("Copy my code");
    private readonly ComboBox friends = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 600, BackColor = Color.FromArgb(56,58,64), ForeColor = Color.White, Font = new("Segoe UI", 11) };
    private readonly TextBox friendName = new() { Width = 600, MaxLength = 48, BackColor = Color.FromArgb(56,58,64), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new("Segoe UI", 11) };
    private readonly Button saveFriend = Button("Save friend"), forgetFriend = Button("Forget friend");
    private readonly Label friendNotice = Label("", 10);
    private readonly FlowLayoutPanel pairing = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
    private readonly Label state = Label("Disconnected · Connect is off", 12), timing = Label("", 10), devices = Label("Your friend's connected toys and shockers will appear here.", 11);
    private bool closed, refreshing, initialized;
    private string shownFriend = "";
    private sealed record FriendChoice(string Id, string Name) { public override string ToString() => Name; }
    public RemotePage(RemoteSession session, Func<string> node, string directory)
    {
        this.session = session; Dock = DockStyle.Fill; BackColor = Color.FromArgb(30,31,34); AutoScroll = true;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(0,0,12,18) };
        Controls.Add(flow);
        var title = Label("Connect", 21, true); title.Height = 44; flow.Controls.Add(title);
        var intro = Label("Connect directly to your friend's ShockWave app. Each app controls its own hardware.\nTurning Connect on allows the paired person to use your connected toys and configured shockers within your limits.", 10);
        intro.Height = 66; flow.Controls.Add(intro);
        flow.Controls.Add(Label("Saved friends", 11, true)); flow.Controls.Add(friends);
        friends.SelectedIndexChanged += (_, _) => { if (!refreshing) RefreshState(); };
        var buttons = new FlowLayoutPanel { Width = 500, Height = 48, Margin = new(0,8,0,8) }; buttons.Controls.AddRange([connect, disconnect]); flow.Controls.Add(buttons);
        connect.Click += (_, _) => {
            if (friends.SelectedItem is FriendChoice { Id.Length: > 0 } selected) session.ConnectFriend(selected.Id, node(), directory);
            else session.Connect(friend.Text.Trim(), node(), directory);
        };
        disconnect.Click += (_, _) => session.Disconnect();
        flow.Controls.Add(state); flow.Controls.Add(timing);
        flow.Controls.Add(pairing);
        pairing.Controls.Add(Label("Your connection code", 11, true)); pairing.Controls.Add(own);
        pairing.Controls.Add(copy); copy.Click += (_, _) => { if (own.Text.Length > 0) try { Clipboard.SetText(own.Text); } catch { state.Text = "Could not copy. Select and copy the code manually."; } };
        var help = Label("One person turns Connect on and sends their code. The other pastes it below and turns Connect on.\nLeave the friend's code empty when you are the person sharing the first code. Codes expire after 24 hours.", 10);
        help.Height = 64; pairing.Controls.Add(help);
        pairing.Controls.Add(Label("Friend's connection code", 11, true)); pairing.Controls.Add(friend);
        flow.Controls.Add(Label("Friend name (optional)", 11, true)); flow.Controls.Add(friendName);
        var friendButtons = new FlowLayoutPanel { Width = 500, Height = 42, Margin = new(0,8,0,0) };
        friendButtons.Controls.AddRange([saveFriend, forgetFriend]); flow.Controls.Add(friendButtons);
        saveFriend.Click += (_, _) => {
            if (friends.SelectedItem is FriendChoice { Id.Length: > 0 } selected) session.RenameFriend(selected.Id, friendName.Text.Trim());
            else session.RememberFriend(friendName.Text.Trim());
        };
        forgetFriend.Click += (_, _) => { if (friends.SelectedItem is FriendChoice { Id.Length: > 0 } selected) session.ForgetFriend(selected.Id); };
        var rememberHelp = Label("Save friend remembers the pairing on both apps. Both apps need the saved-friends update. Next time, choose your friend and press Connect. Forget friend disconnects and revokes access.", 10);
        rememberHelp.Height = 60; flow.Controls.Add(rememberHelp);
        friendNotice.Height = 54; flow.Controls.Add(friendNotice);
        var explanation = Label("Direct connections only · no TURN relay or hosting account.\nSome routers cannot connect directly. Keep both apps open. Either person can disconnect at any time.", 10);
        explanation.Height = 60; flow.Controls.Add(explanation);
        flow.Controls.Add(Label("Friend's devices", 13, true)); devices.Height = 150; flow.Controls.Add(devices);
        var destinations = Label("Remote toys appear in Toy control; remote shockers appear in Shocker control.\nOwner Stop, limits and Test mode apply. Reconnecting starts with no activity.", 10);
        destinations.Height = 65; flow.Controls.Add(destinations);
        void Fit()
        {
            var width = Math.Max(260, ClientSize.Width - 42); pairing.Width = width;
            foreach (Control child in flow.Controls.Cast<Control>().Concat(pairing.Controls.Cast<Control>()))
                if (child is System.Windows.Forms.Label or TextBox or ComboBox) child.Width = width;
        }
        Resize += (_, _) => Fit(); Fit();
        session.Changed += RefreshState;
        timer.Tick += (_, _) => { session.Tick(); timing.Text = session.Connected ? $"Direct · {session.RoundTripMs:0} ms round trip · no relay usage" : "Setup helper: PeerJS Cloud · address helper: Google STUN"; };
        timer.Start(); RefreshState();
    }
    private void RefreshState()
    {
        if (closed) return;
        var selected = (friends.SelectedItem as FriendChoice)?.Id ?? "";
        if (session.Enabled && session.SelectedFriendId.Length > 0) selected = session.SelectedFriendId;
        if (!initialized && session.Friends.Count > 0) selected = session.Friends[0].Id;
        var choices = new[] { new FriendChoice("", "Pair a new friend using a code") }.Concat(session.Friends.Select(f => new FriendChoice(f.Id, f.Name))).ToArray();
        if (!friends.Items.Cast<FriendChoice>().SequenceEqual(choices))
        {
            refreshing = true; friends.Items.Clear(); friends.Items.AddRange(choices);
            friends.SelectedIndex = Math.Max(0, Array.FindIndex(choices, f => f.Id == selected)); refreshing = false;
        }
        else if (friends.SelectedItem is not FriendChoice current || current.Id != selected)
        {
            refreshing = true; friends.SelectedIndex = Math.Max(0, Array.FindIndex(choices, f => f.Id == selected)); refreshing = false;
        }
        initialized = true;
        selected = (friends.SelectedItem as FriendChoice)?.Id ?? "";
        if (shownFriend != selected)
        {
            friendName.Text = session.Friends.FirstOrDefault(f => f.Id == selected)?.Name ?? "";
            if (selected.Length == 0) friend.Clear();
            shownFriend = selected;
        }
        pairing.Visible = selected.Length == 0; friends.Enabled = !session.Enabled;
        friendName.ReadOnly = session.RememberingFriend;
        saveFriend.Text = selected.Length > 0 ? "Rename friend" : "Save friend";
        saveFriend.Enabled = !session.RememberingFriend && (selected.Length > 0 || session.CanRememberFriend);
        forgetFriend.Enabled = selected.Length > 0;
        friendNotice.Text = session.FriendNotice;
        own.Text = session.Invitation; state.Text = session.Status; friend.ReadOnly = session.Enabled;
        connect.Enabled = !session.Enabled; disconnect.Enabled = session.Enabled; copy.Enabled = own.Text.Length > 0;
        devices.Text = session.Connected ? string.Join("\n", session.Toys.Snapshot.Devices.Select(d => $"Toy · {d.DisplayName} · {d.Capabilities.Count} functions")
            .Concat(session.Shockers.Select(s => $"Shocker · {s.Name} · {s.Service} · max {s.Maximum}%"))) : "Your friend's connected toys and shockers will appear here.";
        if (session.Connected && devices.Text.Length == 0) devices.Text = "Connected · waiting for your friend's device inventory";
    }
    private static TextBox Box(bool readOnly) => new() { Multiline = true, Height = 64, Width = 600, ReadOnly = readOnly, ScrollBars = ScrollBars.Vertical, MaxLength = 600,
        BackColor = Color.FromArgb(56,58,64), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Margin = new(0,3,0,8) };
    private static Label Label(string text, float size, bool bold = false) => new() { Text = text, Font = new("Segoe UI",size,bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = Color.FromArgb(225,228,233), Width = 600, Height = 32, Margin = new(0,5,0,0) };
    private static Button Button(string text) => new() { Text = text, BackColor = Color.FromArgb(16,163,127), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Size = new(146,34), Margin = new(0,0,10,0) };
    public void Shutdown() { if (closed) return; closed = true; timer.Stop(); session.Changed -= RefreshState; session.Dispose(); }
    protected override void Dispose(bool disposing) { if (disposing) { Shutdown(); timer.Dispose(); } base.Dispose(disposing); }
}
