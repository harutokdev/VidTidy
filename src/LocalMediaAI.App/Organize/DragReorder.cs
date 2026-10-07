using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace LocalMediaAI.App.Organize;

/// <summary>
/// Visible drag feedback for reordering lists: the grabbed item dims in place, a translucent copy follows the pointer,
/// and a blue line shows where it will be inserted (above or below the item under the pointer).
/// </summary>
public static class DragReorder
{
    private const string Format = "LocalMediaAI.DragReorder";

    /// <summary>Runs the drag. Returns the insertion index in the original list (0..count), or null if cancelled.</summary>
    /// <param name="items">The rows of the list, in order. Each must be inside <paramref name="scope"/>.</param>
    public static int? Run(FrameworkElement source, IReadOnlyList<FrameworkElement> items, UIElement scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(items);
        var layer = AdornerLayer.GetAdornerLayer(scope);
        if (layer is null) return null;

        var grab = Mouse.GetPosition(source);
        var ghost = new GhostAdorner(scope, source, grab);
        var line = new InsertionAdorner(scope);
        layer.Add(ghost);
        layer.Add(line);
        var oldOpacity = source.Opacity;
        source.Opacity = 0.35;
        var oldCursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.SizeAll;
        int? insertAt = null;

        void Track(DragEventArgs e)
        {
            ghost.MoveTo(e.GetPosition(scope));
            insertAt = null;
            line.Hide();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var p = e.GetPosition(item);
                if (p.Y < 0 || p.Y > item.ActualHeight || p.X < -40 || p.X > item.ActualWidth + 40) continue;
                var below = p.Y > item.ActualHeight / 2;
                insertAt = below ? i + 1 : i;
                var topLeft = item.TranslatePoint(new Point(0, below ? item.ActualHeight : 0), scope);
                line.Show(topLeft, item.ActualWidth);
                break;
            }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        DragEventHandler over = (_, e) => Track(e);
        DragEventHandler drop = (_, e) => { Track(e); e.Handled = true; };
        GiveFeedbackEventHandler feedback = (_, e) => { e.UseDefaultCursors = false; e.Handled = true; };
        var allowDrop = scope.AllowDrop;
        scope.AllowDrop = true;
        scope.PreviewDragOver += over;
        scope.PreviewDrop += drop;
        source.GiveFeedback += feedback;
        try
        {
            var result = DragDrop.DoDragDrop(source, new DataObject(Format, true), DragDropEffects.Move);
            return result == DragDropEffects.None && insertAt is null ? null : insertAt;
        }
        finally
        {
            scope.PreviewDragOver -= over;
            scope.PreviewDrop -= drop;
            source.GiveFeedback -= feedback;
            scope.AllowDrop = allowDrop;
            layer.Remove(ghost);
            layer.Remove(line);
            source.Opacity = oldOpacity;
            Mouse.OverrideCursor = oldCursor;
        }
    }

    /// <summary>Converts an insertion index (before removing the dragged item) into the item's new index.</summary>
    public static int TargetIndex(int from, int insertAt) => insertAt > from ? insertAt - 1 : insertAt;

    /// <summary>A lifted, translucent copy of the grabbed row that follows the pointer.</summary>
    private sealed class GhostAdorner : Adorner
    {
        private readonly Rect _size;
        private readonly ImageBrush _brush;
        private readonly Point _grab;
        private Point _at;

        public GhostAdorner(UIElement scope, FrameworkElement source, Point grab) : base(scope)
        {
            IsHitTestVisible = false;
            _size = new Rect(0, 0, source.ActualWidth, source.ActualHeight);
            // Snapshot before the source is dimmed, so the copy stays bright.
            var dpi = VisualTreeHelper.GetDpi(source);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(source.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(source.ActualHeight * dpi.DpiScaleY)),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(source);
            bitmap.Freeze();
            _brush = new ImageBrush(bitmap) { Opacity = 0.9, Stretch = Stretch.Fill };
            _grab = grab;
            _at = source.TranslatePoint(grab, scope);
        }

        public void MoveTo(Point point)
        {
            _at = point;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var rect = new Rect(_at.X - _grab.X + 6, _at.Y - _grab.Y + 4, _size.Width, _size.Height);
            var shadow = rect;
            shadow.Offset(0, 6);
            drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, shadow, 8, 8);
            drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2A, 0x34, 0x41)), new Pen(new SolidColorBrush(Color.FromRgb(0x5A, 0x9C, 0xE6)), 1.5), rect, 6, 6);
            drawingContext.DrawRectangle(_brush, null, rect);
        }
    }

    /// <summary>The blue line where the dragged item will be inserted.</summary>
    private sealed class InsertionAdorner(UIElement scope) : Adorner(scope)
    {
        private Point? _at;
        private double _width;

        public void Show(Point at, double width)
        {
            _at = at;
            _width = width;
            IsHitTestVisible = false;
            InvalidateVisual();
        }

        public void Hide()
        {
            _at = null;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (_at is not { } at) return;
            var brush = new SolidColorBrush(Color.FromRgb(0x3C, 0x86, 0xDA));
            drawingContext.DrawRoundedRectangle(brush, null, new Rect(at.X, at.Y - 1.5, _width, 3), 1.5, 1.5);
            drawingContext.DrawEllipse(brush, null, new Point(at.X, at.Y), 4, 4);
        }
    }
}
