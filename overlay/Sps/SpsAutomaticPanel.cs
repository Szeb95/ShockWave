namespace ShockWave.Overlay.Sps;

internal sealed class SpsAutomaticPanel : UserControl
{
    private readonly ThemedScrollPanel scroll=new() { Dock=DockStyle.Fill,AutoScroll=true,BackColor=Color.FromArgb(41,43,47) };
    private readonly Panel content=new() { Dock=DockStyle.Top,BackColor=Color.FromArgb(41,43,47) };
    private readonly Dictionary<string,SpsAutomaticHoleCard> cards=[];
    private List<SpsAutomaticHoleCard> ordered=[];
    private string signature="\0";
    internal readonly SpsAutomaticSourceOptions SourceOptions=new() { Dock=DockStyle.Top,Height=176 };
    internal event Action<SpsSources>? SourcesChanged;
    internal IReadOnlyCollection<SpsAutomaticHoleCard> Cards=>ordered;
    internal SpsAutomaticPanel()
    {
        BackColor=scroll.BackColor; Controls.Add(scroll); Controls.Add(SourceOptions); scroll.Controls.Add(content);
        SourceOptions.SourcesChanged+=sources=>SourcesChanged?.Invoke(sources);
        scroll.SizeChanged+=(_,_)=>ArrangeCards();
    }
    internal void Synchronize(SpsBoardDocument document,Func<SpsHole,bool?> enabled,Func<SpsHole,bool> contact)
    {
        SourceOptions.Synchronize(SpsBoardModel.EffectiveAutomaticSources(document));
        var next=string.Join("\n",document.Holes.Select(h=>h.Id+"/"+h.Name+"/"+h.Kind));
        if(signature!=next || cards.Count!=document.Holes.Count)
        {
            signature=next; content.SuspendLayout();
            foreach(Control control in content.Controls.Cast<Control>().ToArray()) control.Dispose();
            content.Controls.Clear(); cards.Clear(); ordered.Clear();
            foreach(var hole in document.Holes)
            {
                var card=new SpsAutomaticHoleCard(hole);
                cards.Add(hole.Id,card); content.Controls.Add(card);
            }
            if(document.Holes.Count==0) content.Controls.Add(new Label { Text="Use Discovery to add plugs and sockets",Dock=DockStyle.Top,Height=64,ForeColor=Color.FromArgb(181,186,193),TextAlign=ContentAlignment.MiddleLeft });
            content.ResumeLayout(); ArrangeCards();
        }
        foreach(var hole in document.Holes) cards[hole.Id].Synchronize(hole,enabled(hole),contact(hole));
        var sorted=document.Holes.OrderByDescending(h=>h.Kind=="Socket" && cards[h.Id].Active).Select(h=>cards[h.Id]).ToList();
        if(!ordered.SequenceEqual(sorted)) { ordered=sorted; ArrangeCards(); }
    }
    private void ArrangeCards()
    {
        var y=10;
        foreach(var card in ordered) { card.SetBounds(0,y,Math.Max(1,scroll.ClientSize.Width-20),64); y+=74; }
        content.Height=Math.Max(64,y); content.Width=Math.Max(1,scroll.ClientSize.Width-20);
    }
}

internal sealed class SpsAutomaticSourceOptions : RoundedPanel
{
    internal readonly Dictionary<SpsSources,ThemeToggle> Switches=[];
    private bool updating;
    internal event Action<SpsSources>? SourcesChanged;
    internal SpsAutomaticSourceOptions()
    {
        BackColor=Color.FromArgb(54,57,63); Padding=new(12,6,8,6);
        var name=new Label { Text="All holes",Dock=DockStyle.Top,Height=26,ForeColor=Color.White,Font=new("Segoe UI",11,FontStyle.Bold) };
        var description=new Label { Text="These six options apply to every hole",Dock=DockStyle.Top,Height=24,AutoEllipsis=true,ForeColor=Color.FromArgb(181,186,193) };
        var grid=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=3,RowCount=4,Margin=Padding.Empty,Padding=Padding.Empty };
        grid.ColumnStyles.Add(new(SizeType.Percent,52)); grid.ColumnStyles.Add(new(SizeType.Percent,27)); grid.ColumnStyles.Add(new(SizeType.Percent,21));
        for(var row=0;row<4;row++) grid.RowStyles.Add(new(SizeType.Percent,25));
        Label Text(string text)=>new() { Text=text,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,ForeColor=Color.FromArgb(221,224,228),Margin=Padding.Empty };
        grid.Controls.Add(Text("Interactions from"),0,0); grid.Controls.Add(Text("Other players"),1,0); grid.Controls.Add(Text("Me"),2,0);
        var labels=new[]{"SPS sockets","Hands / feet / head","SPS plugs"};
        var other=new[]{SpsSources.OtherSockets,SpsSources.OtherBody,SpsSources.OtherPlugs};
        var own=new[]{SpsSources.MySockets,SpsSources.MyBody,SpsSources.MyPlugs};
        for(var row=0;row<3;row++)
        {
            grid.Controls.Add(Text(labels[row]),0,row+1);
            foreach(var (source,column) in new[]{(other[row],1),(own[row],2)})
            {
                var toggle=new ThemeToggle { Dock=DockStyle.Fill,BackColor=BackColor,Margin=Padding.Empty,
                    AccessibleName=(column==1?"Other players: ":"My ")+labels[row]+" · all holes" };
                toggle.CheckedChanged+=(_,_)=> { if(!updating) SourcesChanged?.Invoke(Switches.Where(s=>s.Value.Checked).Aggregate((SpsSources)0,(all,item)=>all|item.Key)); };
                Switches.Add(source,toggle); grid.Controls.Add(toggle,column,row+1);
            }
        }
        Controls.Add(grid); Controls.Add(description); Controls.Add(name);
    }
    internal void Synchronize(SpsSources sources)
    {
        updating=true;
        try
        {
            foreach(var pair in Switches) pair.Value.Checked=sources.HasFlag(pair.Key);
        }
        finally { updating=false; }
    }
}

internal sealed class SpsAutomaticHoleCard : RoundedPanel
{
    internal string HoleId { get; }
    internal bool Active { get; private set; }
    private readonly Label name=new() { Dock=DockStyle.Top,Height=26,AutoEllipsis=true,ForeColor=Color.White,Font=new("Segoe UI",11,FontStyle.Bold) };
    private readonly Label state=new() { Dock=DockStyle.Top,Height=24,ForeColor=Color.FromArgb(181,186,193) };
    internal SpsAutomaticHoleCard(SpsHole hole)
    {
        HoleId=hole.Id; BackColor=Color.FromArgb(54,57,63); Padding=new(12,6,8,6);
        name.Text=hole.Name;
        Controls.Add(state); Controls.Add(name);
    }
    internal void Synchronize(SpsHole hole,bool? enabled,bool contact)
    {
        var active=Active=enabled??contact;
        state.Text=(active?"● ":"○ ")+(enabled==true?"On":enabled==false?"Off":contact?"Contact":"Waiting for contact")+" · "+hole.Kind;
        state.ForeColor=active?Color.FromArgb(16,183,140):Color.FromArgb(181,186,193);
        name.ForeColor=active?Color.FromArgb(16,183,140):Color.White;
    }
}
