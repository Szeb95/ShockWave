namespace ShockWave.Overlay;

internal sealed class RemotePage : UserControl
{
    private readonly RemoteSession session;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    private readonly TextBox own = Box(true), friend = Box(false);
    private readonly Button connect = Button("Connect"), disconnect = Button("Disconnect"), copy = Button("Copy my code");
    private readonly Label state = Label("Disconnected · Connect is off", 12), timing = Label("", 10), devices = Label("Your friend's connected toys and shockers will appear here.", 11);
    private bool closed;
    public RemotePage(RemoteSession session, Func<string> node, string directory)
    {
        this.session = session; Dock = DockStyle.Fill; BackColor = Color.FromArgb(30,31,34); AutoScroll = true;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(0,0,12,18) };
        Controls.Add(flow);
        var title = Label("Connect", 21, true); title.Height = 44; flow.Controls.Add(title);
        var intro = Label("Connect directly to your friend's ShockWave app. Each app controls its own hardware.\nTurning Connect on allows the paired person to use your connected toys and configured shockers within your limits.", 10);
        intro.Height = 66; flow.Controls.Add(intro);
        flow.Controls.Add(Label("Your connection code", 11, true)); flow.Controls.Add(own);
        flow.Controls.Add(copy); copy.Click += (_, _) => { if (own.Text.Length > 0) try { Clipboard.SetText(own.Text); } catch { state.Text = "Could not copy. Select and copy the code manually."; } };
        var help = Label("One person turns Connect on and sends their code. The other pastes it below and turns Connect on.\nLeave the friend's code empty when you are the person sharing the first code. Codes expire after 24 hours.", 10);
        help.Height = 64; flow.Controls.Add(help);
        flow.Controls.Add(Label("Friend's connection code", 11, true)); flow.Controls.Add(friend);
        var buttons = new FlowLayoutPanel { Width = 500, Height = 48, Margin = new(0,8,0,8) }; buttons.Controls.AddRange([connect, disconnect]); flow.Controls.Add(buttons);
        connect.Click += (_, _) => session.Connect(friend.Text.Trim(), node(), directory);
        disconnect.Click += (_, _) => session.Disconnect();
        flow.Controls.Add(state); flow.Controls.Add(timing);
        var explanation = Label("Direct connections only · no TURN relay or hosting account.\nSome routers cannot connect directly. Keep both apps open. Either person can disconnect at any time.", 10);
        explanation.Height = 60; flow.Controls.Add(explanation);
        flow.Controls.Add(Label("Friend's devices", 13, true)); devices.Height = 150; flow.Controls.Add(devices);
        var destinations = Label("Remote toys appear in Toy control. OWN / REMOTE / BOTH chooses the destination.\nRemote shocker inventory is ready for the upcoming shocker tab. Owner Stop, limits and Test mode apply.", 10);
        destinations.Height = 65; flow.Controls.Add(destinations);
        Resize += (_, _) => { foreach (Control child in flow.Controls) if (child is System.Windows.Forms.Label or TextBox) child.Width = Math.Max(260, ClientSize.Width - 30); };
        session.Changed += RefreshState;
        timer.Tick += (_, _) => { session.Tick(); timing.Text = session.Connected ? $"Direct · {session.RoundTripMs:0} ms round trip · no relay usage" : "Setup helper: PeerJS Cloud · address helper: Google STUN"; };
        timer.Start(); RefreshState();
    }
    private void RefreshState()
    {
        if (closed) return;
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
