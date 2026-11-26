using MagneticBorders.Core;
using Xunit;

namespace MagneticBorders.Application.Tests;

public class SettingsParserTests
{
    [Fact]
    public void EmptyObjectGivesDefaults()
    {
        Assert.True(SettingsParser.TryParse("{}", out var m, out _));
        Assert.True(m.Enabled && m.GlueEnabled);
        Assert.Equal(12, m.GlueTolerancePx);
        Assert.Equal(16, m.SnapDistancePx);
    }

    [Fact]
    public void FullFileParses()
    {
        const string j = """{"schema":1,"enabled":false,"glue":{"enabled":false,"tolerance_px":30},"snap":{"enabled":false,"distance_px":5},"excluded_apps":["b.exe"," x.exe "],"start_at_login":true,"extra":1}""";
        Assert.True(SettingsParser.TryParse(j, out var m, out _));
        Assert.False(m.Enabled); Assert.False(m.GlueEnabled); Assert.False(m.SnapEnabled);
        Assert.Equal(30, m.GlueTolerancePx); Assert.Equal(5, m.SnapDistancePx);
        Assert.Equal(["b.exe", "x.exe"], m.ExcludedApps);
        Assert.True(m.StartAtLogin);
    }

    [Fact]
    public void PartialFileKeepsDefaultsForMissingKeys()
    {
        Assert.True(SettingsParser.TryParse("""{"glue":{"tolerance_px":0}}""", out var m, out _));
        Assert.Equal(0, m.GlueTolerancePx);
        Assert.True(m.GlueEnabled && m.Enabled);
    }

    [Fact]
    public void ToleranceIsClamped()
    {
        Assert.True(SettingsParser.TryParse("""{"glue":{"tolerance_px":500},"snap":{"distance_px":-3}}""", out var m, out _));
        Assert.Equal(64, m.GlueTolerancePx);
        Assert.Equal(0, m.SnapDistancePx);
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("")]
    [InlineData("[1]")]
    public void BadJsonFails(string j)
    {
        Assert.False(SettingsParser.TryParse(j, out _, out var err));
        Assert.NotNull(err);
    }
}

public class PipeProtocolTests
{
    static readonly string V = AppInfo.FromAssembly(typeof(EngineState).Assembly).Version;
    static readonly StatusSnapshot S = new("dotnet", V, 1234, true, true, 12, 17, 9, 360);

    [Fact]
    public void StatusExactShape()
    {
        var r = PipeProtocol.Handle("""{"cmd":"status"}""", S);
        Assert.Equal(PipeAction.None, r.Action);
        Assert.Equal($$"""{"ok":true,"engine":"dotnet","version":"{{V}}","pid":1234,"enabled":true,"glue":{"enabled":true,"tolerance_px":12},"drags":17,"glued_drags":9,"uptime_s":360}""", r.Response);
    }

    [Theory]
    [InlineData("reload", """{"ok":true}""", PipeAction.Reload)]
    [InlineData("quit", """{"ok":true}""", PipeAction.Quit)]
    public void Commands(string cmd, string resp, PipeAction action)
    {
        var r = PipeProtocol.Handle($"{{\"cmd\":\"{cmd}\"}}", S);
        Assert.Equal(resp, r.Response);
        Assert.Equal(action, r.Action);
    }

    [Theory]
    [InlineData("""{"cmd":"nope"}""")]
    [InlineData("""{"cmd":"pause"}""")]
    [InlineData("""{"cmd":"resume"}""")]
    [InlineData("garbage")]
    [InlineData("""{"x":1}""")]
    [InlineData("""{"cmd":5}""")]
    public void Unknown(string line)
    {
        var r = PipeProtocol.Handle(line, S);
        Assert.Equal("""{"ok":false,"error":"unknown command"}""", r.Response);
        Assert.Equal(PipeAction.None, r.Action);
    }
}

public class EngineStateTests
{
    static (EngineState, DragController) Make(out List<bool> autostart, out List<string> excl)
    {
        var snap = new Snapshot([new WinInfo(1, new Rect(0, 0, 100, 100), 1, 1)], new Rect(0, 0, 1000, 1000), 1.0);
        var c = new DragController(new FakeInventory(snap), new FakeMover(), new FakeClock(), new NullLog(), new FakeTimer());
        var a = new List<bool>(); var e = new List<string>();
        autostart = a; excl = e;
        return (new EngineState(c, new FakeClock(), new NullLog(), AppInfo.FromAssembly(typeof(EngineState).Assembly), 42, x => { e.Clear(); e.AddRange(x); }, a.Add), c);
    }

    private static readonly string[] expected = ["a.exe"];
    private static readonly bool[] expectedArray = [true];

    [Fact]
    public void ApplyPushesSettings()
    {
        var (st, c) = Make(out var auto, out var excl);
        Assert.True(SettingsParser.TryParse("""{"glue":{"tolerance_px":40},"excluded_apps":["a.exe"],"start_at_login":true}""", out var m, out _));
        st.Apply(m);
        Assert.Equal(40, c.TolerancePx);
        Assert.True(c.Active);
        Assert.Equal(expected, excl);
        Assert.Equal(expectedArray, auto);
    }

    [Fact]
    public void DisableAndCouplingSwitchDeactivate()
    {
        var (st, c) = Make(out _, out _);
        st.Apply(new SettingsModel());
        Assert.True(c.Active);
        Assert.DoesNotContain("paused", st.Handle("""{"cmd":"status"}"""));
        st.Apply(new SettingsModel { GlueEnabled = false });
        Assert.False(c.Active);
        st.Apply(new SettingsModel { Enabled = false });
        Assert.False(c.Active);
    }

    [Fact]
    public void WithEnabledPreservesUnknownKeys()
    {
        const string input = """{"schema":1,"enabled":true,"future":{"a":[1,2,{"b":null}]},"glue":{"enabled":true,"tolerance_px":12,"x":"y"},"zzz":"last"}""";
        Assert.True(SettingsParser.TryWithEnabled(input, false, out var output, out _));
        var expected = System.Text.Json.Nodes.JsonNode.Parse(input)!.AsObject();
        expected["enabled"] = false;
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(expected, System.Text.Json.Nodes.JsonNode.Parse(output)));
        Assert.True(SettingsParser.TryWithEnabled(output, true, out var back, out _));
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(input), System.Text.Json.Nodes.JsonNode.Parse(back)));
    }

    [Fact]
    public void WithEnabledAddsKeyAndRejectsNonObjects()
    {
        Assert.True(SettingsParser.TryWithEnabled("""{"keep":1}""", false, out var o, out _));
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse("""{"keep":1,"enabled":false}"""), System.Text.Json.Nodes.JsonNode.Parse(o)));
        Assert.True(SettingsParser.TryWithEnabled("  ", false, out o, out _));
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse("""{"enabled":false}"""), System.Text.Json.Nodes.JsonNode.Parse(o)));
        Assert.False(SettingsParser.TryWithEnabled("[1]", true, out _, out _));
        Assert.False(SettingsParser.TryWithEnabled("{ broken", true, out _, out _));
    }

    [Fact]
    public void ReloadAndQuitInvokeCallbacks()
    {
        var (st, _) = Make(out _, out _);
        int r = 0, q = 0;
        st.ReloadRequested = () => r++; st.QuitRequested = () => q++;
        st.Handle("""{"cmd":"reload"}""");
        st.Handle("""{"cmd":"quit"}""");
        Assert.Equal((1, 1), (r, q));
    }

    [Fact]
    public void StatusReportsPid()
    {
        var (st, _) = Make(out _, out _);
        Assert.Contains("\"pid\":42", st.Handle("""{"cmd":"status"}"""));
    }
}
