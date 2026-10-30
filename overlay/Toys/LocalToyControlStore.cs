using System.Text.Json;

namespace ShockWave.Overlay.Toys;

internal sealed record ToyControlPreference(string DeviceKey, string ChannelId, int Sensitivity = 100,
    int Maximum = 100, int DurationMs = 5000);

internal sealed class LocalToyControlStore(string? path = null)
{
    private sealed record Document(int Version, List<ToyControlPreference> Channels);
    private readonly Dictionary<(string, string), ToyControlPreference> preferences = [];
    private readonly HashSet<string> transient = new(StringComparer.Ordinal);
    private bool writable = true;
    public string? Warning { get; private set; }

    public void Load()
    {
        if (path is null || !File.Exists(path)) return;
        try
        {
            var data = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
            if (data is null || data.Version != 1 || data.Channels is null || data.Channels.Count > 4096)
                throw new InvalidDataException();
            foreach (var entry in data.Channels)
            {
                Validate(entry);
                if (!preferences.TryAdd((entry.DeviceKey, entry.ChannelId), entry)) throw new InvalidDataException();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            preferences.Clear(); writable = false;
            Warning = "Toy control settings could not be loaded. The original file has been kept; changes cannot be saved.";
        }
    }

    public ToyControlPreference Get(string device, string channel) => preferences.GetValueOrDefault((device, channel)) ?? new(device, channel);
    public void Save(ToyControlPreference preference, bool persistent = true)
    {
        Validate(preference);
        if (!writable) throw new InvalidOperationException(Warning);
        var next = new Dictionary<(string, string), ToyControlPreference>(preferences) { [(preference.DeviceKey, preference.ChannelId)] = preference };
        if (!persistent) transient.Add(preference.DeviceKey);
        if (path is not null && persistent)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(1, next.Values.Where(value => !transient.Contains(value.DeviceKey)).ToList())));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        preferences[(preference.DeviceKey, preference.ChannelId)] = preference;
    }

    private static void Validate(ToyControlPreference entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.DeviceKey) || entry.DeviceKey.Length > 128 ||
            entry.Sensitivity is < 0 or > 100 || entry.Maximum is < 0 or > 100 || entry.DurationMs is < 250 or > 15000)
            throw new InvalidDataException("Toy controls require 0–100% and 0.25–15 seconds.");
        _ = LovenseModelCatalog.Command(entry.ChannelId);
    }
}
