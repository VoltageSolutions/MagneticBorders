using MagneticBorders.Core;
using Xunit;

namespace MagneticBorders.Application.Tests;

internal sealed class RecordingLog : ILog
{
    public List<string> Infos { get; } = [];
    public List<string> Debugs { get; } = [];
    public void Info(string msg) => Infos.Add(msg);
    public void Debug(string msg) => Debugs.Add(msg);
}

public class DragControllerBehaviorTests
{
    static readonly Rect Work = new(0, 0, 3440, 1440);

    static Snapshot Pair(double dpi = 1.0, int gap = 0) => new(
    [
        new WinInfo(1, new Rect(0, 0, 1720, 1440), 1, 1),
        new WinInfo(2, new Rect(1720 + gap, 0, 3440, 1440), 1, 1),
    ], Work, dpi);

    static (DragController c, FakeInventory inv, FakeMover mv, FakeClock clk, FakeTimer tm, RecordingLog log) Make(Snapshot s)
    {
        var inv = new FakeInventory(s);
        var mv = new FakeMover();
        var clk = new FakeClock();
        var tm = new FakeTimer();
        var log = new RecordingLog();
        return (new DragController(inv, mv, clk, log, tm), inv, mv, clk, tm, log);
    }

    [Fact]
    public void Inactive_controller_ignores_the_drag_and_never_snapshots()
    {
        var (c, inv, mv, _, tm, _) = Make(Pair());
        c.Active = false;
        c.SnapActive = false;
        c.OnMoveSizeStart(1);
        Assert.Equal(DragState.Ignoring, c.State);
        inv.Current = new Rect(0, 0, 1820, 1440);
        c.OnLocationChange(1);
        Assert.Equal(DragState.Ignoring, c.State);
        c.OnMoveSizeEnd(1);
        Assert.Equal(DragState.Idle, c.State);
        Assert.Empty(mv.Batches);
        Assert.Empty(tm.Armed);
        Assert.Equal(0, c.Drags);
    }

    [Fact]
    public void Location_change_before_any_drag_is_ignored()
    {
        var (c, inv, mv, _, _, _) = Make(Pair());
        inv.Current = new Rect(0, 0, 1820, 1440);
        c.OnLocationChange(1);
        Assert.Equal(DragState.Idle, c.State);
        Assert.Empty(mv.Batches);
    }

    [Fact]
    public void Location_change_of_another_window_is_ignored()
    {
        var (c, inv, mv, _, _, _) = Make(Pair());
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 1820, 1440);
        c.OnLocationChange(2);
        Assert.Equal(DragState.Classifying, c.State);
        Assert.Empty(mv.Batches);
    }

    [Fact]
    public void Unreadable_frame_keeps_classifying()
    {
        var (c, inv, mv, _, _, _) = Make(Pair());
        c.OnMoveSizeStart(1);
        inv.Current = null;
        c.OnLocationChange(1);
        Assert.Equal(DragState.Classifying, c.State);
        Assert.Empty(mv.Batches);
    }

    [Fact]
    public void Resize_without_a_touching_neighbor_is_counted_but_not_glued()
    {
        var (c, inv, mv, _, _, _) = Make(Pair(gap: 200));
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 1820, 1440);
        c.OnLocationChange(1);
        Assert.Equal(DragState.Ignoring, c.State);
        Assert.Equal(1, c.Drags);
        Assert.Equal(0, c.GluedDrags);
        Assert.Empty(mv.Batches);
    }

    [Fact]
    public void Glued_resize_increments_both_counters_and_logs_the_seam()
    {
        var (c, inv, _, _, _, log) = Make(Pair());
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 1820, 1440);
        c.OnLocationChange(1);
        Assert.Equal((1L, 1L), (c.Drags, c.GluedDrags));
        Assert.Contains(log.Infos, l => l.Contains("seam Right x=1720 members=[2]", StringComparison.Ordinal));
        Assert.Contains(log.Debugs, l => l.Contains("apply members=1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(2.0, 20, DragState.Resizing)]
    [InlineData(1.0, 20, DragState.Ignoring)]
    public void Glue_tolerance_scales_with_the_window_dpi(double dpi, int gap, DragState expected)
    {
        var (c, inv, _, _, _, _) = Make(Pair(dpi, gap));
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 1820, 1440);
        c.OnLocationChange(1);
        Assert.Equal(expected, c.State);
    }

    [Fact]
    public void Corner_drag_merges_both_seams_into_one_batch()
    {
        var ws = new[]
        {
            new WinInfo(1, new Rect(0, 0, 1000, 1000), 1, 1),
            new WinInfo(2, new Rect(1000, 0, 2000, 1000), 1, 1),
            new WinInfo(3, new Rect(0, 1000, 1000, 2000), 1, 1),
            new WinInfo(4, new Rect(1000, 1000, 2000, 2000), 1, 1),
        };
        var (c, inv, mv, _, _, _) = Make(new Snapshot(ws, new Rect(0, 0, 2000, 2000), 1.0));
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 1100, 1100);
        c.OnLocationChange(1);
        var batch = Assert.Single(mv.Batches).ToDictionary(m => m.Id, m => m.Frame);
        Assert.Equal(3, batch.Count);
        Assert.Equal(new Rect(1100, 0, 2000, 1100), batch[2]);
        Assert.Equal(new Rect(0, 1100, 1100, 2000), batch[3]);
        Assert.Equal(new Rect(1100, 1100, 2000, 2000), batch[4]);
    }

    [Fact]
    public void Starting_a_new_drag_cancels_armed_timers()
    {
        var (c, inv, _, _, tm, _) = Make(Pair());
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 1820, 1440);
        c.OnLocationChange(1);
        Assert.NotEmpty(tm.Armed);
        c.OnMoveSizeStart(1);
        Assert.Empty(tm.Armed);
        Assert.Equal(DragState.Classifying, c.State);
    }

    [Fact]
    public void Ending_an_ignored_drag_returns_to_idle_without_moving_anything()
    {
        var (c, inv, mv, _, tm, _) = Make(Pair());
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(50, 50, 1770, 1490);
        c.OnLocationChange(1);
        c.OnMoveSizeEnd(1);
        Assert.Equal(DragState.Idle, c.State);
        Assert.Empty(mv.Batches);
        Assert.Empty(tm.Armed);
    }

    [Fact]
    public void Timers_outside_a_resize_do_nothing()
    {
        var (c, _, mv, _, _, _) = Make(Pair());
        c.OnTimer(TimerKind.Flush);
        c.OnTimer(TimerKind.Settle);
        c.OnTimer(TimerKind.Verify);
        Assert.Empty(mv.Batches);
    }

    [Fact]
    public void Flush_without_a_pending_frame_applies_nothing()
    {
        var (c, inv, mv, _, _, _) = Make(Pair());
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 1820, 1440);
        c.OnLocationChange(1);
        c.OnTimer(TimerKind.Flush);
        Assert.Single(mv.Batches);
    }

    [Fact]
    public void Settle_with_matching_sizes_applies_nothing_and_logs_the_gap()
    {
        var (c, inv, mv, _, _, log) = Make(Pair());
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 1820, 1440);
        c.OnLocationChange(1);
        inv.Others[2] = new Rect(1820, 0, 3440, 1440);
        c.OnTimer(TimerKind.Settle);
        Assert.Single(mv.Batches);
        Assert.Contains(log.Infos, l => l.Contains("settled", StringComparison.Ordinal) && l.Contains("gap=0", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_after_a_clean_drag_logs_ok_and_moves_nothing()
    {
        var (c, inv, mv, _, _, log) = Make(Pair());
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 1820, 1440);
        c.OnLocationChange(1);
        c.OnMoveSizeEnd(1);
        inv.Others[2] = new Rect(1820, 0, 3440, 1440);
        int before = mv.Batches.Count;
        c.OnTimer(TimerKind.Verify);
        Assert.Equal(before, mv.Batches.Count);
        Assert.Contains(log.Infos, l => l.EndsWith("ok", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_restores_a_member_that_drifted_from_the_requested_frame()
    {
        var (c, inv, mv, _, _, _) = Make(Pair());
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 1820, 1440);
        c.OnLocationChange(1);
        c.OnMoveSizeEnd(1);
        inv.Others[2] = new Rect(1900, 0, 3440, 1440);
        c.OnTimer(TimerKind.Verify);
        Assert.Equal((2UL, new Rect(1820, 0, 3440, 1440)), Assert.Single(mv.Batches[^1]));
    }

    [Fact]
    public void Verify_runs_only_once_per_drag()
    {
        var (c, inv, mv, _, _, _) = Make(Pair());
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 1820, 1440);
        c.OnLocationChange(1);
        c.OnMoveSizeEnd(1);
        inv.Others[2] = new Rect(1900, 0, 3440, 1440);
        c.OnTimer(TimerKind.Verify);
        int after = mv.Batches.Count;
        c.OnTimer(TimerKind.Verify);
        Assert.Equal(after, mv.Batches.Count);
    }
}
