using System.Windows;
using CommunityToolkit.Mvvm.Input;
using ServiceMonitor.Core;
using ServiceMonitor.Core.Configuration;
using ServiceMonitor.Core.Hosting;

namespace ServiceMonitor.ViewModels;

/// <summary>Most settings bind directly to <see cref="MainViewModel.Config"/> (Config.Settings.*, Config.OAuth.*).</summary>
public sealed partial class SettingsViewModel(MainViewModel main) : PageViewModel(main, "Settings", "")
{
    public IReadOnlyList<HeartbeatFrequency> HeartbeatOptions { get; } = Enum.GetValues<HeartbeatFrequency>();
    public IReadOnlyList<DayOfWeek> Days { get; } = Enum.GetValues<DayOfWeek>();
    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();

    public string DataFolder => AppInfo.DataDirectory;
    public string BuiltInMicrosoft => string.IsNullOrEmpty(BuildDefaults.MicrosoftClientId) ? "none built in" : $"built in: {BuildDefaults.MicrosoftClientId}";
    public string BuiltInGoogle => string.IsNullOrEmpty(BuildDefaults.GoogleClientId) ? "none built in" : "built in";

    public override void OnConfigReplaced() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private async Task UninstallAsync()
    {
        var answer = MessageBox.Show(
            "Remove the background monitor service from this computer?\n\nYes = remove the service but keep settings and sign-ins.\n" +
            "No = remove the service AND delete all settings, sign-ins and logs.\nCancel = do nothing.",
            AppInfo.ProductName, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Cancel) return;
        var removeData = answer == MessageBoxResult.No;
        try
        {
            await Main.RunBusyAsync("Removing…", progress => Task.Run(() => MonitorServiceInstaller.Uninstall(removeData, progress)));
            Main.ShowToast(removeData ? "Monitor removed and all data deleted." : "Monitor service removed. Settings were kept.", ToastLevel.Success);
        }
        catch (Exception ex)
        {
            Main.ShowToast($"Could not remove the monitor: {ex.Message}", ToastLevel.Error);
        }
    }

    [RelayCommand]
    private static void OpenRegistrationGuide() =>
        MainViewModel.OpenUrl("https://learn.microsoft.com/entra/identity-platform/quickstart-register-app");
}

public sealed partial class ActivityViewModel(MainViewModel main) : PageViewModel(main, "Activity", "")
{
    public System.Collections.ObjectModel.ObservableCollection<Core.Monitoring.ActivityEntry> Entries { get; } = [];

    private bool _problemsOnly;

    public bool ProblemsOnly
    {
        get => _problemsOnly;
        set
        {
            if (SetProperty(ref _problemsOnly, value)) Refresh();
        }
    }

    public bool HasEntries => Entries.Count > 0;

    public override void OnShown() => Refresh();

    public void Refresh()
    {
        var entries = (Main.Status?.Activity ?? [])
            .Where(e => !ProblemsOnly || e.Level is Core.Monitoring.ActivityLevel.Error or Core.Monitoring.ActivityLevel.Warning)
            .ToList();
        if (entries.Count == Entries.Count && entries.Zip(Entries).All(p => p.First == p.Second)) return;
        Entries.Clear();
        foreach (var e in entries) Entries.Add(e);
        OnPropertyChanged(nameof(HasEntries));
    }
}
