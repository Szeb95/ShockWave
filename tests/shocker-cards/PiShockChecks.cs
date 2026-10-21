using System.Reflection;
using System.Text.Json;
using ShockWave.Overlay;

internal static class PiShockChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Private)!.GetValue(target)!;
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private)!.SetValue(target, value);
    private static void Call(object target, string method, params object[] values) => target.GetType().GetMethod(method, Private)!.Invoke(target, values);
    private static void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); Console.WriteLine("PASS " + detail); }
    private static IEnumerable<Control> All(Control root)
    { foreach (Control child in root.Controls) { yield return child; foreach (var next in All(child)) yield return next; } }

    public static void Run()
    {
        var original = new BridgeConfig { Provider = "PiShock", PiShockUsername = "fixture-user",
            Shockers = [new() { Key = "open-fixture", Name = "Open fixture", Uuid = "11111111-1111-1111-1111-111111111111", ManualMaxIntensity = 17, CooldownMs = 1600 }],
            SelfTestShockerKeys = ["open-fixture"], SelfTestEnabled = true,
            PiShockShockers = [new() { Service = "PiShock", Key = "pi-fixture", Name = "Pi fixture", Uuid = "42/7", ManualMaxIntensity = 23, MaxIntensity = 11 }] };
        var parsed = BridgeConfig.Parse(original.ToJson());
        Check(parsed.ActiveShockers.Count == 1 && parsed.ActiveShockers[0].IsPiShock && parsed.ActiveShockers[0].Uuid == "42/7", "PiShock selection uses its separate saved cards");
        Check(parsed.Validate("fixture-key") is null, "PiShock numeric target validates without an OpenShock UUID");
        Check(!parsed.ToJson().Contains("fixture-key"), "bridge settings contain no API key");
        var tokenDirectory = Path.Combine(AppContext.BaseDirectory, "pishock-token-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tokenDirectory);
        try
        {
            var openStore = new TokenStore(Path.Combine(tokenDirectory, "open.bin"));
            var piStore = new TokenStore(Path.Combine(tokenDirectory, "pi.bin"));
            openStore.Save("fixture-open-token", true); piStore.Save("fixture-pi-token", true);
            Check(openStore.Load() == "fixture-open-token" && piStore.Load() == "fixture-pi-token" && !System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(tokenDirectory, "pi.bin"))).Contains("fixture-pi-token"), "separate credential stores encrypt and reload both keys");
            piStore.Save("", false);
            Check(!File.Exists(Path.Combine(tokenDirectory, "pi.bin")) && openStore.Load() == "fixture-open-token", "forgetting PiShock credentials preserves the OpenShock token");
        }
        finally { foreach (var file in Directory.GetFiles(tokenDirectory)) File.Delete(file); Directory.Delete(tokenDirectory); }
        var duplicate = parsed.Clone(); duplicate.PiShockShockers.Add(new() { Service = "PiShock", Key = "another", Uuid = "42/7/30" });
        Check(duplicate.Validate("fixture-key") is not null, "alternate shares cannot assign the same physical shocker twice");
        Check(!ShockerConfig.ValidPiShockTarget("2147483648/1"), "PiShock identifiers have bounded numeric ranges");
        Check(parsed.PiShockShockers[0].MatchesControlAddress("/avatar/parameters/PiCtrl_Action") &&
            !parsed.PiShockShockers[0].MatchesControlAddress("/avatar/parameters/PiCtrl_FeedbackEvent"), "new card default avatar inputs route without accepting feedback");

        BridgeConfig? saved = null;
        using var form = new OverlayForm(parsed, initialSaveConfig: value => saved = value.Clone());
        Set(form, "apiToken", "fixture-open-key"); Set(form, "piShockApiKey", "fixture-pi-key"); Call(form, "FillSettings");
        var cards = Get<List<ShockerCard>>(form, "shockerCards");
        Check(cards.Count == 1 && cards[0].Device.IsPiShock, "only the selected provider's cards are displayed");
        var card = cards[0];
        card.SetRuntime(true, false);
        Check(!Get<Button>(card, "applyShares").Enabled, "PiShock cannot apply OpenShock account share changes");
        Check(All(card).Any(control => control.Text == "PiShock hub/shocker[/share]"), "PiShock options replace the UUID field label");
        Get<TextBox>(card, "nameInput").Text = "My local PiShock name"; Call(card, "SaveName");
        Check(saved?.PiShockShockers[0].Name == "My local PiShock name" && !saved.PiShockShockers[0].NameSyncPending, "PiShock names save locally without a cloud rename intent");
        Check(saved!.Shockers[0].ManualMaxIntensity == 17 && saved.Shockers[0].CooldownMs == 1600, "PiShock changes preserve OpenShock card settings");
        var maximum = Get<RangeSlider>(card, "otherIntensity"); maximum.SetRange(0, 30);
        Call(maximum, "OnKeyDown", new KeyEventArgs(Keys.Right)); card.FlushPendingChanges();
        Check(saved!.PiShockShockers[0].MaxIntensity > 11 && Get<System.Windows.Forms.Timer>(card, "autoApplyTimer").Enabled == false,
            "PiShock lower limits save without scheduling an OpenShock share write");
        var piCard = saved.PiShockShockers[0].Clone();

        Get<ComboBox>(form, "providerInput").SelectedItem = "OpenShock"; Call(form, "SaveSettings");
        Check(saved!.Provider == "OpenShock" && saved.Shockers[0].ManualMaxIntensity == 17 && saved.PiShockShockers[0].Uuid == "42/7", "provider switch retains both device lists");
        Check(Get<List<ShockerCard>>(form, "shockerCards")[0].Device.Key == "open-fixture", "provider switch displays OpenShock's original card");
        Check(saved.SelfTestEnabled && saved.SelfTestShockerKeys.SequenceEqual(["open-fixture"]) && !saved.PiShockSelfTestEnabled, "self-test selections remain separate by provider");
        Check(Get<TextBox>(form, "tokenInput").Text == "fixture-open-key" && Get<TextBox>(form, "piShockKeyInput").Text == "fixture-pi-key", "switching preserves separate credentials in memory");
        Check(All(Get<TableLayoutPanel>(form, "activityLayout")).Any(control => control.AccessibleName == "OpenShock API"), "status panels follow OpenShock selection");
        Get<ComboBox>(form, "providerInput").SelectedItem = "PiShock"; Call(form, "SaveSettings");
        Check(Get<List<ShockerCard>>(form, "shockerCards")[0].Device.Name == piCard.Name, "switching back restores PiShock's local name and limits");
        Check(All(Get<TableLayoutPanel>(form, "activityLayout")).Any(control => control.AccessibleName == "PiShock API"), "status panels follow PiShock selection");
        Call(form, "HandleBridgeLine", "bridge", "@api " + JsonSerializer.Serialize(new { kind = "api-status", connection = new { state = "Connected" },
            hub = new { state = "Unknown", total = 1, online = 0, detail = "No supported hub report.", shockers = new[] { new { key = "pi-fixture", state = "Unknown" } } },
            shockers = new[] { new { key = "pi-fixture", state = "Connected", detail = "Fixture account access verified." } } }));
        Check(Get<Label>(form, "apiStatus").Text.Contains("Connected") && Get<Label>(form, "hubStatus").Text.Contains("Unknown"), "PiShock API access does not invent hub connectivity");
        Call(form, "HandleBridgeLine", "bridge", "@devices {\"kind\":\"pishock-devices\",\"devices\":[{\"uuid\":\"42/7\",\"name\":\"Fixture\",\"shared\":false}]}");
        Check(((System.Collections.ICollection)Get<object>(form, "piShockDevices")).Count == 1, "PiShock inventory IPC is accepted without account secrets");
        Call(form, "AppendLog", "api", "fixture-open-key fixture-pi-key");
        Check(!Get<List<string>>(form, "recentLog").Last().Contains("fixture-"), "both credential types are redacted from displayed logs");

        // Render only hidden fixture controls: Shown never starts a real bridge,
        // OSC listener, remembered Bluetooth session or personal settings store.
        using var help = new ToolTip();
        using var renderCard = new ShockerCard(piCard, help) { Width = 243, Expanded = true };
        using var cardImage = new Bitmap(renderCard.Width, renderCard.Height);
        renderCard.DrawToBitmap(cardImage, new Rectangle(Point.Empty, cardImage.Size));
        cardImage.Save(Path.Combine(AppContext.BaseDirectory, "pishock-card.png"));
        using var settingsCard = Get<Panel>(form, "piShockSettingsCard"); settingsCard.Parent = null; settingsCard.Visible = true; settingsCard.Width = 496;
        using var settingsImage = new Bitmap(settingsCard.Width, settingsCard.Height);
        settingsCard.DrawToBitmap(settingsImage, new Rectangle(Point.Empty, settingsImage.Size));
        settingsImage.Save(Path.Combine(AppContext.BaseDirectory, "pishock-settings.png"));
        Console.WriteLine("PiShock native integration checks passed; no account or device was contacted.");
    }
}
