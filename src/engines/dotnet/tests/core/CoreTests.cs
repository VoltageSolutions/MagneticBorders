using FixtureRunner;
using Xunit;

namespace MagneticBorders.Core.Tests;

public class FixtureTests
{
    static string FixturesDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var p = Path.Combine(d.FullName, "tests", "fixtures");
            if (Directory.Exists(p) && Directory.GetFiles(p, "*.layout").Length > 0) return p;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("fixtures dir not found");
    }

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(FixturesDir(), "*.layout").OrderBy(s => s, StringComparer.Ordinal)) data.Add(Path.GetFileName(f));
        return data;
    }

    [Theory, MemberData(nameof(Files))]
    public void Fixture_passes(string name)
    {
        var fx = Fixture.Parse(File.ReadAllText(Path.Combine(FixturesDir(), name)));
        Assert.Null(fx.Check());
    }
}

public class SeamTests
{
    static WinInfo W(ulong id, int l, int t, int r, int b, int minW = 1, int minH = 1) => new(id, new Rect(l, t, r, b), minW, minH);
    static readonly Rect Work = new(0, 0, 3440, 1440);

    [Fact]
    public void ScreenEdge_is_not_a_seam()
    {
        var ws = new[] { W(1, 0, 0, 1720, 1440), W(2, 1720, 0, 3440, 1440) };
        Assert.Null(SeamBuilder.Build(ws, 2, Edge.Right, Work, new Config(12)));
    }

    [Fact]
    public void Opposite_preferred_and_gap_recorded()
    {
        var ws = new[] { W(1, 0, 0, 1272, 1440), W(2, 1280, 0, 3440, 1440) };
        var s = SeamBuilder.Build(ws, 1, Edge.Right, Work, new Config(12))!;
        var m = Assert.Single(s.Members);
        Assert.Equal(Side.Opposite, m.Side);
        Assert.Equal(8, m.D);
    }

    [Fact]
    public void Propagate_clamps_to_min_width()
    {
        var ws = new[] { W(1, 0, 0, 1720, 1440), W(2, 1720, 0, 3440, 1440, 1600, 200) };
        var s = SeamBuilder.Build(ws, 1, Edge.Right, Work, new Config(12))!;
        Assert.Equal(1840, SeamBuilder.Propagate(s, 2020).ClampedPos);
    }

    [Fact]
    public void No_members_gives_null()
    {
        var ws = new[] { W(1, 0, 0, 1000, 1440), W(2, 2000, 0, 3440, 1440) };
        Assert.Null(SeamBuilder.Build(ws, 1, Edge.Right, Work, new Config(12)));
    }
}
