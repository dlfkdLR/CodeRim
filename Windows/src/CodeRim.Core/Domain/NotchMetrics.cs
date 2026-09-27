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
    public const double CellHeight = Ring + 26.9 * Unit + 17;
    public const double CellGap = 83.5 * Unit;
    public const double PadStart = 69.5 * Unit;
    public const double PadEnd = 50.1 * Unit;
    public const double CardWidth = 600 * Unit;
    public const double CardCorner = 49.5 * Unit;
    public const double CardPadding = 32 * Unit;
    public const double CardBodyFontSize = 18 * Unit / 0.714;
    public const double CardTitleFontSize = 26 * Unit / 0.714;
    public const double HeaderGap = 17 * Unit;
    public const double HeaderToBlock = 21 * Unit;
    public const double AccountRowHeight = 22;
    public const double BlockSpacing = 20 * Unit;
    public const double LabelToBar = 16.8 * Unit;
    public const double BarToUsed = 17.8 * Unit;
    public const double BarHeight = 10.5 * Unit;
    public const double LocalTokenScopeGap = 8 * Unit;
    public const double StatusDot = 17 * Unit;
    public const double StatusDotStroke = 3.4 * Unit;
    public const double StatusDotGap = 11 * Unit;
    public const double Tail = 75 * Unit;
    public const double TailHeight = 87 * Unit;
    public const double TailGap = 28 * Unit;
    public const double Control = 96 * Unit;
    public const double OrbStroke = 18 * Unit;
    public const double OrbArcRadius = (103 - 27) * Unit;
    public const double OrbHotZone = 152 * Unit;
    public const double OrbGlyph = 56 * Unit;
    public const double ControlSpacing = (OrbHotZone + Control) / 2 + 1;
    public static double ControlExtent => Math.Ceiling(ControlSpacing + Control / 2);

    public static double StartPadding(NotchEdge edge) => edge is NotchEdge.Left or NotchEdge.Right ? PadStart : (PadStart + PadEnd) / 2;
    public static double EndPadding(NotchEdge edge) => edge is NotchEdge.Left or NotchEdge.Right ? PadEnd : (PadStart + PadEnd) / 2;
    public static bool ControlsAtStart(NotchEdge edge, string preference, double bodyLength, double scale, double screenAlong, double offset)
    {
        if (edge is not (NotchEdge.Left or NotchEdge.Right)) return false;
        if (preference == "Start") return true;
        if (preference == "End") return false;
        return offset > 0 && (screenAlong - bodyLength) / 2 - offset < ControlExtent * scale + 8;
    }
    public static double RestingArcStart(NotchEdge edge, bool atStart)
    {
        var start = edge switch { NotchEdge.Right => 0.75, NotchEdge.Bottom => 0.25, _ => 0.5 };
        return atStart && edge is NotchEdge.Left or NotchEdge.Right ? 1 - (start + 0.25) : start;
    }
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
