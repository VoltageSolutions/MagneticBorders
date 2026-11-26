using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json.Nodes;
using MagneticBorders.Settings.Core;
using Xunit;

namespace MagneticBorders.Settings.Tests;

public class AppSettingsTests
{
    private static AppSettings Parse(string json) => AppSettings.FromJson(JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void FromJson_Null_GivesDefaults()
    {
        var s = AppSettings.FromJson(null);
        Assert.True(s.Enabled);
        Assert.Equal(12, s.GlueTolerancePx);
        Assert.Empty(s.ExcludedApps);
        Assert.Equal(16, s.SnapDistancePx);
        Assert.Equal(24, s.SnapBreakawayPx);
        Assert.Equal(600, s.SnapMaxSpeed);
        Assert.True(s.SnapScreenEdges);
        Assert.True(s.SnapWindows);
    }

    [Fact]
    public void FromJson_ReadsNewSnapKeys()
    {
        var s = Parse("{\"snap\":{\"breakaway_px\":40,\"max_speed\":1500,\"screen_edges\":false,\"windows\":false}}");
        Assert.Equal(40, s.SnapBreakawayPx);
        Assert.Equal(1500, s.SnapMaxSpeed);
        Assert.False(s.SnapScreenEdges);
        Assert.False(s.SnapWindows);
    }

    [Fact]
    public void FromJson_NewSnapKeysWithWrongTypes_FallBackToDefaults()
    {
        var s = Parse("{\"snap\":{\"breakaway_px\":\"far\",\"max_speed\":false,\"screen_edges\":1,\"windows\":\"yes\"}}");
        Assert.Equal(24, s.SnapBreakawayPx);
        Assert.Equal(600, s.SnapMaxSpeed);
        Assert.True(s.SnapScreenEdges);
        Assert.True(s.SnapWindows);
    }

    [Theory]
    [InlineData("{\"snap\":{\"breakaway_px\":-1}}", 0)]
    [InlineData("{\"snap\":{\"breakaway_px\":128}}", 128)]
    [InlineData("{\"snap\":{\"breakaway_px\":129}}", 128)]
    [InlineData("{\"snap\":{\"breakaway_px\":30.6}}", 31)]
    public void FromJson_Breakaway_IsRoundedAndClamped(string json, int expected) =>
        Assert.Equal(expected, Parse(json).SnapBreakawayPx);

    [Theory]
    [InlineData("{\"snap\":{\"max_speed\":0}}", 100)]
    [InlineData("{\"snap\":{\"max_speed\":99}}", 100)]
    [InlineData("{\"snap\":{\"max_speed\":5000}}", 5000)]
    [InlineData("{\"snap\":{\"max_speed\":5001}}", 5000)]
    [InlineData("{\"snap\":{\"max_speed\":750.4}}", 750)]
    public void FromJson_MaxSpeed_IsRoundedAndClamped(string json, int expected) =>
        Assert.Equal(expected, Parse(json).SnapMaxSpeed);

    [Fact]
    public void WriteTo_NewSnapKeys_AreClampedAndUnknownSnapKeysSurvive()
    {
        var root = JsonNode.Parse("{\"snap\":{\"future\":\"x\"}}")!.AsObject();
        new AppSettings { SnapBreakawayPx = 500, SnapMaxSpeed = 5, SnapScreenEdges = false, SnapWindows = false }.WriteTo(root);
        var snap = root["snap"]!;
        Assert.Equal("x", snap["future"]!.GetValue<string>());
        Assert.Equal(128, snap["breakaway_px"]!.GetValue<int>());
        Assert.Equal(100, snap["max_speed"]!.GetValue<int>());
        Assert.False(snap["screen_edges"]!.GetValue<bool>());
        Assert.False(snap["windows"]!.GetValue<bool>());
    }

    [Fact]
    public void FromJson_ReadsEveryKnownKey()
    {
        var s = Parse("{\"enabled\":false,\"start_at_login\":true,\"glue\":{\"enabled\":false,\"tolerance_px\":5}," +
            "\"snap\":{\"enabled\":false,\"distance_px\":7},\"ui\":{\"flyout_backdrop\":\"solid\"},\"excluded_apps\":[\"a.exe\"]}");
        Assert.False(s.Enabled);
        Assert.True(s.StartAtLogin);
        Assert.False(s.GlueEnabled);
        Assert.Equal(5, s.GlueTolerancePx);
        Assert.False(s.SnapEnabled);
        Assert.Equal(7, s.SnapDistancePx);
        Assert.Equal(FlyoutBackdrop.Solid, s.FlyoutBackdrop);
        Assert.Equal(new[] { "a.exe" }, s.ExcludedApps);
    }

    [Fact]
    public void FromJson_WrongTypes_FallBackToDefaults()
    {
        var s = Parse("{\"enabled\":\"yes\",\"start_at_login\":3,\"glue\":{\"enabled\":1,\"tolerance_px\":\"x\"}," +
            "\"snap\":{\"enabled\":\"no\",\"distance_px\":true},\"excluded_apps\":\"notalist\"}");
        Assert.True(s.Enabled);
        Assert.False(s.StartAtLogin);
        Assert.True(s.GlueEnabled);
        Assert.Equal(12, s.GlueTolerancePx);
        Assert.True(s.SnapEnabled);
        Assert.Equal(16, s.SnapDistancePx);
        Assert.Empty(s.ExcludedApps);
    }

    [Theory]
    [InlineData("{\"glue\":{\"tolerance_px\":12.6}}", 13)]
    [InlineData("{\"glue\":{\"tolerance_px\":-4}}", 0)]
    [InlineData("{\"glue\":{\"tolerance_px\":64}}", 64)]
    [InlineData("{\"glue\":{\"tolerance_px\":65}}", 64)]
    public void FromJson_Tolerance_IsRoundedAndClamped(string json, int expected) =>
        Assert.Equal(expected, Parse(json).GlueTolerancePx);

    [Fact]
    public void FromJson_ExcludedApps_TrimsAndSkipsJunk()
    {
        var s = Parse("{\"excluded_apps\":[\"  game.exe \",\"\",\"   \",null,5,\"b.exe\"]}");
        Assert.Equal(new[] { "game.exe", "b.exe" }, s.ExcludedApps);
    }

    [Theory]
    [InlineData("solid", FlyoutBackdrop.Solid)]
    [InlineData("SOLID", FlyoutBackdrop.Solid)]
    [InlineData("translucent", FlyoutBackdrop.Translucent)]
    [InlineData("other", FlyoutBackdrop.Translucent)]
    [InlineData(null, FlyoutBackdrop.Translucent)]
    public void BackdropFromJson_MapsValues(string? json, FlyoutBackdrop expected) =>
        Assert.Equal(expected, AppSettings.BackdropFromJson(json));

    [Fact]
    public void BackdropToJson_MapsValues()
    {
        Assert.Equal("solid", AppSettings.BackdropToJson(FlyoutBackdrop.Solid));
        Assert.Equal("translucent", AppSettings.BackdropToJson(FlyoutBackdrop.Translucent));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(30, 30)]
    [InlineData(64, 64)]
    [InlineData(500, 64)]
    public void ClampZone_LimitsToRange(int input, int expected) =>
        Assert.Equal(expected, AppSettings.ClampZone(input));

    [Fact]
    public void Clone_DoesNotShareTheExcludedList()
    {
        var a = new AppSettings { GlueTolerancePx = 3 };
        var b = a.Clone();
        b.ExcludedApps.Add("x.exe");
        b.GlueTolerancePx = 9;
        Assert.Empty(a.ExcludedApps);
        Assert.Equal(3, a.GlueTolerancePx);
        Assert.Equal(9, b.GlueTolerancePx);
    }

    [Fact]
    public void WriteTo_WritesSchemaAndClampsAndKeepsUnknownKeys()
    {
        var root = JsonNode.Parse("{\"keep\":1,\"glue\":{\"extra\":true},\"ui\":{\"theme\":\"t\"}}")!.AsObject();
        new AppSettings { GlueTolerancePx = 900, SnapDistancePx = -2, FlyoutBackdrop = FlyoutBackdrop.Solid, StartAtLogin = true }.WriteTo(root);
        Assert.Equal(1, root["schema"]!.GetValue<int>());
        Assert.Equal(1, root["keep"]!.GetValue<int>());
        Assert.True(root["glue"]!["extra"]!.GetValue<bool>());
        Assert.Equal(64, root["glue"]!["tolerance_px"]!.GetValue<int>());
        Assert.Equal(0, root["snap"]!["distance_px"]!.GetValue<int>());
        Assert.Equal("t", root["ui"]!["theme"]!.GetValue<string>());
        Assert.Equal("solid", root["ui"]!["flyout_backdrop"]!.GetValue<string>());
        Assert.True(root["start_at_login"]!.GetValue<bool>());
        Assert.Empty(root["excluded_apps"]!.AsArray());
    }

    [Fact]
    public void WriteTo_ThenFromJson_RoundTrips()
    {
        var original = new AppSettings
        {
            Enabled = false,
            GlueEnabled = false,
            GlueTolerancePx = 20,
            SnapEnabled = false,
            SnapDistancePx = 33,
            SnapBreakawayPx = 70,
            SnapMaxSpeed = 2000,
            SnapScreenEdges = false,
            SnapWindows = false,
            StartAtLogin = true,
            FlyoutBackdrop = FlyoutBackdrop.Solid,
            ExcludedApps = new List<string> { "one.exe", "two.exe" },
        };
        var root = new JsonObject();
        original.WriteTo(root);
        var copy = AppSettings.FromJson(root);
        Assert.False(copy.Enabled);
        Assert.False(copy.GlueEnabled);
        Assert.Equal(20, copy.GlueTolerancePx);
        Assert.False(copy.SnapEnabled);
        Assert.Equal(33, copy.SnapDistancePx);
        Assert.Equal(70, copy.SnapBreakawayPx);
        Assert.Equal(2000, copy.SnapMaxSpeed);
        Assert.False(copy.SnapScreenEdges);
        Assert.False(copy.SnapWindows);
        Assert.True(copy.StartAtLogin);
        Assert.Equal(FlyoutBackdrop.Solid, copy.FlyoutBackdrop);
        Assert.Equal(original.ExcludedApps, copy.ExcludedApps);
    }
}

public class AppInfoTests
{
    private static AssemblyBuilder Dynamic(string? informationalVersion)
    {
        var ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("MbDynamic" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        if (informationalVersion is not null)
        {
            var ctor = typeof(AssemblyInformationalVersionAttribute).GetConstructor(new[] { typeof(string) })!;
            ab.SetCustomAttribute(new CustomAttributeBuilder(ctor, new object[] { informationalVersion }));
        }
        return ab;
    }

    [Fact]
    public void FromAssembly_StripsBuildMetadata() =>
        Assert.Equal("1.2.3", AppInfo.FromAssembly(Dynamic("1.2.3+abcdef")).Version);

    [Fact]
    public void FromAssembly_KeepsPlainVersion() =>
        Assert.Equal("4.5.6", AppInfo.FromAssembly(Dynamic("4.5.6")).Version);

    [Fact]
    public void FromAssembly_MissingAttribute_Gives000() =>
        Assert.Equal("0.0.0", AppInfo.FromAssembly(Dynamic(null)).Version);

    [Fact]
    public void FromEntryAssembly_ReturnsAVersion() =>
        Assert.False(string.IsNullOrEmpty(AppInfo.FromEntryAssembly().Version));

    [Fact]
    public void CoreAssembly_HasNoPlusInVersion() =>
        Assert.DoesNotContain('+', AppInfo.FromAssembly(typeof(AppInfo).Assembly).Version);
}
