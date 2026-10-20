using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using CodeRim.Core.Domain;

namespace CodeRim.Windows.Views;

/// Same canonical path and edge transforms as Sources/CodeRim/Notch/SideNotchShape.swift.
internal sealed class NotchShape : Shape
{
    public NotchEdge Edge { get; init; }
    public double DesignScale { get; init; } = 1;
    public static readonly DependencyProperty ExpansionProperty = DependencyProperty.Register(nameof(Expansion), typeof(double), typeof(NotchShape),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender, (o, _) => ((NotchShape)o).FrameChanged?.Invoke()));
    internal event Action? FrameChanged;
    public double Expansion { get => (double)GetValue(ExpansionProperty); set => SetValue(ExpansionProperty, value); }
    protected override Geometry DefiningGeometry => GeometryFor(new Size(ActualWidth, ActualHeight));
    internal Geometry GeometryFor(Size size)
    {
            var vertical = Edge is NotchEdge.Left or NotchEdge.Right;
            var fullDepth = vertical ? size.Width : size.Height; var fullLength = vertical ? size.Height : size.Width;
            var progress = Math.Clamp(Expansion, 0, 1);
            var depth = Math.Min(NotchMetrics.PillDepth * DesignScale, fullDepth) * (1 - progress) + fullDepth * progress;
            var length = Math.Min(NotchMetrics.PillLength * DesignScale, fullLength) * (1 - progress) + fullLength * progress;
            if (depth <= 0 || length <= 0) return Geometry.Empty;
            var wanted = Math.Min(NotchMetrics.Corner * DesignScale, depth / 2); var curl = Math.Max(0, Math.Min(NotchMetrics.Curl * DesignScale, Math.Min(length / 2, depth - wanted)));
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
            var edgeTransform = new MatrixTransform(Edge switch
            {
                NotchEdge.Left => new Matrix(-1, 0, 0, 1, depth, 0),
                NotchEdge.Top => new Matrix(0, -1, 1, 0, 0, depth),
                NotchEdge.Bottom => new Matrix(0, 1, 1, 0, 0, 0),
                _ => Matrix.Identity
            });
            var along = (fullLength - length) / 2;
            var translation = Edge switch {
                NotchEdge.Left => new TranslateTransform(0, along),
                NotchEdge.Right => new TranslateTransform(fullDepth - depth, along),
                NotchEdge.Top => new TranslateTransform(along, 0),
                _ => new TranslateTransform(along, fullDepth - depth)
            };
            var transforms = new TransformGroup(); transforms.Children.Add(edgeTransform); transforms.Children.Add(translation);
            path.Transform = transforms;
            path.Freeze(); return path;
    }
}
