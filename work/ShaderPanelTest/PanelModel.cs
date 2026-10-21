using System.Text.Json;
using System.Numerics;
using System.Text;

namespace ShockWave.ShaderLab;

internal sealed class ToyRow
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public bool Connected { get; set; }
    public int Battery { get; set; } = -1;
    public List<ToyFunction> Functions { get; } = [];
}

internal sealed class ToyFunction
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public float Level { get; set; }
    public bool Selected { get; set; }
    public Color Color { get; init; }
    public Guid Generation { get; init; } = Guid.NewGuid();
    public PatternClip? Pattern { get; set; }
    public bool LoopRunning { get; set; }
    public double LoopStarted { get; set; }
}

internal sealed record FunctionUpdate(string Id, string Name, float? Level = null);
internal sealed record ToyUpdate(string Id, string Name, bool Connected, int Battery, float? Level,
    IReadOnlyList<FunctionUpdate>? Functions = null);
internal sealed record FunctionBinding(ToyRow Toy, ToyFunction Function)
{
    public bool Connected => Toy.Connected;
    public bool Selected => Function.Selected;
    public float Level => Function.Level;
}

internal sealed partial class PanelModel
{
    public const int PageSize = 6, MaximumToys = 128, MaximumFunctionsPerToy = 16, MaximumFunctions = 256;
    public bool AdaptivePaging { get; init; }
    public int VisiblePageSize => AdaptivePaging && FunctionCount <= 7 ? 7 : PageSize;
    public readonly List<ToyRow> Toys = [];
    public int Page { get; private set; }
    public int Route { get; private set; }
    public float Level { get; private set; }
    public bool Touching { get; private set; }
    // Graph coordinates: X moves the feedback dot, Y is intensity. Time is separate.
    public Vector2 TouchPosition { get; private set; } = new(0.5f, 0);
    public int Revision { get; private set; }
    public string Source { get; set; } = "Waiting for OSC";
    public string VrStatus { get; set; } = "VR OSC: off";
    public DateTime? LastSync { get; private set; }
    public int FunctionCount => Toys.Sum(t => t.Functions.Count);
    public IEnumerable<FunctionBinding> Functions => Toys.SelectMany(t => t.Functions.Select(f => new FunctionBinding(t, f)));
    public int PageCount => Math.Max(1, (FunctionCount + VisiblePageSize - 1) / VisiblePageSize);
    public IEnumerable<FunctionBinding> Visible => Functions.Skip(Page * VisiblePageSize).Take(VisiblePageSize);
    public int SelectedCount => Functions.Count(f => f.Connected && f.Selected);
    public Color LiveColor
    {
        get
        {
            var selected = Functions.Where(f => f.Connected && f.Selected).Take(2).ToArray();
            return selected.Length == 1 ? selected[0].Function.Color : selected.Length > 1 ? Color.FromArgb(242, 243, 245) : Color.FromArgb(16, 163, 127);
        }
    }

    private static void ValidateText(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 96 || text.Any(char.IsControl)) throw new FormatException("Invalid " + label);
    }
    private static void ValidateLevel(float? level)
    {
        if (level.HasValue && (!float.IsFinite(level.Value) || level.Value is < 0 or > 1)) throw new FormatException("Level must be finite 0..1");
    }
    public static ToyUpdate Validate(string id, string name, bool connected, int battery, float? level,
        IReadOnlyList<FunctionUpdate>? functions = null)
    {
        ValidateText(id, "toy alias"); ValidateText(name, "toy name");
        if (battery is < -1 or > 100) throw new FormatException("Battery must be -1 or 0..100");
        ValidateLevel(level);
        if (functions is not null)
        {
            if (level.HasValue) throw new FormatException("Use per-function levels with an explicit function list");
            if (functions.Count is < 1 or > MaximumFunctionsPerToy) throw new FormatException("A toy must have 1..16 preview functions");
            HashSet<string> aliases = new(StringComparer.Ordinal);
            foreach (var f in functions)
            {
                ValidateText(f.Id, "function alias"); ValidateText(f.Name, "function name"); ValidateLevel(f.Level);
                if (!aliases.Add(f.Id)) throw new FormatException("Duplicate function alias within a toy");
            }
        }
        return new(id, name, connected, battery, level, functions);
    }

    public static IReadOnlyList<ToyUpdate> ParseInventory(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() > MaximumToys) throw new FormatException("Inventory must contain at most 128 toys");
        List<ToyUpdate> updates = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            var name = item.GetProperty("name").GetString() ?? "";
            var connected = !item.TryGetProperty("connected", out var c) || c.GetBoolean();
            var battery = item.TryGetProperty("battery", out var b) ? b.GetInt32() : -1;
            float? level = item.TryGetProperty("level", out var l) ? l.GetSingle() : null;
            IReadOnlyList<FunctionUpdate>? functions = null;
            if (item.TryGetProperty("functions", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaximumFunctionsPerToy) throw new FormatException("Invalid function list");
                functions = list.EnumerateArray().Select(f => new FunctionUpdate(
                    f.GetProperty("id").GetString() ?? "", f.GetProperty("name").GetString() ?? "",
                    f.TryGetProperty("level", out var fl) ? fl.GetSingle() : null)).ToArray();
            }
            if (!ids.Add(id)) throw new FormatException("Duplicate toy alias");
            updates.Add(Validate(id, name, connected, battery, level, functions));
        }
        return updates;
    }

    public void ReplaceInventory(IReadOnlyList<ToyUpdate> updates)
    {
        // Validate the entire snapshot before touching current state.
        if (updates.Count > MaximumToys || updates.Select(u => u.Id).Distinct().Count() != updates.Count) throw new FormatException("Invalid inventory size/aliases");
        foreach (var u in updates) Validate(u.Id, u.Name, u.Connected, u.Battery, u.Level, u.Functions);
        var previous = Toys.ToDictionary(t => t.Id, StringComparer.Ordinal);
        List<ToyRow> replacement = [];
        foreach (var u in updates)
        {
            previous.TryGetValue(u.Id, out var row);
            replacement.Add(Build(row, u));
        }
        if (replacement.Sum(t => t.Functions.Count) > MaximumFunctions) throw new FormatException("Inventory must contain at most 256 functions");
        Toys.Clear(); Toys.AddRange(replacement);
        Page = Math.Min(Page, PageCount - 1);
        LastSync = DateTime.UtcNow; Revision++;
        ReconcileRecording();
    }

    public void Upsert(ToyUpdate update)
    {
        Validate(update.Id, update.Name, update.Connected, update.Battery, update.Level, update.Functions);
        var row = Toys.FirstOrDefault(t => t.Id == update.Id);
        if (row is null && Toys.Count >= MaximumToys) throw new FormatException("Inventory is full");
        var replacement = Build(row, update);
        if (FunctionCount - (row?.Functions.Count ?? 0) + replacement.Functions.Count > MaximumFunctions) throw new FormatException("Function inventory is full");
        if (row is null) Toys.Add(replacement); else Toys[Toys.IndexOf(row)] = replacement;
        Page = Math.Min(Page, PageCount - 1); LastSync = DateTime.UtcNow; Revision++;
        ReconcileRecording();
    }

    private static ToyRow Build(ToyRow? previous, ToyUpdate u)
    {
        // Build fresh objects so a later invalid entry cannot partly mutate a snapshot.
        var row = new ToyRow { Id = u.Id, Name = u.Name, Battery = u.Battery, Connected = u.Connected };
        var definitions = u.Functions ?? previous?.Functions.Select(f => new FunctionUpdate(f.Id, f.Name)).ToArray()
            ?? [new FunctionUpdate("main", "Input")];
        if (u.Level.HasValue && definitions.Count != 1) throw new FormatException("Toy-level input is ambiguous for multiple functions");
        foreach (var definition in definitions)
        {
            var old = previous?.Functions.FirstOrDefault(f => f.Id == definition.Id);
            var preserve = u.Connected && previous?.Connected == true && old is not null;
            var explicitLevel=definition.Level.HasValue || u.Level.HasValue;
            row.Functions.Add(new ToyFunction
            {
                Id = definition.Id, Name = definition.Name,
                Color = old?.Color ?? FunctionColors.For(u.Id, definition.Id),
                Generation=preserve && !explicitLevel ? old!.Generation : Guid.NewGuid(),
                Pattern=preserve ? old!.Pattern : null,
                LoopRunning=preserve && !explicitLevel && old!.LoopRunning,
                LoopStarted=preserve ? old!.LoopStarted : 0,
                Level = u.Connected ? definition.Level ?? u.Level ?? (preserve ? old!.Level : 0) : 0,
                Selected = preserve && old!.Selected
            });
        }
        return row;
    }

    public void Remove(string id)
    {
        Toys.RemoveAll(t => t.Id == id); Page = Math.Min(Page, PageCount - 1); LastSync = DateTime.UtcNow; Revision++;
        ReconcileRecording();
    }
    public void ToggleVisible(int index)
    {
        var row = Visible.ElementAtOrDefault(index);
        if (row is null || !row.Connected) return;
        row.Function.Selected = !row.Selected; Revision++;
    }
    public void ChangePage(int delta) { Page = Math.Clamp(Page + delta, 0, PageCount - 1); Revision++; }
    public void ChangeRoute(int route) { Route=Math.Clamp(route,0,2); Revision++; }
    public void Draw(float level)
    {
        if (AdaptivePaging && SelectedCount == 0) return;
        if (!float.IsFinite(level)) return;
        level=Math.Clamp(level,0,1);
        if(!BeginDrawing(level)) return;
        Level = level; Touching = true;
        TouchPosition = new(TouchPosition.X, Level);
        foreach (var row in DrawingTargets) { row.Function.LoopRunning=false; row.Function.Level = Level; }
        if(recording is not null) recording.Latest=Level;
        Revision++;
    }
    public void DrawAt(PointF logical)
    {
        if (!float.IsFinite(logical.X) || !float.IsFinite(logical.Y)) return;
        var graph = PanelLayout.Graph;
        TouchPosition = new(Math.Clamp((logical.X - graph.X) / graph.Width, 0, 1), PanelLayout.Intensity(logical));
        Draw(TouchPosition.Y);
    }
    public void Release(bool commitRecording=true)
    {
        if(commitRecording) FinishRecording(); else if(Recording || RecordArmed) CancelRecording("Touch cancelled · holding");
        Touching=false; blockDrawingUntilRelease=false; AdvanceTo(Now); Revision++;
    }
    public void ZeroSelected()
    {
        CancelRecording("Selected functions zeroed");
        foreach (var row in Functions.Where(t => t.Selected)) { row.Function.LoopRunning=false; row.Function.Level = 0; }
        Level = 0; Touching = false; Revision++;
        TouchPosition = new(TouchPosition.X, 0);
    }
    public void StopAll()
    {
        CancelRecording("Stopped · recordings kept in memory"); blockDrawingUntilRelease=false;
        foreach (var row in Functions) { row.Function.LoopRunning=false; row.Function.Level = 0; }
        Level = 0; Touching = false; Revision++;
        TouchPosition = new(TouchPosition.X, 0);
    }
}

internal static class FunctionColors
{
    // Stable across pages, labels, ordering and launches; aliases are opaque preview IDs.
    public static Color For(string toyId, string functionId)
    {
        uint hash = 2166136261;
        foreach (var value in Encoding.UTF8.GetBytes(toyId + "\0" + functionId)) hash = unchecked((hash ^ value) * 16777619);
        // Avalanche the final bits so sibling IDs such as v1/v2 are visually distinct.
        hash ^= hash >> 16; hash = unchecked(hash * 0x7feb352d);
        hash ^= hash >> 15; hash = unchecked(hash * 0x846ca68b); hash ^= hash >> 16;
        var hue = (hash >> 8) / 16777216f * 6;
        var chroma = 0.60f; var minimum = 0.32f;
        var x = chroma * (1 - Math.Abs(hue % 2 - 1));
        var rgb = (int)hue switch
        {
            0 => new Vector3(chroma, x, 0), 1 => new Vector3(x, chroma, 0),
            2 => new Vector3(0, chroma, x), 3 => new Vector3(0, x, chroma),
            4 => new Vector3(x, 0, chroma), _ => new Vector3(chroma, 0, x)
        };
        rgb += new Vector3(minimum);
        return Color.FromArgb((int)(rgb.X * 255), (int)(rgb.Y * 255), (int)(rgb.Z * 255));
    }
    public static Vector4 Vector(Color color) => new(color.R / 255f, color.G / 255f, color.B / 255f, 1);
}

internal static class PanelLayout
{
    public const int Width = 1240, Height = 800;
    public static readonly RectangleF Graph = new(405, 230, 765, 352);
    public static RectangleF Row(int index) => new(40, 176 + index * 72, 268, 62);
    public static readonly RectangleF Previous = new(40, 648, 52, 36), Next = new(256, 648, 52, 36);
    public static readonly RectangleF Clear = new(365, 630, 135, 45), Zero = new(520, 630, 170, 45), Stop = new(710, 630, 150, 45), Demo = new(950, 630, 232, 45);
    public static readonly RectangleF Open = new(971, 735, 110, 38), Reload = new(1093, 735, 113, 38);
    public static readonly RectangleF Vr = new(820,735,137,38);
    public static readonly RectangleF Record=new(365,175,130,42), Play=new(510,175,155,42), Hold=new(680,175,140,42);
    public static RectangleF Destination(int route) => new(832 + 116 * route, 127, 100, 40);
    public static PointF Logical(Point point, Size size) => new(point.X * Width / (float)Math.Max(1, size.Width), point.Y * Height / (float)Math.Max(1, size.Height));
    public static float Intensity(PointF p) => Math.Clamp(1 - (p.Y - Graph.Y) / Graph.Height, 0, 1);
    public static int Hit(PointF p)
    {
        for (var i = 0; i < PanelModel.PageSize; i++) if (Row(i).Contains(p)) return i + 1;
        if (Graph.Contains(p)) return 10;
        if (Clear.Contains(p)) return 11; if (Zero.Contains(p)) return 12; if (Stop.Contains(p)) return 13;
        if (Demo.Contains(p)) return 14; if (Previous.Contains(p)) return 15; if (Next.Contains(p)) return 16;
        for (var r = 0; r < 3; r++) if (Destination(r).Contains(p)) return 20 + r;
        if (Open.Contains(p)) return 23; if (Reload.Contains(p)) return 24;
        if(Record.Contains(p)) return 25; if(Play.Contains(p)) return 26; if(Hold.Contains(p)) return 27;
        if(Vr.Contains(p)) return 28;
        return 0;
    }
}
