namespace MagneticBorders.Core;

/// <summary>Runs an Attraction from mouse movement instead of from window positions (SNAP-070 to SNAP-076). Windows keeps the window at a fixed offset from the cursor during a move, so holding the window means holding the cursor: on a held axis the cursor stays put while the mouse's movement builds up in the free position, and on a free axis the cursor goes where the mouse sends it. Windows then moves the window to the held position itself, so nothing is corrected after the fact. Single-threaded and pure: the caller supplies the clock.</summary>
public sealed class PointerDrag
{
    private readonly Rect _frame0;
    private readonly Point _cursor0;
    private Point _cursor;
    private Rect _free;

    /// <summary>Starts from the window's frame and the cursor position at the same moment.</summary>
    public PointerDrag(Attraction attraction, Rect frame, Point cursor)
    {
        Attraction = attraction;
        _frame0 = frame;
        _cursor0 = cursor;
        _cursor = cursor;
        _free = frame;
    }

    /// <summary>The attraction, for its events and counters.</summary>
    public Attraction Attraction { get; }

    /// <summary>The window position that matches the cursor returned by the last Move.</summary>
    public Rect Window => AtY(AtX(_frame0, _frame0.L + _cursor.X - _cursor0.X), _frame0.T + _cursor.Y - _cursor0.Y);

    /// <summary>Takes the position Windows is about to give the cursor and returns where the cursor must go instead. The result equals <paramref name="proposed"/> when nothing is held.</summary>
    public Point Move(Point proposed, ulong nowMs)
    {
        int dx = proposed.X - _cursor.X;
        int dy = proposed.Y - _cursor.Y;
        bool heldX = Attraction.HeldX;
        bool heldY = Attraction.HeldY;
        // SNAP-071: a held axis builds up the mouse's movement; a free axis follows the cursor, so a free position
        // left far out by an earlier hold starts again from where the window is.
        _free = heldX ? AtX(_free, _free.L + dx) : AtX(_free, _frame0.L + proposed.X - _cursor0.X);
        _free = heldY ? AtY(_free, _free.T + dy) : AtY(_free, _frame0.T + proposed.Y - _cursor0.Y);

        var held = Attraction.Update(_free, nowMs);
        // SNAP-072: the cursor goes where the held window position puts it.
        var o = new Point(_cursor0.X + held.L - _frame0.L, _cursor0.Y + held.T - _frame0.T);
        // SNAP-073: an axis let go in this move carries on from where the cursor is, so neither the cursor nor the
        // window jumps.
        if (heldX && !Attraction.HeldX) o = o with { X = _cursor.X + dx };
        if (heldY && !Attraction.HeldY) o = o with { Y = _cursor.Y + dy };
        _cursor = o;
        return o;
    }

    /// <summary>Returns r with its left edge at x, same width.</summary>
    private static Rect AtX(Rect r, int x) => new(x, r.T, r.R + x - r.L, r.B);

    /// <summary>Returns r with its top edge at y, same height.</summary>
    private static Rect AtY(Rect r, int y) => new(r.L, y, r.R, r.B + y - r.T);
}
