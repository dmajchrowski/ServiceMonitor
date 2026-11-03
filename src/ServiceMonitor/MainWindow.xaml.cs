using System.ComponentModel;
using System.Windows;
using ServiceMonitor.ViewModels;
#if DEBUG
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ServiceMonitor.Core.Configuration;
#endif

namespace ServiceMonitor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm = new MainViewModel();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
#if DEBUG
        if (_snapshotDir is not null)
        {
            base.OnClosing(e);
            return;
        }
#endif
        if (!_vm.ConfirmCloseWithUnsavedChanges()) e.Cancel = true;
        base.OnClosing(e);
    }

#if DEBUG
    // Development aid: WSM_SNAPSHOT_DIR=<folder> renders every page (and a few channel editors) to PNG files and exits.
    // Used to review the UI on machines without a live display. Never compiled into Release builds.
    private readonly string? _snapshotDir = Environment.GetEnvironmentVariable("WSM_SNAPSHOT_DIR");

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_snapshotDir is null) return;
        Directory.CreateDirectory(_snapshotDir);
        foreach (var page in _vm.Pages)
        {
            _vm.SelectedPage = page;
            await Task.Delay(page == _vm.Services ? 4000 : 1500);
            if (page == _vm.Services && Environment.GetEnvironmentVariable("WSM_SNAPSHOT_SEARCH") is { } search)
            {
                _vm.Services.SearchText = search;
                await Task.Delay(500);
                Snapshot("Services-search");
                if (_vm.Services.View.Cast<ServiceRowViewModel>().FirstOrDefault() is { } row)
                {
                    row.IsMonitored = true;
                    _vm.Services.Selected = row;
                    await Task.Delay(800);
                }
            }
            Snapshot(page.Title);
        }

        _vm.SelectedPage = _vm.Channels;
        _vm.Channels.ShowAddCommand.Execute(null);
        await Task.Delay(800);
        Snapshot("Notifications-add");
        foreach (var option in _vm.Channels.EmailTypes.Concat(_vm.Channels.OtherTypes.Take(2)))
        {
            _vm.Channels.AddCommand.Execute(option);
            await Task.Delay(800);
            Snapshot($"Notifications-{option.Title.Replace('/', '-')}");
        }

        // A Teams workflow URL has a long host; the channel list must cut it off with "…" on one line.
        if (_vm.Channels.Selected is WebhookUrlChannelViewModel teams)
            teams.WebhookUrl = "https://default0123456789abcdef0123456789ab.cd.environment.api.powerplatform.com/powerautomate/automations/direct/workflows/0";
        // The message bar under a page that reaches the bottom (channel list with Export / Import).
        _vm.ShowToast("Imported 8 channels: 0 new, 8 replaced. Check them, then click Save changes.", ToastLevel.Success);
        await Task.Delay(800);
        Snapshot("Notifications-message");
        _vm.ShowToast("Could not export: The process cannot access the file because it is being used by another process.", ToastLevel.Error);
        await Task.Delay(500);
        Snapshot("Notifications-message-error");
        _vm.DismissToastCommand.Execute(null);

        var dialog = new Views.PasswordDialog(ChannelsViewModel.ExportPasswordHeading, ChannelsViewModel.ExportPasswordMessage, "Export",
            choose: true, minimumLength: ChannelTransfer.MinimumPasswordLength, check: null) { Owner = this };
        dialog.Show();
        await Task.Delay(800);
        Snapshot("PasswordDialog-export", dialog);
        dialog.Close();
        Close();
    }

    private void Snapshot(string name, Window? window = null)
    {
        window ??= this;
        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var size = new Size(root.ActualWidth, root.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)FindResource("SolidBackgroundFillColorBaseBrush"), null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(size));
        }
        var bitmap = new RenderTargetBitmap((int)(size.Width * dpi.DpiScaleX), (int)(size.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(_snapshotDir!, name + ".png"));
        encoder.Save(file);
    }
#endif
}
