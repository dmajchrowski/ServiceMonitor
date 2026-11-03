using System.Windows;

namespace ServiceMonitor.Views;

/// <summary>Asks for the password of a channel export: chosen (typed twice) when exporting, entered when importing.</summary>
public partial class PasswordDialog : Window
{
    private readonly bool _choose;
    private readonly int _minimumLength;
    private readonly Func<string, string?>? _check;

    internal PasswordDialog(string heading, string message, string okText, bool choose, int minimumLength, Func<string, string?>? check)
    {
        InitializeComponent();
        Heading.Text = heading;
        Message.Text = message;
        Ok.Content = okText;
        ConfirmPanel.Visibility = choose ? Visibility.Visible : Visibility.Collapsed;
        _choose = choose;
        _minimumLength = minimumLength;
        _check = check;
        Loaded += (_, _) => First.Focus();
    }

    /// <summary>
    /// Shows the dialog and returns the password, or null if cancelled. <paramref name="check"/> returns an error to show
    /// (the dialog then stays open, e.g. for a wrong password) or null to accept.
    /// </summary>
    public static string? Ask(Window? owner, string heading, string message, string okText,
        bool choose = false, int minimumLength = 1, Func<string, string?>? check = null)
    {
        var dialog = new PasswordDialog(heading, message, okText, choose, minimumLength, check) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.First.Password : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var password = First.Password;
        var error = password.Length < _minimumLength
            ? _minimumLength > 1 ? $"Use at least {_minimumLength} characters." : "Enter the password."
            : _choose && password != Second.Password ? "The two passwords are not the same."
            : null;
        if (error is null && _check is not null)
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            try { error = _check(password); }
            finally { Cursor = null; }
        }
        if (error is null)
        {
            DialogResult = true;
            return;
        }
        ErrorText.Text = error;
        ErrorBar.Visibility = Visibility.Visible;
        First.SelectAll();
        First.Focus();
    }
}
