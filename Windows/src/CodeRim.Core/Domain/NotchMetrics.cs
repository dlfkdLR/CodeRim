namespace CodeRim.Core.Domain;

/// <summary>Same design-frame measurements as macOS NotchDesign and NotchLayout, in DIP.</summary>
public static class NotchMetrics
{
    public const double Unit = 44d / 117d;
    public const double Ring = 44;
    public const double Track = 15.5 * Unit;
    public const double Progress = 8 * Unit;
    public const double Glyph = 46 * Unit;
    public const double SideDepth = 186 * Unit;
    public const double Curl = 103 * Unit;
    public const double Corner = 78.8 * Unit;
    public const double CellHeight = Ring + 26.9 * Unit + 18;
    public const double CellGap = 83.5 * Unit;
    public const double PadStart = 69.5 * Unit;
    public const double PadEnd = 50.1 * Unit;
    public const double CardWidth = 600 * Unit;
    public const double CardCorner = 49.5 * Unit;
    public const double CardPadding = 32 * Unit;
    public const double Tail = 75 * Unit;
    public const double TailGap = 28 * Unit;
    public const double Control = 96 * Unit;
    public const double ControlExtent = 88;
    public const double PillDepth = 26 * Unit;
    public const double PillLength = 210 * Unit;

    public static (double Depth, double Length, int Capacity) Fit(NotchEdge edge, int count, double scale, double availableAlong)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var vertical = edge is NotchEdge.Left or NotchEdge.Right;
        var cell = vertical ? CellHeight : Ring;
        var baseLength = 2 * Curl + PadStart + PadEnd;
        var budget = Math.Max(cell, availableAlong / scale - ControlExtent - baseLength);
        var capacity = Math.Max(1, (int)Math.Floor((budget + CellGap) / (cell + CellGap)));
        var visible = Math.Min(Math.Max(1, count), capacity);
        return ((vertical ? SideDepth : SideDepth - Ring + CellHeight) * scale,
            (baseLength + visible * cell + Math.Max(0, visible - 1) * CellGap) * scale, capacity);
    }
}
