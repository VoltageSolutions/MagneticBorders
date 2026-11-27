using MagneticBorders.Core;
using Xunit;

namespace MagneticBorders.Application.Tests;

internal sealed class SnapInventory : IWindowInventory
{
    public Snapshot Snap { get; set; } = new([], default, 1.0);
    public Dictionary<ulong, Rect> Frames { get; } = [];
    public Snapshot Snapshot(ulong draggedId) => Snap;
    public Rect? FrameOf(ulong id) => Frames.TryGetValue(id, out var r) ? r : null;
    public List<(ulong Id, Rect? Held)> Results { get; } = [];
    public void ReportMoveResult(ulong id, Rect? held) => Results.Add((id, held));
}

/// <summary>Pointer hook that records installs and removals and reports a settable cursor.</summary>
internal sealed class FakeHook : IPointerHook
{
    public IPointerEvents? Sink { get; private set; }
    public bool Fail { get; set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public Point? At { get; set; } = new Point(300, 260);

    public bool Install(IPointerEvents sink)
    {
        if (Fail) return false;
        Sink = sink;
        Starts++;
        return true;
    }

    public void Remove()
    {
        if (Sink is not null) Stops++;
        Sink = null;
    }

    public Point? ReadCursor() => At;
}

public class AttractionControllerTests
{
    static readonly Rect Work = new(0, 0, 1000, 800);
    /// <summary>A free position whose right edge is 5 px short of window 2.</summary>
    static readonly Rect Near = new(295, 250, 495, 450);
    /// <summary>The held position for <see cref="Near"/>.</summary>
    static readonly Rect Held = new(300, 250, 500, 450);

    /// <summary>A dragged window (id 1, 200x200) with one other window (id 2) to its right on a 1000x800 work area.</summary>
    sealed class Rig
    {
        public SnapInventory Inv { get; } = new();
        public FakeMover Mover { get; } = new();
        public FakeClock Clock { get; } = new() { Now = 1000 };
        public FakeTimer Timer { get; } = new();
        public RecordingLog Log { get; } = new();
        public FakeHook Hook { get; } = new();
        public DragController Ctl { get; }

        public Rig(bool withHook = false)
        {
            Inv.Snap = new Snapshot(
                [new WinInfo(1, new Rect(200, 250, 400, 450), 1, 1), new WinInfo(2, new Rect(500, 200, 700, 400), 1, 1)],
                Work, 1.0, [Work]);
            Inv.Frames[1] = new Rect(200, 250, 400, 450);
            Ctl = new DragController(Inv, Mover, Clock, Log, Timer);
            if (withHook) Ctl.Hook = Hook;
        }

        /// <summary>Reports the dragged window at <paramref name="frame"/> and delivers a location change.</summary>
        public void MoveTo(Rect frame)
        {
            Inv.Frames[1] = frame;
            Clock.Now += 100;
            Ctl.OnLocationChange(1);
        }

        public void SetWindows(params WinInfo[] windows) => Inv.Snap = Inv.Snap with { Windows = windows };

        public void AssertLogged(string text) => Assert.Contains(Log.Infos, l => l.Contains(text, StringComparison.Ordinal));
    }

    [Fact]
    public void A_move_with_attraction_applies_the_held_position() // SNAP-020, SNAP-023, SNAP-040
    {
        var r = new Rig();
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near); // the first location change classifies the move and is handled too
        var batch = Assert.Single(r.Mover.Batches);
        var move = Assert.Single(batch);
        Assert.Equal((1UL, Held), move);
        Assert.Equal(DragState.Moving, r.Ctl.State);
        Assert.Equal(1, r.Ctl.MoveDrags);
        Assert.Equal(1, r.Ctl.Snaps);
        Assert.Equal(0, r.Ctl.Drags);
        r.AssertLogged("snap hold right=500 target=edge-to-edge");
        r.AssertLogged("move start id=1");
    }

    [Fact]
    public void A_free_position_is_not_moved() // SNAP-023
    {
        var r = new Rig();
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(new Rect(100, 250, 300, 450));
        Assert.Empty(r.Mover.Batches);
    }

    [Fact]
    public void The_own_move_echo_is_ignored_and_the_next_free_position_is_handled() // SNAP-024, SNAP-025
    {
        var r = new Rig();
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        Assert.Single(r.Mover.Batches);
        r.MoveTo(Held); // Windows reports the position we just set
        Assert.Single(r.Mover.Batches);
        r.Clock.Now += 1; // no throttling of the dragged window
        r.Inv.Frames[1] = new Rect(296, 250, 496, 450);
        r.Ctl.OnLocationChange(1);
        Assert.Equal(2, r.Mover.Batches.Count);
        Assert.Equal((1UL, Held), Assert.Single(r.Mover.Batches[1]));
    }

    [Fact]
    public void The_echo_as_read_back_from_the_mover_is_ignored_too() // SNAP-024
    {
        var r = new Rig();
        var readBack = new Rect(301, 250, 501, 450);
        r.Mover.Override = m => (m.Id, readBack);
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        Assert.Single(r.Mover.Batches);
        r.MoveTo(readBack);
        Assert.Single(r.Mover.Batches);
    }

    [Fact]
    public void Sliding_and_breakaway() // SNAP-022, SNAP-041
    {
        var r = new Rig();
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        r.MoveTo(new Rect(292, 300, 492, 500)); // the window slides down while held
        Assert.Equal(2, r.Mover.Batches.Count);
        Assert.Equal(new Rect(300, 300, 500, 500), r.Mover.Batches[1][0].Frame);
        r.MoveTo(new Rect(270, 300, 470, 500)); // 30 px from the line: released, the window is already at the free position
        Assert.Equal(2, r.Mover.Batches.Count);
        r.AssertLogged("snap release right=500 reason=breakaway");
    }

    [Fact]
    public void A_fast_move_does_not_snap() // SNAP-012
    {
        var r = new Rig();
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(new Rect(100, 250, 300, 450));
        r.Clock.Now += 5;
        r.Inv.Frames[1] = new Rect(295, 250, 495, 450);
        r.Ctl.OnLocationChange(1); // 195 px in 5 ms
        Assert.Empty(r.Mover.Batches);
        Assert.Equal(0, r.Ctl.Snaps);
    }

    [Fact]
    public void A_move_end_reports_the_held_position_to_the_inventory()
    {
        var r = new Rig();
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        r.Ctl.OnMoveSizeEnd(1);
        Assert.Equal((1UL, (Rect?)Held), Assert.Single(r.Inv.Results));
    }

    [Fact]
    public void A_move_end_without_a_held_axis_reports_none()
    {
        var r = new Rig();
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(new Rect(400, 500, 600, 700));
        r.Ctl.OnMoveSizeEnd(1);
        Assert.Equal((1UL, (Rect?)null), Assert.Single(r.Inv.Results));
    }

    [Fact]
    public void The_end_applies_the_held_position_once_when_windows_put_the_window_back_at_the_free_position() // SNAP-050, SNAP-052
    {
        var r = new Rig();
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        Assert.Single(r.Mover.Batches);
        r.Inv.Frames[1] = Near;
        r.Ctl.OnMoveSizeEnd(1);
        Assert.Equal(2, r.Mover.Batches.Count);
        Assert.Equal((1UL, Held), Assert.Single(r.Mover.Batches[1]));
        Assert.DoesNotContain(TimerKind.Verify, r.Timer.Armed.Keys);
        Assert.Equal(DragState.Idle, r.Ctl.State);
        r.AssertLogged("move end id=1 snaps=1 applied");
        r.Ctl.OnTimer(TimerKind.Verify);
        r.Ctl.OnTimer(TimerKind.Settle);
        r.Ctl.OnTimer(TimerKind.Flush);
        Assert.Equal(2, r.Mover.Batches.Count);
    }

    [Fact]
    public void The_end_does_nothing_when_the_window_is_already_held() // SNAP-050
    {
        var r = new Rig();
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        r.Inv.Frames[1] = Held;
        r.Ctl.OnMoveSizeEnd(1);
        Assert.Single(r.Mover.Batches);
    }

    [Fact]
    public void The_end_does_nothing_when_another_tool_placed_the_window() // SNAP-051
    {
        var r = new Rig();
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        r.Inv.Frames[1] = new Rect(0, 0, 500, 800); // FancyZones or Windows Snap
        r.Ctl.OnMoveSizeEnd(1);
        Assert.Single(r.Mover.Batches);
    }

    [Fact]
    public void The_end_does_nothing_when_the_size_changed() // SNAP-051
    {
        var r = new Rig();
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        r.MoveTo(new Rect(296, 250, 496, 500)); // another tool resized the window during the drag
        int before = r.Mover.Batches.Count;
        r.Ctl.OnMoveSizeEnd(1);
        Assert.Equal(before, r.Mover.Batches.Count);
    }

    [Fact]
    public void The_end_does_nothing_when_nothing_is_held_or_the_frame_is_unreadable() // SNAP-050
    {
        var r = new Rig();
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(new Rect(100, 250, 300, 450));
        r.Ctl.OnMoveSizeEnd(1);
        Assert.Empty(r.Mover.Batches);
        r.AssertLogged("move end id=1 snaps=0");
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        r.Inv.Frames.Remove(1);
        r.Ctl.OnMoveSizeEnd(1);
        Assert.Single(r.Mover.Batches);
    }

    [Fact]
    public void Attraction_off_ignores_moves() // SNAP-001
    {
        var r = new Rig();
        r.Ctl.SnapActive = false;
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        Assert.Equal(DragState.Ignoring, r.Ctl.State);
        r.Ctl.OnMoveSizeEnd(1);
        Assert.Empty(r.Mover.Batches);
        Assert.Equal(0, r.Ctl.MoveDrags);
    }

    [Fact]
    public void Both_features_off_ignore_the_drag_at_once()
    {
        var r = new Rig();
        r.Ctl.SnapActive = false;
        r.Ctl.Active = false;
        r.Ctl.OnMoveSizeStart(1);
        Assert.Equal(DragState.Ignoring, r.Ctl.State);
    }

    [Fact]
    public void Attraction_still_handles_moves_when_coupling_is_off()
    {
        var r = new Rig();
        r.Ctl.Active = false;
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        Assert.Equal((1UL, Held), Assert.Single(Assert.Single(r.Mover.Batches)));
        r.Ctl.OnMoveSizeEnd(1);
        int before = r.Mover.Batches.Count;
        r.Ctl.OnMoveSizeStart(1);
        r.Inv.Frames[1] = new Rect(200, 250, 450, 450); // a resize is ignored without coupling
        r.Ctl.OnLocationChange(1);
        Assert.Equal(DragState.Ignoring, r.Ctl.State);
        r.Ctl.OnMoveSizeEnd(1);
        Assert.Equal(0, r.Ctl.Drags);
        Assert.Equal(before, r.Mover.Batches.Count);
    }

    [Fact]
    public void Resizes_stay_glued_when_attraction_is_off()
    {
        var r = new Rig();
        r.Ctl.SnapActive = false;
        r.SetWindows(new WinInfo(1, new Rect(0, 0, 500, 800), 1, 1), new WinInfo(2, new Rect(500, 0, 1000, 800), 1, 1));
        r.Inv.Frames[1] = new Rect(0, 0, 500, 800);
        r.Ctl.OnMoveSizeStart(1);
        r.Inv.Frames[1] = new Rect(0, 0, 600, 800);
        r.Ctl.OnLocationChange(1);
        Assert.Equal(DragState.Resizing, r.Ctl.State);
        Assert.Equal((2UL, new Rect(600, 0, 1000, 800)), Assert.Single(Assert.Single(r.Mover.Batches)));
    }

    [Fact]
    public void An_ineligible_dragged_window_is_ignored()
    {
        var r = new Rig();
        r.SetWindows(new WinInfo(2, new Rect(500, 200, 700, 400), 1, 1));
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        Assert.Empty(r.Mover.Batches);
    }

    [Fact]
    public void Distances_are_scaled_by_the_dpi() // SNAP-010
    {
        var r = new Rig();
        var free = new Rect(280, 250, 480, 450); // 20 px from window 2
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(free);
        Assert.Empty(r.Mover.Batches);
        r.Inv.Snap = r.Inv.Snap with { DpiScale = 2.0 }; // 16 px become 32 px
        r.Ctl.OnMoveSizeEnd(1);
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(free);
        Assert.Equal((1UL, Held), Assert.Single(Assert.Single(r.Mover.Batches)));
    }

    [Fact]
    public void Options_apply_on_the_next_drag() // SNAP-032, SNAP-043
    {
        var r = new Rig();
        r.Ctl.Snap = new SnapOptions { Windows = false };
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        Assert.Empty(r.Mover.Batches);
        r.Ctl.OnMoveSizeEnd(1);
        r.Ctl.Snap = new SnapOptions { DistancePx = 0 };
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        Assert.Empty(r.Mover.Batches); // 5 px is out of a 0 px pull-in
    }

    [Fact]
    public void A_side_on_another_monitor_is_not_a_target() // SNAP-031
    {
        var r = new Rig();
        r.SetWindows(new WinInfo(1, new Rect(700, 250, 900, 450), 1, 1));
        r.Inv.Snap = r.Inv.Snap with { WorkAreas = [Work, new Rect(1000, 0, 2000, 800)] };
        r.Inv.Frames[1] = new Rect(700, 250, 900, 450);
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(new Rect(795, 250, 995, 450));
        Assert.Empty(r.Mover.Batches);
        r.MoveTo(new Rect(600, 250, 800, 450)); // nothing near
        Assert.Empty(r.Mover.Batches);
    }

    [Fact]
    public void Missing_work_areas_and_stack_count_as_one_monitor_with_nothing_covering()
    {
        var r = new Rig();
        r.Inv.Snap = new Snapshot(r.Inv.Snap.Windows, Work, 1.0);
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(new Rect(10, 250, 210, 450));
        Assert.Equal(new Rect(0, 250, 200, 450), Assert.Single(Assert.Single(r.Mover.Batches)).Frame);
    }

    [Fact]
    public void A_move_on_a_screen_edge_is_logged() // SNAP-060
    {
        var r = new Rig();
        r.SetWindows(new WinInfo(1, new Rect(200, 250, 400, 450), 1, 1));
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(new Rect(10, 250, 210, 450));
        Assert.Equal(new Rect(0, 250, 200, 450), Assert.Single(Assert.Single(r.Mover.Batches)).Frame);
        r.AssertLogged("snap hold left=0 target=screen");
        r.AssertLogged("move start id=1");
        r.MoveTo(new Rect(-30, 250, 170, 450));
        r.AssertLogged("snap release left=0 reason=breakaway");
        r.Ctl.OnMoveSizeEnd(1);
        r.AssertLogged("move end id=1 snaps=1");
    }

    [Fact]
    public void The_four_edge_names_are_logged() // SNAP-060
    {
        var r = new Rig();
        r.SetWindows(new WinInfo(1, new Rect(200, 250, 400, 450), 1, 1));
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(new Rect(10, 10, 210, 210));
        r.AssertLogged("snap hold left=0 target=screen");
        r.AssertLogged("snap hold top=0 target=screen");
        r.Ctl.OnMoveSizeEnd(1);
        var corner = new Rect(790, 590, 990, 790);
        r.SetWindows(new WinInfo(1, corner, 1, 1));
        r.Inv.Frames[1] = corner;
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(corner);
        r.AssertLogged("snap hold right=1000 target=screen");
        r.AssertLogged("snap hold bottom=800 target=screen");
        Assert.Equal(new Rect(800, 600, 1000, 800), Assert.Single(r.Mover.Batches[^1]).Frame);
    }

    [Fact]
    public void A_slide_off_a_window_is_logged_as_target_gone() // SNAP-035, SNAP-060
    {
        var r = new Rig();
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        r.MoveTo(new Rect(295, 600, 495, 800)); // far below window 2: it is no longer a target
        r.AssertLogged("reason=target-gone");
    }

    [Fact]
    public void An_aligned_hold_is_logged() // SNAP-034, SNAP-060
    {
        var r = new Rig();
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(new Rect(505, 410, 705, 610)); // below window 2, left edge 5 px from its left edge
        r.AssertLogged("target=aligned");
    }

    [Fact]
    public void A_window_hidden_behind_another_does_not_attract() // SNAP-038
    {
        var r = new Rig();
        // Window 3 sits above window 2 and covers it completely.
        r.Inv.Snap = r.Inv.Snap with { Stack = [(1, new Rect(200, 250, 400, 450)), (3, new Rect(450, 150, 750, 450)), (2, new Rect(500, 200, 700, 400))] };
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        Assert.Empty(r.Mover.Batches);
    }

    [Fact]
    public void The_dragged_window_never_covers_a_target() // SNAP-038
    {
        var r = new Rig();
        // The dragged window is on top and overlaps window 2's left edge at drag start.
        r.Inv.Snap = r.Inv.Snap with { Stack = [(1, new Rect(450, 200, 650, 400)), (2, new Rect(500, 200, 700, 400))] };
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        Assert.Equal((1UL, Held), Assert.Single(Assert.Single(r.Mover.Batches)));
    }

    [Fact]
    public void An_end_event_of_another_window_does_not_stop_a_move()
    {
        var r = new Rig();
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        r.Ctl.OnMoveSizeEnd(2);
        Assert.Equal(DragState.Moving, r.Ctl.State);
    }

    // The pointer hook: the cursor at (300, 260) grabs window 1 (200..400) 100 px right of its left.

    /// <summary>Starts a move: the first location change leaves the window where it was.</summary>
    static void Begin(Rig r)
    {
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(r.Inv.Snap.Windows[0].Frame);
    }

    /// <summary>Sends a mouse movement to the controller as the hook would, one step of the clock later.</summary>
    static Point? Pointer(Rig r, int x, int y)
    {
        r.Clock.Now += 100;
        return r.Ctl.OnPointerMove(new Point(x, y));
    }

    [Fact]
    public void A_move_starts_the_hook_and_holds_the_cursor_instead_of_moving_the_window() // SNAP-070, SNAP-072
    {
        var r = new Rig(withHook: true);
        Begin(r);
        Assert.Equal(1, r.Hook.Starts);
        r.AssertLogged("move start id=1 pointer");
        // Window 1's right edge would be 5 px short of window 2 (at 500): the cursor goes 5 px further instead.
        Assert.Equal(new Point(400, 260), Pointer(r, 395, 260));
        Assert.Empty(r.Mover.Batches);
        r.AssertLogged("snap hold right=500 target=edge-to-edge");
        Assert.Equal(1, r.Ctl.Snaps);
    }

    [Fact]
    public void A_free_movement_goes_through_unchanged() // SNAP-072
    {
        var r = new Rig(withHook: true);
        Begin(r);
        Assert.Null(Pointer(r, 310, 270));
    }

    [Fact]
    public void Location_changes_never_move_the_window_with_the_hook() // SNAP-072
    {
        var r = new Rig(withHook: true);
        Begin(r);
        r.MoveTo(Near);
        Assert.Empty(r.Mover.Batches);
    }

    [Fact]
    public void The_end_stops_the_hook_and_applies_nothing() // SNAP-075
    {
        var r = new Rig(withHook: true);
        Begin(r);
        Pointer(r, 395, 260);
        r.Inv.Frames[1] = Held;
        r.Ctl.OnMoveSizeEnd(1);
        Assert.Equal(1, r.Hook.Stops);
        Assert.Empty(r.Mover.Batches);
        r.AssertLogged("move end id=1 snaps=1 pointer");
        Assert.Null(Pointer(r, 390, 260));
    }

    [Fact]
    public void A_button_release_stops_the_hook_before_the_end_event() // SNAP-075
    {
        var r = new Rig(withHook: true);
        Begin(r);
        r.Ctl.OnPointerRelease();
        Assert.Equal(1, r.Hook.Stops);
        r.Ctl.OnMoveSizeEnd(1);
        Assert.Equal(1, r.Hook.Stops);
    }

    [Fact]
    public void A_button_release_without_a_move_does_nothing()
    {
        var r = new Rig(withHook: true);
        r.Ctl.OnPointerRelease();
        Assert.Equal(0, r.Hook.Stops);
    }

    [Fact]
    public void A_new_drag_stops_a_hook_left_running() // SNAP-075
    {
        var r = new Rig(withHook: true);
        Begin(r);
        r.Ctl.OnMoveSizeStart(1);
        Assert.Equal(1, r.Hook.Stops);
    }

    [Fact]
    public void Without_the_hook_the_window_is_moved_back() // SNAP-076
    {
        var r = new Rig(withHook: true);
        r.Hook.Fail = true;
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        Assert.Equal((1UL, Held), Assert.Single(Assert.Single(r.Mover.Batches)));
    }

    [Fact]
    public void An_unreadable_cursor_stops_the_hook_and_falls_back() // SNAP-076
    {
        var r = new Rig(withHook: true);
        r.Hook.At = null;
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(Near);
        Assert.Equal(1, r.Hook.Starts);
        Assert.Equal(1, r.Hook.Stops);
        Assert.Single(r.Mover.Batches);
    }

    [Fact]
    public void A_resize_never_starts_the_hook() // SNAP-002
    {
        var r = new Rig(withHook: true);
        r.Ctl.OnMoveSizeStart(1);
        r.MoveTo(new Rect(200, 250, 420, 450));
        Assert.Equal(0, r.Hook.Starts);
        Assert.Null(r.Ctl.OnPointerMove(new Point(1, 1)));
    }
}
