namespace ShockWave.Overlay.Sps;

internal class SpsOptionsForm : Form
{
    protected static readonly Color Surface = Color.FromArgb(41,43,47), Input = Color.FromArgb(54,57,63), Accent = Color.FromArgb(16,163,127);
    protected readonly TableLayoutPanel Fields = new() { Dock=DockStyle.Fill,ColumnCount=1,Padding=new(22),AutoScroll=true };
    protected SpsOptionsForm(string title, Size size)
    {
        Text=title; ClientSize=size; BackColor=Surface; ForeColor=Color.White; Font=new("Segoe UI",10); StartPosition=FormStartPosition.CenterParent;
        FormBorderStyle=FormBorderStyle.FixedDialog; MaximizeBox=false; MinimizeBox=false; ShowInTaskbar=false; Controls.Add(Fields);
        Fields.ColumnStyles.Add(new(SizeType.Percent,100));
    }
    protected void Row(Control control,int height)
    { var row=Fields.RowCount++; Fields.RowStyles.Add(new(SizeType.Absolute,height)); control.Dock=DockStyle.Fill; Fields.Controls.Add(control,0,row); }
    protected static Label Label(string text,bool bold=false) => new() { Text=text,ForeColor=bold?Color.White:Color.FromArgb(181,186,193),Font=new("Segoe UI",bold?13:10,bold?FontStyle.Bold:FontStyle.Regular),TextAlign=ContentAlignment.MiddleLeft };
    protected static Button Button(string text,Action action,bool primary=false)
    {
        var button=new Button { Text=text,AutoSize=true,Height=36,FlatStyle=FlatStyle.Flat,BackColor=primary?Accent:Input,ForeColor=Color.White,Padding=new(12,3,12,3),Margin=new(0,5,8,0) };
        button.FlatAppearance.BorderSize=0; button.Click+=(_,_)=>action(); return button;
    }
    protected static FlowLayoutPanel Flow() => new() { Dock=DockStyle.Fill,Margin=Padding.Empty,WrapContents=false };
}
internal sealed class SpsSourceSwitch : ThemeToggle
{
    public SpsSourceSwitch(string text,bool value)
    {
        Text=text; Checked=value; AutoSize=false; Height=40; ForeColor=Color.White; BackColor=Color.FromArgb(41,43,47);
    }
}
internal sealed class SpsHoleOptions : SpsOptionsForm
{
    private readonly SpsHole original;
    private readonly Dictionary<SpsSources,SpsSourceSwitch> switches=[];
    public SpsHole Value => original with { Sources=switches.Where(s=>s.Value.Checked).Aggregate((SpsSources)0,(all,item)=>all|item.Key) };
    public SpsHoleOptions(SpsHole hole) : base("Hole options",new(650,280))
    {
        original=hole; Row(Label(hole.Name+" · hole options",true),34); Row(Label("Choose which interactions can activate this hole's connections."),32);
        var grid=new TableLayoutPanel { ColumnCount=2,RowCount=3,Dock=DockStyle.Fill,Margin=Padding.Empty };
        grid.ColumnStyles.Add(new(SizeType.Percent,55)); grid.ColumnStyles.Add(new(SizeType.Percent,45));
        for(var row=0;row<3;row++) grid.RowStyles.Add(new(SizeType.Percent,100f/3));
        var items=new[] { (SpsSources.OtherSockets,"Other players' SPS sockets",0,0),(SpsSources.MySockets,"My SPS sockets",1,0),
            (SpsSources.OtherBody,"Other players' hands / feet / head",0,1),(SpsSources.MyBody,"My hands / feet",1,1),
            (SpsSources.OtherPlugs,"Other players' SPS plugs",0,2),(SpsSources.MyPlugs,"My SPS plugs",1,2) };
        foreach(var (source,text,col,row) in items) { var toggle=new SpsSourceSwitch(text,hole.Sources.HasFlag(source)) { Dock=DockStyle.Fill,Margin=Padding.Empty }; switches[source]=toggle; grid.Controls.Add(toggle,col,row); }
        Row(grid,120); var actions=Flow(); actions.FlowDirection=FlowDirection.RightToLeft;
        actions.Controls.Add(Button("Apply",()=>DialogResult=DialogResult.OK,true)); var cancel=Button("Cancel",()=>DialogResult=DialogResult.Cancel); actions.Controls.Add(cancel); CancelButton=cancel; Row(actions,45);
        if(hole.Kind=="Custom") { grid.Enabled=false; Text="Custom input options"; }
    }
}
internal sealed class SpsWireOptions : SpsOptionsForm
{
    private readonly SpsWire original;
    private readonly RadioButton depth=new() { Text="Depth based",AutoSize=true,Checked=true,ForeColor=Color.White }, motion=new() { Text="Movement based",AutoSize=true,ForeColor=Color.White };
    private readonly CheckBox enabled=new() { Text="Connection enabled",AutoSize=true,Checked=true,ForeColor=Color.White };
    private readonly ComboBox input=new SettingChoiceInput() { DropDownStyle=ComboBoxStyle.DropDownList,BackColor=Input,ForeColor=Color.White,Width=490 };
    private readonly NumericUpDown gain=Number(0,1000,100), dead=Number(0,99,0), smooth=Number(0,2000,0), timeout=Number(250,10000,2000), min=Number(-1000,1000,0,3), max=Number(-1000,1000,1,3);
    private readonly SpsHole? hole;
    public bool Disconnect { get; private set; }
    private sealed record InputChoice(string Label,List<string> Addresses) { public override string ToString()=>Label; }
    public SpsWire Value => original with { Enabled=enabled.Checked,Response=motion.Checked?SpsResponse.Motion:SpsResponse.Level,
        ExactInputs=((InputChoice)input.SelectedItem!).Addresses.ToList(),Gain=(double)gain.Value/100,DeadZone=(int)dead.Value,SmoothingMs=(int)smooth.Value,TimeoutMs=(int)timeout.Value,InputMin=(double)min.Value,InputMax=(double)max.Value };
    public SpsWireOptions(SpsWire wire,SpsHole? hole,string function,bool automatic=false) : base(automatic?"Toy SPS options":"Connection options",new(560,510))
    {
        original=wire; this.hole=hole; Row(Label(function+" → "+(hole?.Name??"all holes"),true),38); Row(Label(automatic?"Applies to this function across all permitted holes.":"Choose how this connection drives motor power."),30);
        input.DrawMode=DrawMode.OwnerDrawFixed; input.ItemHeight=24;
        input.DrawItem+=(_,e)=> { using var background=new SolidBrush(Input); e.Graphics.FillRectangle(background,e.Bounds); TextRenderer.DrawText(e.Graphics,e.Index>=0?input.Items[e.Index]?.ToString()??"":"",input.Font,e.Bounds,Color.White,TextFormatFlags.Left|TextFormatFlags.VerticalCenter); };
        var modes=Flow(); depth.Margin=new(0,8,32,0); motion.Margin=new(0,8,0,0); modes.Controls.AddRange([depth,motion]); Row(modes,42);
        motion.Checked=wire.Response==SpsResponse.Motion; depth.Checked=!motion.Checked; enabled.Checked=wire.Enabled; Row(enabled,30);
        Row(Label("Depth uses insertion/contact level. Movement uses its change per second."),32);
        input.Items.Add(new InputChoice("All permitted interactions",[]));
        if(wire.ExactInputs.Count>0) input.Items.Add(new InputChoice("Imported exact input: "+(SpsSignals.Describe(wire.ExactInputs[0])?.Measurement ?? "custom level"),wire.ExactInputs));
        foreach(var address in hole?.Inputs??[]) input.Items.Add(new InputChoice(SpsSignals.Describe(address) is { } signal ? signal.Interaction+" · "+signal.Measurement : "Custom level",[address]));
        input.SelectedIndex=wire.ExactInputs.Count>0?1:0; input.Visible=!automatic; Row(input,automatic?0:38);
        var fields=new TableLayoutPanel { ColumnCount=4,RowCount=3,Dock=DockStyle.Fill,Margin=Padding.Empty };
        fields.ColumnStyles.Add(new(SizeType.Percent,27)); fields.ColumnStyles.Add(new(SizeType.Percent,23)); fields.ColumnStyles.Add(new(SizeType.Percent,27)); fields.ColumnStyles.Add(new(SizeType.Percent,23));
        void Field(string text,Control control,int col,int row) { fields.Controls.Add(Label(text),col,row); control.Margin=new(0,4,8,4); fields.Controls.Add(control,col+1,row); }
        Field("Scale %",gain,0,0); Field("Dead zone %",dead,2,0); Field("Smoothing ms",smooth,0,1); Field("Expiry ms",timeout,2,1); Field("Input min",min,0,2); Field("Input max",max,2,2);
        gain.Value=(decimal)wire.Gain*100; dead.Value=wire.DeadZone; smooth.Value=wire.SmoothingMs; timeout.Value=wire.TimeoutMs; min.Value=(decimal)wire.InputMin; max.Value=(decimal)wire.InputMax;
        Row(fields,115); Row(Label(hole?.Kind=="Socket"?"Socket root + tip contacts calibrate depth during approach. Raw exact inputs retain their original meaning.":"Toy panel sensitivity, maximum and Test mode still apply."),52);
        var actions=Flow(); if(!automatic) actions.Controls.Add(Button("Disconnect",()=> { Disconnect=true; DialogResult=DialogResult.OK; }));
        actions.Controls.Add(Button("Apply",()=> { try { if(automatic) SpsBoardStore.ValidateAutomatic(Value); else SpsStore.Validate([new() { Name=hole!.Name,Address=hole.Inputs[0],Targets=[wire.Target],Response=Value.Response,Gain=Value.Gain,InputMin=Value.InputMin,InputMax=Value.InputMax,DeadZone=Value.DeadZone,SmoothingMs=Value.SmoothingMs,TimeoutMs=Value.TimeoutMs }]); DialogResult=DialogResult.OK; } catch(InvalidDataException e) { MessageBox.Show(this,e.Message,"Connection options"); } },true));
        var cancel=Button("Cancel",()=>DialogResult=DialogResult.Cancel); actions.Controls.Add(cancel); CancelButton=cancel; Row(actions,46);
    }
    private static NumericUpDown Number(decimal minimum,decimal maximum,decimal value,int places=0) => new SettingNumberInput() { Minimum=minimum,Maximum=maximum,Value=value,DecimalPlaces=places,Increment=places>0?.05m:1,BackColor=Input,ForeColor=Color.White,Width=85 };
}
