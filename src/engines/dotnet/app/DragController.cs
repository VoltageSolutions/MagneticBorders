using MagneticBorders.Core;

namespace MagneticBorders.Application;

/// <summary>Phase of the current drag: Idle (no drag), Classifying (waiting for the first size change), Resizing (seams built, neighbors follow) or Ignoring (not a glued resize).</summary>
public enum DragState { Idle, Classifying, Resizing, Ignoring }

/// <summary>State machine for one resize drag. Receives move/size start, location change, timer and end events and drives IWindowMover. All members run on the UI thread: events arrive from WinEvent callbacks and Win32 timers, both dispatched by the UI thread's message loop. Owned by DI as a singleton; depends only on ports.</summary>
/// <param name="inventory">Window snapshots and frame reads.</param>
/// <param name="mover">Applies batched window moves.</param>
/// <param name="clock">Millisecond clock for throttling and logs.</param>
/// <param name="log">Log sink.</param>
/// <param name="timer">One-shot timers (Flush, Settle, Verify).</param>
/// <param name="baseTolerancePx">Initial glue tolerance in pixels before DPI scaling.</param>
/// <param name="buildName">Prefix for log lines.</param>
public sealed class DragController(
    IWindowInventory inventory, IWindowMover mover, IClock clock, ILog log, ITimer timer,
    int baseTolerancePx = 12, string buildName = "net")
{
    /// <summary>Minimum gap between two applied batches, the settle delay after the last move, and the delay before the post-drag verification.</summary>
    private const ulong ThrottleMs = 8;
    private const int SettleMs = 150, VerifyMs = 60;
    /// <summary>Per-window minimum sizes learned from windows that refused to shrink; fed back into Propagate. Lives for the process.</summary>
    private readonly Dictionary<ulong, (int W, int H)> _learned = [];
    /// <summary>Last frame requested for each seam member in this drag; compared with the real frame during Settle and Verify.</summary>
    private readonly Dictionary<ulong, Rect> _requested = [];
    /// <summary>Seams built for the current drag (at most one per axis).</summary>
    private readonly List<Seam> _seams = [];
    private Snapshot? _snap;
    private ulong _id;
    private Rect _start, _last;
    private Rect? _pending;
    private ulong? _lastApply;
    private ulong _t0;
    private int _applies;
    private bool _verifyArmed;
    private List<(Seam S, int Clamp)> _endClamps = [];

    /// <summary>Current phase. Set only by the controller.</summary>
    public DragState State { get; private set; } = DragState.Idle;

    /// <summary>Live glue tolerance in px (applies on the next drag).</summary>
    public int TolerancePx { get; set; } = baseTolerancePx;
    /// <summary>False when Magnetic Borders or magnetic coupling is disabled: drags are ignored.</summary>
    public bool Active { get; set; } = true;
    /// <summary>Resize drags seen.</summary>
    public long Drags { get; private set; }
    /// <summary>Resize drags that built at least one seam.</summary>
    public long GluedDrags { get; private set; }

    /// <summary>Writes an Info log line prefixed with the build name and ms since drag start.</summary>
    private void L(string m) => log.Info($"{buildName} +{clock.NowMs() - _t0}ms {m}");
    /// <summary>Milliseconds since the current drag started.</summary>
    private ulong Elapsed => clock.NowMs() - _t0;

    /// <summary>Called when a window starts a move or size loop. Cancels timers, takes a snapshot and enters Classifying, or Ignoring when inactive or the window is unknown.</summary>
    public void OnMoveSizeStart(ulong id)
    {
        timer.CancelAll();
        _verifyArmed = false;
        if (!Active) { _seams.Clear(); _id = id; State = DragState.Ignoring; return; }
        _seams.Clear();
        _requested.Clear();
        _pending = null;
        _id = id;
        _lastApply = null;
        _applies = 0;
        _t0 = clock.NowMs();
        _snap = inventory.Snapshot(id);
        WinInfo? me = null;
        foreach (var w in _snap.Windows) if (w.Id == id) me = w;
        if (me is null) { State = DragState.Ignoring; return; }
        _start = _last = me.Value.Frame;
        State = DragState.Classifying;
    }

    /// <summary>Called for each location change of the dragged window. In Classifying it decides which edges changed, builds seams and enters Resizing (or Ignoring for moves); in Resizing it applies the new frame to the seam members and re-arms the Settle timer.</summary>
    public void OnLocationChange(ulong id)
    {
        if (id != _id || _snap is null) return;
        if (State is not (DragState.Classifying or DragState.Resizing)) return;
        var f = inventory.FrameOf(id);
        if (f is null) return;
        var frame = f.Value;

        if (State == DragState.Classifying)
        {
            if (frame.Width == _start.Width && frame.Height == _start.Height) { State = DragState.Ignoring; return; }
            Drags++;
            var cfg = new Config((int)Math.Round(TolerancePx * _snap.DpiScale));
            var edges = new List<Edge>();
            if (frame.L != _start.L && frame.R == _start.R) edges.Add(Edge.Left);
            else if (frame.R != _start.R && frame.L == _start.L) edges.Add(Edge.Right);
            if (frame.T != _start.T && frame.B == _start.B) edges.Add(Edge.Top);
            else if (frame.B != _start.B && frame.T == _start.T) edges.Add(Edge.Bottom);
            foreach (var e in edges)
            {
                var seam = SeamBuilder.Build(_snap.Windows, id, e, _snap.WorkArea, cfg);
                if (seam is not null) _seams.Add(seam);
            }
            if (_seams.Count == 0) { State = DragState.Ignoring; return; }
            GluedDrags++;
            foreach (var s in _seams)
                L($"seam {s.Edge} x={s.X} members=[{string.Join(",", s.Members.Select(m => m.Id))}]");
            State = DragState.Resizing;
        }

        _last = frame;
        ApplyFrame(frame, force: false);
        timer.Arm(TimerKind.Settle, SettleMs);
    }

    /// <summary>Called by Win32Timer when a one-shot timer fires. Flush applies a throttled pending frame, Settle reads back sizes, Verify checks the final layout.</summary>
    public void OnTimer(TimerKind kind)
    {
        switch (kind)
        {
            case TimerKind.Flush:
                if (State == DragState.Resizing && _pending is { } p) ApplyFrame(p, force: true);
                break;
            case TimerKind.Settle:
                if (State == DragState.Resizing) Settle();
                break;
            case TimerKind.Verify:
                if (_verifyArmed) Verify();
                break;
        }
    }

    /// <summary>Runs after the drag pauses: records minimum sizes that members refused, re-applies if any, and logs the gap to the clamped position.</summary>
    private void Settle()
    {
        var mism = new List<string>();
        foreach (var mid in _seams.SelectMany(s => s.Members.Select(m => m.Id)).Distinct())
        {
            if (!_requested.TryGetValue(mid, out var req) || inventory.FrameOf(mid) is not { } got) continue;
            if (got.Width == req.Width && got.Height == req.Height) continue;
            _learned.TryGetValue(mid, out var l);
            if (got.Width > req.Width) l.W = got.Width;
            if (got.Height > req.Height) l.H = got.Height;
            _learned[mid] = l;
            mism.Add($" id{mid} req={req.Width}x{req.Height} got={got.Width}x{got.Height}");
        }
        var frame = inventory.FrameOf(_id) ?? _last;
        if (mism.Count > 0) ApplyFrame(frame, force: true);
        var s0 = _seams[0];
        int pos = frame.Coord(s0.Edge);
        int clamp = SeamBuilder.Propagate(s0, pos, _learned).ClampedPos;
        L($"settled +{Elapsed} {s0.Edge}={pos} clamp={clamp} gap={pos - clamp}{string.Concat(mism)}");
    }

    /// <summary>Called when the move or size loop ends. Applies the final frame, snaps the dragged window back to the clamped position if needed, and arms the Verify timer.</summary>
    public void OnMoveSizeEnd(ulong id)
    {
        if (id == _id && State == DragState.Resizing)
        {
            timer.CancelAll();
            _pending = null;
            var f = inventory.FrameOf(id) ?? _last;
            ApplyFrame(f, force: true);
            var adj = f;
            _endClamps = [];
            foreach (var s in _seams)
            {
                int pos = f.Coord(s.Edge);
                int clamped = SeamBuilder.Propagate(s, pos, _learned).ClampedPos;
                _endClamps.Add((s, clamped));
                if (clamped != pos) adj = adj.With(s.Edge, clamped);
            }
            if (adj != f) mover.Apply([(id, adj)]);
            L($"drag end id={id} applies={_applies} snapBack={adj != f}");
            _verifyArmed = true;
            timer.Arm(TimerKind.Verify, VerifyMs);
        }
        else
        {
            timer.CancelAll();
            _seams.Clear();
        }
        State = DragState.Idle;
        _snap = null;
    }

    /// <summary>Runs shortly after the drag ended. Corrects the dragged window and any member whose real size differs from what was requested, then clears the seams.</summary>
    private void Verify()
    {
        _verifyArmed = false;
        var problems = new List<string>();
        var frame = inventory.FrameOf(_id);
        var adj = frame ?? _last;
        foreach (var (s, clamp) in _endClamps)
        {
            if (frame is { } fr && fr.Coord(s.Edge) != clamp)
            {
                problems.Add($" {s.Edge}={fr.Coord(s.Edge)} want={clamp}");
                adj = adj.With(s.Edge, clamp);
            }
        }
        var moves = new List<(ulong, Rect)>();
        foreach (var (mid, req) in _requested)
            if (inventory.FrameOf(mid) is { } got && (got.Width != req.Width || got.Height != req.Height))
            {
                problems.Add($" id{mid} req={req.Width}x{req.Height} got={got.Width}x{got.Height}");
                moves.Add((mid, req));
            }
        if (problems.Count == 0) { L($"end +{Elapsed} ok"); }
        else
        {
            if (frame is { } fr2 && adj != fr2) moves.Add((_id, adj));
            if (moves.Count > 0) mover.Apply(moves);
            L($"end +{Elapsed} fixed{string.Concat(problems)}");
        }
        _seams.Clear();
        _endClamps = [];
    }

    /// <summary>Computes all member moves for the dragged frame and sends them to the mover as one batch. Unless force is set, batches closer than ThrottleMs are deferred through the Flush timer. Learns minimum sizes from the read-back frames.</summary>
    private void ApplyFrame(Rect frame, bool force)
    {
        var merged = new Dictionary<ulong, Rect>();
        foreach (var s in _seams)
        {
            var plan = SeamBuilder.Propagate(s, frame.Coord(s.Edge), _learned);
            foreach (var (mid, r) in plan.Moves)
            {
                var cur = merged.TryGetValue(mid, out var c) ? c : r;
                merged[mid] = s.Vertical ? cur with { L = r.L, R = r.R } : cur with { T = r.T, B = r.B };
            }
        }
        if (merged.Count == 0) return;
        ulong now = clock.NowMs();
        if (!force && _lastApply is { } la && now - la < ThrottleMs)
        {
            _pending = frame;
            timer.Arm(TimerKind.Flush, (int)(ThrottleMs - (now - la)));
            return;
        }
        _pending = null;
        _lastApply = now;
        _applies++;
        foreach (var kv in merged) _requested[kv.Key] = kv.Value;
        var applied = mover.Apply(merged.Select(kv => (kv.Key, kv.Value)).ToList());
        log.Debug($"{buildName} +{Elapsed}ms apply members={merged.Count} " +
                  string.Join(" ", _seams.Select(s => $"{s.Edge}={frame.Coord(s.Edge)} clamp={SeamBuilder.Propagate(s, frame.Coord(s.Edge), _learned).ClampedPos}")));
        foreach (var (aid, ar) in applied)
        {
            if (!merged.TryGetValue(aid, out var req)) continue;
            _learned.TryGetValue(aid, out var l);
            if (ar.Width > req.Width) l.W = ar.Width;
            if (ar.Height > req.Height) l.H = ar.Height;
            _learned[aid] = l;
        }
    }
}
