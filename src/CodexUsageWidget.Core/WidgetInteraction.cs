namespace CodexUsageWidget.Core;

public readonly record struct WidgetRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2d;
    public double CenterY => Y + Height / 2d;
}

public readonly record struct WidgetPlacementResult(WidgetRect Rect, bool AnchorRight, bool AnchorBottom);

public static class WidgetPlacement
{
    public static WidgetPlacementResult CalculateExpandedTarget(
        WidgetRect compact,
        double expandedWidth,
        double expandedHeight,
        WidgetRect workArea)
    {
        expandedWidth = Math.Min(Math.Max(1, expandedWidth), workArea.Width);
        expandedHeight = Math.Min(Math.Max(1, expandedHeight), workArea.Height);
        var anchorRight = compact.CenterX >= workArea.CenterX;
        var anchorBottom = compact.CenterY >= workArea.CenterY;
        var x = anchorRight ? compact.Right - expandedWidth : compact.X;
        var y = anchorBottom ? compact.Bottom - expandedHeight : compact.Y;
        return new WidgetPlacementResult(
            Clamp(new WidgetRect(x, y, expandedWidth, expandedHeight), workArea),
            anchorRight,
            anchorBottom);
    }

    public static WidgetRect CompactFromExpanded(
        WidgetRect expanded,
        double compactWidth,
        double compactHeight,
        bool anchorRight,
        bool anchorBottom,
        WidgetRect workArea)
    {
        var x = anchorRight ? expanded.Right - compactWidth : expanded.X;
        var y = anchorBottom ? expanded.Bottom - compactHeight : expanded.Y;
        return Clamp(new WidgetRect(x, y, compactWidth, compactHeight), workArea);
    }

    public static WidgetRect Clamp(WidgetRect rect, WidgetRect workArea)
    {
        var width = Math.Min(Math.Max(1, rect.Width), workArea.Width);
        var height = Math.Min(Math.Max(1, rect.Height), workArea.Height);
        var maxX = workArea.Right - width;
        var maxY = workArea.Bottom - height;
        return new WidgetRect(
            Math.Clamp(rect.X, workArea.X, maxX),
            Math.Clamp(rect.Y, workArea.Y, maxY),
            width,
            height);
    }
}

public static class QuotaSelection
{
    public static QuotaWindow? SelectCompact(IReadOnlyList<QuotaWindow> windows) =>
        windows.FirstOrDefault(window => string.Equals(window.Id, "primary", StringComparison.OrdinalIgnoreCase))
        ?? windows.FirstOrDefault();
}

public sealed class HoverIntentTracker
{
    public bool CollapsePending { get; private set; }
    public bool IsDragging { get; private set; }

    public void PointerEntered() => CollapsePending = false;
    public void PointerLeft() => CollapsePending = true;
    public void BeginDrag()
    {
        IsDragging = true;
        CollapsePending = false;
    }

    public void EndDrag() => IsDragging = false;

    public bool ConsumeCollapse(bool isPointerOver)
    {
        var shouldCollapse = CollapsePending && !isPointerOver && !IsDragging;
        CollapsePending = false;
        return shouldCollapse;
    }

    public void Reset()
    {
        CollapsePending = false;
        IsDragging = false;
    }
}
