using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace WindowsJournal.Controls;

/// <summary>
/// Write or sketch with a pen, touch or mouse. The Windows App SDK has no InkCanvas, so strokes are drawn as shapes
/// and saved as a picture. The page is always white with dark ink, so it reads the same in light and dark mode.
/// </summary>
public sealed partial class SketchDialog : ContentDialog
{
    private const double PageWidth = 480;
    private const double PageHeight = 320;
    private const double MouseAndTouchThickness = 3;

    private readonly Canvas _page = new()
    {
        Width = PageWidth,
        Height = PageHeight,
        Background = new SolidColorBrush(Colors.White),
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, PageWidth, PageHeight) },
        // Handling manipulations here stops touch from scrolling the dialog instead of drawing.
        ManipulationMode = ManipulationModes.All,
    };

    private readonly Button _undo = new() { Content = "Undo", IsEnabled = false };
    private readonly Button _clear = new() { Content = "Clear", IsEnabled = false };
    private readonly SolidColorBrush _ink = new(Color.FromArgb(255, 27, 27, 27));
    private Polyline? _stroke;

    /// <summary>The finished picture as a PNG, or null if nothing was added.</summary>
    public byte[]? Image { get; private set; }

    public SketchDialog()
    {
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Title = "Draw";
        PrimaryButtonText = "Add to entry";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;
        Resources["ContentDialogMaxWidth"] = 600.0;

        AutomationProperties.SetName(_page, "Drawing page");
        _page.PointerPressed += Page_PointerPressed;
        _page.PointerMoved += Page_PointerMoved;
        _page.PointerReleased += Page_PointerEnded;
        _page.PointerCanceled += Page_PointerEnded;
        _page.PointerCaptureLost += Page_PointerEnded;

        _undo.Click += (_, _) =>
        {
            _page.Children.RemoveAt(_page.Children.Count - 1);
            UpdateButtons();
        };
        _clear.Click += (_, _) =>
        {
            _page.Children.Clear();
            UpdateButtons();
        };

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(_undo);
        tools.Children.Add(_clear);

        var frame = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            CornerRadius = new CornerRadius(4),
            Child = _page,
        };

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(frame);
        content.Children.Add(tools);
        Content = content;

        PrimaryButtonClick += SketchDialog_PrimaryButtonClick;
    }

    private void Page_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_page);
        _page.CapturePointer(e.Pointer);

        // A pen's pressure sets the line's weight, so handwriting looks like handwriting.
        var thickness = e.Pointer.PointerDeviceType == PointerDeviceType.Pen ? 1.5 + (point.Properties.Pressure * 3) : MouseAndTouchThickness;
        _stroke = new Polyline
        {
            Stroke = _ink,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        _stroke.Points.Add(point.Position);
        // A tap should leave a dot, and a line needs two points to show.
        _stroke.Points.Add(point.Position);
        _page.Children.Add(_stroke);
        UpdateButtons();
        e.Handled = true;
    }

    private void Page_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_stroke is null) return;

        // Windows batches fast movement; using every point keeps curves smooth.
        foreach (var point in e.GetIntermediatePoints(_page))
        {
            _stroke.Points.Add(point.Position);
        }

        e.Handled = true;
    }

    private void Page_PointerEnded(object sender, PointerRoutedEventArgs e)
    {
        _stroke = null;
        _page.ReleasePointerCapture(e.Pointer);
    }

    private void UpdateButtons()
    {
        var hasInk = _page.Children.Count > 0;
        _undo.IsEnabled = hasInk;
        _clear.IsEnabled = hasInk;
        IsPrimaryButtonEnabled = hasInk;
    }

    private async void SketchDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            Image = await RenderAsync();
        }
        catch (Exception ex)
        {
            Services.AIService.LogError(ex);
            args.Cancel = true;
            Title = "Couldn't save the drawing. Try again.";
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<byte[]> RenderAsync()
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(_page);
        var pixels = await bitmap.GetPixelsAsync();

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();

        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }
}
