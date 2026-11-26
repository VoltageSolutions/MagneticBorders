using Xunit;

namespace MagneticBorders.Core.Tests;

public class GeometryTests
{
    [Fact]
    public void Rect_width_and_height_are_exclusive_extents()
    {
        var r = new Rect(10, 20, 110, 70);
        Assert.Equal(100, r.Width);
        Assert.Equal(50, r.Height);
    }

    [Theory]
    [InlineData(Edge.Left, Edge.Right)]
    [InlineData(Edge.Right, Edge.Left)]
    [InlineData(Edge.Top, Edge.Bottom)]
    [InlineData(Edge.Bottom, Edge.Top)]
    public void Opposite_returns_the_other_side_of_the_same_axis(Edge e, Edge expected) =>
        Assert.Equal(expected, e.Opposite());

    [Theory]
    [InlineData(Edge.Left, true)]
    [InlineData(Edge.Right, true)]
    [InlineData(Edge.Top, false)]
    [InlineData(Edge.Bottom, false)]
    public void Left_and_right_edges_are_vertical_lines(Edge e, bool vertical) =>
        Assert.Equal(vertical, e.IsVertical());

    [Theory]
    [InlineData(Edge.Left, 1)]
    [InlineData(Edge.Top, 2)]
    [InlineData(Edge.Right, 3)]
    [InlineData(Edge.Bottom, 4)]
    public void Coord_reads_the_matching_side(Edge e, int expected) =>
        Assert.Equal(expected, new Rect(1, 2, 3, 4).Coord(e));

    [Theory]
    [InlineData(Edge.Left, 9, 2, 3, 4)]
    [InlineData(Edge.Top, 1, 9, 3, 4)]
    [InlineData(Edge.Right, 1, 2, 9, 4)]
    [InlineData(Edge.Bottom, 1, 2, 3, 9)]
    public void With_moves_only_the_named_side(Edge e, int l, int t, int r, int b) =>
        Assert.Equal(new Rect(l, t, r, b), new Rect(1, 2, 3, 4).With(e, 9));
}

public class SeamBuilderTests
{
    static WinInfo W(ulong id, int l, int t, int r, int b, int minW = 1, int minH = 1) => new(id, new Rect(l, t, r, b), minW, minH);
    static readonly Rect Work = new(0, 0, 3000, 3000);
    static readonly Config Cfg = new(12);

    [Fact]
    public void Build_returns_null_when_the_dragged_window_is_not_in_the_snapshot()
    {
        Assert.Null(SeamBuilder.Build([W(2, 0, 0, 100, 100)], 1, Edge.Right, Work, Cfg));
    }

    [Fact]
    public void Bottom_edge_builds_a_horizontal_seam_and_moves_the_top_of_the_window_below()
    {
        var ws = new[] { W(1, 0, 0, 1000, 1000), W(2, 0, 1000, 1000, 2000) };
        var seam = SeamBuilder.Build(ws, 1, Edge.Bottom, Work, Cfg)!;
        Assert.False(seam.Vertical);
        var plan = SeamBuilder.Propagate(seam, 1100);
        Assert.Equal(1100, plan.ClampedPos);
        Assert.Equal((2UL, new Rect(0, 1100, 1000, 2000)), Assert.Single(plan.Moves));
    }

    [Fact]
    public void Window_sharing_the_dragged_edge_moves_with_it_and_clamps_to_its_minimum()
    {
        var ws = new[] { W(1, 0, 0, 1000, 500), W(3, 200, 500, 1000, 1000, minW: 300) };
        var seam = SeamBuilder.Build(ws, 1, Edge.Right, Work, Cfg)!;
        Assert.Equal(Side.Same, Assert.Single(seam.Members).Side);
        Assert.Equal((3UL, new Rect(200, 500, 1100, 1000)), Assert.Single(SeamBuilder.Propagate(seam, 1100).Moves));
        Assert.Equal(500, SeamBuilder.Propagate(seam, 400).ClampedPos);
    }

    [Fact]
    public void Learned_minimum_is_applied_on_top_of_the_declared_one()
    {
        var ws = new[] { W(1, 0, 0, 1720, 1440), W(2, 1720, 0, 3000, 1440, 1000, 1) };
        var seam = SeamBuilder.Build(ws, 1, Edge.Right, Work, Cfg)!;
        var learned = new Dictionary<ulong, (int W, int H)> { [2] = (1200, 0) };
        Assert.Equal(1800, SeamBuilder.Propagate(seam, 2000, learned).ClampedPos);
        Assert.Equal(2000, SeamBuilder.Propagate(seam, 2000, new Dictionary<ulong, (int W, int H)> { [9] = (5000, 0) }).ClampedPos);
    }

    [Fact]
    public void Conflicting_minimums_keep_the_seam_where_it_was()
    {
        var ws = new[]
        {
            W(1, 0, 0, 1000, 500),
            W(2, 1000, 0, 2000, 500, minW: 800),   // hi = 1200
            W(3, 800, 500, 1000, 1000, minW: 500), // lo = 1300
        };
        var seam = SeamBuilder.Build(ws, 1, Edge.Right, Work, Cfg)!;
        Assert.Equal(2, seam.Members.Count);
        var plan = SeamBuilder.Propagate(seam, 1500);
        Assert.Equal(1000, plan.ClampedPos);
        Assert.All(plan.Moves, m => Assert.Equal(1000, m.Frame.Coord(m.Id == 2 ? Edge.Left : Edge.Right)));
    }

    [Fact]
    public void Horizontal_minimum_height_clamps_a_horizontal_seam()
    {
        var ws = new[] { W(1, 0, 0, 1000, 1000), W(2, 0, 1000, 1000, 2000, minH: 700) };
        var seam = SeamBuilder.Build(ws, 1, Edge.Bottom, Work, Cfg)!;
        Assert.Equal(1300, SeamBuilder.Propagate(seam, 1900).ClampedPos);
    }
}
