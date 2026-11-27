using System.Globalization;
using MagneticBorders.Core;

namespace FixtureRunner;

/// <summary>One parsed .layout test case: windows, an optional drag, and the expected result. Parse reads the text format; Run executes the core algorithm; Check compares with the expectations. No Win32 types.</summary>
public sealed class Fixture
{
    /// <summary>Glue tolerance in pixels.</summary>
    public int Tolerance { get; set; } = 12;
    /// <summary>Work area; defaults to a huge rectangle.</summary>
    public Rect WorkArea { get; set; } = new(-100000, -100000, 100000, 100000);
    /// <summary>Windows in file order.</summary>
    public IList<WinInfo> Windows { get; } = [];
    /// <summary>Id of the dragged window.</summary>
    public ulong DragId { get; set; }
    /// <summary>Dragged edge.</summary>
    public Edge DragEdge { get; set; }
    /// <summary>Signed pixel distance the edge is dragged.</summary>
    public int Delta { get; set; }
    /// <summary>True when the file has a drag line.</summary>
    public bool HasDrag { get; set; }
    /// <summary>Expected final frames.</summary>
    public IList<(ulong Id, Rect Frame)> Expects { get; } = [];
    /// <summary>Expected clamped position, if given.</summary>
    public int? ExpectClamp { get; set; }

    /// <summary>Window names by id. Ids are assigned in order of first appearance, so they are the same on every run.</summary>
    public IDictionary<ulong, string> Names { get; } = new Dictionary<ulong, string>();
    private readonly Dictionary<string, ulong> _ids = new(StringComparer.Ordinal);

    /// <summary>Returns the id for a window name, assigning the next id the first time the name is seen.</summary>
    ulong Id(string name)
    {
        if (_ids.TryGetValue(name, out var id)) return id;
        id = (ulong)_ids.Count + 1;
        _ids.Add(name, id);
        Names.Add(id, name);
        return id;
    }

    /// <summary>Returns the window name for an id, or the id itself when unknown.</summary>
    public string NameOf(ulong id) => Names.TryGetValue(id, out var n) ? n : id.ToString(CultureInfo.InvariantCulture);
    static (int, int) Pair(string s, char sep)
    {
        var p = s.Split(sep);
        return (int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture));
    }
    static Rect Frame(string pos, string size)
    {
        var (x, y) = Pair(pos, ',');
        var (w, h) = Pair(size, 'x');
        return new Rect(x, y, x + w, y + h);
    }

    /// <summary>Parses the line-based fixture format (tolerance, workarea, win, drag, expect). Comments start with #.</summary>
    public static Fixture Parse(string text)
    {
        var f = new Fixture();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (t[0])
            {
                case "tolerance": f.Tolerance = int.Parse(t[1], CultureInfo.InvariantCulture); break;
                case "workarea": f.WorkArea = Frame(t[1], t[2]); break;
                case "win":
                    int mw = 1, mh = 1;
                    if (t.Length >= 6 && t[4] == "min") (mw, mh) = Pair(t[5], 'x');
                    f.Windows.Add(new WinInfo(f.Id(t[1]), Frame(t[2], t[3]), mw, mh));
                    break;
                case "drag":
                    f.DragId = f.Id(t[1]);
                    f.DragEdge = Enum.Parse<Edge>(t[2], true);
                    f.Delta = int.Parse(t[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    f.HasDrag = true;
                    break;
                case "expect" when t[1] == "clamp": f.ExpectClamp = int.Parse(t[2], CultureInfo.InvariantCulture); break;
                case "expect": f.Expects.Add((f.Id(t[1]), Frame(t[2], t[3]))); break;
            }
        }
        return f;
    }

    /// <summary>Runs the domain and returns final frames by id plus the clamped position (null if no seam).</summary>
    public (Dictionary<ulong, Rect> Final, int? Clamp) Run()
    {
        var final = Windows.ToDictionary(w => w.Id, w => w.Frame);
        var seam = SeamBuilder.Build((IReadOnlyList<WinInfo>)Windows, DragId, DragEdge, WorkArea, new Config(Tolerance));
        int x = final[DragId].Coord(DragEdge) + Delta;
        if (seam is null) { final[DragId] = final[DragId].With(DragEdge, x); return (final, null); }
        var plan = SeamBuilder.Propagate(seam, seam.X + Delta);
        final[DragId] = final[DragId].With(DragEdge, plan.ClampedPos);
        foreach (var (id, r) in plan.Moves) final[id] = r;
        return (final, plan.ClampedPos);
    }

    /// <summary>Runs the fixture and returns null on success or a failure message.</summary>
    public string? Check()
    {
        var (final, clamp) = Run();
        var named = new HashSet<ulong>();
        foreach (var (id, exp) in Expects)
        {
            named.Add(id);
            if (!final.TryGetValue(id, out var got)) return $"unknown window {NameOf(id)}";
            if (got != exp) return $"{NameOf(id)} expected {Fmt(exp)} got {Fmt(got)}";
        }
        foreach (var w in Windows)
            if (!named.Contains(w.Id) && final[w.Id] != w.Frame) return $"{NameOf(w.Id)} should be unchanged but is {Fmt(final[w.Id])}";
        if (ExpectClamp is { } c && clamp != c) return $"clamp expected {c} got {clamp}";
        return null;
    }

    /// <summary>Formats a rectangle as x,y WxH, the same way the expectations are written.</summary>
    public static string Fmt(Rect r) => $"{r.L},{r.T} {r.Width}x{r.Height}";
}
