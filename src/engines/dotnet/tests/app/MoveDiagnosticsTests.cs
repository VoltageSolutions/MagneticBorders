using MagneticBorders.Core;
using MagneticBorders.Win32;
using Xunit;

namespace MagneticBorders.Application.Tests;

public class MoveDiagnosticsTests
{
    [Fact]
    public void RectAndDeltaText()
    {
        Assert.Equal("1,-2,30,40", MoveDiagnostics.RectText(new Rect(1, -2, 30, 40)));
        Assert.Equal(new Rect(1, 0, -2, 1), MoveDiagnostics.Delta(new Rect(0, 0, 100, 100), new Rect(1, 0, 98, 101)));
    }

    [Fact]
    public void HexAndFlagsText()
    {
        Assert.Equal("0x1a2b", MoveDiagnostics.HexId(0x1a2b));
        Assert.Equal("0x0214", MoveDiagnostics.FlagsText(0x214));
    }

    [Fact]
    public void BatchLineText() =>
        Assert.Equal("move: batch windows=3 mode=deferred flags=0x0214 elapsed_us=812 mismatches=1", MoveDiagnostics.BatchLine(3, "deferred", 0x214, 812, 1));

    [Fact]
    public void WindowLineOmitsDeltasWhenTheWindowMatches()
    {
        var r = new Rect(10, 10, 210, 110);
        Assert.Equal("move: hwnd=0xabc requested=10,10,210,110 actual=10,10,210,110", MoveDiagnostics.WindowLine(0xabc, r, r, default));
    }

    [Fact]
    public void WindowLineReportsARefusedSize() =>
        Assert.Equal(
            "move: hwnd=0x1 requested=0,0,100,100 actual=0,0,120,100 delta=0,0,20,0 window_delta=0,0,20,0",
            MoveDiagnostics.WindowLine(1, new Rect(0, 0, 100, 100), new Rect(0, 0, 120, 100), new Rect(0, 0, 20, 0)));

    [Fact]
    public void WindowLineWithoutReadBack() =>
        Assert.Equal("move: hwnd=0x2 requested=0,0,1,1 actual=unavailable", MoveDiagnostics.WindowLine(2, new Rect(0, 0, 1, 1), null, default));

    [Fact]
    public void FailureAndSkipLines()
    {
        Assert.Equal("move: deferred batch failed step=defer hwnd=0x10 error=5, falling back to SetWindowPos", MoveDiagnostics.DeferredFailureLine("defer", 0x10, 5));
        Assert.Equal("move: SetWindowPos failed hwnd=0x10 error=5", MoveDiagnostics.SetWindowPosFailureLine(0x10, 5));
        Assert.Equal("move: skipped hwnd=0x10 reason=hung", MoveDiagnostics.SkipLine(0x10, "hung"));
    }

    [Fact]
    public void SystemLines()
    {
        Assert.Equal(
            "diag: system os_build=22631.3880 dpi_awareness=per-monitor-v2 dwm_composition=on monitors=2",
            MoveDiagnostics.SystemLine("22631.3880", "per-monitor-v2", true, 2));
        Assert.Equal(
            "diag: monitor 0 rect=0,0,1920,1200 work=0,0,1920,1160 dpi=144x144 primary=yes",
            MoveDiagnostics.MonitorLine(0, new Rect(0, 0, 1920, 1200), new Rect(0, 0, 1920, 1160), 144, 144, true));
    }
}
