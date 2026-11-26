namespace MagneticBorders.Core;

/// <summary>Which edge of a seam member lies on the seam line: Same (member edge is the same kind as the dragged edge) or Opposite (member edge faces the dragged edge).</summary>
public enum Side { Same, Opposite }

/// <summary>A window glued to the dragged edge.</summary>
/// <param name="Id">Window id.</param>
/// <param name="Frame">Frame when the seam was built.</param>
/// <param name="Side">Which of its edges lies on the seam.</param>
/// <param name="D">Signed offset in pixels of that edge from the seam line at build time; preserved while the seam moves.</param>
/// <param name="MinW">Minimum width.</param>
/// <param name="MinH">Minimum height.</param>
public sealed record SeamMember(ulong Id, Rect Frame, Side Side, int D, int MinW, int MinH);

public sealed record Seam(Edge Edge, int X, IReadOnlyList<SeamMember> Members)
{
    /// <summary>True when the seam is a vertical line (Left or Right edge).</summary>
    public bool Vertical => Edge.IsVertical();
}

/// <summary>Result of SeamBuilder.Propagate.</summary>
/// <param name="Moves">New frame for every seam member.</param>
/// <param name="ClampedPos">Seam position after clamping to every member's minimum size.</param>
public sealed record Plan(IReadOnlyList<(ulong Id, Rect Frame)> Moves, int ClampedPos);
