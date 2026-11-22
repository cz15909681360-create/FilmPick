using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FilmPick;

public sealed class ThumbnailGrid : VirtualizingPanel, IScrollInfo
{
    private const double Gap = 6;
    private const double RowGap = 3;
    private const double MinCardWidth = 160;
    private const double CaptionHeight = 39;
    public static readonly DependencyProperty ImageHeightRatioProperty = DependencyProperty.Register(
        nameof(ImageHeightRatio), typeof(double), typeof(ThumbnailGrid),
        new FrameworkPropertyMetadata(2d / 3, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ImageHeightRatio
    {
        get => (double)GetValue(ImageHeightRatioProperty);
        set => SetValue(ImageHeightRatioProperty, value);
    }
    private int columns = 1;
    private double cardWidth;
    private double rowHeight = 220;
    private Size viewport;
    private double extentHeight, offset;
    private int realizedStart = -1, realizedEnd = -1;

    public int FirstVisible => (int)(offset / rowHeight) * columns;
    public int VisibleEnd => (int)Math.Ceiling((offset + viewport.Height) / rowHeight) * columns;
    public int BufferedEnd => (int)Math.Ceiling((offset + 2 * viewport.Height) / rowHeight) * columns;

    protected override Size MeasureOverride(Size available)
    {
        if (ItemsControl.GetItemsOwner(this) is not ItemsControl owner) return new();
        var count = owner.Items.Count;
        var width = double.IsFinite(available.Width) ? available.Width : Math.Max(MinCardWidth, owner.ActualWidth);
        var height = double.IsFinite(available.Height) ? available.Height : owner.ActualHeight;
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        var anchor = FirstVisible;
        var nextColumns = Math.Max(1, (int)((width + Gap) / (MinCardWidth + Gap)));
        var nextWidth = Math.Max(0, (width - Gap * (nextColumns - 1)) / nextColumns);
        var nextHeight = nextWidth * ImageHeightRatio + CaptionHeight + RowGap;
        if (nextColumns != columns || Math.Abs(nextHeight - rowHeight) > .01)
        {
            offset = anchor / nextColumns * nextHeight;
            columns = nextColumns;
            cardWidth = nextWidth;
            rowHeight = nextHeight;
        }
        var total = Math.Ceiling((double)count / columns) * rowHeight;
        var nextViewport = new Size(width, height);
        var nextOffset = Math.Clamp(offset, 0, Math.Max(0, total - height));
        var scrollChanged = nextViewport != viewport || total != extentHeight || nextOffset != offset;
        viewport = nextViewport;
        extentHeight = total;
        offset = nextOffset;
        if (scrollChanged) ScrollOwner?.InvalidateScrollInfo();

        var first = Math.Min(count, FirstVisible);
        var end = Math.Min(count, Math.Max(first, BufferedEnd));
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        if (first != realizedStart || end != realizedEnd || children.Count != end - first)
        {
            // Keep a contiguous viewport window. A new window clears both the generator
            // and its visuals before realization, including after an ItemsSource reset.
            generator.RemoveAll();
            if (children.Count > 0) RemoveInternalChildRange(0, children.Count);
            realizedStart = first;
            realizedEnd = end;
            if (first < end)
                using (generator.StartAt(generator.GeneratorPositionFromIndex(first), GeneratorDirection.Forward, true))
                    for (var index = first; index < end; index++)
                    {
                        var child = (UIElement)generator.GenerateNext();
                        AddInternalChild(child);
                        generator.PrepareItemContainer(child);
                    }
        }
        foreach (UIElement child in children) child.Measure(new Size(cardWidth, rowHeight - RowGap));
        return viewport;
    }

    protected override Size ArrangeOverride(Size size)
    {
        for (var child = 0; child < InternalChildren.Count; child++)
        {
            var index = realizedStart + child;
            if (index >= 0)
                InternalChildren[child].Arrange(new Rect(index % columns * (cardWidth + Gap),
                    index / columns * rowHeight - offset, cardWidth, rowHeight - RowGap));
        }
        return size;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        realizedStart = realizedEnd = -1;
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0) return;
        var top = index / columns * rowHeight;
        if (top < offset) SetVerticalOffset(top);
        else if (top + rowHeight > offset + viewport.Height)
            SetVerticalOffset(viewport.Height < rowHeight ? top : top + rowHeight - viewport.Height);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (ItemsControl.GetItemsOwner(this) is not ItemsControl owner || visual is not DependencyObject element ||
            ItemsControl.ContainerFromElement(owner, element) is not UIElement container) return Rect.Empty;
        var index = owner.ItemContainerGenerator.IndexFromContainer(container);
        if (index < 0) return Rect.Empty;
        BringIndexIntoView(index);
        var result = new Rect(index % columns * (cardWidth + Gap), index / columns * rowHeight - offset,
            cardWidth, rowHeight - RowGap);
        result.Intersect(new Rect(viewport));
        return result;
    }

    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => viewport.Width;
    public double ExtentHeight => extentHeight;
    public double ViewportWidth => viewport.Width;
    public double ViewportHeight => viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => offset;
    public ScrollViewer? ScrollOwner { get; set; }
    public void SetHorizontalOffset(double value) { }

    public void SetVerticalOffset(double value)
    {
        var next = Math.Clamp(value, 0, Math.Max(0, extentHeight - viewport.Height));
        if (next == offset) return;
        offset = next;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public void LineDown() => SetVerticalOffset(offset + 24);
    public void LineUp() => SetVerticalOffset(offset - 24);
    public void MouseWheelDown() => SetVerticalOffset(offset + 72);
    public void MouseWheelUp() => SetVerticalOffset(offset - 72);
    public void PageDown() => SetVerticalOffset(offset + viewport.Height);
    public void PageUp() => SetVerticalOffset(offset - viewport.Height);
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }
}
