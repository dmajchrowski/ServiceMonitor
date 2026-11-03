using System.Text.Json.Serialization;

namespace ServiceMonitor.Core.Configuration;

public enum ChannelKind { Gmail, Microsoft, Smtp, Teams, Slack, Discord, Ntfy, Telegram, Webhook }

/// <summary>
/// A place notifications are delivered to. Properties documented as "protected" hold DPAPI-encrypted text
/// (see <see cref="Security.SecretProtector"/>); a plaintext value typed by hand is also accepted.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GmailChannel), "gmail")]
[JsonDerivedType(typeof(MicrosoftChannel), "microsoft")]
[JsonDerivedType(typeof(SmtpChannel), "smtp")]
[JsonDerivedType(typeof(TeamsChannel), "teams")]
[JsonDerivedType(typeof(SlackChannel), "slack")]
[JsonDerivedType(typeof(DiscordChannel), "discord")]
[JsonDerivedType(typeof(NtfyChannel), "ntfy")]
[JsonDerivedType(typeof(TelegramChannel), "telegram")]
[JsonDerivedType(typeof(WebhookChannel), "webhook")]
public abstract class ChannelConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    [JsonIgnore] public abstract ChannelKind Kind { get; }
    [JsonIgnore] public virtual bool UsesOAuth => false;

    public static ChannelConfig Create(ChannelKind kind) => kind switch
    {
        ChannelKind.Gmail => new GmailChannel { Name = "Gmail" },
        ChannelKind.Microsoft => new MicrosoftChannel { Name = "Outlook.com / Microsoft 365" },
        ChannelKind.Smtp => new SmtpChannel { Name = "SMTP server" },
        ChannelKind.Teams => new TeamsChannel { Name = "Microsoft Teams" },
        ChannelKind.Slack => new SlackChannel { Name = "Slack" },
        ChannelKind.Discord => new DiscordChannel { Name = "Discord" },
        ChannelKind.Ntfy => new NtfyChannel { Name = "Phone push (ntfy)" },
        ChannelKind.Telegram => new TelegramChannel { Name = "Telegram" },
        ChannelKind.Webhook => new WebhookChannel { Name = "Webhook" },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Replaces every protected property with <paramref name="transform"/>(value), e.g. to encrypt or decrypt them.</summary>
    public void TransformSecrets(Func<string, string> transform)
    {
        switch (this)
        {
            case GmailChannel g: g.AppPassword = transform(g.AppPassword); break;
            case SmtpChannel s: s.Password = transform(s.Password); break;
            case TeamsChannel t: t.WebhookUrl = transform(t.WebhookUrl); break;
            case SlackChannel s: s.WebhookUrl = transform(s.WebhookUrl); break;
            case DiscordChannel d: d.WebhookUrl = transform(d.WebhookUrl); break;
            case NtfyChannel n: n.AccessToken = transform(n.AccessToken); break;
            case TelegramChannel t: t.BotToken = transform(t.BotToken); break;
            case WebhookChannel w: w.Url = transform(w.Url); w.HeaderValue = transform(w.HeaderValue); break;
        }
    }
}

public abstract class EmailChannelConfig : ChannelConfig
{
    /// <summary>Comma or semicolon separated. Empty = send to the sending account itself.</summary>
    public string Recipients { get; set; } = "";

    public IReadOnlyList<string> RecipientList(string fallback) => SplitAddresses(Recipients) is { Count: > 0 } list
        ? list
        : string.IsNullOrWhiteSpace(fallback) ? [] : [fallback.Trim()];

    public static List<string> SplitAddresses(string? text) =>
        (text ?? "").Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public enum GmailAuthMode { AppPassword, OAuth }

public sealed class GmailChannel : EmailChannelConfig
{
    public override ChannelKind Kind => ChannelKind.Gmail;
    public override bool UsesOAuth => AuthMode == GmailAuthMode.OAuth;
    public GmailAuthMode AuthMode { get; set; } = GmailAuthMode.AppPassword;
    public string Account { get; set; } = "";
    /// <summary>Protected. 16-character Google app password.</summary>
    public string AppPassword { get; set; } = "";
}

/// <summary>Outlook.com, Hotmail, Live and Microsoft 365 mailboxes, sent through Microsoft Graph with OAuth.</summary>
public sealed class MicrosoftChannel : EmailChannelConfig
{
    public override ChannelKind Kind => ChannelKind.Microsoft;
    public override bool UsesOAuth => true;
    /// <summary>Filled in after a successful sign-in. The tokens live in the token store, not here.</summary>
    public string Account { get; set; } = "";
}

public enum SmtpSecurity { Auto, StartTls, SslOnConnect, None }

public sealed class SmtpChannel : EmailChannelConfig
{
    public override ChannelKind Kind => ChannelKind.Smtp;
    /// <summary>Id of a <see cref="SmtpProvider"/> preset (e.g. "yahoo"), or empty for a custom server.</summary>
    public string Provider { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;
    public string Username { get; set; } = "";
    /// <summary>Protected.</summary>
    public string Password { get; set; } = "";
    /// <summary>Sender address. Defaults to <see cref="Username"/>.</summary>
    public string From { get; set; } = "";

    /// <summary>Switches to a provider preset and takes its server settings; null switches back to a custom server.</summary>
    public void ApplyProvider(SmtpProvider? provider)
    {
        Provider = provider?.Id ?? "";
        if (provider is null) return;
        Host = provider.Host;
        Port = provider.Port;
        Security = provider.Security;
        From = "";
    }
}

public sealed class TeamsChannel : ChannelConfig
{
    public override ChannelKind Kind => ChannelKind.Teams;
    /// <summary>Protected. Teams "Workflows" webhook URL (or a legacy incoming webhook URL).</summary>
    public string WebhookUrl { get; set; } = "";
}

public sealed class SlackChannel : ChannelConfig
{
    public override ChannelKind Kind => ChannelKind.Slack;
    /// <summary>Protected.</summary>
    public string WebhookUrl { get; set; } = "";
}

public sealed class DiscordChannel : ChannelConfig
{
    public override ChannelKind Kind => ChannelKind.Discord;
    /// <summary>Protected.</summary>
    public string WebhookUrl { get; set; } = "";
}

/// <summary>Push notifications to phones through ntfy (https://ntfy.sh or a self-hosted server).</summary>
public sealed class NtfyChannel : ChannelConfig
{
    public override ChannelKind Kind => ChannelKind.Ntfy;
    public string ServerUrl { get; set; } = "https://ntfy.sh";
    public string Topic { get; set; } = "";
    /// <summary>Protected. Optional access token for protected topics.</summary>
    public string AccessToken { get; set; } = "";
}

public sealed class TelegramChannel : ChannelConfig
{
    public override ChannelKind Kind => ChannelKind.Telegram;
    /// <summary>Protected.</summary>
    public string BotToken { get; set; } = "";
    public string ChatId { get; set; } = "";
}

/// <summary>Generic JSON POST for RMM/PSA tools, PagerDuty-style relays, Power Automate, n8n, etc.</summary>
public sealed class WebhookChannel : ChannelConfig
{
    public override ChannelKind Kind => ChannelKind.Webhook;
    /// <summary>Protected.</summary>
    public string Url { get; set; } = "";
    public string HeaderName { get; set; } = "";
    /// <summary>Protected.</summary>
    public string HeaderValue { get; set; } = "";
}
