using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ServiceMonitor.UI;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Inverse { get; set; }

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasValue = value is not null && (value is not string s || s.Length > 0);
        return hasValue ^ Inverse ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the bound collection or count is empty.</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = value switch
        {
            null => true,
            int i => i == 0,
            ICollection c => c.Count == 0,
            IEnumerable e => !e.Cast<object>().Any(),
            _ => false,
        };
        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>For radio buttons bound to an enum: IsChecked="{Binding Mode, Converter={StaticResource EnumEquals}, ConverterParameter=OAuth}".</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}

/// <summary>Makes PasswordBox.Password bindable.</summary>
public static class PasswordBoxAssist
{
    // The default is null rather than "" so that binding an empty password (every new channel) still raises
    // OnBoundPasswordChanged, which is where PasswordChanged gets hooked up. With "" typed text never reached the view model.
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordBoxAssist),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    private static readonly DependencyProperty UpdatingProperty =
        DependencyProperty.RegisterAttached("Updating", typeof(bool), typeof(PasswordBoxAssist));

    public static string? GetBoundPassword(DependencyObject d) => (string?)d.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject d, string? value) => d.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        box.PasswordChanged -= OnPasswordChanged;
        if (!(bool)box.GetValue(UpdatingProperty)) box.Password = e.NewValue as string ?? "";
        box.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        box.SetValue(UpdatingProperty, true);
        SetBoundPassword(box, box.Password);
        box.SetValue(UpdatingProperty, false);
    }
}

/// <summary>Grey placeholder text inside a TextBox.</summary>
public static class Placeholder
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Placeholder), new PropertyMetadata("", OnChanged));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.Loaded -= Update;
        box.TextChanged -= Update;
        box.IsVisibleChanged -= OnVisibleChanged;
        box.Loaded += Update;
        box.TextChanged += Update;
        box.IsVisibleChanged += OnVisibleChanged;
        if (box.IsLoaded) Update(box, e: null!);
    }

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Update(sender, e: null!);

    private static void Update(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(box);
        if (layer is null) return;
        foreach (var adorner in layer.GetAdorners(box)?.OfType<PlaceholderAdorner>() ?? []) layer.Remove(adorner);
        // Boxes inside collapsed panels still load; drawing for them would leave a stray "…" at their zero-size position.
        if (box.IsVisible && string.IsNullOrEmpty(box.Text) && GetText(box) is { Length: > 0 } text) layer.Add(new PlaceholderAdorner(box, text));
    }

    private sealed class PlaceholderAdorner(TextBox box, string text) : System.Windows.Documents.Adorner(box)
    {
        protected override void OnRender(System.Windows.Media.DrawingContext dc)
        {
            IsHitTestVisible = false;
            var brush = (System.Windows.Media.Brush?)box.TryFindResource("TextFillColorTertiaryBrush") ?? System.Windows.Media.Brushes.Gray;
            var ft = new System.Windows.Media.FormattedText(text, CultureInfo.CurrentUICulture, box.FlowDirection,
                new System.Windows.Media.Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch),
                box.FontSize, brush, System.Windows.Media.VisualTreeHelper.GetDpi(box).PixelsPerDip);
            ft.MaxTextWidth = Math.Max(1, box.ActualWidth - box.Padding.Left - box.Padding.Right - 8);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(ft, new Point(box.Padding.Left + box.BorderThickness.Left + 2, (box.ActualHeight - ft.Height) / 2));
        }
    }
}
