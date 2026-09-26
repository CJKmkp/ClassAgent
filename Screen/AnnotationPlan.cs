using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace ClassAgent.Screen
{
    public enum AnnotationMarkKind
    {
        Rectangle,
        Ellipse,
        Arrow,
        Underline,
        Highlight,
        Text
    }

    public sealed class AnnotationPlan
    {
        public string CoordinateSpace { get; set; } = "screen-pixel";
        public List<AnnotationMark> Marks { get; set; } = new List<AnnotationMark>();
    }

    public sealed class AnnotationMark
    {
        public AnnotationMarkKind Kind { get; set; }
        public Rect Rect { get; set; }
        public Point Start { get; set; }
        public Point End { get; set; }
        public string Text { get; set; } = "";
        public string Color { get; set; } = "#FFFF3B30";
        public double StrokeWidth { get; set; } = 4;
    }

    public sealed class AnnotationPlanValidationResult
    {
        public bool IsValid { get; set; }
        public string Error { get; set; } = "";
        public int MarkCount { get; set; }
    }
}
