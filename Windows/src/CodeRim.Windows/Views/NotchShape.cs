using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using CodeRim.Core.Domain;

namespace CodeRim.Windows.Views;

/// Same canonical path and edge transforms as Sources/CodeRim/Notch/SideNotchShape.swift.
internal sealed class NotchShape : Shape
{
    public NotchEdge Edge { get; init; }
    protected override Geometry DefiningGeometry
    {
        get
        {
            var vertical = Edge is NotchEdge.Left or NotchEdge.Right;
            var depth = vertical ? ActualWidth : ActualHeight; var length = vertical ? ActualHeight : ActualWidth;
            if (depth <= 0 || length <= 0) return Geometry.Empty;
            var wanted = Math.Min(NotchMetrics.Corner, depth / 2); var curl = Math.Max(0, Math.Min(NotchMetrics.Curl, Math.Min(length / 2, depth - wanted)));
            var corner = Math.Max(0, Math.Min(wanted, (length - 2 * curl) / 2));
            var path = new StreamGeometry();
            using (var context = path.Open())
            {
                context.BeginFigure(new Point(depth, 0), true, true);
                context.ArcTo(new Point(depth - curl, curl), new Size(curl, curl), 0, false, SweepDirection.Clockwise, true, false);
                context.LineTo(new Point(corner, curl), true, false);
                context.ArcTo(new Point(0, curl + corner), new Size(corner, corner), 0, false, SweepDirection.Counterclockwise, true, false);
                context.LineTo(new Point(0, length - curl - corner), true, false);
                context.ArcTo(new Point(corner, length - curl), new Size(corner, corner), 0, false, SweepDirection.Counterclockwise, true, false);
                context.LineTo(new Point(depth - curl, length - curl), true, false);
                context.ArcTo(new Point(depth, length), new Size(curl, curl), 0, false, SweepDirection.Clockwise, true, false);
            }
            path.Transform = new MatrixTransform(Edge switch
            {
                NotchEdge.Left => new Matrix(-1, 0, 0, 1, depth, 0),
                NotchEdge.Top => new Matrix(0, -1, 1, 0, 0, depth),
                NotchEdge.Bottom => new Matrix(0, 1, 1, 0, 0, 0),
                _ => Matrix.Identity
            });
            path.Freeze(); return path;
        }
    }
}
