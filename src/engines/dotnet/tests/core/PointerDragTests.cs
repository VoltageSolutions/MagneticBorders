using Xunit;

namespace MagneticBorders.Core.Tests;

public class PointerDragTests
{
    static readonly Rect Work = new(0, 0, 1000, 800);
    /// <summary>The dragged window: 100x100 at (100, 400), grabbed with the cursor at (150, 410).</summary>
    static readonly Rect Frame = new(100, 400, 200, 500);
    static readonly Point Grab = new(150, 410);

    static Attraction ScreenEdges() => Attraction.FromFrames(new AttractionConfig(), Array.Empty<Rect>(), Work, new SideTargets());

    /// <summary>A PointerDrag with only screen edges as targets and a clock that advances 100 ms per move (never fast). Windows reports each mouse movement as the cursor's position plus that movement, so Nudge does the same.</summary>
    sealed class Rig
    {
        private Point _cursor = Grab;
        private ulong _t;

        public PointerDrag D { get; } = new(ScreenEdges(), Frame, Grab);

        public Point Nudge(int dx, int dy)
        {
            _t += 100;
            _cursor = D.Move(new Point(_cursor.X + dx, _cursor.Y + dy), _t);
            return _cursor;
        }
    }

    [Fact]
    public void The_cursor_goes_where_the_mouse_sends_it_when_nothing_is_held() // SNAP-072
    {
        var r = new Rig();
        Assert.Equal(new Point(160, 420), r.Nudge(10, 10));
        Assert.Equal(new Rect(110, 410, 210, 510), r.D.Window);
        Assert.False(r.D.Attraction.IsHeld);
    }

    [Fact]
    public void Pull_in_moves_the_cursor_so_the_window_lands_on_the_line() // SNAP-040, SNAP-072
    {
        var r = new Rig();
        // The window's left edge would be at 10: pulled to 0, so the cursor lands 10 px further left.
        Assert.Equal(new Point(50, 410), r.Nudge(-90, 0));
        Assert.Equal(0, r.D.Window.L);
        Assert.True(r.D.Attraction.HeldX);
    }

    [Fact]
    public void A_held_axis_keeps_the_cursor_still_while_the_other_axis_slides() // SNAP-022, SNAP-071
    {
        var r = new Rig();
        r.Nudge(-90, 0); // held at x = 0, cursor x 50
        Assert.Equal(new Point(50, 450), r.Nudge(-5, 40)); // pushing left and down: only y moves
        Assert.Equal(new Point(50, 500), r.Nudge(8, 50)); // a small wiggle right: x stays
        Assert.Equal(new Rect(0, 490, 100, 590), r.D.Window);
    }

    [Fact]
    public void Breakaway_lets_go_without_a_jump() // SNAP-041, SNAP-073
    {
        var r = new Rig();
        r.Nudge(-90, 0); // free left edge 10, held at 0, cursor x 50
        // Push left: the free position builds up (0, then -20) while the cursor stays.
        Assert.Equal(new Point(50, 410), r.Nudge(-10, 0));
        Assert.Equal(new Point(50, 410), r.Nudge(-20, 0));
        // The next push takes the free position to -25, past the 24 px breakaway: the cursor moves by this push only.
        Assert.Equal(new Point(45, 410), r.Nudge(-5, 0));
        Assert.False(r.D.Attraction.HeldX);
        Assert.Equal(-5, r.D.Window.L);
    }

    [Fact]
    public void After_breakaway_the_line_waits_until_the_window_leaves_the_zone() // SNAP-042, SNAP-074
    {
        var r = new Rig();
        r.Nudge(-90, 0);
        r.Nudge(-30, 0);
        r.Nudge(-5, 0); // let go, window left edge at -5
        Assert.False(r.D.Attraction.HeldX);
        // Back toward the line: still blocked, the cursor moves freely.
        Assert.Equal(new Point(55, 410), r.Nudge(10, 0));
        Assert.False(r.D.Attraction.HeldX);
        // Out of the zone (left edge 25, more than 16 px away), then back in: held again.
        Assert.Equal(new Point(75, 410), r.Nudge(20, 0));
        Assert.Equal(new Point(50, 410), r.Nudge(-10, 0));
        Assert.True(r.D.Attraction.HeldX);
    }

    [Fact]
    public void A_corner_holds_the_cursor_on_both_axes() // SNAP-021
    {
        var r = new Rig();
        // Left edge 10 from 0 and bottom edge 795 from 800: both pulled.
        Assert.Equal(new Point(50, 710), r.Nudge(-90, 295));
        Assert.Equal(new Point(50, 710), r.Nudge(-3, 3));
        Assert.Equal(new Rect(0, 700, 100, 800), r.D.Window);
    }

    [Fact]
    public void A_fast_move_passes_through_the_line() // SNAP-012
    {
        var d = new PointerDrag(ScreenEdges(), Frame, Grab);
        d.Move(new Point(150, 410), 0); // the first move gives the speed its first sample
        // 90 px in 10 ms is 9000 px/s: nothing is held.
        Assert.Equal(new Point(60, 410), d.Move(new Point(60, 410), 10));
        Assert.False(d.Attraction.IsHeld);
    }
}
