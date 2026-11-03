using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ServiceMonitor.Core;
using ServiceMonitor.Core.Configuration;
using ServiceMonitor.Views;

namespace ServiceMonitor.ViewModels;

public sealed record ChannelTypeOption(ChannelKind Kind, string Title, string Description, string Glyph, string Group, SmtpProvider? Provider = null);

public sealed partial class ChannelsViewModel : PageViewModel
{
    public ChannelsViewModel(MainViewModel main) : base(main, "Notifications", "") => Load();

    public ObservableCollection<ChannelViewModel> Items { get; } = [];

    /// <summary>
    /// Outlook.com is only offered once a Microsoft app registration is built in or entered under Settings,
    /// because Microsoft no longer accepts passwords for it.
    /// </summary>
    public IReadOnlyList<ChannelTypeOption> EmailTypes =>
    [
        new(ChannelKind.Gmail, "Gmail", "Recommended. Send from any Gmail account with an app password.", "\uE715", "Email"),
        new(ChannelKind.Smtp, "Yahoo Mail", "Send from a Yahoo account with an app password. Brand-new accounts can't create one for a few days.", "\uE715", "Email", SmtpProvider.Yahoo),
        new(ChannelKind.Smtp, "iCloud Mail", "Send from an @icloud.com address with an app-specific password.", "\uE715", "Email", SmtpProvider.ICloud),
        .. Main.Config.OAuth.MicrosoftConfigured
            ? [new ChannelTypeOption(ChannelKind.Microsoft, "Outlook.com / Microsoft 365", "Sign in with Microsoft – works with outlook.com, hotmail.com, live.com and work accounts.", "\uE715", "Email")]
            : Array.Empty<ChannelTypeOption>(),
        new(ChannelKind.Smtp, "Other SMTP server", "Company mail relay or any provider that accepts SMTP sign-in.", "\uE715", "Email"),
    ];

    public IReadOnlyList<ChannelTypeOption> OtherTypes { get; } =
    [
        new(ChannelKind.Ntfy, "Phone push (ntfy)", "Free push notifications to Android and iPhone – no account needed.", "", "Push"),
        new(ChannelKind.Teams, "Microsoft Teams", "Post to a Teams channel through a Workflows webhook.", "", "Chat"),
        new(ChannelKind.Slack, "Slack", "Post to a Slack channel through an incoming webhook.", "", "Chat"),
        new(ChannelKind.Discord, "Discord", "Post to a Discord channel through a webhook.", "", "Chat"),
        new(ChannelKind.Telegram, "Telegram", "Message a Telegram chat or group through a bot.", "", "Chat"),
        new(ChannelKind.Webhook, "Webhook (JSON)", "POST to any URL – RMM/PSA tools, Power Automate, n8n, PagerDuty relays…", "", "Other"),
    ];

    [ObservableProperty] private ChannelViewModel? _selected;
    [ObservableProperty] private bool _isAdding;

    public override void OnConfigReplaced() => Load();

    private void Load()
    {
        Items.Clear();
        foreach (var channel in Main.Config.Channels) Items.Add(ChannelViewModel.Create(Main, channel));
        Selected = Items.FirstOrDefault();
        IsAdding = Items.Count == 0;
    }

    [RelayCommand]
    private void ShowAdd()
    {
        OnPropertyChanged(nameof(EmailTypes));
        IsAdding = true;
        Selected = null;
    }

    [RelayCommand]
    private void Add(ChannelTypeOption option)
    {
        var config = ChannelConfig.Create(option.Kind);
        if (config is SmtpChannel smtp && option.Provider is { } provider)
        {
            smtp.ApplyProvider(provider);
            smtp.Name = provider.Name;
        }
        var sameKind = Main.Config.Channels.Count(c => c.Kind == option.Kind && (c as SmtpChannel)?.Provider == (config as SmtpChannel)?.Provider);
        if (sameKind > 0) config.Name += $" {sameKind + 1}";
        Main.Config.Channels.Add(config);
        var vm = ChannelViewModel.Create(Main, config);
        Items.Add(vm);
        IsAdding = false;
        Selected = vm;
        Main.Overview.Refresh();
    }

    [RelayCommand]
    private void Remove(ChannelViewModel? vm)
    {
        if (vm is null) return;
        var answer = MessageBox.Show($"Remove the notification channel '{vm.Name}'?", AppInfo.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        Main.Config.Channels.Remove(vm.Model);
        Items.Remove(vm);
        Selected = Items.FirstOrDefault();
        IsAdding = Items.Count == 0;
        Main.Overview.Refresh();
    }

    partial void OnSelectedChanged(ChannelViewModel? value)
    {
        if (value is not null) IsAdding = false;
    }

    internal const string ExportPasswordHeading = "Choose a password for the file";
    internal const string ExportPasswordMessage =
        "The file holds every channel with its passwords, tokens and webhook URLs, encrypted with this password. " +
        "You need it to import the file. Sign-ins with Google or Microsoft are not included.";

    /// <summary>Saves all channels, as shown (unsaved edits included), to a password-protected file.</summary>
    [RelayCommand]
    private void Export()
    {
        if (Main.Config.Channels.Count == 0)
        {
            Main.ShowToast("There are no channels to export yet.", ToastLevel.Warning);
            return;
        }
        var owner = Application.Current.MainWindow;
        var dialog = new SaveFileDialog
        {
            Title = "Export channels",
            Filter = ChannelTransfer.FileFilter,
            DefaultExt = ChannelTransfer.FileExtension,
            FileName = $"{AppInfo.MachineName} channels {DateTime.Now:yyyy-MM-dd}{ChannelTransfer.FileExtension}",
        };
        if (dialog.ShowDialog(owner) != true) return;
        var password = PasswordDialog.Ask(owner, ExportPasswordHeading, ExportPasswordMessage,
            "Export", choose: true, minimumLength: ChannelTransfer.MinimumPasswordLength);
        if (password is null) return;
        try
        {
            File.WriteAllText(dialog.FileName, ChannelTransfer.Export(Main.Config.Channels, password));
            Main.ShowToast($"Exported {Plural(Main.Config.Channels.Count)} to {dialog.FileName}.", ToastLevel.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Main.ShowToast($"Could not export: {ex.Message}", ToastLevel.Error);
        }
    }

    /// <summary>Adds the channels from an export file; ones with the same name as an existing channel replace it.</summary>
    [RelayCommand]
    private void Import()
    {
        var owner = Application.Current.MainWindow;
        var dialog = new OpenFileDialog { Title = "Import channels", Filter = ChannelTransfer.FileFilter };
        if (dialog.ShowDialog(owner) != true) return;
        string text;
        ChannelExportInfo info;
        try
        {
            text = File.ReadAllText(dialog.FileName);
            info = ChannelTransfer.ReadInfo(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Main.ShowToast($"Could not import: {ex.Message}", ToastLevel.Error);
            return;
        }

        List<ChannelConfig>? imported = null;
        var from = info.Machine.Length > 0 ? $" from {info.Machine}" : "";
        var password = PasswordDialog.Ask(owner, "Enter the file's password",
            $"{Plural(info.Count)} exported{from} on {info.Exported.LocalDateTime:g}. Enter the password chosen when they were exported. " +
            "A channel with the same name as one here replaces it.",
            "Import", check: p =>
            {
                try { imported = ChannelTransfer.Import(text, p); return null; }
                catch (InvalidDataException ex) { return ex.Message; }
            });
        if (password is null || imported is null) return;

        var (added, replaced) = ChannelTransfer.Merge(Main.Config.Channels, imported);
        Load();
        Selected = Items.FirstOrDefault(i => i.Model == imported.FirstOrDefault()) ?? Items.FirstOrDefault();
        Main.Overview.Refresh();
        var signIn = imported.Where(c => c.UsesOAuth).Select(c => c.Name).ToList();
        Main.ShowToast($"Imported {Plural(imported.Count)}: {added} new, {replaced} replaced. Check them, then click Save changes." +
                       (signIn.Count > 0 ? $" Sign in again on {string.Join(", ", signIn)}." : ""),
            signIn.Count > 0 ? ToastLevel.Warning : ToastLevel.Success);
    }

    private static string Plural(int count) => count == 1 ? "1 channel" : $"{count} channels";
}
