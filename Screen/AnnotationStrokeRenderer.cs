using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Ink_Canvas.Plugins;

namespace ClassAgent.Screen
{
    internal sealed class AnnotationPlanValidator
    {
        private const int MaxMarks = 64;
        private const int MaxTextLength = 240;

        public AnnotationPlanValidationResult Validate(AnnotationPlan plan, Rect screenBounds)
        {
            var result = new AnnotationPlanValidationResult();
            if (plan == null)
            {
                result.Error = "批注计划为空";
                return result;
            }
            if (!string.Equals(plan.CoordinateSpace, "screen-pixel", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(plan.CoordinateSpace, "canvas-dip", StringComparison.OrdinalIgnoreCase))
            {
                result.Error = "不支持的坐标空间";
                return result;
            }
            if (plan.Marks == null || plan.Marks.Count == 0)
            {
                result.Error = "批注计划没有标记";
                return result;
            }
            if (plan.Marks.Count > MaxMarks)
            {
                result.Error = "批注数量超过限制";
                return result;
            }

            foreach (var mark in plan.Marks)
            {
                if (mark == null)
                {
                    result.Error = "批注包含空标记";
                    return result;
                }
                if (mark.Text?.Length > MaxTextLength)
                {
                    result.Error = "批注文字过长";
                    return result;
                }
                if (mark.StrokeWidth < 0.5 || mark.StrokeWidth > 32
                    || double.IsNaN(mark.StrokeWidth) || double.IsInfinity(mark.StrokeWidth))
                {
                    result.Error = "批注线宽超出范围";
                    return result;
                }
                if (!IsFinite(mark.Rect) || !IsFinite(mark.Start) || !IsFinite(mark.End))
                {
                    result.Error = "批注坐标无效";
                    return result;
                }
                if (string.Equals(plan.CoordinateSpace, "screen-pixel", StringComparison.OrdinalIgnoreCase)
                    && mark.Kind != AnnotationMarkKind.Text
                    && !screenBounds.IsEmpty
                    && !screenBounds.IntersectsWith(mark.Rect)
                    && mark.Kind != AnnotationMarkKind.Arrow
                    && mark.Kind != AnnotationMarkKind.Underline)
                {
                    result.Error = "批注区域不在屏幕范围内";
                    return result;
                }
            }

            result.IsValid = true;
            result.MarkCount = plan.Marks.Count;
            return result;
        }

        private static bool IsFinite(Point point)
            => !double.IsNaN(point.X) && !double.IsNaN(point.Y)
                && !double.IsInfinity(point.X) && !double.IsInfinity(point.Y);

        private static bool IsFinite(Rect rect)
            => !double.IsNaN(rect.X) && !double.IsNaN(rect.Y)
                && !double.IsNaN(rect.Width) && !double.IsNaN(rect.Height)
                && !double.IsInfinity(rect.X) && !double.IsInfinity(rect.Y)
                && !double.IsInfinity(rect.Width) && !double.IsInfinity(rect.Height)
                && rect.Width >= 0 && rect.Height >= 0;
    }

    internal sealed class AnnotationStrokeRenderer
    {
        private readonly ICanvasCoordinateService _coordinates;
        private readonly AnnotationPlanValidator _validator = new AnnotationPlanValidator();

        public AnnotationStrokeRenderer(ICanvasCoordinateService coordinates)
        {
            _coordinates = coordinates;
        }

        public bool TryRender(AnnotationPlan plan, Rect screenBounds,
            out StrokeCollection strokes, out string error)
        {
            strokes = new StrokeCollection();
            error = "";
            var validation = _validator.Validate(plan, screenBounds);
            if (!validation.IsValid)
            {
                error = validation.Error;
                return false;
            }

            foreach (var mark in plan.Marks)
            {
                var color = ParseColor(mark.Color);
                var attributes = new DrawingAttributes
                {
                    Color = color,
                    Width = Math.Max(0.5, mark.StrokeWidth),
                    Height = Math.Max(0.5, mark.StrokeWidth),
                    FitToCurve = true,
                    IsHighlighter = mark.Kind == AnnotationMarkKind.Highlight
                };

                var points = BuildPoints(plan.CoordinateSpace, mark);
                if (points == null || points.Count < 2) continue;
                strokes.Add(new Stroke(points, attributes));
            }

            if (strokes.Count == 0)
            {
                error = "没有生成有效墨迹";
                return false;
            }
            return true;
        }

        private StylusPointCollection BuildPoints(string coordinateSpace, AnnotationMark mark)
        {
            var isCanvas = string.Equals(coordinateSpace, "canvas-dip",
                StringComparison.OrdinalIgnoreCase);
            var result = new StylusPointCollection();

            Point Map(Point point)
            {
                if (isCanvas) return point;
                return _coordinates != null && _coordinates.TryScreenToCanvas(point, out var canvas)
                    ? canvas : new Point(double.NaN, double.NaN);
            }

            void Add(Point point)
            {
                var mapped = Map(point);
                if (!double.IsNaN(mapped.X) && !double.IsNaN(mapped.Y))
                    result.Add(new StylusPoint(mapped.X, mapped.Y));
            }

            var rect = mark.Rect;
            switch (mark.Kind)
            {
                case AnnotationMarkKind.Rectangle:
                case AnnotationMarkKind.Highlight:
                    if (mark.Kind == AnnotationMarkKind.Highlight)
                    {
                        Add(new Point(rect.Left, rect.Top + rect.Height / 2));
                        Add(new Point(rect.Right, rect.Top + rect.Height / 2));
                    }
                    else
                    {
                        Add(new Point(rect.Left, rect.Top));
                        Add(new Point(rect.Right, rect.Top));
                        Add(new Point(rect.Right, rect.Bottom));
                        Add(new Point(rect.Left, rect.Bottom));
                        Add(new Point(rect.Left, rect.Top));
                    }
                    break;
                case AnnotationMarkKind.Ellipse:
                    for (var i = 0; i <= 48; i++)
                    {
                        var angle = Math.PI * 2 * i / 48;
                        Add(new Point(rect.Left + rect.Width / 2 + Math.Cos(angle) * rect.Width / 2,
                            rect.Top + rect.Height / 2 + Math.Sin(angle) * rect.Height / 2));
                    }
                    break;
                case AnnotationMarkKind.Arrow:
                    Add(mark.Start);
                    Add(mark.End);
                    AddArrowHead(mark.Start, mark.End, Add);
                    break;
                case AnnotationMarkKind.Underline:
                    Add(new Point(rect.Left, rect.Bottom));
                    Add(new Point(rect.Right, rect.Bottom));
                    break;
                case AnnotationMarkKind.Text:
                    // 文字由 IInkTextService 处理；此处不生成伪文字线条。
                    break;
            }

            return result;
        }

        private static void AddArrowHead(Point start, Point end, Action<Point> add)
        {
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 0.01) return;
            var ux = dx / length;
            var uy = dy / length;
            var size = Math.Min(32, Math.Max(10, length * 0.16));
            var left = new Point(end.X - ux * size - uy * size * 0.55,
                end.Y - uy * size + ux * size * 0.55);
            var right = new Point(end.X - ux * size + uy * size * 0.55,
                end.Y - uy * size - ux * size * 0.55);
            add(end);
            add(left);
            add(end);
            add(right);
        }

        private static Color ParseColor(string value)
        {
            try
            {
                var parsed = (Color)ColorConverter.ConvertFromString(value ?? "#FFFF3B30");
                return parsed.A == 0 && parsed != Colors.Transparent
                    ? Color.FromArgb(255, parsed.R, parsed.G, parsed.B) : parsed;
            }
            catch
            {
                return Color.FromArgb(255, 255, 59, 48);
            }
        }
    }
}
