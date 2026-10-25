using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PadMapper.Core.Model;

namespace PadMapper.App.Views.Controls;

/// <summary>
/// コントローラー図。ボタンをクリックすると InputClicked が呼ばれる（ビジュアル編集用）。
/// 割り当てのあるボタンは枠を強調し、実機で押されているボタンは色を変える。背面ボタン（M1〜M4）と C/Z も含む。
/// </summary>
public sealed class ControllerDiagram : UserControl
{
    private sealed record Spot(LogicalInput Input, double X, double Y, double W, double H, bool Round, string Label);

    // 正面（560 x 300）
    private static readonly Spot[] Front =
    {
        new(LogicalInput.LT, 70, 6, 80, 26, false, "LT"),
        new(LogicalInput.RT, 410, 6, 80, 26, false, "RT"),
        new(LogicalInput.LB, 70, 38, 90, 22, false, "LB"),
        new(LogicalInput.RB, 400, 38, 90, 22, false, "RB"),
        new(LogicalInput.L3, 112, 98, 64, 64, true, "L3"),
        new(LogicalInput.R3, 318, 176, 60, 60, true, "R3"),
        new(LogicalInput.DPadUp, 184, 170, 24, 24, false, "↑"),
        new(LogicalInput.DPadDown, 184, 218, 24, 24, false, "↓"),
        new(LogicalInput.DPadLeft, 160, 194, 24, 24, false, "←"),
        new(LogicalInput.DPadRight, 208, 194, 24, 24, false, "→"),
        new(LogicalInput.Y, 397, 84, 28, 28, true, "Y"),
        new(LogicalInput.X, 369, 110, 28, 28, true, "X"),
        new(LogicalInput.B, 425, 110, 28, 28, true, "B"),
        new(LogicalInput.A, 397, 136, 28, 28, true, "A"),
        new(LogicalInput.C, 450, 70, 22, 22, true, "C"),
        new(LogicalInput.Z, 474, 92, 22, 22, true, "Z"),
        new(LogicalInput.Back, 226, 112, 34, 18, false, "Back"),
        new(LogicalInput.Start, 300, 112, 34, 18, false, "Start"),
        new(LogicalInput.Guide, 266, 78, 28, 28, true, "⌂"),
        new(LogicalInput.Plus, 268, 146, 24, 18, false, "+"),
    };

    // 背面（560 x 70）
    private static readonly Spot[] Rear =
    {
        new(LogicalInput.M1, 120, 18, 60, 34, false, "M1"),
        new(LogicalInput.M2, 190, 18, 60, 34, false, "M2"),
        new(LogicalInput.M3, 310, 18, 60, 34, false, "M3"),
        new(LogicalInput.M4, 380, 18, 60, 34, false, "M4"),
    };

    private readonly Dictionary<LogicalInput, Border> _spots = new();
    private HashSet<LogicalInput> _bound = new();
    private InputSet _pressed = InputSet.Empty;
    private LogicalInput? _selected;

    public event Action<LogicalInput>? InputClicked;

    public ControllerDiagram()
    {
        var panel = new StackPanel();
        panel.Children.Add(BuildFront());
        var rearLabel = new TextBlock { Margin = new Thickness(0, 8, 0, 2), FontSize = 12 };
        rearLabel.SetResourceReference(TextBlock.TextProperty, "Diagram_Rear");
        rearLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.Children.Add(rearLabel);
        panel.Children.Add(BuildRear());
        Content = new Viewbox { Child = panel, Stretch = Stretch.Uniform, MaxWidth = 640 };
        Refresh();
    }

    private Canvas BuildFront()
    {
        var canvas = new Canvas { Width = 560, Height = 260 };
        // 本体（左右のグリップと中央）
        canvas.Children.Add(Body(new Ellipse(), 40, 60, 200, 190));
        canvas.Children.Add(Body(new Ellipse(), 320, 60, 200, 190));
        canvas.Children.Add(Body(new Rectangle { RadiusX = 30, RadiusY = 30 }, 130, 62, 300, 130));
        foreach (var s in Front) AddSpot(canvas, s);
        return canvas;
    }

    private Canvas BuildRear()
    {
        var canvas = new Canvas { Width = 560, Height = 70 };
        canvas.Children.Add(Body(new Rectangle { RadiusX = 24, RadiusY = 24 }, 90, 6, 380, 58));
        foreach (var s in Rear) AddSpot(canvas, s);
        return canvas;
    }

    private static Shape Body(Shape shape, double x, double y, double w, double h)
    {
        shape.Width = w;
        shape.Height = h;
        shape.SetResourceReference(Shape.FillProperty, "ControlFillColorSecondaryBrush");
        shape.SetResourceReference(Shape.StrokeProperty, "ControlStrokeColorDefaultBrush");
        shape.StrokeThickness = 1;
        Canvas.SetLeft(shape, x);
        Canvas.SetTop(shape, y);
        return shape;
    }

    private void AddSpot(Canvas canvas, Spot s)
    {
        var text = new TextBlock
        {
            Text = s.Label,
            FontSize = s.H < 24 ? 10 : 12,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var b = new Border
        {
            Width = s.W,
            Height = s.H,
            CornerRadius = new CornerRadius(s.Round ? Math.Min(s.W, s.H) / 2 : 5),
            BorderThickness = new Thickness(1.5),
            Child = text,
            Cursor = Cursors.Hand,
            Tag = s.Input,
            ToolTip = s.Input.ToString(),
        };
        b.MouseLeftButtonUp += (_, _) => InputClicked?.Invoke(s.Input);
        Canvas.SetLeft(b, s.X);
        Canvas.SetTop(b, s.Y);
        canvas.Children.Add(b);
        _spots[s.Input] = b;
    }

    public LogicalInput? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Refresh();
        }
    }

    public void SetBound(IEnumerable<LogicalInput> bound)
    {
        _bound = bound.ToHashSet();
        Refresh();
    }

    public void SetPressed(InputSet pressed)
    {
        if (pressed == _pressed) return;
        _pressed = pressed;
        Refresh();
    }

    private void Refresh()
    {
        foreach (var (input, b) in _spots)
        {
            bool selected = _selected == input;
            bool pressed = _pressed.Has(input);
            bool bound = _bound.Contains(input);
            b.SetResourceReference(Border.BackgroundProperty,
                pressed ? "SystemFillColorSuccessBrush" : selected ? "AccentFillColorDefaultBrush" : "ControlFillColorDefaultBrush");
            b.SetResourceReference(Border.BorderBrushProperty, bound || selected ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush");
            b.BorderThickness = new Thickness(bound ? 2.5 : 1);
            if (b.Child is TextBlock t)
                t.SetResourceReference(TextBlock.ForegroundProperty,
                    selected || pressed ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
        }
    }
}
