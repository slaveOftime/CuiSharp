namespace CuiSharp;

public partial struct ChartAnnotation
{
    /// <summary>Creates an annotation using the chart's default color.</summary>
    public ChartAnnotation(AnnotationKind Kind, double X, double Y, string? Label, string? Series, bool Accent)
        : this(Kind, X, Y, Label, Series, Accent, Color: default) { }
}

public partial struct TraceResult
{
    /// <summary>Creates a trace result with no hovered link.</summary>
    public TraceResult(bool SelectionChanged, bool ViewChanged, int Hovered)
        : this(SelectionChanged, ViewChanged, Hovered, HoveredLink: -1) { }
}
