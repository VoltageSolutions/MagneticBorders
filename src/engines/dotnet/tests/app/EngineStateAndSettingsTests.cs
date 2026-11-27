using System.Text.Json;
using MagneticBorders.Core;
using Xunit;

namespace MagneticBorders.Application.Tests;

public class SettingsParserEdgeCaseTests
{
    [Fact]
    public void Comments_and_trailing_commas_are_accepted()
    {
        const string j = """
            {
              // master switch
              "enabled": false,
              "excluded_apps": ["a.exe",],
            }
            """;
        Assert.True(SettingsParser.TryParse(j, out var m, out _));
        Assert.False(m.Enabled);
        Assert.Equal(["a.exe"], m.ExcludedApps);
    }

    [Fact]
    public void Values_of_the_wrong_type_fall_back_to_defaults()
    {
        const string j = """{"enabled":"yes","glue":5,"snap":[],"excluded_apps":"x","start_at_login":1}""";
        Assert.True(SettingsParser.TryParse(j, out var m, out _));
        Assert.Equal(new SettingsModel().GlueTolerancePx, m.GlueTolerancePx);
        Assert.True(m.Enabled && m.GlueEnabled && m.SnapEnabled);
        Assert.False(m.StartAtLogin);
        Assert.Empty(m.ExcludedApps);
    }

    [Fact]
    public void Fractional_tolerance_falls_back_to_default()
    {
        Assert.True(SettingsParser.TryParse("""{"glue":{"tolerance_px":1.5}}""", out var m, out _));
        Assert.Equal(12, m.GlueTolerancePx);
    }

    [Fact]
    public void Blank_and_non_string_excluded_apps_are_dropped()
    {
        Assert.True(SettingsParser.TryParse("""{"excluded_apps":["a.exe","","  ",5,null," b.exe"]}""", out var m, out _));
        Assert.Equal(["a.exe", "b.exe"], m.ExcludedApps);
    }

    [Fact]
    public void WithEnabled_accepts_comments_and_reports_a_parse_error_for_bad_text()
    {
        Assert.True(SettingsParser.TryWithEnabled("""{"a":1, /* c */ }""", false, out var o, out var err));
        Assert.Null(err);
        Assert.EndsWith("\n", o, StringComparison.Ordinal);
        Assert.False(SettingsParser.TryWithEnabled("{ broken", true, out _, out var err2));
        Assert.NotNull(err2);
    }
}

public class EngineStateTests2
{
    static (EngineState, FakeClock, RecordingLog, DragController) Make()
    {
        var snap = new Snapshot([new WinInfo(1, new Rect(0, 0, 100, 100), 1, 1)], new Rect(0, 0, 1000, 1000), 1.0);
        var clock = new FakeClock();
        var log = new RecordingLog();
        var c = new DragController(new FakeInventory(snap), new FakeMover(), clock, log, new FakeTimer());
        return (new EngineState(c, clock, log, new AppInfo("1.2.3"), 7, _ => { }, _ => { }), clock, log, c);
    }

    [Fact]
    public void Snap_not_implemented_is_logged_once_until_snap_is_turned_off_again()
    {
        var (st, _, log, _) = Make();
        st.Apply(new SettingsModel());
        st.Apply(new SettingsModel());
        Assert.Equal(1, log.Infos.Count(l => l.Contains("snap: not implemented", StringComparison.Ordinal)));
        st.Apply(new SettingsModel { SnapEnabled = false });
        st.Apply(new SettingsModel());
        Assert.Equal(2, log.Infos.Count(l => l.Contains("snap: not implemented", StringComparison.Ordinal)));
    }

    [Fact]
    public void Apply_raises_Changed_and_exposes_the_new_settings()
    {
        var (st, _, _, _) = Make();
        int changed = 0;
        st.Changed += () => changed++;
        var m = new SettingsModel { GlueEnabled = false };
        st.Apply(m);
        Assert.Equal(1, changed);
        Assert.Same(m, st.Settings);
        Assert.False(st.GlueActive);
    }

    [Fact]
    public void Status_reports_uptime_version_and_counters()
    {
        var (st, clock, _, _) = Make();
        clock.Now = 5_500;
        using var doc = JsonDocument.Parse(st.Handle("""{"cmd":"status"}"""));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("dotnet", root.GetProperty("engine").GetString());
        Assert.Equal("1.2.3", root.GetProperty("version").GetString());
        Assert.Equal(5, root.GetProperty("uptime_s").GetInt64());
        Assert.Equal(0, root.GetProperty("drags").GetInt64());
    }

    [Fact]
    public void Reload_and_quit_without_handlers_still_reply_ok()
    {
        var (st, _, _, _) = Make();
        Assert.Equal("""{"ok":true}""", st.Handle("""{"cmd":"reload"}"""));
        Assert.Equal("""{"ok":true}""", st.Handle("""{"cmd":"quit"}"""));
    }
}

public class AppInfoTests
{
    [Fact]
    public void FromAssembly_strips_build_metadata_and_never_returns_empty()
    {
        var v = AppInfo.FromAssembly(typeof(string).Assembly).Version;
        Assert.NotEmpty(v);
        Assert.DoesNotContain('+', v);
    }

    [Fact]
    public void FromAssembly_reads_the_version_stamped_on_the_application_assembly()
    {
        // The tests and the application share <Version> from Directory.Build.props, so this holds for any version.
        var expected = AppInfo.FromAssembly(typeof(AppInfoTests).Assembly).Version;
        Assert.Matches(@"^\d+\.\d+\.\d+$", expected);
        Assert.Equal(expected, AppInfo.FromAssembly(typeof(EngineState).Assembly).Version);
    }

    [Fact]
    public void Pipe_name_and_engine_name_are_the_shared_contract_values()
    {
        Assert.Equal("MagneticBorders", PipeProtocol.PipeName);
        Assert.Equal("dotnet", EngineInfo.Name);
    }
}
