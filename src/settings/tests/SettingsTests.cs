using System.Text.Json.Nodes;
using MagneticBorders.Settings.Core;
using Xunit;

namespace MagneticBorders.Settings.Tests;

public sealed class TempDir : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("mbtests").FullName;
    public string File => Path.Combine(Dir, "settings.json");
    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
}

public class SettingsStoreTests
{
    [Fact]
    public void MissingFile_LoadsDefaults()
    {
        using var t = new TempDir();
        var s = new SettingsStore(t.File).Load();
        Assert.True(s.Enabled);
        Assert.True(s.GlueEnabled);
        Assert.Equal(12, s.GlueTolerancePx);
        Assert.True(s.SnapEnabled);
        Assert.Equal(16, s.SnapDistancePx);
        Assert.Empty(s.ExcludedApps);
        Assert.False(s.StartAtLogin);
        Assert.Equal(FlyoutBackdrop.Translucent, s.FlyoutBackdrop);
    }

    [Fact]
    public void PartialFile_FillsMissingKeysWithDefaults()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File, "{\"glue\":{\"tolerance_px\":40},\"excluded_apps\":[]}");
        var s = new SettingsStore(t.File).Load();
        Assert.Equal(40, s.GlueTolerancePx);
        Assert.True(s.GlueEnabled);
        Assert.True(s.Enabled);
        Assert.Empty(s.ExcludedApps);
    }

    [Fact]
    public void BadJson_LoadsDefaults_AndToleranceIsClamped()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File, "{ not json");
        Assert.Equal(12, new SettingsStore(t.File).Load().GlueTolerancePx);
        File.WriteAllText(t.File, "{\"glue\":{\"tolerance_px\":999}}");
        Assert.Equal(64, new SettingsStore(t.File).Load().GlueTolerancePx);
    }

    [Fact]
    public void Save_PreservesUnknownKeys_AtAllLevels()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File, "{\"schema\":1,\"future\":{\"a\":1},\"glue\":{\"enabled\":true,\"tolerance_px\":5,\"mode\":\"x\"}}");
        var store = new SettingsStore(t.File);
        var s = store.Load();
        s.GlueTolerancePx = 30;
        s.ExcludedApps.Add("game.exe");
        store.Save(s);
        var root = JsonNode.Parse(File.ReadAllText(t.File))!.AsObject();
        Assert.Equal(1, root["future"]!["a"]!.GetValue<int>());
        Assert.Equal("x", root["glue"]!["mode"]!.GetValue<string>());
        Assert.Equal(30, root["glue"]!["tolerance_px"]!.GetValue<int>());
        Assert.Contains("game.exe", store.Load().ExcludedApps);
    }

    [Theory]
    [InlineData("{\"ui\":{\"flyout_backdrop\":\"solid\"}}", FlyoutBackdrop.Solid)]
    [InlineData("{\"ui\":{\"flyout_backdrop\":\"translucent\"}}", FlyoutBackdrop.Translucent)]
    [InlineData("{\"ui\":{\"flyout_backdrop\":\"neon\"}}", FlyoutBackdrop.Translucent)]
    [InlineData("{\"ui\":{\"flyout_backdrop\":5}}", FlyoutBackdrop.Translucent)]
    [InlineData("{\"ui\":3}", FlyoutBackdrop.Translucent)]
    [InlineData("{}", FlyoutBackdrop.Translucent)]
    public void FlyoutBackdrop_ParsesWithFallback(string json, FlyoutBackdrop expected)
    {
        using var t = new TempDir();
        File.WriteAllText(t.File, json);
        Assert.Equal(expected, new SettingsStore(t.File).Load().FlyoutBackdrop);
    }

    [Fact]
    public void FlyoutBackdrop_RoundTrips_AndKeepsUnknownUiKeys()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File, "{\"ui\":{\"theme\":\"x\"}}");
        var store = new SettingsStore(t.File);
        var s = store.Load();
        s.FlyoutBackdrop = FlyoutBackdrop.Solid;
        store.Save(s);
        var root = JsonNode.Parse(File.ReadAllText(t.File))!.AsObject();
        Assert.Equal("solid", root["ui"]!["flyout_backdrop"]!.GetValue<string>());
        Assert.Equal("x", root["ui"]!["theme"]!.GetValue<string>());
        Assert.Equal(FlyoutBackdrop.Solid, store.Load().FlyoutBackdrop);
    }

    [Fact]
    public void Save_IsAtomic_NoTempFilesLeftAndFileAlwaysParses()
    {
        using var t = new TempDir();
        var store = new SettingsStore(t.File);
        for (var i = 0; i < 20; i++)
        {
            var s = store.Load();
            s.GlueTolerancePx = i;
            store.Save(s);
            Assert.NotNull(JsonNode.Parse(File.ReadAllText(t.File)));
        }
        Assert.Equal(new[] { t.File }, Directory.GetFiles(t.Dir));
        Assert.Equal(19, store.Load().GlueTolerancePx);
    }

    [Fact]
    public void Save_CreatesDirectory()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Dir, "sub", "settings.json");
        new SettingsStore(path).Save(new AppSettings());
        Assert.True(File.Exists(path));
    }
}

public class PipeResponseTests
{
    private static readonly string AppVersion = AppInfo.FromAssembly(typeof(SettingsViewModel).Assembly).Version;

    [Fact]
    public void ParsesStatus()
    {
        var r = PipeResponse.Parse("{\"ok\":true,\"engine\":\"cpp\",\"version\":\"" + AppVersion + "\",\"pid\":1234,\"enabled\":true," +
            "\"glue\":{\"enabled\":true,\"tolerance_px\":12},\"drags\":17,\"glued_drags\":9,\"uptime_s\":360}");
        Assert.True(r.Ok);
        var s = r.Status!;
        Assert.Equal("cpp", s.Engine);
        Assert.Equal(AppVersion, s.Version);
        Assert.Equal(1234, s.Pid);
        Assert.Equal(9, s.GluedDrags);
        Assert.Equal(360, s.UptimeSeconds);
    }

    [Fact]
    public void StatusWithoutPausedField_Parses()
    {
        var r = PipeResponse.Parse("{\"ok\":true,\"engine\":\"rust\",\"version\":\"1\",\"pid\":2,\"enabled\":true,\"drags\":1,\"glued_drags\":1,\"uptime_s\":1}");
        Assert.True(r.Ok);
        Assert.Equal("rust", r.Status!.Engine);
    }

    [Fact]
    public void StatusWithExtraPausedField_IsIgnored()
    {
        var r = PipeResponse.Parse("{\"ok\":true,\"engine\":\"cpp\",\"pid\":2,\"paused\":true}");
        Assert.True(r.Ok);
        Assert.NotNull(r.Status);
    }

    [Fact]
    public void ParsesErrorAndGarbage()
    {
        Assert.Equal("unknown command", PipeResponse.Parse("{\"ok\":false,\"error\":\"unknown command\"}").Error);
        Assert.False(PipeResponse.Parse("garbage").Ok);
        Assert.False(PipeResponse.Parse(null).Ok);
        Assert.False(PipeResponse.Parse("[1]").Ok);
    }
}

public class ViewModelTests
{
    private static readonly string AppVersion = AppInfo.FromAssembly(typeof(SettingsViewModel).Assembly).Version;

    private sealed class FakeClient : IEngineClient
    {
        public List<string> Sent = new();
        public Task<PipeResponse> SendAsync(string cmd, CancellationToken ct = default)
        {
            Sent.Add(cmd);
            return Task.FromResult(PipeResponse.Parse(
                "{\"ok\":true,\"engine\":\"rust\",\"version\":\"" + AppVersion + "\",\"pid\":1,\"enabled\":true," +
                "\"glue\":{\"enabled\":true,\"tolerance_px\":12},\"drags\":3,\"glued_drags\":2,\"uptime_s\":5}"));
        }
    }

    [Fact]
    public void Changes_AreSavedAfterFlush_WithoutOnLoadSave()
    {
        using var t = new TempDir();
        var store = new SettingsStore(t.File);
        using var vm = new SettingsViewModel(store, new FakeClient(), debounceMs: 10_000);
        Assert.False(File.Exists(t.File));
        vm.GlueTolerance = 40;
        vm.NewApp = "foo";
        vm.AddAppCommand.Execute(null);
        vm.FlushSave();
        var s = store.Load();
        Assert.Equal(40, s.GlueTolerancePx);
        Assert.Contains("foo.exe", s.ExcludedApps);
    }

    [Fact]
    public async Task Refresh_StoresStatus_AndSendsOnlyStatus()
    {
        using var t = new TempDir();
        var c = new FakeClient();
        using var vm = new SettingsViewModel(new SettingsStore(t.File), c);
        await vm.RefreshAsync();
        Assert.True(vm.EngineRunning);
        Assert.Equal(2, vm.Status!.GluedDrags);
        Assert.Equal(new[] { "status" }, c.Sent);
    }

    [Fact]
    public void ViewModel_HasNoPauseMembers()
    {
        var names = typeof(SettingsViewModel).GetMembers().Select(m => m.Name);
        Assert.DoesNotContain(names, n => n.Contains("Pause", StringComparison.OrdinalIgnoreCase) || n.Contains("Resume", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(EngineStatus).GetProperties(), p => p.Name == "Paused");
    }

    [Fact]
    public void Enabled_GatesFeatureControls_AndNotifies()
    {
        using var t = new TempDir();
        using var vm = new SettingsViewModel(new SettingsStore(t.File), new FakeClient(), debounceMs: 10_000);
        Assert.True(vm.FeaturesEnabled);
        Assert.True(vm.CouplingZoneEnabled);
        Assert.True(vm.AttractionZoneEnabled);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.Enabled = false;
        Assert.False(vm.FeaturesEnabled);
        Assert.False(vm.CouplingZoneEnabled);
        Assert.False(vm.AttractionZoneEnabled);
        Assert.Contains(nameof(vm.FeaturesEnabled), raised);
        Assert.Contains(nameof(vm.CouplingZoneEnabled), raised);
        Assert.Contains(nameof(vm.AttractionZoneEnabled), raised);
        vm.Enabled = true;
        vm.GlueEnabled = false;
        Assert.False(vm.CouplingZoneEnabled);
        Assert.True(vm.AttractionZoneEnabled);
        vm.SnapEnabled = false;
        Assert.False(vm.AttractionZoneEnabled);
    }

    [Fact]
    public void FlyoutBackdrop_ViewModelRoundTrip()
    {
        using var t = new TempDir();
        var store = new SettingsStore(t.File);
        using (var vm = new SettingsViewModel(store, new FakeClient(), debounceMs: 10_000))
        {
            Assert.Equal(0, vm.FlyoutBackdropIndex);
            vm.FlyoutBackdropIndex = 1;
            vm.FlyoutBackdropIndex = 7;   // ignored
            Assert.Equal(FlyoutBackdrop.Solid, vm.FlyoutBackdrop);
            vm.FlushSave();
        }
        Assert.Equal(FlyoutBackdrop.Solid, store.Load().FlyoutBackdrop);
        using var again = new SettingsViewModel(store, new FakeClient());
        Assert.Equal(1, again.FlyoutBackdropIndex);
    }
}
