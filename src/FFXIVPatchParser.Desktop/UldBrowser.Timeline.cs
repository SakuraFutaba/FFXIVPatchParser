using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lumina.Data.Parsing.Uld;
using static Lumina.Data.Parsing.Uld.UldRoot;
using LayoutNodeData = Lumina.Data.Parsing.Uld.UldRoot.NodeData;

namespace FFXIVPatchParser.Desktop;

public partial class UldBrowser
{
    private sealed class LayoutNode(LayoutNodeData node)
    {
        private readonly ScaleTransform scale = new(1, 1, node.OriginX, node.OriginY);
        private readonly RotateTransform rotation = new(0, node.OriginX, node.OriginY);
        private Canvas? canvas;
        private Border? marker;

        public LayoutNodeData Node { get; } = node;
        public Canvas Canvas => canvas ??= CreateCanvas();
        public bool HasImage { get; set; }

        private Canvas CreateCanvas()
        {
            var element = new Canvas
            {
                Width = Node.W,
                Height = Node.H,
                RenderTransform = new TransformGroup { Children = { scale, rotation } }
            };
            ApplyTo(element, default);
            return element;
        }

        public void Apply(NodePose pose) => ApplyTo(Canvas, pose);

        private void ApplyTo(Canvas element, NodePose pose)
        {
            var x = pose.Position?.X ?? Node.X;
            var y = pose.Position?.Y ?? Node.Y;
            System.Windows.Controls.Canvas.SetLeft(element, x);
            System.Windows.Controls.Canvas.SetTop(element, y);
            scale.ScaleX = pose.Scale?.X ?? FiniteOr(Node.ScaleX, 1);
            scale.ScaleY = pose.Scale?.Y ?? FiniteOr(Node.ScaleY, 1);
            // 节点初始旋转以度存储，时间线的 Rotation 关键帧以弧度存储。
            rotation.Angle = pose.Rotation is { } radians ? radians * 180 / Math.PI : FiniteOr(Node.Rotation, 0);
            element.Opacity = pose.Alpha ?? Node.Alpha / 255.0;
        }

        private static double FiniteOr(float value, double fallback) => float.IsFinite(value) ? value : fallback;

        public void ShowMarker(bool show)
        {
            if (!show || Node.W == 0 || Node.H == 0)
            {
                if (marker != null) marker.Visibility = Visibility.Collapsed;
                return;
            }
            if (marker == null)
            {
                marker = new Border
                {
                    Width = Node.W, Height = Node.H,
                    BorderBrush = Brushes.LightSlateGray, BorderThickness = new Thickness(1),
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = $"#{Node.NodeId}", Foreground = Brushes.White, FontSize = 10,
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                        Background = Brushes.DarkSlateGray, TextTrimming = TextTrimming.CharacterEllipsis
                    }
                };
                Panel.SetZIndex(marker, 1);
                Canvas.Children.Add(marker);
            }
            marker.Visibility = Visibility.Visible;
        }
    }

    private readonly record struct NodePose(Vector? Position, double? Rotation, Vector? Scale, double? Alpha);

    private sealed class UldTimeline
    {
        private readonly record struct Key(Keyframes.BaseKeyframeData Data, Vector Value)
        {
            public uint Frame => Data.Time;
        }
        private sealed record Track(KeyUsage Usage, Key[] Keys);
        private sealed record Segment(uint Start, uint End, Track[] Tracks);
        private readonly Segment[] segments;
        public uint End { get; }

        public UldTimeline(Timeline timeline)
        {
            segments = (timeline.FrameData ?? [])
                .Where(segment => segment.StartFrame <= segment.EndFrame)
                .Select(segment => new Segment(segment.StartFrame, segment.EndFrame,
                    segment.KeyGroups.Select(group => new Track(group.Usage,
                        (group.Frames ?? []).Select(key => ReadKey(group.Usage, key)).OfType<Key>()
                            .OrderBy(key => key.Frame).ToArray()))
                        .Where(track => track.Keys.Length > 0).ToArray()))
                .Where(segment => segment.Tracks.Length > 0).ToArray();
            End = segments.Select(segment => segment.End).DefaultIfEmpty().Max();
        }

        private static Key? ReadKey(KeyUsage usage, IKeyframe frame)
        {
            return (usage, frame) switch
            {
                (KeyUsage.Position or KeyUsage.Scale, Keyframes.Float2Keyframe { Value: [var x, var y] } value)
                    when float.IsFinite(x) && float.IsFinite(y) =>
                    new Key(value.Keyframe, new(x, y)),
                (KeyUsage.Rotation, Keyframes.Float1Keyframe value) when float.IsFinite(value.Value) =>
                    new Key(value.Keyframe, new(value.Value, 0)),
                (KeyUsage.Alpha, Keyframes.Byte1Keyframe value) =>
                    new Key(value.Keyframe, new(value.Value, 0)),
                // Lumina 当前会把 ULD 的 Byte1 轨道读为 SByte1；取回原始的 0–255 值。
                (KeyUsage.Alpha, Keyframes.SByte1Keyframe value) =>
                    new Key(value.Keyframe, new(unchecked((byte)value.Value), 0)),
                _ => null
            };
        }

        public NodePose Sample(uint frame)
        {
            var pose = default(NodePose);
            foreach (var segment in segments)
            {
                if (frame < segment.Start || frame > segment.End) continue;
                foreach (var track in segment.Tracks)
                {
                    if (frame < track.Keys[0].Frame) continue;
                    var value = SampleTrack(track.Keys, frame);
                    pose = track.Usage switch
                    {
                        KeyUsage.Position => pose with { Position = value },
                        KeyUsage.Rotation => pose with { Rotation = value.X },
                        KeyUsage.Scale => pose with { Scale = value },
                        KeyUsage.Alpha => pose with { Alpha = Math.Clamp(Math.Truncate(value.X), 0, 255) / 255.0 },
                        _ => pose
                    };
                }
            }
            return pose;
        }

        // 以两键间隔归一化为 1，积分各段速度，得到已经走过的位移比例。
        private static double IntegratedSpeed(double t, double a, double b)
        {
            var reversed = a > b;
            var first = Math.Min(a, b);
            var second = Math.Max(a, b);
            var edgeSpeed = reversed ? 2 / (1 + (a - b) * (a - b)) : 0;
            var middleSpeed = reversed ? edgeSpeed * (a - b) : 2 / (1 + b - a);
            return Integral(0, first, edgeSpeed, middleSpeed)
                + Integral(first, second, middleSpeed, middleSpeed)
                + Integral(second, 1, middleSpeed, edgeSpeed);

            double Integral(double start, double end, double from, double to)
            {
                if (end <= start || t <= start) return 0;
                var elapsed = Math.Min(t, end) - start;
                return elapsed * (from + (to - from) * elapsed / (2 * (end - start)));
            }
        }

        private static Vector SampleTrack(Key[] keys, uint frame)
        {
            for (var i = 1; i < keys.Length; i++)
            {
                if (frame >= keys[i].Frame) continue;
                var left = keys[i - 1];
                if (frame <= left.Frame || left.Data.Interpolation == 0) return left.Value;
                var right = keys[i];
                var t = (frame - left.Frame) / (double)(right.Frame - left.Frame);
                if (left.Data.Interpolation == 2 && left.Data.Acceleration is >= 0 and <= 1 &&
                    left.Data.Deceleration is >= 0 and <= 1)
                    t = IntegratedSpeed(t, left.Data.Acceleration, left.Data.Deceleration);
                return left.Value + (right.Value - left.Value) * t;
            }
            return keys[^1].Value;
        }
    }
}
