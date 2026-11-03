using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceMonitor.Core.Configuration;
using ServiceMonitor.Core.Windows;

namespace ServiceMonitor.ViewModels;

public sealed partial class ServicesViewModel : PageViewModel
{
    private readonly ServiceInspector _inspector = new();

    public ServicesViewModel(MainViewModel main) : base(main, "Services", "")
    {
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = Filter;
        View.SortDescriptions.Add(new SortDescription(nameof(ServiceRowViewModel.IsMonitored), ListSortDirection.Descending));
        View.SortDescriptions.Add(new SortDescription(nameof(ServiceRowViewModel.DisplayName), ListSortDirection.Ascending));
    }

    public ObservableCollection<ServiceRowViewModel> Rows { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showMonitoredOnly;
    [ObservableProperty] private ServiceRowViewModel? _selected;
    [ObservableProperty] private bool _isLoading;

    public int MonitoredCount => Main.Config.Services.Count(s => s.Enabled);
    public int MatchCount => View.Cast<object>().Count();
    public bool CanMonitorAllMatches => !string.IsNullOrWhiteSpace(SearchText) && View.Cast<ServiceRowViewModel>().Any(r => !r.IsMonitored);
    public string MonitorAllText => MatchCount == 1 ? "Monitor this match" : $"Monitor all {MatchCount} matches";

    partial void OnSearchTextChanged(string value) => RefreshView();
    partial void OnShowMonitoredOnlyChanged(bool value) => RefreshView();

    public override void OnShown()
    {
        if (Rows.Count == 0) _ = RefreshAsync();
    }

    public override void OnConfigReplaced() => _ = RefreshAsync();

    private bool Filter(object item)
    {
        var row = (ServiceRowViewModel)item;
        if (ShowMonitoredOnly && !row.IsMonitored) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        return SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term =>
            row.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) || row.ServiceName.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshView()
    {
        View.Refresh();
        OnPropertyChanged(nameof(MatchCount));
        OnPropertyChanged(nameof(MonitorAllText));
        OnPropertyChanged(nameof(CanMonitorAllMatches));
    }

    internal void OnMonitoringChanged()
    {
        OnPropertyChanged(nameof(MonitoredCount));
        OnPropertyChanged(nameof(CanMonitorAllMatches));
        Main.Overview.Refresh();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var selectedName = Selected?.ServiceName;
            var services = await Task.Run(() => _inspector.ListServices());
            Rows.Clear();
            foreach (var s in services)
                Rows.Add(new ServiceRowViewModel(this, s.ServiceName, s.DisplayName, ServiceSnapshot.Describe(s.State), s.StartType, installed: true));
            // Monitored services that are not installed on this machine (yet) still need a row.
            foreach (var m in Main.Config.Services.Where(m => services.All(s => !s.ServiceName.Equals(m.ServiceName, StringComparison.OrdinalIgnoreCase))))
                Rows.Add(new ServiceRowViewModel(this, m.ServiceName, string.IsNullOrWhiteSpace(m.DisplayName) ? m.ServiceName : m.DisplayName, "Not installed", "", installed: false));
            RefreshView();
            Selected = Rows.FirstOrDefault(r => r.ServiceName == selectedName) ?? View.Cast<ServiceRowViewModel>().FirstOrDefault(r => r.IsMonitored);
        }
        catch (Exception ex)
        {
            Main.ShowToast($"Could not list services: {ex.Message}", ToastLevel.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void MonitorAllMatches()
    {
        foreach (var row in View.Cast<ServiceRowViewModel>().ToList()) row.IsMonitored = true;
        RefreshView();
    }
}

public sealed partial class ServiceRowViewModel : ObservableObject
{
    private readonly ServicesViewModel _owner;

    public ServiceRowViewModel(ServicesViewModel owner, string serviceName, string displayName, string state, string startType, bool installed)
    {
        _owner = owner;
        ServiceName = serviceName;
        DisplayName = displayName;
        State = state;
        StartType = startType;
        IsInstalled = installed;
    }

    public string ServiceName { get; }
    public string DisplayName { get; }
    public string State { get; }
    public string StartType { get; }
    public bool IsInstalled { get; }

    private MonitorConfig Config => _owner.Main.Config;
    public MonitoredService? Model => Config.Services.FirstOrDefault(s => s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase));

    public bool IsMonitored
    {
        get => Model is { Enabled: true };
        set
        {
            if (value == IsMonitored) return;
            if (value)
            {
                if (Model is { } existing) existing.Enabled = true;
                else Config.Services.Add(new MonitoredService { ServiceName = ServiceName, DisplayName = DisplayName });
            }
            else if (Model is { } m)
            {
                Config.Services.Remove(m);
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(Model));
            NotifyOptions();
            _owner.OnMonitoringChanged();
        }
    }

    public bool AutoRestart
    {
        get => Model?.AutoRestart ?? false;
        set
        {
            if (Model is { } m) m.AutoRestart = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Blank = use the global default.</summary>
    public string HangThresholdText
    {
        get => Model?.HangThresholdSeconds?.ToString() ?? "";
        set
        {
            if (Model is not { } m) return;
            m.HangThresholdSeconds = int.TryParse(value, out var v) && v > 0 ? v : null;
            OnPropertyChanged();
        }
    }

    public bool PortCheckEnabled
    {
        get => Model?.PortCheck is { Enabled: true };
        set
        {
            if (Model is not { } m) return;
            if (value) (m.PortCheck ??= new PortCheck()).Enabled = true;
            else if (m.PortCheck is { } pc) pc.Enabled = false;
            OnPropertyChanged();
            if (value && PortText.Length == 0) DetectPorts();
        }
    }

    public string PortHost
    {
        get => Model?.PortCheck?.Host ?? "127.0.0.1";
        set
        {
            if (Model is not { } m) return;
            (m.PortCheck ??= new PortCheck { Enabled = false }).Host = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim();
            OnPropertyChanged();
        }
    }

    public string PortText
    {
        get => Model?.PortCheck is { Port: > 0 } pc ? pc.Port.ToString() : "";
        set
        {
            if (Model is not { } m) return;
            (m.PortCheck ??= new PortCheck { Enabled = false }).Port = int.TryParse(value, out var p) && p is > 0 and < 65536 ? p : 0;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<ListeningPort> DetectedPorts { get; } = [];
    [ObservableProperty] private string? _detectMessage;

    [RelayCommand]
    private void DetectPorts()
    {
        DetectedPorts.Clear();
        try
        {
            var snap = new ServiceInspector().Query(ServiceName);
            if (snap.State != ServiceRunState.Running || snap.ProcessId <= 0)
            {
                DetectMessage = "Start the service first – ports can only be detected while it is running.";
                return;
            }
            foreach (var port in ListeningPorts.ForProcess(snap.ProcessId)) DetectedPorts.Add(port);
            DetectMessage = DetectedPorts.Count == 0
                ? "This service does not listen on any TCP port, so a port check is not possible for it."
                : DetectedPorts.Count == 1 ? null : "This service listens on several ports. Pick one:";
            if (DetectedPorts.Count >= 1 && PortText.Length == 0) UsePort(DetectedPorts[0]);
        }
        catch (Exception ex)
        {
            DetectMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void UsePort(ListeningPort port)
    {
        PortHost = port.ProbeHost;
        PortText = port.Port.ToString();
    }

    private void NotifyOptions()
    {
        OnPropertyChanged(nameof(AutoRestart));
        OnPropertyChanged(nameof(HangThresholdText));
        OnPropertyChanged(nameof(PortCheckEnabled));
        OnPropertyChanged(nameof(PortHost));
        OnPropertyChanged(nameof(PortText));
    }
}
