using Xunit;

namespace MagneticBorders.Core.Tests;

public class AttractionTests
{
    static readonly Rect Work = new(0, 0, 1000, 800);
    /// <summary>A window at x 300..500, y 300..500.</summary>
    static readonly Rect W = new(300, 300, 500, 500);

    /// <summary>A 100x100 window with its top-left at (l, t).</summary>
    static Rect Box(int l, int t) => new(l, t, l + 100, t + 100);

    /// <summary>Config with only the given switches on.</summary>
    static AttractionConfig Only(bool screen, bool windows) => new() { ScreenEdges = screen, Windows = windows };

    /// <summary>An Attraction on Work plus a clock that advances 100 ms per step, so steps never count as fast.</summary>
    sealed class Rig
    {
        public Attraction A { get; }
        public ulong T { get; private set; }

        public Rig(IReadOnlyList<Rect>? others = null, AttractionConfig? cfg = null, SideTargets? sides = null) =>
            A = Attraction.FromFrames(cfg ?? new AttractionConfig(), others ?? Array.Empty<Rect>(), Work, sides ?? new SideTargets());

        public Rect Step(Rect free)
        {
            T += 100;
            return A.Update(free, T);
        }
    }

    static Attraction Plain(AttractionConfig? cfg = null) =>
        Attraction.FromFrames(cfg ?? new AttractionConfig(), Array.Empty<Rect>(), Work, new SideTargets());

    [Fact]
    public void Pulls_an_edge_within_the_pull_in_distance_onto_the_screen_edge() // SNAP-010, SNAP-030, SNAP-040
    {
        var r = new Rig();
        Assert.Equal(new Rect(0, 400, 100, 500), r.Step(Box(10, 400)));
        Assert.True(r.A.IsHeld);
        Assert.Equal(1, r.A.Snaps);
        var right = new Rig();
        Assert.Equal(new Rect(900, 400, 1000, 500), right.Step(Box(890, 400)));
    }

    [Fact]
    public void Pull_in_distance_is_inclusive() // SNAP-010
    {
        Assert.Equal(Box(0, 400), new Rig().Step(Box(16, 400)));
        var outside = new Rig();
        Assert.Equal(Box(17, 400), outside.Step(Box(17, 400)));
        Assert.False(outside.A.IsHeld);
    }

    [Fact]
    public void Snaps_top_and_bottom_to_the_work_area() // SNAP-030
    {
        Assert.Equal(Box(400, 0), new Rig().Step(Box(400, 12)));
        Assert.Equal(Box(400, 700), new Rig().Step(Box(400, 690)));
    }

    [Fact]
    public void The_size_never_changes()
    {
        var o = new Rig().Step(new Rect(10, 12, 410, 312));
        Assert.Equal(400, o.Width);
        Assert.Equal(300, o.Height);
    }

    [Fact]
    public void Zero_distance_holds_without_moving()
    {
        var r = new Rig();
        Assert.Equal(Box(0, 400), r.Step(Box(0, 400)));
        Assert.True(r.A.IsHeld);
    }

    [Fact]
    public void A_negative_pull_in_distance_is_treated_as_zero()
    {
        var cfg = new AttractionConfig { PullIn = -5 };
        var on = new Rig(null, cfg);
        Assert.Equal(Box(0, 400), on.Step(Box(0, 400)));
        Assert.True(on.A.IsHeld);
        var off = new Rig(null, cfg);
        Assert.Equal(Box(1, 400), off.Step(Box(1, 400)));
        Assert.False(off.A.IsHeld);
    }

    [Fact]
    public void A_fast_move_does_not_start_a_snap() // SNAP-012, SNAP-040
    {
        var a = Plain();
        a.Update(Box(500, 400), 0);
        var o = a.Update(Box(10, 400), 10); // 49 000 px/s
        Assert.Equal(Box(10, 400), o);
        Assert.False(a.IsHeld);
        Assert.True(a.Speed > 600.0);
    }

    [Fact]
    public void The_snapping_speed_is_inclusive() // SNAP-012
    {
        var at = Plain();
        at.Update(Box(40, 400), 0);
        Assert.Equal(Box(0, 400), at.Update(Box(10, 400), 50)); // exactly 600 px/s
        var over = Plain();
        over.Update(Box(41, 400), 0);
        Assert.Equal(Box(10, 400), over.Update(Box(10, 400), 50)); // 620 px/s
    }

    [Fact]
    public void The_speed_limit_scales_with_the_config() // SNAP-012
    {
        var a = Plain(new AttractionConfig { MaxSpeed = 1000.0 });
        a.Update(Box(50, 400), 0);
        Assert.Equal(Box(0, 400), a.Update(Box(10, 400), 50)); // 800 px/s is within 1000
    }

    [Fact]
    public void A_speed_limit_does_not_release_a_held_edge() // SNAP-014
    {
        var r = new Rig();
        Assert.Equal(Box(0, 400), r.Step(Box(10, 400)));
        var o = r.A.Update(Box(5, 450), r.T + 10); // 5 000 px/s
        Assert.Equal(Box(0, 450), o);
        Assert.True(r.A.IsHeld);
    }

    [Fact]
    public void The_speed_is_zero_at_the_start_and_measured_over_the_last_80_ms() // SNAP-013
    {
        var a = Plain();
        Assert.Equal(0.0, a.Speed);
        a.Update(Box(500, 400), 1000);
        Assert.Equal(0.0, a.Speed); // a single position
        a.Update(Box(510, 400), 1010);
        Assert.Equal(1000.0, a.Speed, 6);
        a.Update(Box(520, 430), 1020); // from (500,400) over 20 ms: hypot(20,30) / 0.02
        Assert.Equal(1802.8, a.Speed, 1);
    }

    [Fact]
    public void With_fewer_than_two_positions_the_last_speed_is_kept() // SNAP-013
    {
        var a = Plain();
        a.Update(Box(500, 400), 1000);
        a.Update(Box(510, 400), 1010);
        Assert.Equal(1000.0, a.Speed, 6);
        a.Update(Box(512, 400), 1500); // the earlier positions are older than 80 ms
        Assert.Equal(1000.0, a.Speed, 6);
        a.Update(Box(512, 400), 1500); // same millisecond: no time has passed
        Assert.Equal(1000.0, a.Speed, 6);
    }

    [Fact]
    public void A_held_edge_slides_along_the_line() // SNAP-022
    {
        var r = new Rig();
        Assert.Equal(Box(0, 100), r.Step(Box(10, 100)));
        Assert.Equal(Box(0, 200), r.Step(Box(5, 200)));
        Assert.Equal(Box(0, 300), r.Step(Box(8, 300)));
    }

    [Fact]
    public void Breakaway_releases_past_the_breakaway_distance_and_jumps_back() // SNAP-041
    {
        var r = new Rig();
        Assert.Equal(Box(0, 400), r.Step(Box(10, 400)));
        Assert.Equal(Box(0, 400), r.Step(Box(-24, 400))); // exactly 24 away: still held
        Assert.Equal(Box(-25, 400), r.Step(Box(-25, 400))); // the window is back under the mouse
        Assert.False(r.A.IsHeld);
        var events = r.A.TakeEvents();
        Assert.Equal(2, events.Count);
        Assert.False(events[1].Engaged);
        Assert.Equal(Edge.Left, events[1].Edge);
        Assert.Equal(0, events[1].Line);
        Assert.Equal(ReleaseReason.Breakaway, events[1].Reason);
    }

    [Fact]
    public void A_breakaway_below_the_pull_in_distance_uses_the_pull_in_distance() // SNAP-043
    {
        var r = new Rig(null, new AttractionConfig { Breakaway = 5 });
        Assert.Equal(Box(0, 400), r.Step(Box(10, 400)));
        Assert.Equal(Box(0, 400), r.Step(Box(-16, 400))); // 16 is the pull-in distance
        Assert.Equal(Box(-17, 400), r.Step(Box(-17, 400)));
    }

    [Fact]
    public void A_released_edge_can_be_held_again_after_leaving_the_zone() // SNAP-042
    {
        var r = new Rig();
        r.Step(Box(10, 400));
        Assert.Equal(Box(-30, 400), r.Step(Box(-30, 400))); // breakaway
        Assert.Equal(Box(-30, 400), r.Step(Box(-30, 400))); // a later position out of the zone unblocks the line
        Assert.Equal(Box(0, 400), r.Step(Box(12, 400)));
        Assert.Equal(2, r.A.Snaps);
    }

    [Fact]
    public void A_released_line_stays_blocked_when_the_next_position_is_back_in_the_zone() // SNAP-042
    {
        var r = new Rig();
        r.Step(Box(10, 400));
        Assert.Equal(Box(-30, 400), r.Step(Box(-30, 400))); // breakaway
        Assert.Equal(Box(5, 400), r.Step(Box(5, 400))); // straight back into the zone: still blocked
        Assert.Equal(1, r.A.Snaps);
    }

    [Fact]
    public void A_slide_off_the_end_of_a_window_releases_and_blocks_the_line_until_far_away() // SNAP-035, SNAP-042
    {
        var r = new Rig([W], Only(false, true));
        Assert.Equal(Box(500, 320), r.Step(Box(505, 320))); // left edge on the right side of W
        Assert.Equal(Box(500, 330), r.Step(Box(505, 330))); // sliding along the line
        // Slide until the vertical gap to W exceeds the pull-in distance.
        Assert.Equal(Box(505, 530), r.Step(Box(505, 530)));
        var last = r.A.TakeEvents()[^1];
        Assert.False(last.Engaged);
        Assert.Equal(ReleaseReason.TargetGone, last.Reason);
        // Back in range with the edge still near the line: the line stays blocked.
        Assert.Equal(505, r.Step(Box(505, 510)).L);
        // Farther than the pull-in distance clears the block.
        Assert.Equal(520, r.Step(Box(520, 510)).L);
        Assert.Equal(500, r.Step(Box(505, 510)).L);
    }

    [Fact]
    public void A_corner_holds_two_axes_and_each_releases_on_its_own() // SNAP-021
    {
        var r = new Rig();
        Assert.Equal(Box(0, 0), r.Step(Box(10, 12)));
        Assert.Equal(2, r.A.Snaps);
        Assert.Equal(Box(0, -30), r.Step(Box(10, -30))); // only y breaks away
        Assert.True(r.A.IsHeld);
        Assert.Equal(Box(-30, -30), r.Step(Box(-30, -30)));
        Assert.False(r.A.IsHeld);
    }

    [Fact]
    public void Edge_to_edge_left_of_a_window() // SNAP-033
    {
        var r = new Rig([W], Only(false, true));
        Assert.Equal(Box(200, 350), r.Step(Box(205, 350)));
        var e = Assert.Single(r.A.TakeEvents());
        Assert.Equal(Edge.Right, e.Edge);
        Assert.Equal(300, e.Line);
        Assert.Equal(TargetKind.EdgeToEdge, e.Kind);
    }

    [Fact]
    public void Edge_to_edge_right_of_above_and_below_a_window() // SNAP-033
    {
        Assert.Equal(Box(500, 350), new Rig([W], Only(false, true)).Step(Box(505, 350)));
        Assert.Equal(Box(350, 200), new Rig([W], Only(false, true)).Step(Box(350, 195)));
        Assert.Equal(Box(350, 500), new Rig([W], Only(false, true)).Step(Box(350, 505)));
    }

    [Fact]
    public void Edge_to_edge_needs_overlap_or_being_within_pull_in_of_overlapping() // SNAP-033
    {
        // 10 px below W: within pull-in of overlapping, so its right side is a target for the left edge.
        Assert.Equal(new Rect(500, 500, 600, 600), new Rig([W], Only(false, true)).Step(Box(505, 510)));
        var far = new Rig([W], Only(false, true));
        Assert.Equal(Box(505, 530), far.Step(Box(505, 530))); // 30 px below: no target at all
        Assert.False(far.A.IsHeld);
    }

    [Fact]
    public void Aligned_needs_stacked_windows() // SNAP-034
    {
        var stacked = new Rig([W], Only(false, true));
        Assert.Equal(Box(300, 500), stacked.Step(Box(305, 510)));
        var e = stacked.A.TakeEvents()[0];
        Assert.Equal(Edge.Left, e.Edge);
        Assert.Equal(TargetKind.Aligned, e.Kind);
        var apart = new Rig([W], Only(false, true));
        Assert.Equal(Box(305, 530), apart.Step(Box(305, 530)));
        Assert.Equal(Box(400, 500), new Rig([W], Only(false, true)).Step(Box(405, 510))); // right to right: 505 -> 500
    }

    [Fact]
    public void Overlapping_windows_have_no_inner_targets() // SNAP-036
    {
        var r = new Rig([W], Only(false, true));
        Assert.Equal(Box(305, 350), r.Step(Box(305, 350)));
        Assert.False(r.A.IsHeld);
        Assert.Equal(Box(395, 350), r.Step(Box(395, 350))); // right 495 against W.R 500 as well
        Assert.False(r.A.IsHeld);
    }

    [Fact]
    public void Sliding_off_the_end_of_an_aligned_target_releases() // SNAP-035
    {
        var r = new Rig([W], Only(false, true));
        Assert.Equal(Box(300, 500), r.Step(Box(305, 510)));
        Assert.Equal(Box(305, 560), r.Step(Box(305, 560))); // the gap grew to 60
        Assert.Contains(r.A.TakeEvents(), e => !e.Engaged && e.Edge == Edge.Left && e.Reason == ReleaseReason.TargetGone);
    }

    [Fact]
    public void A_window_wins_a_tie_against_a_screen_edge() // SNAP-037
    {
        var w = new Rect(-90, 100, 10, 300); // its right side is at x = 10, the screen edge at x = 0
        var r = new Rig([w]);
        Assert.Equal(Box(10, 150), r.Step(Box(5, 150)));
        Assert.Equal(TargetKind.EdgeToEdge, r.A.TakeEvents()[0].Kind);
    }

    [Fact]
    public void The_nearest_target_wins() // SNAP-037
    {
        var w = new Rect(-90, 100, 10, 300);
        Assert.Equal(Box(0, 150), new Rig([w]).Step(Box(4, 150))); // the screen edge is 4 away, the window 6
        var w3 = new Rect(-90, 350, 14, 500);
        Assert.Equal(Box(10, 280), new Rig([w, w3], Only(false, true)).Step(Box(8, 280)));
    }

    [Fact]
    public void A_side_on_another_monitor_is_not_a_target() // SNAP-031
    {
        var primary = new Rect(0, 0, 1920, 1080);
        var s = SideTargets.For(primary, [primary, new Rect(1920, 0, 3840, 1080)]);
        Assert.True(s.Left);
        Assert.True(s.Top);
        Assert.False(s.Right);
        Assert.True(s.Bottom);
        Assert.False(s.Allows(Edge.Right));
        Assert.True(s.Allows(Edge.Left));
        Assert.True(s.Allows(Edge.Top));
        Assert.True(s.Allows(Edge.Bottom));
    }

    [Fact]
    public void Every_side_can_touch_another_monitor() // SNAP-031
    {
        var work = new Rect(1000, 1000, 2000, 2000);
        var s = SideTargets.For(work,
        [
            work,
            new Rect(0, 1000, 1000, 2000),
            new Rect(2000, 1000, 3000, 2000),
            new Rect(1000, 0, 2000, 1000),
            new Rect(1000, 2000, 2000, 3000),
        ]);
        Assert.False(s.Left);
        Assert.False(s.Right);
        Assert.False(s.Top);
        Assert.False(s.Bottom);
    }

    [Fact]
    public void A_side_is_a_target_when_the_other_monitor_only_touches_at_a_corner_or_is_offset() // SNAP-031
    {
        var primary = new Rect(0, 0, 1920, 1080);
        var s = SideTargets.For(primary, [primary, new Rect(1920, 1080, 3840, 2160), new Rect(2500, 0, 3000, 1080)]);
        Assert.True(s.Left && s.Top && s.Right && s.Bottom);
        Assert.True(SideTargets.For(primary, [primary]).Right);
    }

    [Fact]
    public void An_untargeted_side_does_not_hold() // SNAP-031
    {
        var r = new Rig(null, new AttractionConfig(), new SideTargets { Right = false });
        Assert.Equal(Box(895, 400), r.Step(Box(895, 400)));
        Assert.False(r.A.IsHeld);
        Assert.Equal(Box(0, 400), r.Step(Box(10, 400)));
    }

    [Fact]
    public void Screen_edges_off_removes_the_screen_targets() // SNAP-030
    {
        var r = new Rig(null, Only(false, true));
        Assert.Equal(Box(10, 400), r.Step(Box(10, 400)));
        Assert.False(r.A.IsHeld);
    }

    [Fact]
    public void Windows_off_removes_the_window_targets() // SNAP-032
    {
        var r = new Rig([W], Only(true, false));
        Assert.Equal(Box(205, 350), r.Step(Box(205, 350)));
        Assert.False(r.A.IsHeld);
        Assert.Equal(Box(0, 350), r.Step(Box(10, 350)));
    }

    [Fact]
    public void Events_carry_edge_line_kind_and_speed_and_are_cleared() // SNAP-060
    {
        var r = new Rig();
        r.Step(Box(10, 400));
        var e = Assert.Single(r.A.TakeEvents());
        Assert.True(e.Engaged);
        Assert.Equal(Edge.Left, e.Edge);
        Assert.Equal(0, e.Line);
        Assert.Equal(TargetKind.Screen, e.Kind);
        Assert.Equal(0.0, e.Speed);
        Assert.Empty(r.A.TakeEvents());
    }

    [Fact]
    public void Current_is_the_last_returned_position()
    {
        var r = new Rig();
        Assert.Equal(default(Rect), r.A.Current);
        var o = r.Step(Box(10, 400));
        Assert.Equal(o, r.A.Current);
    }

    [Fact]
    public void Only_one_edge_per_axis_is_held_and_it_stays_until_released() // SNAP-021, SNAP-040
    {
        var w = new Rect(109, 300, 209, 500);
        var r = new Rig([w]);
        Assert.Equal(Box(0, 350), r.Step(Box(4, 350))); // left to the screen edge (4) beats right to w.L (5)
        Assert.Equal(1, r.A.Snaps);
        Assert.Equal(Box(0, 350), r.Step(Box(6, 350))); // right edge is now 3 from w.L, but left stays held
        Assert.Equal(1, r.A.Snaps);
    }

    [Fact]
    public void Edges_are_fully_visible_with_nothing_above() // SNAP-038
    {
        var t = TargetWindow.Create(W, []);
        foreach (var e in new[] { Edge.Left, Edge.Top, Edge.Right, Edge.Bottom })
            Assert.Equal<EdgeSpan>([new(300, 500)], t.Visible[(int)e]);
    }

    [Fact]
    public void A_window_above_cuts_the_edges_it_covers() // SNAP-038
    {
        // Covers the middle of the right edge (column 499) and nothing else.
        var t = TargetWindow.Create(W, [new Rect(450, 350, 700, 400)]);
        Assert.Equal<EdgeSpan>([new(300, 350), new(400, 500)], t.Visible[(int)Edge.Right]);
        Assert.Equal<EdgeSpan>([new(300, 500)], t.Visible[(int)Edge.Left]);
        Assert.Equal<EdgeSpan>([new(300, 500)], t.Visible[(int)Edge.Top]);
    }

    [Fact]
    public void A_window_that_only_touches_the_edge_does_not_cover_it() // SNAP-038
    {
        // Starts at x 500, the first column right of the window: the right edge (column 499) stays visible.
        var t = TargetWindow.Create(W, [new Rect(500, 300, 700, 500)]);
        Assert.Equal<EdgeSpan>([new(300, 500)], t.Visible[(int)Edge.Right]);
    }

    [Fact]
    public void A_fully_covered_window_has_no_visible_edges() // SNAP-038
    {
        var t = TargetWindow.Create(W, [new Rect(0, 0, 1000, 800)]);
        foreach (var spans in t.Visible) Assert.Empty(spans);
    }

    [Fact]
    public void A_hidden_window_does_not_attract() // SNAP-038
    {
        var a = new Attraction(Only(false, true), [TargetWindow.Create(W, [new Rect(200, 200, 700, 700)])], Work, new SideTargets());
        // The left edge is 10 px right of the hidden window's right edge.
        Assert.Equal(Box(510, 350), a.Update(Box(510, 350), 100));
        Assert.False(a.IsHeld);
    }

    [Fact]
    public void Only_the_visible_part_of_an_edge_attracts() // SNAP-038
    {
        // The window's right edge is covered from y 300 to 450; only 450..500 shows.
        var cover = new[] { new Rect(400, 280, 600, 450) };
        var high = new Attraction(Only(false, true), [TargetWindow.Create(W, cover)], Work, new SideTargets());
        // Next to the covered part (y 300..400, 16 px pull-in reaches 416): no snap.
        Assert.Equal(Box(510, 300), high.Update(Box(510, 300), 100));
        Assert.False(high.IsHeld);
        var low = new Attraction(Only(false, true), [TargetWindow.Create(W, cover)], Work, new SideTargets());
        // Next to the visible part (y 420..520): snaps.
        Assert.Equal(Box(500, 420), low.Update(Box(510, 420), 100));
        Assert.True(low.IsHeld);
    }

    [Fact]
    public void Aligned_needs_the_facing_edge_visible() // SNAP-034, SNAP-038
    {
        // Dragged window stacked below W, its left edge 5 px off W's left edge.
        var free = new Rect(305, 500, 405, 600);
        var visible = new Attraction(Only(false, true), [TargetWindow.Create(W, [])], Work, new SideTargets());
        Assert.Equal(300, visible.Update(free, 100).L);
        // Cover W's bottom edge: no longer stacked against anything visible.
        var hidden = new Attraction(Only(false, true), [TargetWindow.Create(W, [new Rect(250, 450, 550, 520)])], Work, new SideTargets());
        Assert.Equal(305, hidden.Update(free, 100).L);
    }
}
