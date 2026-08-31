using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
// The project pulls in System.Drawing, which has its own Rectangle and Point.
using Rectangle = System.Windows.Shapes.Rectangle;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace RTSPCameraConfigurator;

/// <summary>
/// Draws the detection / privacy rectangles over a still from the camera.
///
/// The firmware stores rectangles in a normalised 0-10000 space across the frame,
/// not in pixels - person_detect.js does x = left / boxWidth * 10000. That makes them
/// independent of both the encoder resolution and the size of this window, so the
/// only conversion needed is between canvas pixels and that fixed range.
/// </summary>
public partial class RegionEditor : Window
{
    private const int Normalised = 10000;

    private readonly int _maxRectangles;
    private readonly List<Rectangle> _shapes = new();

    private Point _origin;
    private Rectangle? _dragging;

    /// <summary>The rectangles as the camera stores them, in draw order.</summary>
    public JsonArray Rectangles { get; private set; } = new();

    public RegionEditor(string prompt, JsonArray? existing, int maxRectangles, ImageSource? frame)
    {
        InitializeComponent();

        _maxRectangles = maxRectangles;
        PromptText.Text = prompt;

        if (frame is not null)
        {
            FrameImage.Source = frame;
        }
        else
        {
            NoFrameText.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            LoadExisting(existing);
            UpdateCount();
        };
    }

    /// <summary>
    /// A zero-sized rectangle is the firmware's way of saying "unused", so those are
    /// dropped rather than drawn as invisible shapes that still count towards the limit.
    /// </summary>
    private void LoadExisting(JsonArray? existing)
    {
        if (existing is null) return;

        foreach (var node in existing.OfType<JsonObject>())
        {
            var w = Value(node, "w");
            var h = Value(node, "h");
            if (w <= 0 || h <= 0) continue;

            var shape = NewShape();
            Canvas.SetLeft(shape, Value(node, "x") / (double)Normalised * DrawCanvas.ActualWidth);
            Canvas.SetTop(shape, Value(node, "y") / (double)Normalised * DrawCanvas.ActualHeight);
            shape.Width = w / (double)Normalised * DrawCanvas.ActualWidth;
            shape.Height = h / (double)Normalised * DrawCanvas.ActualHeight;

            DrawCanvas.Children.Add(shape);
            _shapes.Add(shape);
        }
    }

    private static int Value(JsonObject rect, string key) =>
        rect.TryGetPropertyValue(key, out var node) && node is not null &&
        int.TryParse(node.ToString(), out var parsed) ? parsed : 0;

    private static Rectangle NewShape() => new()
    {
        Stroke = Brushes.DeepSkyBlue,
        StrokeThickness = 2,
        Fill = new SolidColorBrush(Color.FromArgb(48, 0, 191, 255))
    };

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_shapes.Count >= _maxRectangles)
        {
            CountText.Text = $"Limit is {_maxRectangles} - remove one first.";
            return;
        }

        _origin = e.GetPosition(DrawCanvas);

        _dragging = NewShape();
        Canvas.SetLeft(_dragging, _origin.X);
        Canvas.SetTop(_dragging, _origin.Y);

        DrawCanvas.Children.Add(_dragging);
        DrawCanvas.CaptureMouse();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging is null) return;

        var current = e.GetPosition(DrawCanvas);

        // Clamped so a drag off the edge cannot produce a rectangle outside the frame,
        // which the camera would store as an out-of-range coordinate.
        var x = Math.Clamp(current.X, 0, DrawCanvas.ActualWidth);
        var y = Math.Clamp(current.Y, 0, DrawCanvas.ActualHeight);

        Canvas.SetLeft(_dragging, Math.Min(x, _origin.X));
        Canvas.SetTop(_dragging, Math.Min(y, _origin.Y));
        _dragging.Width = Math.Abs(x - _origin.X);
        _dragging.Height = Math.Abs(y - _origin.Y);
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        DrawCanvas.ReleaseMouseCapture();
        if (_dragging is null) return;

        // A click rather than a drag: discard it instead of storing a sliver.
        if (_dragging.Width < 8 || _dragging.Height < 8)
            DrawCanvas.Children.Remove(_dragging);
        else
            _shapes.Add(_dragging);

        _dragging = null;
        UpdateCount();
    }

    private void OnRemoveLast(object sender, RoutedEventArgs e)
    {
        if (_shapes.Count == 0) return;

        DrawCanvas.Children.Remove(_shapes[^1]);
        _shapes.RemoveAt(_shapes.Count - 1);
        UpdateCount();
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        foreach (var shape in _shapes) DrawCanvas.Children.Remove(shape);
        _shapes.Clear();
        UpdateCount();
    }

    private void UpdateCount() =>
        CountText.Text = _shapes.Count == 0
            ? "No regions - the whole image is used."
            : $"{_shapes.Count} of {_maxRectangles} region(s).";

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var result = new JsonArray();

        foreach (var shape in _shapes)
        {
            result.Add(new JsonObject
            {
                ["x"] = ToNormalised(Canvas.GetLeft(shape), DrawCanvas.ActualWidth),
                ["y"] = ToNormalised(Canvas.GetTop(shape), DrawCanvas.ActualHeight),
                ["w"] = ToNormalised(shape.Width, DrawCanvas.ActualWidth),
                ["h"] = ToNormalised(shape.Height, DrawCanvas.ActualHeight)
            });
        }

        Rectangles = result;
        DialogResult = true;
    }

    private static int ToNormalised(double value, double extent)
    {
        if (double.IsNaN(value) || extent <= 0) return 0;
        return Math.Clamp((int)Math.Round(value / extent * Normalised), 0, Normalised);
    }
}
