using System.Text.Json.Nodes;
using MagneticBorders.Settings.Core;
using Xunit;

namespace MagneticBorders.Settings.Tests;

public class SettingsStoreExtraTests
{
    [Fact]
    public void DefaultCtor_UsesDefaultPathUnderDataDir()
    {
        var store = new SettingsStore();
        Assert.Equal(SettingsStore.DefaultPath, store.Path);
        Assert.Equal(SettingsStore.DataDir, System.IO.Path.GetDirectoryName(store.Path));
        Assert.EndsWith("MagneticBorders", SettingsStore.DataDir, StringComparison.Ordinal);
        Assert.EndsWith("settings.json", SettingsStore.DefaultPath, StringComparison.Ordinal);
    }

    [Fact]
    public void LastWriteUtc_IsMinValueUntilSaved()
    {
        using var t = new TempDir();
        var store = new SettingsStore(t.File);
        Assert.Equal(DateTime.MinValue, store.LastWriteUtc);
        store.Save(new AppSettings());
        Assert.True(store.LastWriteUtc > DateTime.MinValue);
    }

    [Fact]
    public void Load_AcceptsCommentsAndTrailingCommas()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File, "{\n // note\n \"glue\": { \"tolerance_px\": 33, },\n}");
        Assert.Equal(33, new SettingsStore(t.File).Load().GlueTolerancePx);
    }

    [Fact]
    public void Load_NonObjectRoot_GivesDefaults()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File, "[1,2,3]");
        Assert.Equal(12, new SettingsStore(t.File).Load().GlueTolerancePx);
    }

    [Fact]
    public void Load_LockedFile_GivesDefaults()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File, "{\"glue\":{\"tolerance_px\":50}}");
        using var hold = new FileStream(t.File, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(12, new SettingsStore(t.File).Load().GlueTolerancePx);
    }

    [Fact]
    public void Save_OverExistingFile_ReplacesIt_AndLeavesNoTempFiles()
    {
        using var t = new TempDir();
        var store = new SettingsStore(t.File);
        store.Save(new AppSettings { GlueTolerancePx = 1 });
        store.Save(new AppSettings { GlueTolerancePx = 2 });
        Assert.Equal(2, store.Load().GlueTolerancePx);
        Assert.Single(Directory.GetFiles(t.Dir));
    }

    [Fact]
    public async Task Save_FromManyThreads_KeepsFileValid()
    {
        using var t = new TempDir();
        var store = new SettingsStore(t.File);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 10; n++) store.Save(new AppSettings { GlueTolerancePx = i });
        })));
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(t.File)));
        Assert.Single(Directory.GetFiles(t.Dir));
    }
}

public class SettingsViewModelExtraTests
{
    private sealed class ScriptedClient : IEngineClient
    {
        private readonly PipeResponse _reply;
        public ScriptedClient(PipeResponse reply) => _reply = reply;
        public List<string> Sent { get; } = new();
        public Task<PipeResponse> SendAsync(string cmd, CancellationToken ct = default)
        {
            Sent.Add(cmd);
            return Task.FromResult(_reply);
        }
    }

    private static ScriptedClient Down() => new(PipeResponse.Failure("engine not reachable"));

    private static ScriptedClient Up() =>
        new(PipeResponse.Parse("{\"ok\":true,\"engine\":\"dotnet\",\"version\":\"1\",\"pid\":4,\"drags\":2,\"glued_drags\":1,\"uptime_s\":3}"));

    private static SettingsViewModel Make(TempDir t, IEngineClient? client = null, int debounceMs = 60_000) =>
        new(new SettingsStore(t.File), client ?? Down(), debounceMs, "9.9.9");

    [Fact]
    public void Ctor_LoadsDefaults_AndExposesAppVersion()
    {
        using var t = new TempDir();
        using var vm = Make(t);
        Assert.Equal("9.9.9", vm.AppVersion);
        Assert.True(vm.Enabled);
        Assert.Equal(12, vm.GlueTolerance);
        Assert.Equal(16, vm.SnapDistance);
        Assert.Empty(vm.ExcludedApps);
        Assert.False(vm.EngineRunning);
        Assert.Equal("", vm.NewApp);
    }

    [Fact]
    public void Ctor_WithoutVersion_UsesEntryAssemblyVersion()
    {
        using var t = new TempDir();
        using var vm = new SettingsViewModel(new SettingsStore(t.File), Down());
        Assert.False(string.IsNullOrEmpty(vm.AppVersion));
    }

    [Fact]
    public void Ctor_LoadsExistingFile()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File, "{\"enabled\":false,\"start_at_login\":true,\"snap\":{\"enabled\":false,\"distance_px\":40},\"excluded_apps\":[\"x.exe\"]}");
        using var vm = Make(t);
        Assert.False(vm.Enabled);
        Assert.True(vm.StartAtLogin);
        Assert.False(vm.SnapEnabled);
        Assert.Equal(40, vm.SnapDistance);
        Assert.Equal(new[] { "x.exe" }, vm.ExcludedApps);
        Assert.False(File.Exists(t.File + ".tmp"));
    }

    [Fact]
    public void FlushSave_WithoutChanges_WritesNothing()
    {
        using var t = new TempDir();
        using var vm = Make(t);
        vm.FlushSave();
        Assert.False(File.Exists(t.File));
    }

    [Fact]
    public void FlushSave_RoundsAndClampsSliderValues()
    {
        using var t = new TempDir();
        using var vm = Make(t);
        vm.GlueTolerance = 12.6;
        vm.SnapDistance = 999;
        vm.FlushSave();
        var s = new SettingsStore(t.File).Load();
        Assert.Equal(13, s.GlueTolerancePx);
        Assert.Equal(64, s.SnapDistancePx);
        vm.GlueTolerance = -5;
        vm.FlushSave();
        Assert.Equal(0, new SettingsStore(t.File).Load().GlueTolerancePx);
    }

    [Fact]
    public void EachSetting_IsPersisted()
    {
        using var t = new TempDir();
        using (var vm = Make(t))
        {
            vm.Enabled = false;
            vm.StartAtLogin = true;
            vm.GlueEnabled = false;
            vm.SnapEnabled = false;
            vm.FlyoutBackdrop = FlyoutBackdrop.Solid;
            vm.FlushSave();
        }
        var s = new SettingsStore(t.File).Load();
        Assert.False(s.Enabled);
        Assert.True(s.StartAtLogin);
        Assert.False(s.GlueEnabled);
        Assert.False(s.SnapEnabled);
        Assert.Equal(FlyoutBackdrop.Solid, s.FlyoutBackdrop);
    }

    [Fact]
    public void Dispose_FlushesPendingChange()
    {
        using var t = new TempDir();
        var vm = Make(t);
        vm.GlueTolerance = 21;
        vm.Dispose();
        Assert.Equal(21, new SettingsStore(t.File).Load().GlueTolerancePx);
    }

    [Fact]
    public void Debounce_SavesWithoutAnExplicitFlush()
    {
        using var t = new TempDir();
        using var vm = Make(t, debounceMs: 30);
        vm.GlueTolerance = 17;
        Assert.True(SpinWait.SpinUntil(() => File.Exists(t.File), TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => new SettingsStore(t.File).Load().GlueTolerancePx == 17, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Save_KeepsUnknownKeysAlreadyInTheFile()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File, "{\"future\":{\"a\":1}}");
        using var vm = Make(t);
        vm.GlueTolerance = 8;
        vm.FlushSave();
        var root = JsonNode.Parse(File.ReadAllText(t.File))!.AsObject();
        Assert.Equal(1, root["future"]!["a"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("foo", "foo.exe")]
    [InlineData("  bar.exe  ", "bar.exe")]
    [InlineData("Game.v2", "Game.v2")]
    public void AddApp_NormalizesName(string typed, string expected)
    {
        using var t = new TempDir();
        using var vm = Make(t);
        vm.NewApp = typed;
        vm.AddAppCommand.Execute(null);
        Assert.Contains(expected, vm.ExcludedApps);
        Assert.Equal("", vm.NewApp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddApp_BlankInput_DoesNothing(string typed)
    {
        using var t = new TempDir();
        using var vm = Make(t);
        vm.NewApp = typed;
        vm.AddAppCommand.Execute(null);
        Assert.Empty(vm.ExcludedApps);
        vm.FlushSave();
        Assert.False(File.Exists(t.File));
    }

    [Fact]
    public void AddApp_Duplicate_IgnoresCase_AndClearsTheBox()
    {
        using var t = new TempDir();
        using var vm = Make(t);
        vm.NewApp = "Game.exe";
        vm.AddAppCommand.Execute(null);
        vm.NewApp = "GAME.EXE";
        vm.AddAppCommand.Execute(null);
        Assert.Single(vm.ExcludedApps);
        Assert.Equal("", vm.NewApp);
    }

    [Fact]
    public void RemoveApp_RemovesAndPersists_OrIgnoresUnknown()
    {
        using var t = new TempDir();
        using var vm = Make(t);
        vm.RemoveAppCommand.Execute(null);
        vm.RemoveAppCommand.Execute("nope.exe");
        vm.FlushSave();
        Assert.False(File.Exists(t.File));
        vm.NewApp = "Game.exe";
        vm.AddAppCommand.Execute(null);
        vm.RemoveAppCommand.Execute("Game.exe");
        Assert.Empty(vm.ExcludedApps);
        vm.FlushSave();
        Assert.Empty(new SettingsStore(t.File).Load().ExcludedApps);
    }

    [Fact]
    public async Task Refresh_EngineDown_ClearsStatus()
    {
        using var t = new TempDir();
        using var vm = Make(t, Up());
        await vm.RefreshAsync();
        Assert.True(vm.EngineRunning);
        using var t2 = new TempDir();
        using var down = Make(t2, Down());
        await down.RefreshAsync();
        Assert.Null(down.Status);
        Assert.False(down.EngineRunning);
    }

    [Fact]
    public async Task Refresh_RaisesEngineRunningChange()
    {
        using var t = new TempDir();
        using var vm = Make(t, Up());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        await vm.RefreshAsync();
        Assert.Contains(nameof(vm.EngineRunning), raised);
        Assert.Contains(nameof(vm.Status), raised);
    }

    [Fact]
    public async Task Refresh_PicksUpOutsideEdit_WhenNothingIsPending()
    {
        using var t = new TempDir();
        using var vm = Make(t);
        File.WriteAllText(t.File, "{\"enabled\":false,\"glue\":{\"tolerance_px\":44}}");
        await vm.RefreshAsync();
        Assert.False(vm.Enabled);
        Assert.Equal(44, vm.GlueTolerance);
        vm.FlushSave();   // loading must not have marked it dirty
        Assert.Contains("44", File.ReadAllText(t.File), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_KeepsUnsavedChanges_OverOutsideEdit()
    {
        using var t = new TempDir();
        using var vm = Make(t);
        vm.GlueTolerance = 30;
        File.WriteAllText(t.File, "{\"glue\":{\"tolerance_px\":44}}");
        await vm.RefreshAsync();
        Assert.Equal(30, vm.GlueTolerance);
    }

    [Fact]
    public void ReloadFromDisk_ReplacesExcludedApps_WithoutSaving()
    {
        using var t = new TempDir();
        using var vm = Make(t);
        File.WriteAllText(t.File, "{\"excluded_apps\":[\"a.exe\",\"b.exe\"]}");
        var before = File.ReadAllText(t.File);
        vm.ReloadFromDisk();
        Assert.Equal(new[] { "a.exe", "b.exe" }, vm.ExcludedApps);
        vm.FlushSave();
        Assert.Equal(before, File.ReadAllText(t.File));
    }

    [Fact]
    public async Task QuitEngine_SendsQuitThenStatus()
    {
        using var t = new TempDir();
        var client = Up();
        using var vm = Make(t, client);
        await vm.QuitEngineAsync();
        Assert.Equal(new[] { "quit", "status" }, client.Sent);
    }

    [Fact]
    public async Task Commands_AreExposedForBinding()
    {
        using var t = new TempDir();
        var client = Up();
        using var vm = Make(t, client);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.QuitEngineCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "status", "quit", "status" }, client.Sent);
    }

    [Fact]
    public void FlyoutBackdropIndex_MirrorsBackdrop_AndNotifies()
    {
        using var t = new TempDir();
        using var vm = Make(t);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.FlyoutBackdrop = FlyoutBackdrop.Solid;
        Assert.Equal(1, vm.FlyoutBackdropIndex);
        Assert.Contains(nameof(vm.FlyoutBackdropIndex), raised);
        vm.FlyoutBackdropIndex = -1;
        Assert.Equal(FlyoutBackdrop.Solid, vm.FlyoutBackdrop);
        vm.FlyoutBackdropIndex = 0;
        Assert.Equal(FlyoutBackdrop.Translucent, vm.FlyoutBackdrop);
    }
}
