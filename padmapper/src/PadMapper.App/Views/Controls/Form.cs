using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace PadMapper.App.Views.Controls;

/// <summary>設定画面の行（ラベル + スライダー / 選択 / チェック / 文字入力）をコードで組み立てる補助。</summary>
internal static class Form
{
    public static FrameworkElement Label(string resourceKey, bool hint = false)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.TextProperty, resourceKey);
        t.SetResourceReference(FrameworkElement.StyleProperty, hint ? "Hint" : "Label");
        return t;
    }

    private static Grid Row(string labelKey, FrameworkElement control, FrameworkElement? extra = null)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Label(labelKey);
        g.Children.Add(label);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        if (extra != null)
        {
            Grid.SetColumn(extra, 2);
            g.Children.Add(extra);
        }
        return g;
    }

    public static FrameworkElement Slider(string labelKey, double min, double max, double value, double step, string format, Action<double> onChange)
    {
        var valueText = new TextBlock { Width = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        valueText.SetResourceReference(FrameworkElement.StyleProperty, "Label");
        var s = new Slider
        {
            Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
            SmallChange = step, LargeChange = step * 5, TickFrequency = step, IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        valueText.Text = value.ToString(format, CultureInfo.InvariantCulture);
        s.ValueChanged += (_, e) =>
        {
            valueText.Text = e.NewValue.ToString(format, CultureInfo.InvariantCulture);
            onChange(e.NewValue);
        };
        return Row(labelKey, s, valueText);
    }

    public static FrameworkElement Combo<T>(string labelKey, IEnumerable<(string textKey, T value)> items, T selected, Action<T> onChange)
    {
        var c = new ComboBox { MinWidth = 160 };
        var list = items.ToList();
        foreach (var (key, _) in list)
        {
            var item = new ComboBoxItem();
            item.SetResourceReference(ContentControl.ContentProperty, key);
            c.Items.Add(item);
        }
        c.SelectedIndex = Math.Max(0, list.FindIndex(i => EqualityComparer<T>.Default.Equals(i.value, selected)));
        c.SelectionChanged += (_, _) =>
        {
            if (c.SelectedIndex >= 0) onChange(list[c.SelectedIndex].value);
        };
        return Row(labelKey, c);
    }

    /// <summary>項目が固定の文字列（ボタン名など）の選択。</summary>
    public static FrameworkElement Choice(string labelKey, IEnumerable<string> items, string? selected, Action<string> onChange)
    {
        var c = new ComboBox { MinWidth = 160, ItemsSource = items.ToList(), SelectedItem = selected };
        c.SelectionChanged += (_, _) =>
        {
            if (c.SelectedItem is string s) onChange(s);
        };
        return Row(labelKey, c);
    }

    public static FrameworkElement Check(string labelKey, bool value, Action<bool> onChange)
    {
        var c = new CheckBox { IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        c.Click += (_, _) => onChange(c.IsChecked == true);
        return Row(labelKey, c);
    }

    public static FrameworkElement Number(string labelKey, double value, Action<double> onChange)
    {
        var t = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), MinWidth = 100 };
        t.TextChanged += (_, _) =>
        {
            if (double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) onChange(v);
        };
        return Row(labelKey, t);
    }
}
