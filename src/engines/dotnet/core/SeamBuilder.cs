namespace MagneticBorders.Core;

/// <summary>Pure geometry: finds the windows glued to a dragged edge and computes where they must go. No Win32 types, no state. Called from DragController on the UI thread and from the fixture runner and tests.</summary>
public static class SeamBuilder
{
    /// <summary>Builds the seam for one edge of the dragged window. Collects windows whose same or opposite edge lies within the tolerance of the dragged edge, then keeps only those chained to the dragged window along the seam line.</summary>
    /// <returns>The seam, or null when the dragged window is missing, the edge sits on the work area border, or no neighbor touches it.</returns>
    public static Seam? Build(IReadOnlyList<WinInfo> windows, ulong draggedId, Edge edge, Rect workArea, Config cfg)
    {
        WinInfo? dragged = null;
        foreach (var w in windows) if (w.Id == draggedId) dragged = w;
        if (dragged is null) return null;

        int tol = cfg.GlueTolerance;
        int x = dragged.Value.Frame.Coord(edge);
        if (Math.Abs(x - workArea.Coord(edge)) <= tol) return null;

        bool vertical = edge.IsVertical();
        var opp = edge.Opposite();
        (int lo, int hi) Span(Rect r) => vertical ? (r.T, r.B) : (r.L, r.R);
        var s = Span(dragged.Value.Frame);

        var cands = new List<(WinInfo W, Side Side, int D)>();
        foreach (var w in windows)
        {
            if (w.Id == draggedId) continue;
            int dOpp = w.Frame.Coord(opp) - x;
            int dSame = w.Frame.Coord(edge) - x;
            if (Math.Abs(dOpp) <= tol) cands.Add((w, Side.Opposite, dOpp));
            else if (Math.Abs(dSame) <= tol) cands.Add((w, Side.Same, dSame));
        }

        var members = new List<SeamMember>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = cands.Count - 1; i >= 0; i--)
            {
                var (w, side, d) = cands[i];
                var (lo, hi) = Span(w.Frame);
                int gap = Math.Max(0, Math.Max(lo - s.hi, s.lo - hi));
                if (gap > tol) continue;
                s = (Math.Min(s.lo, lo), Math.Max(s.hi, hi));
                members.Add(new SeamMember(w.Id, w.Frame, side, d, w.MinW, w.MinH));
                cands.RemoveAt(i);
                changed = true;
            }
        }
        return members.Count == 0 ? null : new Seam(edge, x, members);
    }

    /// <summary>Moves the seam to newPos, clamped so no member shrinks below its minimum size, and returns the new frame of each member and the clamped position. If the minimums conflict, the seam stays where it was.</summary>
    /// <param name="seam"></param>
    /// <param name="newPos"></param>
    /// <param name="learned">Optional per-window learned minimums (W,H), applied on top of declared ones.</param>
    public static Plan Propagate(Seam seam, int newPos, IReadOnlyDictionary<ulong, (int W, int H)>? learned = null)
    {
        int lo = int.MinValue, hi = int.MaxValue;
        foreach (var m in seam.Members)
        {
            int min = seam.Vertical ? m.MinW : m.MinH;
            if (learned is not null && learned.TryGetValue(m.Id, out var l))
                min = Math.Max(min, seam.Vertical ? l.W : l.H);
            var moved = m.Side == Side.Same ? seam.Edge : seam.Edge.Opposite();
            switch (moved)
            {
                case Edge.Left: hi = Math.Min(hi, m.Frame.R - min - m.D); break;
                case Edge.Top: hi = Math.Min(hi, m.Frame.B - min - m.D); break;
                case Edge.Right: lo = Math.Max(lo, m.Frame.L + min - m.D); break;
                default: lo = Math.Max(lo, m.Frame.T + min - m.D); break;
            }
        }
        int pos = lo > hi ? seam.X : Math.Min(hi, Math.Max(lo, newPos));
        var moves = new List<(ulong, Rect)>(seam.Members.Count);
        foreach (var m in seam.Members)
        {
            var moved = m.Side == Side.Same ? seam.Edge : seam.Edge.Opposite();
            moves.Add((m.Id, m.Frame.With(moved, pos + m.D)));
        }
        return new Plan(moves, pos);
    }
}
