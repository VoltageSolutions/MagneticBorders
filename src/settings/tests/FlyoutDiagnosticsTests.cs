using MagneticBorders.Settings.Core;
using Xunit;

namespace MagneticBorders.Settings.Tests;

public class FlyoutDiagnosticsTests
{
    [Theory]
    [InlineData(FlyoutBackdrop.Solid, true, true, false, "solid", "setting is solid")]
    [InlineData(FlyoutBackdrop.Translucent, false, true, false, "solid-fallback", "DesktopAcrylicController is not supported")]
    [InlineData(FlyoutBackdrop.Translucent, true, false, false, "solid-fallback", "transparency effects are off")]
    [InlineData(FlyoutBackdrop.Translucent, true, true, true, "solid-fallback", "energy saver is on")]
    [InlineData(FlyoutBackdrop.Translucent, true, true, false, "acrylic", "setting is translucent")]
    public void ChooseBackdrop_ExplainsTheExpectedBackdrop(FlyoutBackdrop setting, bool acrylic, bool transparency, bool saver, string effective, string reason) =>
        Assert.Equal(new BackdropChoice(effective, reason), FlyoutDiagnostics.ChooseBackdrop(setting, acrylic, transparency, saver));

    [Fact]
    public void Hex_PadsToEightDigits()
    {
        Assert.Equal("0x00404040", FlyoutDiagnostics.Hex(0x404040));
        Assert.Equal("0x80070057", FlyoutDiagnostics.Hex(unchecked((int)0x80070057)));
    }

    [Fact]
    public void Lines_HaveTheDocumentedShape()
    {
        Assert.Equal(
            "flyout open os=10.0.26100.0 app_theme=Dark content_theme=Dark system_theme=dark high_contrast=off",
            FlyoutDiagnostics.Open("10.0.26100.0", "Dark", "Dark", "dark", false));
        Assert.Equal(
            "flyout backdrop setting=translucent effective=acrylic reason=\"setting is translucent\" desktop_acrylic_supported=true mica_supported=false transparency_effects=on",
            FlyoutDiagnostics.Backdrop(FlyoutBackdrop.Translucent, new("acrylic", "setting is translucent"), true, false, true));
        Assert.Equal("flyout power energy_saver=Off power_supply=Adequate", FlyoutDiagnostics.Power("Off", "Adequate"));
        Assert.Equal(
            "flyout dwm corner_preference=round hr=0x00000000 border_color=0x00404040 hr=0x00000000 border_color_readback=0x00404040 hr=0x00000000 frame_extend hr=0x00000000",
            FlyoutDiagnostics.Dwm(0, 0x404040, 0, 0x404040, 0, 0));
        Assert.Equal(
            "flyout window dpi=144 scale=1.50 size=540x450 position=1380,690 work_area=0,0,1920,1160",
            FlyoutDiagnostics.Window(144, 540, 450, 1380, 690, 0, 0, 1920, 1160));
        Assert.Equal("flyout activation state=Deactivated after_ms=812", FlyoutDiagnostics.Activation("Deactivated", 812));
        Assert.Equal("flyout closed after_ms=3000", FlyoutDiagnostics.Closed(3000));
    }

    [Fact]
    public void Window_TreatsAnUnknownDpiAsFullScale() =>
        Assert.Contains("scale=1.00", FlyoutDiagnostics.Window(0, 1, 1, 0, 0, 0, 0, 1, 1), StringComparison.Ordinal);
}

public class DiagnosticsLogTests
{
    [Fact]
    public void Write_AppendsTimestampedLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mb-difflog-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "logs", "settings.log");
        try
        {
            DiagnosticsLog.Write("flyout one", file);
            DiagnosticsLog.Write("flyout two", file);
            var lines = File.ReadAllLines(file);
            Assert.Equal(2, lines.Length);
            Assert.EndsWith(" flyout one", lines[0], StringComparison.Ordinal);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} flyout two$", lines[1]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Write_StartsANewFileAfterOneMegabyte()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mb-difflog-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "settings.log");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, new string('x', (int)DiagnosticsLog.MaxBytes + 1));
            DiagnosticsLog.Write("fresh", file);
            Assert.Single(File.ReadAllLines(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Write_NeverThrows() => DiagnosticsLog.Write("flyout", "\0bad");
}
