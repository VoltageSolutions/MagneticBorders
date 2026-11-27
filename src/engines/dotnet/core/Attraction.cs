namespace MagneticBorders.Core;

/// <summary>What kind of line an edge is held on: a work-area side (Screen), another window's facing edge (EdgeToEdge, SNAP-033) or another window's edge of the same side for stacked windows (Aligned, SNAP-034).</summary>
public enum TargetKind { Screen, EdgeToEdge, Aligned }

/// <summary>Why a held edge was let go: the free position moved past the breakaway distance (SNAP-041) or the target stopped being a target (SNAP-035).</summary>
public enum ReleaseReason { Breakaway, TargetGone }

/// <summary>Tunables of one attraction, already scaled to physical pixels.</summary>
public sealed record AttractionConfig
{
    /// <summary>Pull-in distance in pixels (SNAP-010).</summary>
    public int PullIn { get; init; } = 16;
    /// <summary>Breakaway distance in pixels (SNAP-011). Values below PullIn are raised to PullIn (SNAP-043).</summary>
    public int Breakaway { get; init; } = 24;
    /// <summary>Highest speed in pixels per second at which an edge may start being held (SNAP-012).</summary>
    public double MaxSpeed { get; init; } = 600.0;
    /// <summary>Work-area sides are targets (SNAP-030).</summary>
    public bool ScreenEdges { get; init; } = true;
    /// <summary>Other windows are targets (SNAP-032).</summary>
    public bool Windows { get; init; } = true;
}

/// <summary>Which sides of the work area are targets for the same side of the dragged window (SNAP-031). All four are targets by default.</summary>
public sealed record SideTargets
{
    /// <summary>Left side.</summary>
    public bool Left { get; init; } = true;
    /// <summary>Top side.</summary>
    public bool Top { get; init; } = true;
    /// <summary>Right side.</summary>
    public bool Right { get; init; } = true;
    /// <summary>Bottom side.</summary>
    public bool Bottom { get; init; } = true;

    /// <summary>True when the side of the given edge is a target.</summary>
    public bool Allows(Edge e) => e switch
    {
        Edge.Left => Left,
        Edge.Top => Top,
        Edge.Right => Right,
        _ => Bottom,
    };

    /// <summary>Decides which sides of the work area are targets: a side is not a target when another work area in <paramref name="all"/> touches it (within 1 px) with an overlapping span (SNAP-031). The work area itself, found in <paramref name="all"/>, is skipped.</summary>
    public static SideTargets For(Rect work, IReadOnlyList<Rect> all)
    {
        bool left = true, top = true, right = true, bottom = true;
        foreach (var o in all)
        {
            if (o == work) continue;
            bool xOverlap = work.L < o.R && o.L < work.R;
            bool yOverlap = work.T < o.B && o.T < work.B;
            if (yOverlap && Math.Abs(o.R - work.L) <= 1) left = false;
            if (yOverlap && Math.Abs(o.L - work.R) <= 1) right = false;
            if (xOverlap && Math.Abs(o.B - work.T) <= 1) top = false;
            if (xOverlap && Math.Abs(o.T - work.B) <= 1) bottom = false;
        }
        return new SideTargets { Left = left, Top = top, Right = right, Bottom = bottom };
    }
}

/// <summary>A half-open span [Lo, Hi) along an edge.</summary>
/// <param name="Lo">First coordinate.</param>
/// <param name="Hi">One past the last coordinate.</param>
public readonly record struct EdgeSpan(int Lo, int Hi);

/// <summary>Another window as a target: its frame and, for each edge (indexed by Edge), the parts of that edge that no window above it in the z-order covers (SNAP-038).</summary>
/// <param name="Frame">Visual frame.</param>
/// <param name="Visible">Visible spans of the left, top, right and bottom edges, in Edge order.</param>
public sealed record TargetWindow(Rect Frame, IReadOnlyList<IReadOnlyList<EdgeSpan>> Visible)
{
    private static readonly Edge[] s_edges = [Edge.Left, Edge.Top, Edge.Right, Edge.Bottom];

    /// <summary>Builds a target from a frame and the frames of the windows above it. An edge pixel is covered when it lies inside one of <paramref name="above"/> (SNAP-038). With no windows above, every edge is fully visible.</summary>
    public static TargetWindow Create(Rect frame, IReadOnlyList<Rect> above)
    {
        var visible = new IReadOnlyList<EdgeSpan>[s_edges.Length];
        foreach (var e in s_edges)
        {
            bool vertical = e.IsVertical();
            // The edge's own row or column of pixels, inside the frame (R and B are exclusive).
            int at = e switch
            {
                Edge.Right => frame.R - 1,
                Edge.Bottom => frame.B - 1,
                _ => frame.Coord(e),
            };
            var spans = new List<EdgeSpan> { vertical ? new EdgeSpan(frame.T, frame.B) : new EdgeSpan(frame.L, frame.R) };
            foreach (var o in above)
            {
                bool crosses = vertical ? (o.L <= at && at < o.R) : (o.T <= at && at < o.B);
                if (!crosses) continue;
                var cut = vertical ? new EdgeSpan(o.T, o.B) : new EdgeSpan(o.L, o.R);
                var next = new List<EdgeSpan>();
                foreach (var s in spans)
                {
                    if (cut.Hi <= s.Lo || s.Hi <= cut.Lo)
                    {
                        next.Add(s);
                        continue;
                    }
                    if (s.Lo < cut.Lo) next.Add(new EdgeSpan(s.Lo, cut.Lo));
                    if (cut.Hi < s.Hi) next.Add(new EdgeSpan(cut.Hi, s.Hi));
                }
                spans = next;
            }
            visible[(int)e] = spans;
        }
        return new TargetWindow(frame, visible);
    }
}

/// <summary>One engage or release, for the log (SNAP-060).</summary>
/// <param name="Engaged">True when an edge started being held, false when it was released.</param>
/// <param name="Edge">The window edge concerned.</param>
/// <param name="Line">The target line.</param>
/// <param name="Kind">Kind of the target line.</param>
/// <param name="Speed">Speed in pixels per second at the time of the event.</param>
/// <param name="Reason">Reason of a release; meaningless for an engage.</param>
public readonly record struct AttractionEvent(bool Engaged, Edge Edge, int Line, TargetKind Kind, double Speed, ReleaseReason Reason = ReleaseReason.Breakaway);

/// <summary>The attraction of one move drag (SNAP-001 to SNAP-060). Feed it every free position with Update; it returns the held position: the free position with each held edge moved onto its line. The x axis (left and right edges) and the y axis (top and bottom edges) are independent, so a corner holds two lines. Single-threaded and pure: the caller supplies the clock.</summary>
public sealed class Attraction
{
    /// <summary>Length of the span of free positions the speed is measured over (SNAP-013).</summary>
    private const ulong SpeedWindowMs = 80;

    /// <summary>A held edge: which edge, the line, what it is held on and, for window targets, which window (index into the others, or -1 for a work-area side).</summary>
    private readonly record struct Held(Edge Edge, int Line, TargetKind Kind, int Window);

    /// <summary>A possible target found for a free edge, with its distance from the free edge.</summary>
    private readonly record struct Candidate(Edge Edge, int Line, TargetKind Kind, int Window, int Dist);

    private static readonly Edge[] s_xEdges = [Edge.Left, Edge.Right];
    private static readonly Edge[] s_yEdges = [Edge.Top, Edge.Bottom];

    private readonly int _pullIn;
    private readonly int _breakaway;
    private readonly double _maxSpeed;
    private readonly bool _screenEdges;
    private readonly bool _windows;
    private readonly IReadOnlyList<TargetWindow> _others;
    private readonly Rect _work;
    private readonly SideTargets _sides;
    /// <summary>Lines released and not yet left behind, per Edge (SNAP-042).</summary>
    private readonly List<int>[] _blocked = [[], [], [], []];
    private readonly List<(ulong Ms, int L, int T)> _samples = [];
    private readonly List<AttractionEvent> _events = [];
    private Held? _x, _y;

    /// <summary>Creates the attraction. <paramref name="others"/> are the other eligible windows on the same monitor, <paramref name="work"/> is the work area of that monitor and <paramref name="sides"/> tells which of its sides are targets.</summary>
    public Attraction(AttractionConfig config, IReadOnlyList<TargetWindow> others, Rect work, SideTargets sides)
    {
        _pullIn = Math.Max(config.PullIn, 0);
        _breakaway = Math.Max(config.Breakaway, _pullIn); // SNAP-043
        _maxSpeed = config.MaxSpeed;
        _screenEdges = config.ScreenEdges;
        _windows = config.Windows;
        _others = others;
        _work = work;
        _sides = sides;
    }

    /// <summary>The position returned by the last Update (an empty Rect before the first one).</summary>
    public Rect Current { get; private set; }
    /// <summary>Number of times an edge started being held.</summary>
    public long Snaps { get; private set; }
    /// <summary>Speed in pixels per second measured at the last Update (SNAP-013).</summary>
    public double Speed { get; private set; }
    /// <summary>True when at least one edge is held after the last Update.</summary>
    public bool IsHeld => _x is not null || _y is not null;
    /// <summary>True when the left or right edge is held after the last Update.</summary>
    public bool HeldX => _x is not null;
    /// <summary>True when the top or bottom edge is held after the last Update.</summary>
    public bool HeldY => _y is not null;

    /// <summary>Convenience for windows with nothing above them: every edge of <paramref name="frames"/> is fully visible.</summary>
    public static Attraction FromFrames(AttractionConfig config, IReadOnlyList<Rect> frames, Rect work, SideTargets sides) =>
        new(config, [.. frames.Select(f => TargetWindow.Create(f, []))], work, sides);

    /// <summary>Takes the free position at time <paramref name="nowMs"/> and returns the held position. The size never changes.</summary>
    public Rect Update(Rect free, ulong nowMs)
    {
        Measure(free, nowMs);
        // x targets are checked against the y position the window had (held or free), y targets against the new x
        // position. When y changed, x is checked once more so a slide off a target is seen in the same update.
        int dy = 0;
        if (_y is { } yh) dy = yh.Line - free.Coord(yh.Edge);
        int dx = Resolve(ref _x, true, Shifted(free, 0, dy), free);
        int newDy = Resolve(ref _y, false, Shifted(free, dx, 0), free);
        if (newDy != dy) dx = Resolve(ref _x, true, Shifted(free, 0, newDy), free);
        Current = Shifted(free, dx, newDy);
        return Current;
    }

    /// <summary>Returns the engage and release events since the last call and clears them.</summary>
    public IReadOnlyList<AttractionEvent> TakeEvents()
    {
        var taken = _events.ToArray();
        _events.Clear();
        return taken;
    }

    /// <summary>Returns r moved by (dx, dy).</summary>
    private static Rect Shifted(Rect r, int dx, int dy) => new(r.L + dx, r.T + dy, r.R + dx, r.B + dy);

    /// <summary>True when candidate a beats b: nearer, or equally near and a window rather than a screen edge (SNAP-037).</summary>
    private static bool Better(int aDist, TargetKind aKind, int bDist, TargetKind bKind) =>
        aDist != bDist ? aDist < bDist : aKind != TargetKind.Screen && bKind == TargetKind.Screen;

    /// <summary>Records the sample and refreshes Speed (SNAP-013).</summary>
    private void Measure(Rect free, ulong nowMs)
    {
        _samples.Add((nowMs, free.L, free.T));
        while (_samples.Count > 1 && nowMs - _samples[0].Ms > SpeedWindowMs) _samples.RemoveAt(0);
        var first = _samples[0];
        var last = _samples[^1];
        if (_samples.Count < 2 || last.Ms <= first.Ms) return; // keep the last measured value
        double dx = last.L - first.L;
        double dy = last.T - first.T;
        Speed = double.Hypot(dx, dy) * 1000.0 / (last.Ms - first.Ms);
    }

    /// <summary>True when the line is blocked for the edge.</summary>
    private bool Blocked(Edge edge, int line) => _blocked[(int)edge].Contains(line);

    /// <summary>True when a window target for the edge still satisfies its condition (SNAP-033, SNAP-034) and the part of the other window it depends on is visible near the dragged window (SNAP-038).</summary>
    private bool WindowTargetValid(Edge edge, TargetKind kind, Rect reference, TargetWindow target)
    {
        bool vertical = edge.IsVertical();
        var other = target.Frame;
        // The other axis of a vertical line is y, and the other axis of a horizontal line is x.
        int dLo = vertical ? reference.T : reference.L;
        int dHi = vertical ? reference.B : reference.R;
        int wLo = vertical ? other.T : other.L;
        int wHi = vertical ? other.B : other.R;
        // The edge of the other window this target depends on, and the span of the dragged window along it.
        var needed = edge.Opposite();
        int nearLo = dLo - _pullIn;
        int nearHi = dHi + _pullIn;
        if (kind == TargetKind.EdgeToEdge)
        {
            int gap = Math.Max(dLo - wHi, wLo - dHi); // negative while they overlap
            if (gap > _pullIn) return false;
        }
        else if (kind == TargetKind.Aligned)
        {
            int below = Math.Abs(dLo - wHi); // dragged window below (or right of) the other
            int above = Math.Abs(dHi - wLo);
            if (Math.Min(below, above) > _pullIn) return false;
            // Stacked: the facing edge of the other window must be visible where the dragged window is.
            needed = below <= above ? (vertical ? Edge.Bottom : Edge.Right) : (vertical ? Edge.Top : Edge.Left);
            nearLo = (vertical ? reference.L : reference.T) - _pullIn;
            nearHi = (vertical ? reference.R : reference.B) + _pullIn;
        }
        else
        {
            return true;
        }
        foreach (var s in target.Visible[(int)needed])
            if (s.Lo < nearHi && nearLo < s.Hi) return true;
        return false;
    }

    /// <summary>Finds the best target for a free edge of the axis, or null.</summary>
    private Candidate? BestCandidate(bool vertical, Rect reference, Rect free)
    {
        Candidate? best = null;
        void Consider(Edge edge, int line, TargetKind kind, int window)
        {
            int dist = Math.Abs(free.Coord(edge) - line);
            if (dist > _pullIn || Blocked(edge, line)) return;
            if (best is not { } b || Better(dist, kind, b.Dist, b.Kind))
                best = new Candidate(edge, line, kind, window, dist);
        }
        foreach (var edge in vertical ? s_xEdges : s_yEdges)
        {
            if (_screenEdges && _sides.Allows(edge))
                Consider(edge, _work.Coord(edge), TargetKind.Screen, -1);
            if (!_windows) continue;
            for (int i = 0; i < _others.Count; i++)
            {
                var w = _others[i];
                if (WindowTargetValid(edge, TargetKind.EdgeToEdge, reference, w))
                    Consider(edge, w.Frame.Coord(edge.Opposite()), TargetKind.EdgeToEdge, i);
                if (WindowTargetValid(edge, TargetKind.Aligned, reference, w))
                    Consider(edge, w.Frame.Coord(edge), TargetKind.Aligned, i);
            }
        }
        return best;
    }

    /// <summary>Releases the held edge of the axis and blocks its line (SNAP-042).</summary>
    private void Release(ref Held? axis, ReleaseReason reason)
    {
        var h = axis!.Value;
        axis = null;
        _blocked[(int)h.Edge].Add(h.Line);
        _events.Add(new AttractionEvent(false, h.Edge, h.Line, h.Kind, Speed, reason));
    }

    /// <summary>Resolves one axis against <paramref name="free"/> and returns the shift to add to it. <paramref name="reference"/> is the position used for the other-axis conditions of window targets.</summary>
    private int Resolve(ref Held? axis, bool vertical, Rect reference, Rect free)
    {
        // A released line stays blocked until the free edge is more than the pull-in distance away (SNAP-042).
        foreach (var edge in vertical ? s_xEdges : s_yEdges)
        {
            int coord = free.Coord(edge);
            _blocked[(int)edge].RemoveAll(line => Math.Abs(coord - line) > _pullIn);
        }
        if (axis is { } h)
        {
            int coord = free.Coord(h.Edge);
            if (Math.Abs(coord - h.Line) > _breakaway) Release(ref axis, ReleaseReason.Breakaway);
            else if (h.Window >= 0 && !WindowTargetValid(h.Edge, h.Kind, reference, _others[h.Window])) Release(ref axis, ReleaseReason.TargetGone);
            else return h.Line - coord; // SNAP-022: the other axis keeps following the mouse
            // The released line stays blocked (SNAP-042), and nothing else is held on this axis in the same update,
            // so the edge goes free before it can be caught again.
            return 0;
        }
        if (Speed > _maxSpeed) return 0; // SNAP-012, SNAP-040
        if (BestCandidate(vertical, reference, free) is not { } c) return 0;
        axis = new Held(c.Edge, c.Line, c.Kind, c.Window);
        Snaps++;
        _events.Add(new AttractionEvent(true, c.Edge, c.Line, c.Kind, Speed));
        return c.Line - free.Coord(c.Edge);
    }
}
