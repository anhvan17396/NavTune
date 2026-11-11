using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using NavTune.Model;

namespace NavTune.Infrastructure;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void Raise(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Refreshes every binding on this object, e.g. after the language changed.</summary>
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute) : this(_ => execute()) { }

    public RelayCommand(Action execute, Func<object?, bool> canExecute) : this(_ => execute(), canExecute) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>Binding source for the UI language: every localized text binds to <see cref="IsVietnamese"/>.</summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    public static LocalizationSource Instance { get; } = new();

    private LocalizationSource()
    {
        L10n.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVietnamese)));
    }

    public bool IsVietnamese => L10n.IsVietnamese;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Inline Vietnamese / English text that follows the app language:
/// <c>Text="{l:Tr 'Bộ chỉnh âm', 'Equalizer'}"</c>.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string vi, string en)
    {
        Vi = vi;
        En = en;
    }

    [ConstructorArgument("vi")] public string Vi { get; set; } = "";
    [ConstructorArgument("en")] public string En { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(LocalizationSource.IsVietnamese))
        {
            Source = LocalizationSource.Instance,
            Mode = BindingMode.OneWay,
            Converter = new PickConverter(Vi, En),
        };
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class PickConverter(string vi, string en) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? vi : en;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value switch
        {
            bool b => b,
            null => false,
            string s => s.Length > 0,
            int i => i != 0,
            _ => true,
        };
        return flag ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && (v == Visibility.Visible) ^ Invert;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>True when the bound value equals the parameter; used for radio-style choices.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString());

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true) return Binding.DoNothing;
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return type.IsEnum && parameter is string name ? Enum.Parse(type, name) : parameter;
    }
}

/// <summary>A Color as a brush, optionally with a different opacity (status dots and their soft backgrounds).</summary>
public sealed class ColorBrushConverter : IValueConverter
{
    public byte Alpha { get; set; } = 255;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not System.Windows.Media.Color color) return System.Windows.Media.Brushes.Transparent;
        color.A = Alpha;
        var brush = new System.Windows.Media.SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
