using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceMonitor.Core.Configuration;
using ServiceMonitor.Core.Monitoring;
using ServiceMonitor.Core.OAuth;
using ServiceMonitor.Core.Security;

namespace ServiceMonitor.ViewModels;

/// <summary>Editor state for one notification channel. Properties write straight through to the configuration object.</summary>
public abstract partial class ChannelViewModel(MainViewModel main, ChannelConfig model) : ObservableObject
{
    public MainViewModel Main { get; } = main;
    public ChannelConfig Model { get; } = model;

    public static ChannelViewModel Create(MainViewModel main, ChannelConfig model) => model switch
    {
        GmailChannel g => new GmailChannelViewModel(main, g),
        MicrosoftChannel m => new MicrosoftChannelViewModel(main, m),
        SmtpChannel s => new SmtpChannelViewModel(main, s),
        NtfyChannel n => new NtfyChannelViewModel(main, n),
        TelegramChannel t => new TelegramChannelViewModel(main, t),
        WebhookChannel w => new WebhookChannelViewModel(main, w),
        _ => new WebhookUrlChannelViewModel(main, model),
    };

    public string Name
    {
        get => Model.Name;
        set
        {
            Model.Name = value;
            OnPropertyChanged();
        }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            Model.Enabled = value;
            OnPropertyChanged();
            Main.Overview.Refresh();
        }
    }

    public abstract string KindLabel { get; }
    public virtual string Glyph => "";

    /// <summary>One line shown in the channel list, e.g. the account or topic.</summary>
    public abstract string Summary { get; }

    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string? _testMessage;
    [ObservableProperty] private bool _testSucceeded;

    [RelayCommand]
    private async Task TestAsync()
    {
        IsTesting = true;
        TestMessage = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            TestMessage = await Main.Sender.SendAsync(Model, Main.Config, SystemNotifications.Test(Main.Config, Model.Name), cts.Token);
            TestSucceeded = true;
            Main.Overview.StepTestDone = true;
        }
        catch (Exception ex)
        {
            TestMessage = ex.Message;
            TestSucceeded = false;
        }
        finally
        {
            IsTesting = false;
        }
    }

    protected void Changed(params string[] names)
    {
        foreach (var name in names) OnPropertyChanged(name);
        OnPropertyChanged(nameof(Summary));
    }

    protected static string Mask(string protectedValue)
    {
        var value = SecretProtector.Unprotect(protectedValue);
        if (value.Length == 0) return "Not configured";
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? $"{uri.Host}/…" : "Configured";
    }
}

/// <summary>Shared sign-in plumbing for Microsoft and Google.</summary>
public abstract partial class OAuthChannelViewModel(MainViewModel main, EmailChannelConfig model) : EmailChannelViewModel(main, model)
{
    private CancellationTokenSource? _signInCts;

    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private string? _signInLink;
    [ObservableProperty] private string? _signInError;
    [ObservableProperty] private string? _deviceCode;
    [ObservableProperty] private string? _deviceUrl;

    public string? SignedInAccount => TokenStore.Load(Model.Id)?.Account is { Length: > 0 } a ? a : null;
    public bool IsSignedIn => TokenStore.Load(Model.Id) is not null;
    public string SignInButtonText => IsSignedIn ? "Sign in with a different account" : $"Sign in with {Provider.DisplayName}";
    public bool SupportsDeviceCode => Provider.DeviceCodeEndpoint is not null;

    protected abstract OAuthProvider Provider { get; }
    protected abstract void OnSignedIn(StoredToken token);
    protected virtual string? LoginHint => null;

    [RelayCommand]
    private async Task SignInAsync()
    {
        await RunSignInAsync(ct => Main.OAuth.SignInWithBrowserAsync(Provider, Main.Config.OAuth, LoginHint, url =>
        {
            SignInLink = url;
            MainViewModel.OpenUrl(url);
        }, ct));
    }

    [RelayCommand]
    private async Task SignInWithCodeAsync()
    {
        await RunSignInAsync(ct => Main.OAuth.SignInWithDeviceCodeAsync(Provider, Main.Config.OAuth, prompt =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                DeviceCode = prompt.UserCode;
                DeviceUrl = prompt.VerificationUri;
            });
        }, ct));
    }

    private async Task RunSignInAsync(Func<CancellationToken, Task<StoredToken>> signIn)
    {
        _signInCts?.Cancel();
        _signInCts = new CancellationTokenSource();
        IsSigningIn = true;
        SignInError = null;
        SignInLink = null;
        DeviceCode = null;
        DeviceUrl = null;
        try
        {
            var token = await signIn(_signInCts.Token);
            TokenStore.Save(Model.Id, token);
            OnSignedIn(token);
            Changed(nameof(SignedInAccount), nameof(IsSignedIn), nameof(SignInButtonText));
            Main.ShowToast($"Signed in as {token.Account}. Click Send test to check it, then Save.", ToastLevel.Success);
        }
        catch (OperationCanceledException)
        {
            SignInError = null;
        }
        catch (Exception ex)
        {
            SignInError = ex.Message;
        }
        finally
        {
            IsSigningIn = false;
            DeviceCode = null;
            DeviceUrl = null;
        }
    }

    [RelayCommand]
    private void CancelSignIn() => _signInCts?.Cancel();

    [RelayCommand]
    private void CopySignInLink()
    {
        if (SignInLink is { } link) Clipboard.SetText(link);
        Main.ShowToast("Sign-in link copied. Paste it into a browser on this computer.");
    }

    [RelayCommand]
    private void CopyDeviceCode()
    {
        if (DeviceCode is { } code) Clipboard.SetText(code);
    }

    [RelayCommand]
    private void OpenDeviceUrl()
    {
        if (DeviceUrl is { } url) MainViewModel.OpenUrl(url);
    }

    [RelayCommand]
    private void SignOut()
    {
        TokenStore.Delete(Model.Id);
        Changed(nameof(SignedInAccount), nameof(IsSignedIn), nameof(SignInButtonText));
    }
}

public abstract partial class EmailChannelViewModel(MainViewModel main, EmailChannelConfig model) : ChannelViewModel(main, model)
{
    private EmailChannelConfig Email => (EmailChannelConfig)Model;
    public override string Glyph => "";

    public string Recipients
    {
        get => Email.Recipients;
        set
        {
            Email.Recipients = value;
            Changed(nameof(Recipients));
        }
    }
}

public sealed partial class GmailChannelViewModel(MainViewModel main, GmailChannel model) : OAuthChannelViewModel(main, model)
{
    public override string KindLabel => "Gmail";
    public override string Summary => UseOAuth ? SignedInAccount ?? "Not signed in" : string.IsNullOrWhiteSpace(model.Account) ? "Not configured" : model.Account;
    public bool GoogleConfigured => Main.Config.OAuth.GoogleConfigured;
    protected override OAuthProvider Provider => OAuthProvider.Google;
    protected override string? LoginHint => model.Account;

    public bool UseOAuth
    {
        get => model.AuthMode == GmailAuthMode.OAuth;
        set
        {
            model.AuthMode = value ? GmailAuthMode.OAuth : GmailAuthMode.AppPassword;
            Changed(nameof(UseOAuth), nameof(UseAppPassword));
        }
    }

    public bool UseAppPassword
    {
        get => !UseOAuth;
        set => UseOAuth = !value;
    }

    public string Account
    {
        get => model.Account;
        set
        {
            model.Account = value.Trim();
            Changed(nameof(Account));
        }
    }

    public string AppPassword
    {
        get => SecretProtector.Unprotect(model.AppPassword);
        set
        {
            model.AppPassword = SecretProtector.Protect(value.Replace(" ", ""));
            Changed(nameof(AppPassword));
        }
    }

    protected override void OnSignedIn(StoredToken token) => Account = token.Account;

    [RelayCommand]
    private static void OpenAppPasswords() => MainViewModel.OpenUrl("https://myaccount.google.com/apppasswords");

    [RelayCommand]
    private static void OpenTwoStep() => MainViewModel.OpenUrl("https://myaccount.google.com/signinoptions/twosv");
}

public sealed partial class MicrosoftChannelViewModel(MainViewModel main, MicrosoftChannel model) : OAuthChannelViewModel(main, model)
{
    public override string KindLabel => "Outlook.com / Microsoft 365";
    public override string Summary => SignedInAccount ?? "Not signed in";
    public bool MicrosoftConfigured => Main.Config.OAuth.MicrosoftConfigured;
    protected override OAuthProvider Provider => OAuthProvider.Microsoft(Main.Config.OAuth.EffectiveMicrosoftTenant);
    protected override string? LoginHint => model.Account;

    protected override void OnSignedIn(StoredToken token) => model.Account = token.Account;
}

public sealed record SmtpProviderOption(string Id, string Name);

/// <summary>Yahoo Mail, iCloud Mail (provider presets) or any other SMTP server.</summary>
public sealed partial class SmtpChannelViewModel(MainViewModel main, SmtpChannel model) : EmailChannelViewModel(main, model)
{
    public override string KindLabel => Preset?.Name ?? "SMTP server";
    public override string Summary => Preset is not null
        ? string.IsNullOrWhiteSpace(model.Username) ? "Not configured" : model.Username
        : string.IsNullOrWhiteSpace(model.Host) ? "Not configured" : $"{model.Host}:{model.Port}";
    public IReadOnlyList<SmtpSecurity> SecurityOptions { get; } = Enum.GetValues<SmtpSecurity>();

    public IReadOnlyList<SmtpProviderOption> ProviderOptions { get; } =
        [.. SmtpProvider.All.Select(p => new SmtpProviderOption(p.Id, p.Name)), new("", "Other SMTP server")];

    public SmtpProvider? Preset => SmtpProvider.Find(model.Provider);
    public bool IsPreset => Preset is not null;
    public string AddressLabel => Preset is { } p ? $"{p.Name} address" : "";
    public string ServerSummary => $"Sends through {model.Host}, port {model.Port}.";

    public string ProviderId
    {
        get => model.Provider;
        set
        {
            model.ApplyProvider(SmtpProvider.Find(value));
            Changed(nameof(ProviderId), nameof(Preset), nameof(IsPreset), nameof(KindLabel), nameof(AddressLabel), nameof(ServerSummary),
                nameof(Host), nameof(Port), nameof(Security), nameof(From));
        }
    }

    [RelayCommand]
    private void OpenAccountPage()
    {
        if (Preset is { } p) MainViewModel.OpenUrl(p.AccountPageUrl);
    }

    public string Host { get => model.Host; set { model.Host = value.Trim(); Changed(nameof(Host)); } }
    public int Port { get => model.Port; set { model.Port = value; Changed(nameof(Port)); } }
    public SmtpSecurity Security { get => model.Security; set { model.Security = value; Changed(nameof(Security)); } }
    public string Username { get => model.Username; set { model.Username = value.Trim(); Changed(nameof(Username)); } }
    public string From { get => model.From; set { model.From = value.Trim(); Changed(nameof(From)); } }

    /// <summary>App passwords are often copied with spaces between the groups; providers expect them without.</summary>
    public string Password
    {
        get => SecretProtector.Unprotect(model.Password);
        set { model.Password = SecretProtector.Protect(IsPreset ? value.Replace(" ", "") : value); Changed(nameof(Password)); }
    }
}

/// <summary>Teams, Slack and Discord: just a webhook URL.</summary>
public sealed partial class WebhookUrlChannelViewModel(MainViewModel main, ChannelConfig model) : ChannelViewModel(main, model)
{
    public override string KindLabel => Model.Kind switch
    {
        ChannelKind.Teams => "Microsoft Teams",
        ChannelKind.Slack => "Slack",
        _ => "Discord",
    };

    public override string Summary => Mask(Raw);

    private string Raw => Model switch
    {
        TeamsChannel t => t.WebhookUrl,
        SlackChannel s => s.WebhookUrl,
        DiscordChannel d => d.WebhookUrl,
        _ => "",
    };

    public string WebhookUrl
    {
        get => SecretProtector.Unprotect(Raw);
        set
        {
            var p = SecretProtector.Protect(value.Trim());
            switch (Model)
            {
                case TeamsChannel t: t.WebhookUrl = p; break;
                case SlackChannel s: s.WebhookUrl = p; break;
                case DiscordChannel d: d.WebhookUrl = p; break;
            }
            Changed(nameof(WebhookUrl));
        }
    }

    public string Instructions => Model.Kind switch
    {
        ChannelKind.Teams =>
            "1. In Teams, open the channel, click ⋯ (More options) → Workflows.\n" +
            "2. Choose the template \"Send webhook alerts to a channel\" (or \"Post to a channel when a webhook request is received\").\n" +
            "3. Finish the wizard and copy the URL it shows. Paste it below.",
        ChannelKind.Slack =>
            "1. Go to api.slack.com/apps, sign in and click Create an App → Blank app. Name it (e.g. Service Monitor), pick your workspace and click Continue.\n" +
            "2. Under \"What it can do in Slack?\" search for incoming-webhook, click it, then click Continue. Click Continue again on the events step without adding any.\n" +
            "3. Click Create and Install, pick the channel for the alerts and click Allow.\n" +
            "4. Click Go to App Settings → Incoming Webhooks. In the Webhook URL table, click Copy on the channel's row (not the Copy under the curl example). Paste it below.",
        _ =>
            "1. In Discord, open Server Settings → Integrations → Webhooks → New Webhook.\n" +
            "2. Pick the channel, then click Copy Webhook URL. Paste it below.",
    };
}

public sealed partial class NtfyChannelViewModel(MainViewModel main, NtfyChannel model) : ChannelViewModel(main, model)
{
    public override string KindLabel => "Phone push (ntfy)";
    public override string Glyph => "";
    public override string Summary => string.IsNullOrWhiteSpace(model.Topic) ? "No topic yet" : $"Topic {model.Topic}";

    public string ServerUrl { get => model.ServerUrl; set { model.ServerUrl = value.Trim(); Changed(nameof(ServerUrl), nameof(SubscribeUrl), nameof(SubscribeLinkText)); } }
    public string Topic { get => model.Topic; set { model.Topic = value.Trim(); Changed(nameof(Topic), nameof(SubscribeUrl), nameof(SubscribeLinkText)); } }
    public string SubscribeLinkText => $"Or subscribe in a web browser: {SubscribeUrl}";

    public string AccessToken
    {
        get => SecretProtector.Unprotect(model.AccessToken);
        set { model.AccessToken = SecretProtector.Protect(value.Trim()); Changed(nameof(AccessToken)); }
    }

    public string SubscribeUrl => $"{(string.IsNullOrWhiteSpace(model.ServerUrl) ? "https://ntfy.sh" : model.ServerUrl.TrimEnd('/'))}/{model.Topic}";

    /// <summary>On the public ntfy.sh server anyone who knows the topic can read it, so make it unguessable.</summary>
    [RelayCommand]
    private void GenerateTopic()
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyz23456789";
        var suffix = new string(RandomNumberGenerator.GetItems<char>(alphabet, 12));
        var site = new string(Core.AppInfo.MachineName.ToLowerInvariant().Where(char.IsLetterOrDigit).Take(12).ToArray());
        Topic = $"svcmon-{site}-{suffix}";
    }

    [RelayCommand]
    private void OpenSubscribe() => MainViewModel.OpenUrl(SubscribeUrl);
}

public sealed partial class TelegramChannelViewModel(MainViewModel main, TelegramChannel model) : ChannelViewModel(main, model)
{
    public override string KindLabel => "Telegram";
    public override string Summary => string.IsNullOrWhiteSpace(model.ChatId) ? "Not configured" : $"Chat {model.ChatId}";

    public string BotToken
    {
        get => SecretProtector.Unprotect(model.BotToken);
        set { model.BotToken = SecretProtector.Protect(value.Trim()); Changed(nameof(BotToken)); }
    }

    public string ChatId { get => model.ChatId; set { model.ChatId = value.Trim(); Changed(nameof(ChatId)); } }

    [ObservableProperty] private string? _detectMessage;

    /// <summary>Reads the bot's recent updates to find the chat that just messaged it.</summary>
    [RelayCommand]
    private async Task DetectChatIdAsync()
    {
        DetectMessage = null;
        if (BotToken.Length == 0)
        {
            DetectMessage = "Enter the bot token first.";
            return;
        }
        try
        {
            var json = await Main.Http.GetFromJsonAsync<JsonObject>($"https://api.telegram.org/bot{BotToken}/getUpdates");
            var chats = (json?["result"] as JsonArray ?? [])
                .Select(u => u?["message"]?["chat"] ?? u?["my_chat_member"]?["chat"] ?? u?["channel_post"]?["chat"])
                .Where(c => c is not null)
                .Select(c => (Id: c!["id"]!.ToString(), Title: (string?)c["title"] ?? (string?)c["username"] ?? (string?)c["first_name"] ?? ""))
                .DistinctBy(c => c.Id)
                .ToList();
            if (chats.Count == 0)
            {
                DetectMessage = "No messages found. Send any message to your bot (or add it to a group and post there), then click again.";
                return;
            }
            ChatId = chats[^1].Id;
            DetectMessage = chats.Count == 1 ? $"Found chat \"{chats[0].Title}\"." : $"Found {chats.Count} chats; using the most recent (\"{chats[^1].Title}\").";
        }
        catch (Exception ex)
        {
            DetectMessage = $"Could not reach Telegram: {ex.Message}";
        }
    }
}

public sealed partial class WebhookChannelViewModel(MainViewModel main, WebhookChannel model) : ChannelViewModel(main, model)
{
    public override string KindLabel => "Webhook (JSON)";
    public override string Glyph => "";
    public override string Summary => Mask(model.Url);

    public string Url
    {
        get => SecretProtector.Unprotect(model.Url);
        set { model.Url = SecretProtector.Protect(value.Trim()); Changed(nameof(Url)); }
    }

    public string HeaderName { get => model.HeaderName; set { model.HeaderName = value.Trim(); Changed(nameof(HeaderName)); } }

    public string HeaderValue
    {
        get => SecretProtector.Unprotect(model.HeaderValue);
        set { model.HeaderValue = SecretProtector.Protect(value); Changed(nameof(HeaderValue)); }
    }
}
