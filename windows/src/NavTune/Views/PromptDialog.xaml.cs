using System.Windows;
using static NavTune.Model.L10n;

namespace NavTune.Views;

/// <summary>Small Fluent dialog for a name (save preset) or a confirmation (reset).</summary>
public partial class PromptDialog : Window
{
    private PromptDialog(Window? owner, string title, string message, string ok, bool withInput)
    {
        InitializeComponent();
        Owner = owner is { IsVisible: true } ? owner : null;
        if (Owner is null) WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = title;
        Heading.Text = title;
        Message.Text = message;
        Message.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        Ok.Content = ok;
        Cancel.Content = Tr("Huỷ", "Cancel");
        if (withInput)
        {
            Input.Visibility = Visibility.Visible;
            Loaded += (_, _) => Input.Focus();
        }
    }

    /// <summary>Returns the text entered, or null if cancelled.</summary>
    public static string? Ask(Window? owner, string title, string message, string ok)
    {
        var dialog = new PromptDialog(owner, title, message, ok, withInput: true);
        return dialog.ShowDialog() == true ? dialog.Input.Text : null;
    }

    public static bool Confirm(Window? owner, string title, string message, string ok) =>
        new PromptDialog(owner, title, message, ok, withInput: false).ShowDialog() == true;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
