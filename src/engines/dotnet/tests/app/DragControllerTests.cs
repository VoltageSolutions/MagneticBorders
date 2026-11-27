using MagneticBorders.Core;
using Xunit;

namespace MagneticBorders.Application.Tests;

internal sealed class FakeInventory(Snapshot snap) : IWindowInventory
{
    public Rect? Current;
    public Dictionary<ulong, Rect> Others = [];
    public Snapshot Snapshot(ulong draggedId) => snap;
    public Rect? FrameOf(ulong id) => id == 1 ? Current : Others.TryGetValue(id, out var r) ? r : null;
}

internal sealed class FakeMover : IWindowMover
{
    public List<IReadOnlyList<(ulong Id, Rect Frame)>> Batches = [];
    public Func<(ulong Id, Rect Frame), (ulong Id, Rect Frame)>? Override;
    public IReadOnlyList<(ulong Id, Rect Frame)> Apply(IReadOnlyList<(ulong Id, Rect Frame)> moves)
    {
        Batches.Add(moves);
        return [.. moves.Select(m => Override?.Invoke(m) ?? m)];
    }
}

internal sealed class FakeClock : IClock { public ulong Now; public ulong NowMs() => Now; }
internal sealed class FakeTimer : ITimer
{
    public Dictionary<TimerKind, int> Armed = [];
    public void Arm(TimerKind kind, int ms) => Armed[kind] = ms;
    public void CancelAll() => Armed.Clear();
}

internal sealed class NullLog : ILog { public void Info(string m) { } public void Debug(string m) { } }

public class DragControllerTests
{
    static readonly Rect Work = new(0, 0, 3440, 1440);
    static Snapshot Snap(int bMinW = 1) => new(
    [
        new WinInfo(1, new Rect(0, 0, 1720, 1440), 1, 1),
        new WinInfo(2, new Rect(1720, 0, 3440, 1440), bMinW, 1),
    ], Work, 1.0);

    static (DragController c, FakeInventory inv, FakeMover mv, FakeClock clk, FakeTimer tm) Make(Snapshot s)
    {
        var inv = new FakeInventory(s); var mv = new FakeMover(); var clk = new FakeClock(); var tm = new FakeTimer();
        return (new DragController(inv, mv, clk, new NullLog(), tm), inv, mv, clk, tm);
    }

    [Fact]
    public void Resize_moves_neighbour_in_one_batch()
    {
        var (c, inv, mv, _, _) = Make(Snap());
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 1820, 1440);
        c.OnLocationChange(1);
        Assert.Equal(DragState.Resizing, c.State);
        var batch = Assert.Single(mv.Batches);
        Assert.Equal((2UL, new Rect(1820, 0, 3440, 1440)), Assert.Single(batch));
    }

    [Fact]
    public void Pure_move_is_ignored_without_attraction()
    {
        var (c, inv, mv, _, _) = Make(Snap());
        c.SnapActive = false;
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(50, 50, 1770, 1490);
        c.OnLocationChange(1);
        Assert.Equal(DragState.Ignoring, c.State);
        Assert.Empty(mv.Batches);
    }

    [Fact]
    public void Trailing_flush_applies_last_frame()
    {
        var (c, inv, mv, clk, tm) = Make(Snap());
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 1800, 1440); c.OnLocationChange(1);
        clk.Now = 3; inv.Current = new Rect(0, 0, 1850, 1440); c.OnLocationChange(1);
        Assert.Single(mv.Batches);
        Assert.Equal(5, tm.Armed[TimerKind.Flush]);
        clk.Now = 8; c.OnTimer(TimerKind.Flush); // mouse is still: pending must still be applied
        Assert.Equal(2, mv.Batches.Count);
        Assert.Equal(1850, mv.Batches[1][0].Frame.L);
    }

    [Fact]
    public void Settle_learns_asynchronously_enforced_minimum()
    {
        var (c, inv, mv, clk, tm) = Make(Snap());
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 2400, 1440);
        c.OnLocationChange(1);
        Assert.True(tm.Armed.ContainsKey(TimerKind.Settle));
        inv.Others[2] = new Rect(2000, 0, 3440, 1440); // app later enforced width 1440
        clk.Now = 150; c.OnTimer(TimerKind.Settle);
        Assert.Equal(2, mv.Batches.Count);
        Assert.Equal(2000, mv.Batches[1][0].Frame.L);
    }

    [Fact]
    public void Verify_reapplies_overwritten_snap_back()
    {
        var (c, inv, mv, clk, tm) = Make(Snap(bMinW: 1600));
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 2020, 1440);
        c.OnLocationChange(1);
        c.OnMoveSizeEnd(1);
        Assert.True(tm.Armed.ContainsKey(TimerKind.Verify));
        int before = mv.Batches.Count;
        inv.Current = new Rect(0, 0, 2020, 1440); // system overwrote the snap-back
        inv.Others[2] = new Rect(1840, 0, 3440, 1440);
        clk.Now = 60; c.OnTimer(TimerKind.Verify);
        Assert.Equal(before + 1, mv.Batches.Count);
        Assert.Equal((1UL, new Rect(0, 0, 1840, 1440)), Assert.Single(mv.Batches[^1]));
    }

    [Fact]
    public void Min_width_causes_snap_back_of_dragged_window()
    {
        var (c, inv, mv, _, _) = Make(Snap(bMinW: 1600));
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 2020, 1440);
        c.OnLocationChange(1);
        c.OnMoveSizeEnd(1);
        var last = mv.Batches[^1];
        Assert.Equal((1UL, new Rect(0, 0, 1840, 1440)), Assert.Single(last));
    }

    [Fact]
    public void Learns_minimum_from_applied_frame()
    {
        var (c, inv, mv, clk, _) = Make(Snap());
        mv.Override = m => m.Id == 2 ? (2, m.Frame with { L = 2000 }) : m; // window refuses to shrink below 1440 wide
        c.OnMoveSizeStart(1);
        inv.Current = new Rect(0, 0, 2400, 1440);
        c.OnLocationChange(1);
        clk.Now = 100; inv.Current = new Rect(0, 0, 2500, 1440);
        c.OnLocationChange(1);
        Assert.Equal(2000, mv.Batches[1][0].Frame.L); // clamped to learned min (width 1440)
    }

    [Fact]
    public void Non_eligible_dragged_window_is_ignored()
    {
        var snap = new Snapshot([new WinInfo(2, new Rect(1720, 0, 3440, 1440), 1, 1)], Work, 1.0);
        var (c, _, mv, _, _) = Make(snap);
        c.OnMoveSizeStart(1);
        Assert.Equal(DragState.Ignoring, c.State);
        c.OnMoveSizeEnd(1);
        Assert.Empty(mv.Batches);
    }
}
