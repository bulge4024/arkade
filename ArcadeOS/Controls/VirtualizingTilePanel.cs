using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ArcadeOS.Controls;

// Pixel-based scrolling and a small overscan keep large libraries from creating
// thousands of WPF images and controls at once.
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int),
        typeof(VirtualizingTilePanel), new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(nameof(ItemHeight), typeof(double),
        typeof(VirtualizingTilePanel), new FrameworkPropertyMetadata(210d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public int Columns { get => Math.Max(1, (int)GetValue(ColumnsProperty)); set => SetValue(ColumnsProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }
    private double RowHeight => Math.Max(32, ItemHeight + 16);
    public double RowStride => RowHeight;
    public int FirstVisibleIndex => Math.Max(0, (int)(offset / RowHeight) * Columns);
    private Size extent;
    private Size viewport;
    private double offset;

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return default;
        double width = double.IsInfinity(availableSize.Width) ? Math.Max(1, ScrollOwner?.ActualWidth ?? 700) : Math.Max(1, availableSize.Width);
        double height = double.IsInfinity(availableSize.Height) ? Math.Max(1, ScrollOwner?.ActualHeight ?? 500) : Math.Max(1, availableSize.Height);
        viewport = new(width, height);
        extent = new(width, Math.Ceiling(owner.Items.Count / (double)Columns) * RowHeight);
        offset = Math.Clamp(offset, 0, Math.Max(0, extent.Height - viewport.Height));
        ScrollOwner?.InvalidateScrollInfo();
        int first = Math.Max(0, ((int)(offset / RowHeight) - 1) * Columns);
        int last = Math.Min(owner.Items.Count - 1, ((int)Math.Ceiling((offset + height) / RowHeight) + 1) * Columns - 1);
        // Accessing InternalChildren initializes the panel's item generator on its first measure.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        for (int child = InternalChildren.Count - 1; child >= 0; child--)
        {
            var position = new GeneratorPosition(child, 0);
            int index = generator.IndexFromGeneratorPosition(position);
            if (index < first || index > last)
            { generator.Remove(position, 1); RemoveInternalChildRange(child, 1); }
        }
        if (last >= first)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
            {
                for (int index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out bool created);
                    if (created)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(width / Columns, RowHeight));
                }
            }
        }
        return viewport;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int child = 0; child < InternalChildren.Count; child++)
        {
            int index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(child, 0));
            if (index < 0) continue;
            InternalChildren[child].Arrange(new Rect(index % Columns * finalSize.Width / Columns,
                index / Columns * RowHeight - offset, finalSize.Width / Columns, RowHeight));
        }
        return finalSize;
    }
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Reset)
        { if (InternalChildren.Count > 0) RemoveInternalChildRange(0, InternalChildren.Count); }
        else if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Move)
        {
            int index = args.Action == NotifyCollectionChangedAction.Move ? args.OldPosition.Index : args.Position.Index;
            if (index >= 0 && args.ItemUICount > 0 && index < InternalChildren.Count)
                RemoveInternalChildRange(index, Math.Min(args.ItemUICount, InternalChildren.Count - index));
        }
        InvalidateMeasure();
    }
    protected override void BringIndexIntoView(int index)
    {
        double top = index / Columns * RowHeight;
        if (top < offset) SetVerticalOffset(top);
        else if (top + RowHeight > offset + viewport.Height) SetVerticalOffset(top + RowHeight - viewport.Height);
    }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual == this || !IsAncestorOf(visual)) return rectangle;
        var bounds = visual.TransformToAncestor(this).TransformBounds(rectangle);
        if (bounds.Top < 0) SetVerticalOffset(offset + bounds.Top);
        else if (bounds.Bottom > viewport.Height) SetVerticalOffset(offset + bounds.Bottom - viewport.Height);
        return bounds;
    }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentHeight => extent.Height;
    public double ExtentWidth => extent.Width;
    public double HorizontalOffset => 0;
    public double VerticalOffset => offset;
    public double ViewportHeight => viewport.Height;
    public double ViewportWidth => viewport.Width;
    public ScrollViewer? ScrollOwner { get; set; }
    public void SetVerticalOffset(double value)
    {
        double next = Math.Clamp(double.IsNaN(value) ? 0 : value, 0, Math.Max(0, extent.Height - viewport.Height));
        if (Math.Abs(offset - next) < 0.1) return;
        offset = next; ScrollOwner?.InvalidateScrollInfo(); InvalidateMeasure();
    }
    public void SetHorizontalOffset(double value) { }
    public void LineUp() => SetVerticalOffset(offset - 40);
    public void LineDown() => SetVerticalOffset(offset + 40);
    public void PageUp() => SetVerticalOffset(offset - viewport.Height);
    public void PageDown() => SetVerticalOffset(offset + viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(offset - 120);
    public void MouseWheelDown() => SetVerticalOffset(offset + 120);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
}
