using PadMapper.Core.Engine;
using PadMapper.Core.Output;
using PadMapper.Core.Profiles;
using PadMapper.Core.Startup;

namespace PadMapper.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "padmapper-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}

public class ProfileTests
{
    private const string SpecExample = """
    {
      "version": 1,
      "id": "game-default",
      "name": "ゲーム共通",
      "type": "Game",
      "bindings": [
        { "input": "M1", "trigger": "press",  "action": { "type": "key", "key": "F13" } },
        { "input": "M2", "trigger": "hold",   "holdMs": 500, "action": { "type": "profile", "id": "desktop" } },
        { "input": "C",  "trigger": "double", "action": { "type": "mouseButton", "button": "middle" } }
      ],
      "gyro": {
        "enabled": true,
        "output": "mouse",
        "sensitivityX": 1.0, "sensitivityY": 1.0,
        "deadzoneDps": 0.5, "smoothing": 0.3,
        "accel": { "enabled": false },
        "activation": { "mode": "whileHeld", "input": "LT" }
      },
      "overlay": { "trigger": { "input": "M4", "trigger": "hold", "holdMs": 600 } }
    }
    """;

    [Fact]
    public void SpecExample_Parses()
    {
        var p = ProfileJson.Deserialize<Profile>(SpecExample);
        Assert.Equal(ProfileType.Game, p.Type);
        Assert.Equal(3, p.Bindings.Count);
        Assert.Equal(TriggerKind.Hold, p.Bindings[1].Trigger);
        Assert.Equal(ActionType.Profile, p.Bindings[1].Action.Type);
        Assert.Equal(MouseButton.Middle, p.Bindings[2].Action.Button);
        Assert.Equal(GyroActivationMode.WhileHeld, p.Gyro.Activation.Mode);
        Assert.Equal(600, p.Overlay.Trigger!.HoldMs);
        var json = ProfileJson.Serialize(p);
        Assert.Contains("\"type\": \"Game\"", json);
        Assert.Contains("\"trigger\": \"hold\"", json);
        Assert.Contains("ゲーム共通", json);
    }

    [Fact]
    public void Store_CreatesDefaultsAndRoundTrips()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        var profiles = store.LoadProfiles();
        Assert.Contains(profiles, p => p.Id == DefaultProfiles.DesktopId);
        Assert.Contains(profiles, p => p.Id == DefaultProfiles.GameId);

        var p = profiles.First(x => x.Id == DefaultProfiles.GameId);
        p.Name = "変更後";
        store.SaveProfile(p);
        var again = new ProfileStore(dir.Path).LoadProfiles().First(x => x.Id == DefaultProfiles.GameId);
        Assert.Equal("変更後", again.Name);
    }

    [Fact]
    public void Store_KeepsFiveBackups()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        var p = DefaultProfiles.Desktop();
        for (int i = 0; i < 9; i++)
        {
            p.Name = "v" + i;
            store.SaveProfile(p);
            Thread.Sleep(2);
        }
        Assert.Equal(ProfileStore.BackupGenerations, store.BackupsOf(store.ProfilePath(p.Id)).Count());
    }

    [Fact]
    public void Store_FallsBackToBackupWhenBroken()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        var p = DefaultProfiles.Desktop();
        p.Name = "good";
        store.SaveProfile(p);
        p.Name = "newer";
        store.SaveProfile(p); // "good" がバックアップに入る
        File.WriteAllText(store.ProfilePath(p.Id), "{ broken json");

        var store2 = new ProfileStore(dir.Path);
        var loaded = store2.LoadProfiles().Single(x => x.Id == p.Id);
        Assert.Equal("good", loaded.Name);
        Assert.Contains(store2.Notices, n => n.Kind == LoadNoticeKind.RestoredFromBackup);
        Assert.True(File.Exists(store.ProfilePath(p.Id) + ".broken"));
    }

    [Fact]
    public void Store_SettingsRoundTrip_AndBrokenSettingsFallBack()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        var s = new AppSettings { Language = "en", Rules = { new AppRule { Match = new AppMatch { Exe = "game.exe" }, ProfileId = "game-default" } } };
        store.SaveSettings(s);
        var loaded = new ProfileStore(dir.Path).LoadSettings();
        Assert.Equal("en", loaded.Language);
        Assert.Single(loaded.Rules);

        File.WriteAllText(store.SettingsPath, "not json");
        var store3 = new ProfileStore(dir.Path);
        var fallback = store3.LoadSettings();
        Assert.Equal("ja", fallback.Language); // バックアップが無いので既定値
        Assert.Contains(store3.Notices, n => n.Kind == LoadNoticeKind.Failed);
    }

    [Fact]
    public void Store_ImportGivesUniqueId()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        var path = System.IO.Path.Combine(dir.Path, "in.json");
        store.Export(DefaultProfiles.Game(), path);
        var imported = store.Import(path, new[] { DefaultProfiles.GameId });
        Assert.Equal(DefaultProfiles.GameId + "-2", imported.Id);
    }

    [Fact]
    public void Resolver_FirstMatchWins()
    {
        var rules = new List<AppRule>
        {
            new() { Match = new AppMatch { Exe = "game" }, ProfileId = "g1" },
            new() { Match = new AppMatch { Exe = "game.exe" }, ProfileId = "g2" },
            new() { Match = new AppMatch { TitleRegex = "^Chrome.*YouTube" }, ProfileId = "yt" },
            new() { Match = new AppMatch { Path = @"C:\Games\*" }, ProfileId = "games" },
        };
        Assert.Equal("g1", ProfileResolver.Resolve(new ForegroundInfo("GAME.EXE", @"D:\x\game.exe", "", 1), rules, "def"));
        Assert.Equal("yt", ProfileResolver.Resolve(new ForegroundInfo("chrome.exe", "", "chrome - youtube", 1), rules, "def"));
        Assert.Equal("games", ProfileResolver.Resolve(new ForegroundInfo("a.exe", @"c:/games/sub/a.exe", "", 1), rules, "def"));
        Assert.Equal("def", ProfileResolver.Resolve(new ForegroundInfo("notepad.exe", @"C:\Windows\notepad.exe", "", 1), rules, "def"));
    }

    [Fact]
    public void Resolver_SkipsDisabledAndMissingProfiles_AndBadRegex()
    {
        var rules = new List<AppRule>
        {
            new() { Match = new AppMatch { Exe = "a.exe" }, ProfileId = "x", Enabled = false },
            new() { Match = new AppMatch { Exe = "a.exe" }, ProfileId = "missing" },
            new() { Match = new AppMatch { TitleRegex = "([" }, ProfileId = "bad" },
            new() { Match = new AppMatch { Exe = "a.exe", TitleRegex = "edit" }, ProfileId = "both" },
        };
        var existing = new HashSet<string> { "x", "bad", "both" };
        Assert.Equal("def", ProfileResolver.Resolve(new ForegroundInfo("a.exe", "", "view", 1), rules, "def", existing));
        Assert.Equal("both", ProfileResolver.Resolve(new ForegroundInfo("a.exe", "", "Editor", 1), rules, "def", existing));
    }

    [Fact]
    public void Validator_ReportsGameLimitations()
    {
        var p = new Profile
        {
            Id = "g", Type = ProfileType.Game,
            Sticks = new SticksSettings { Left = new StickSettings { Mode = StickMode.Mouse } },
            Bindings =
            {
                new Binding { Input = "LB", Trigger = TriggerKind.ModeShift, Layer = "s" },
                new Binding { Input = "A", Action = ActionDef.KeyPress("F1") },
                new Binding { Input = "M1", Action = ActionDef.KeyPress("F2") },
            },
        };
        var issues = ProfileValidator.Validate(p);
        Assert.Contains(issues, i => i.Code == "ModeShiftInGame" && i.Severity == IssueSeverity.Error);
        Assert.Contains(issues, i => i.Code == "StickInGame");
        Assert.Contains(issues, i => i.Code == "DoubleInputInGame" && i.BindingIndex == 1);
        Assert.DoesNotContain(issues, i => i.Code == "DoubleInputInGame" && i.BindingIndex == 2);
    }

    [Fact]
    public void Validator_DefaultProfilesHaveNoErrors()
    {
        var all = DefaultProfiles.Create();
        var ids = all.Select(p => p.Id).ToList();
        foreach (var p in all.Append(DefaultProfiles.UiNavigation()))
            Assert.DoesNotContain(ProfileValidator.Validate(p, ids), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Validator_FindsBadKeysAndMacros()
    {
        var p = new Profile
        {
            Id = "d",
            Bindings =
            {
                new Binding { Input = "A", Action = ActionDef.KeyPress("NoSuchKey") },
                new Binding { Input = "B", Action = new ActionDef { Type = ActionType.Macro, Macro = "tap Foo" } },
                new Binding { Input = "Q", Action = ActionDef.KeyPress("A") },
                new Binding { Input = "X", Action = new ActionDef { Type = ActionType.Profile, Id = "nope" } },
            },
        };
        var issues = ProfileValidator.Validate(p, new[] { "d" });
        Assert.Contains(issues, i => i.Code == "UnknownKey");
        Assert.Contains(issues, i => i.Code == "MacroInvalid");
        Assert.Contains(issues, i => i.Code == "UnknownInput");
        Assert.Contains(issues, i => i.Code == "ProfileMissing");
    }

    [Fact]
    public void Macro_ParsesAllCommands()
    {
        var steps = MacroParser.Parse("down LShift; tap a; up LShift\nwait 30; click right; scroll -2; hscroll 1; move 3 4; Ctrl+V", out var errors);
        Assert.Empty(errors);
        Assert.Equal(MacroStepKind.KeyDown, steps[0].Kind);
        Assert.Equal("LShift", steps[0].Key);
        Assert.Contains(steps, s => s.Kind == MacroStepKind.Wait && s.Ms == 30);
        Assert.Contains(steps, s => s.Kind == MacroStepKind.MouseDown && s.Button == MouseButton.Right);
        Assert.Contains(steps, s => s.Kind == MacroStepKind.Scroll && s.Amount == -2 && !s.Horizontal);
        Assert.Contains(steps, s => s.Kind == MacroStepKind.Scroll && s.Horizontal);
        Assert.Contains(steps, s => s.Kind == MacroStepKind.Move && s.Dx == 3 && s.Dy == 4);
        Assert.Equal("LCtrl", steps[^5].Key);

        MacroParser.Parse("jump 3; wait x; tap", out var bad);
        Assert.Equal(3, bad.Count);
    }

    [Fact]
    public void KeyCatalog_HasScanCodesAndAliases()
    {
        Assert.True(KeyCatalog.TryGet("F13", out var f13));
        Assert.Equal(0x64, f13.Scan);
        Assert.True(KeyCatalog.TryGet("ctrl", out var ctrl));
        Assert.Equal("LCtrl", ctrl.Name);
        Assert.True(KeyCatalog.TryGet("Up", out var up));
        Assert.True(up.Extended);
        Assert.True(KeyCatalog.TryGet("A", out var a));
        Assert.Equal(0x1E, a.Scan);
        Assert.True(KeyCatalog.TryGet("5", out var five));
        Assert.Equal(0x06, five.Scan);
        Assert.True(KeyCatalog.TryGet("Pause", out var pause));
        Assert.Equal(0, pause.Scan);
        Assert.False(KeyCatalog.TryGet("Nope", out _));
    }

    [Fact]
    public void TaskXml_HasRequiredSettings()
    {
        var xml = TaskXmlBuilder.Build(@"C:\Tools\PadMapper\PadMapper.exe", @"PC\user");
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
        Assert.Contains("<LogonTrigger>", xml);
        Assert.Contains("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>", xml);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
        Assert.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", xml);
        Assert.Contains("<Interval>PT1M</Interval>", xml);
        Assert.Contains("<Count>3</Count>", xml);
        Assert.Contains("--autostart", xml);
        Assert.Contains("encoding=\"utf-16\"", xml, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"C:\Tools\PadMapper\PadMapper.exe", TaskXmlBuilder.ReadCommand(xml));
    }

    [Fact]
    public void MouseAccumulator_KeepsFractions()
    {
        var m = new MouseAccumulator();
        int total = 0;
        for (int i = 0; i < 12; i++) total += m.AddMove(0.25, 0).dx;
        Assert.Equal(3, total);
        Assert.Equal(-1, m.AddMove(-1.2, 0).dx);
        Assert.Equal(60, m.AddScroll(0.5, false));
    }
}
