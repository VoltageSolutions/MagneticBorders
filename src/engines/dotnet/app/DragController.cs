using MagneticBorders.Core;

namespace MagneticBorders.Application;

/// <summary>Phase of the current drag: Idle (no drag), Classifying (waiting for the first location change), Resizing (seams built, neighbors follow), Moving (magnetic attraction holds the dragged window's edges), or Ignoring (neither a glued resize nor an attracted move).</summary>
public enum DragState { Idle, Classifying, Resizing, Moving, Ignoring }

/// <summary>Magnetic attraction tunables in effective pixels (96 DPI); the controller scales them by Snapshot.DpiScale.</summary>
public sealed record SnapOptions
{
    /// <summary>Pull-in distance (snap.distance_px).</summary>
    public int DistancePx { get; init; } = 16;
    /// <summary>Breakaway distance (snap.breakaway_px).</summary>
    public int BreakawayPx { get; init; } = 24;
    /// <summary>Snapping speed limit in pixels per second (snap.max_speed).</summary>
    public int MaxSpeed { get; init; } = 600;
    /// <summary>Work-area sides are targets (snap.screen_edges).</summary>
    public bool ScreenEdges { get; init; } = true;
    /// <summary>Other windows are targets (snap.windows).</summary>
    public bool Windows { get; init; } = true;
}

/// <summary>State machine for one drag. A resize builds seams and moves the glued neighbors (magnetic coupling); a move runs an Attraction (magnetic attraction) that either holds the cursor through the pointer hook or moves the dragged window back to the held position. Receives move/size start, location change, timer, pointer, and end events and drives IWindowMover. All members run on the UI thread: events arrive from WinEvent callbacks and Win32 timers, both dispatched by the UI thread's message loop. Owned by DI as a singleton; depends only on ports.</summary>
/// <param name="inventory">Window snapshots and frame reads.</param>
/// <param name="mover">Applies batched window moves.</param>
/// <param name="clock">Millisecond clock for throttling and logs.</param>
/// <param name="log">Log sink.</param>
/// <param name="timer">One-shot timers (Flush, Settle, Verify).</param>
/// <param name="baseTolerancePx">Initial glue tolerance in pixels before DPI scaling.</param>
/// <param name="buildName">Prefix for log lines.</param>
public sealed class DragController(
    IWindowInventory inventory, IWindowMover mover, IClock clock, ILog log, ITimer timer,
    int baseTolerancePx = 12, string buildName = "net") : IPointerEvents
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
    /// <summary>The dragged window's frame and the cursor, read when the drag starts (SNAP-071); null when either could not be read.</summary>
    private (Rect Frame, Point Cursor)? _pointerRef;
    private Rect? _pending;
    private ulong? _lastApply;
    private ulong _t0;
    private int _applies;
    private bool _verifyArmed;
    private List<(Seam S, int Clamp)> _endClamps = [];
    private bool _dragGlue, _dragSnap; // Active and SnapActive as they were at drag start
    private Attraction? _attraction; // set while Moving without the pointer hook
    private PointerDrag? _pointer; // set while Moving with the pointer hook (SNAP-070)
    private Rect _lastFree;
    private Rect? _lastHeld; // frame the window reported after our last held move
    private long _dragSnaps;

    /// <summary>Current phase. Set only by the controller.</summary>
    public DragState State { get; private set; } = DragState.Idle;

    /// <summary>Live glue tolerance in px (applies on the next drag).</summary>
    public int TolerancePx { get; set; } = baseTolerancePx;
    /// <summary>False when Magnetic Borders or magnetic coupling is disabled: resizes are ignored. Applies on the next drag.</summary>
    public bool Active { get; set; } = true;
    /// <summary>False when Magnetic Borders or magnetic attraction is disabled: moves are ignored (SNAP-001). Applies on the next drag.</summary>
    public bool SnapActive { get; set; } = true;
    /// <summary>Magnetic attraction tunables; apply on the next drag.</summary>
    public SnapOptions Snap { get; set; } = new();
    /// <summary>Holds the cursor during moves; applies on the next drag. Null, or a hook that cannot start, makes a move put the window back on the held position after Windows moves it (SNAP-076).</summary>
    public IPointerHook? Hook { get; set; }
    /// <summary>Resize drags seen.</summary>
    public long Drags { get; private set; }
    /// <summary>Resize drags that built at least one seam.</summary>
    public long GluedDrags { get; private set; }
    /// <summary>Move drags handled by magnetic attraction.</summary>
    public long MoveDrags { get; private set; }
    /// <summary>Times an edge started being held.</summary>
    public long Snaps { get; private set; }

    /// <summary>Writes an Info log line prefixed with the build name and ms since drag start.</summary>
    private void L(string m) => log.Info($"{buildName} +{clock.NowMs() - _t0}ms {m}");
    /// <summary>Milliseconds since the current drag started.</summary>
    private ulong Elapsed => clock.NowMs() - _t0;

    /// <summary>Called when a window starts a move or size loop. Cancels timers, stops a pointer hook left running, takes a snapshot, and enters Classifying, or Ignoring when both features are inactive or the window is unknown.</summary>
    public void OnMoveSizeStart(ulong id)
    {
        // SNAP-071: read the reference first. The snapshot takes milliseconds, and Windows keeps the cursor's offset in the window from this moment for the whole drag.
        _pointerRef = null;
        if (Hook is { } startHook && inventory.FrameOf(id) is { } startFrame && startHook.ReadCursor() is { } startCursor)
            _pointerRef = (startFrame, startCursor);
        timer.CancelAll();
        _verifyArmed = false;
        StopPointer();
        _attraction = null;
        _lastHeld = null;
        _dragSnaps = 0;
        _dragGlue = Active;
        _dragSnap = SnapActive;
        if (!_dragGlue && !_dragSnap) { _seams.Clear(); _id = id; State = DragState.Ignoring; return; }
        _seams.Clear();
        _requested.Clear();
        _pending = null;
        _id = id;
        _lastApply = null;
        _applies = 0;
        _t0 = clock.NowMs();
        _snap = inventory.Snapshot(id);
        WinInfo? me = null;
        foreach (var w in _snap.Windows.Where(c => c.Id == id)) me = w;
        if (me is null) { State = DragState.Ignoring; return; }
        _start = _last = me.Value.Frame;
        State = DragState.Classifying;
    }

    /// <summary>Called for each location change of the dragged window. In Classifying a change that keeps the size is a move (Moving, or Ignoring without magnetic attraction); otherwise it decides which edges changed, builds seams, and enters Resizing. In Resizing it applies the new frame to the seam members and re-arms the Settle timer. In Moving it updates the attraction.</summary>
    public void OnLocationChange(ulong id)
    {
        if (id != _id || _snap is null) return;
        if (State is not (DragState.Classifying or DragState.Resizing or DragState.Moving)) return;
        var f = inventory.FrameOf(id);
        if (f is null) return;
        var frame = f.Value;

        if (State == DragState.Moving)
        {
            OnMove(frame);
            return;
        }

        if (State == DragState.Classifying)
        {
            if (frame.Width == _start.Width && frame.Height == _start.Height)
            {
                if (!_dragSnap) { State = DragState.Ignoring; return; }
                BeginMove();
                State = DragState.Moving;
                OnMove(frame);
                return;
            }
            if (!_dragGlue) { State = DragState.Ignoring; return; }
            Drags++;
            var cfg = new Config(Scaled(TolerancePx, _snap.DpiScale));  // RESIZE-014
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

    /// <summary>Called when the move or size loop ends. After a move, applies the held position once when it still matters (SNAP-050 to SNAP-052) or just stops the pointer hook. After a resize, applies the final frame, snaps the dragged window back to the clamped position if needed, and arms the Verify timer.</summary>
    public void OnMoveSizeEnd(ulong id)
    {
        if (State == DragState.Moving && id != _id) return;
        if (id == _id && State == DragState.Moving)
        {
            timer.CancelAll();
            FinishMove(id);
        }
        else if (id == _id && State == DragState.Resizing)
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

    /// <summary>Starts a move: builds the attraction from the snapshot (scaled by the window's DPI) and starts the pointer hook when one is set.</summary>
    private void BeginMove()
    {
        MoveDrags++;
        var snap = _snap!;
        double scale = snap.DpiScale;
        var o = Snap;
        var cfg = new AttractionConfig
        {
            PullIn = Scaled(o.DistancePx, scale),
            Breakaway = Scaled(o.BreakawayPx, scale),
            MaxSpeed = o.MaxSpeed * scale,
            ScreenEdges = o.ScreenEdges,
            Windows = o.Windows,
        };
        var others = new List<TargetWindow>();
        foreach (var w in snap.Windows)
        {
            if (w.Id == _id) continue;
            // SNAP-038: only the parts of w that the windows above it leave visible attract. The dragged window moves,
            // so it never counts as covering anything.
            var above = new List<Rect>();
            foreach (var (sid, sframe) in snap.Stack ?? Array.Empty<(ulong Id, Rect Frame)>())
            {
                if (sid == w.Id) break;
                if (sid != _id) above.Add(sframe);
            }
            others.Add(TargetWindow.Create(w.Frame, above));
        }
        var sides = SideTargets.For(snap.WorkArea, snap.WorkAreas ?? Array.Empty<Rect>());
        _attraction = new Attraction(cfg, others, snap.WorkArea, sides);
        StartPointer();
        _lastFree = _start;
        L($"move start id={_id}{(_pointer is null ? "" : " pointer")}");
    }

    /// <summary>Rounds a distance in effective pixels to physical pixels, halves away from zero.</summary>
    private static int Scaled(int px, double scale) => (int)Math.Round(px * scale, MidpointRounding.AwayFromZero);

    /// <summary>Hands the attraction to a PointerDrag when the hook starts, holding the cursor from the frame and cursor read at drag start (SNAP-071) or, when those could not be read, from the ones read now; otherwise leaves it to the fallback (SNAP-076).</summary>
    private void StartPointer()
    {
        if (Hook is not { } hook || _attraction is null) return;
        if (_pointerRef is { } reference)
        {
            if (!hook.Install(this)) return;
            _pointer = new PointerDrag(_attraction, reference.Frame, reference.Cursor);
            _attraction = null;
            return;
        }
        if (inventory.FrameOf(_id) is not { } frame || !hook.Install(this)) return;
        if (hook.ReadCursor() is not { } cursor)
        {
            hook.Remove();
            return;
        }
        _pointer = new PointerDrag(_attraction, frame, cursor);
        _attraction = null;
    }

    /// <summary>Removes the pointer hook and drops the PointerDrag.</summary>
    private void StopPointer()
    {
        if (_pointer is null) return;
        Hook?.Remove();
        _pointer = null;
    }

    /// <summary>During a move with the pointer hook, returns where the cursor must go so the window lands on its held position (SNAP-070 to SNAP-074). Otherwise lets the movement through (null).</summary>
    public Point? OnPointerMove(Point proposed)
    {
        if (_pointer is null || State != DragState.Moving) return null;
        var o = _pointer.Move(proposed, clock.NowMs());
        LogSnapEvents();
        return o == proposed ? null : o;
    }

    /// <summary>Stops the pointer hook as soon as the mouse button is released, so the cursor can never stay held after a move (SNAP-075).</summary>
    public void OnPointerRelease()
    {
        if (_pointer is not null) Hook?.Remove();
    }

    /// <summary>The attraction of the current move, with or without the pointer hook.</summary>
    private Attraction? ActiveAttraction => _pointer?.Attraction ?? _attraction;

    /// <summary>Counts and logs the edges that started or stopped being held (SNAP-060).</summary>
    private void LogSnapEvents()
    {
        if (ActiveAttraction is not { } attraction) return;
        foreach (var ev in attraction.TakeEvents())
        {
            if (ev.Engaged)
            {
                Snaps++;
                _dragSnaps++;
                L($"snap hold {EdgeName(ev.Edge)}={ev.Line} target={KindName(ev.Kind)} speed={(long)Math.Round(ev.Speed, MidpointRounding.AwayFromZero)}");
            }
            else
            {
                L($"snap release {EdgeName(ev.Edge)}={ev.Line} reason={(ev.Reason == ReleaseReason.Breakaway ? "breakaway" : "target-gone")}");
            }
        }
    }

    /// <summary>Lower-case edge name for log lines.</summary>
    private static string EdgeName(Edge e) => e switch
    {
        Edge.Left => "left",
        Edge.Right => "right",
        Edge.Top => "top",
        _ => "bottom",
    };

    /// <summary>Log name of a target kind.</summary>
    private static string KindName(TargetKind k) => k switch
    {
        TargetKind.Screen => "screen",
        TargetKind.EdgeToEdge => "edge-to-edge",
        _ => "aligned",
    };

    /// <summary>Without the pointer hook: feeds a location change of the dragged window to the attraction and moves the window to the held position when it differs (SNAP-023 to SNAP-025).</summary>
    private void OnMove(Rect frame)
    {
        if (_attraction is null) return;
        if (_lastHeld is { } lh && frame == lh && frame != _lastFree) return; // SNAP-024: our own move
        _lastFree = frame;
        var held = _attraction.Update(frame, clock.NowMs());
        LogSnapEvents();
        if (held == frame)
        {
            _lastHeld = null;
            return;
        }
        var applied = mover.Apply([(_id, held)]); // SNAP-025: never throttled
        _lastHeld = applied.Count == 0 ? held : applied[0].Frame;
    }

    /// <summary>Ends a move. With the pointer hook Windows already put the window where the cursor is, so nothing is applied (SNAP-075). Without it, the held position is applied once when the window sits at the last free position and kept its size (SNAP-050, SNAP-051); nothing is applied after that (SNAP-052).</summary>
    private void FinishMove(ulong id)
    {
        Rect? held = ActiveAttraction is { IsHeld: true } active ? active.Current : null;
        if (_pointer is not null)
        {
            StopPointer();
            L($"move end id={id} snaps={_dragSnaps} pointer");
            inventory.ReportMoveResult(id, held);
            return;
        }
        var frame = inventory.FrameOf(id);
        if (_attraction is null) return;
        bool applied = false;
        if (frame is { } fr && _attraction.IsHeld && fr == _lastFree && fr.Width == _start.Width && fr.Height == _start.Height && _attraction.Current != fr)
        {
            mover.Apply([(id, _attraction.Current)]);
            applied = true;
        }
        L($"move end id={id} snaps={_dragSnaps}{(applied ? " applied" : "")}");
        inventory.ReportMoveResult(id, held);
        _attraction = null;
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
