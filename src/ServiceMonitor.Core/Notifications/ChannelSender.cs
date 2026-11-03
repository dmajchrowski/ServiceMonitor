using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using ServiceMonitor.Core.Configuration;
using ServiceMonitor.Core.OAuth;
using ServiceMonitor.Core.Security;

namespace ServiceMonitor.Core.Notifications;

/// <summary>A delivery failure with a message a technician can act on.</summary>
public sealed class DeliveryException(string message, bool retryable, Exception? inner = null) : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
}

/// <summary>Delivers one notification to one channel.</summary>
public sealed class ChannelSender(HttpClient http, OAuthClient oauth)
{
    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            // Same as for SMTP: a local ntfy server or webhook reached by PC name shouldn't wait on an address that never answers.
            ConnectCallback = async (context, cancellationToken) =>
                new NetworkStream(await TcpConnector.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken), ownsSocket: true),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ServiceMonitor", AppInfo.Version));
        return client;
    }

    /// <summary>Sends and returns a short description of what happened, e.g. "Email sent to a@b.com".</summary>
    public async Task<string> SendAsync(ChannelConfig channel, MonitorConfig config, Notification n, CancellationToken cancellationToken)
    {
        try
        {
            return channel switch
            {
                GmailChannel { AuthMode: GmailAuthMode.AppPassword } g => await SendGmailAppPasswordAsync(g, n, cancellationToken),
                GmailChannel g => await SendGmailApiAsync(g, config, n, cancellationToken),
                MicrosoftChannel m => await SendGraphAsync(m, config, n, cancellationToken),
                SmtpChannel s => await SendSmtpAsync(s, n, cancellationToken),
                TeamsChannel t => await PostJsonAsync(Secret(t.WebhookUrl, "Teams webhook URL"), NotificationRenderer.Teams(n), null, cancellationToken, "Posted to Teams"),
                SlackChannel s => await PostJsonAsync(Secret(s.WebhookUrl, "Slack webhook URL"), NotificationRenderer.Slack(n), null, cancellationToken, "Posted to Slack"),
                DiscordChannel d => await PostJsonAsync(Secret(d.WebhookUrl, "Discord webhook URL"), NotificationRenderer.Discord(n), null, cancellationToken, "Posted to Discord"),
                NtfyChannel nt => await SendNtfyAsync(nt, n, cancellationToken),
                TelegramChannel tg => await SendTelegramAsync(tg, n, cancellationToken),
                WebhookChannel w => await SendWebhookAsync(w, n, cancellationToken),
                _ => throw new DeliveryException($"Unsupported channel type {channel.Kind}", false),
            };
        }
        catch (ReauthorizationRequiredException)
        {
            throw;
        }
        catch (DeliveryException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeliveryException("Timed out talking to the server.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            throw new DeliveryException($"Network error: {ex.Message}", retryable: true, ex);
        }
        catch (AuthenticationException ex)
        {
            throw new DeliveryException(FriendlySmtpAuthError(channel, ex), retryable: false, ex);
        }
        catch (SmtpCommandException ex)
        {
            throw new DeliveryException($"Mail server refused the message: {ex.Message}", retryable: (int)ex.StatusCode >= 400 && (int)ex.StatusCode < 500, ex);
        }
        catch (Exception ex) when (ex is SmtpProtocolException or System.Net.Sockets.SocketException or IOException or SslHandshakeException)
        {
            throw new DeliveryException($"Could not talk to the mail server: {ex.Message}", retryable: true, ex);
        }
        catch (OAuthException ex)
        {
            throw new DeliveryException(ex.Message, retryable: true, ex);
        }
    }

    // ------------------------------------------------------------------ email

    private static MimeMessage BuildMessage(string from, IReadOnlyList<string> to, Notification n)
    {
        if (to.Count == 0) throw new DeliveryException("No recipients configured.", false);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress($"{AppInfo.ProductName} ({n.MachineName})", from));
        foreach (var address in to) message.To.Add(MailboxAddress.Parse(address));
        message.Subject = n.Subject;
        if (n.Severity == Severity.Critical) message.Priority = MessagePriority.Urgent;
        message.Body = new BodyBuilder { TextBody = NotificationRenderer.PlainText(n), HtmlBody = NotificationRenderer.Html(n) }.ToMessageBody();
        return message;
    }

    private static async Task<string> SendSmtpCoreAsync(string host, int port, SecureSocketOptions security, string? user, string? password,
        MimeMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient { Timeout = 30_000 };
        var socket = await TcpConnector.ConnectAsync(host, port, cancellationToken);
        try
        {
            await client.ConnectAsync(socket, host, port, security, cancellationToken);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        if (!string.IsNullOrEmpty(user)) await client.AuthenticateAsync(user, password ?? "", cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
        return $"Email sent to {string.Join(", ", message.To.Mailboxes.Select(m => m.Address))}";
    }

    private static Task<string> SendGmailAppPasswordAsync(GmailChannel g, Notification n, CancellationToken cancellationToken)
    {
        var account = g.Account.Trim();
        if (account.Length == 0) throw new DeliveryException("Enter the Gmail address.", false);
        var password = SecretProtector.Unprotect(g.AppPassword).Replace(" ", "");
        if (password.Length == 0) throw new DeliveryException("Enter the 16-letter Gmail app password.", false);
        var message = BuildMessage(account, g.RecipientList(account), n);
        return SendSmtpCoreAsync("smtp.gmail.com", 587, SecureSocketOptions.StartTls, account, password, message, cancellationToken);
    }

    private static Task<string> SendSmtpAsync(SmtpChannel s, Notification n, CancellationToken cancellationToken)
    {
        // A provider preset always uses the provider's own server settings, so a hand-written config only needs the address and password.
        var provider = SmtpProvider.Find(s.Provider);
        var (host, port, smtpSecurity) = provider is null ? (s.Host.Trim(), s.Port, s.Security) : (provider.Host, provider.Port, provider.Security);
        if (host.Length == 0) throw new DeliveryException("Enter the SMTP server name.", false);
        if (provider is not null)
        {
            if (string.IsNullOrWhiteSpace(s.Username)) throw new DeliveryException($"Enter the {provider.Name} address.", false);
            if (SecretProtector.Unprotect(s.Password).Length == 0) throw new DeliveryException("Enter the app password.", false);
        }
        var from = string.IsNullOrWhiteSpace(s.From) ? s.Username.Trim() : s.From.Trim();
        if (from.Length == 0) throw new DeliveryException("Enter a From address.", false);
        var security = smtpSecurity switch
        {
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto,
        };
        var message = BuildMessage(from, s.RecipientList(from), n);
        return SendSmtpCoreAsync(host, port <= 0 ? 587 : port, security, s.Username.Trim(), SecretProtector.Unprotect(s.Password), message, cancellationToken);
    }

    private async Task<string> SendGmailApiAsync(GmailChannel g, MonitorConfig config, Notification n, CancellationToken cancellationToken)
    {
        var token = await oauth.GetValidTokenAsync(g, config.OAuth, forceRefresh: false, cancellationToken);
        var from = string.IsNullOrWhiteSpace(token.Account) ? g.Account : token.Account;
        var message = BuildMessage(from, g.RecipientList(from), n);

        using var buffer = new MemoryStream();
        await message.WriteToAsync(buffer, cancellationToken);
        var raw = Convert.ToBase64String(buffer.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://gmail.googleapis.com/gmail/v1/users/me/messages/send")
        {
            Content = Json(new JsonObject { ["raw"] = raw }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Gmail", cancellationToken);
        return $"Email sent to {string.Join(", ", message.To.Mailboxes.Select(m => m.Address))} via Gmail ({from})";
    }

    private async Task<string> SendGraphAsync(MicrosoftChannel m, MonitorConfig config, Notification n, CancellationToken cancellationToken)
    {
        var token = await oauth.GetValidTokenAsync(m, config.OAuth, forceRefresh: false, cancellationToken);
        var from = string.IsNullOrWhiteSpace(token.Account) ? m.Account : token.Account;
        var recipients = m.RecipientList(from);
        if (recipients.Count == 0) throw new DeliveryException("No recipients configured.", false);

        var to = new JsonArray();
        foreach (var address in recipients) to.Add(new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = address } });
        var payload = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["subject"] = n.Subject,
                ["importance"] = n.Severity == Severity.Critical ? "high" : "normal",
                ["body"] = new JsonObject { ["contentType"] = "HTML", ["content"] = NotificationRenderer.Html(n) },
                ["toRecipients"] = to,
            },
            ["saveToSentItems"] = true,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/sendMail") { Content = Json(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Microsoft", cancellationToken);
        return $"Email sent to {string.Join(", ", recipients)} via Microsoft ({from})";
    }

    private static string FriendlySmtpAuthError(ChannelConfig channel, AuthenticationException ex) => channel switch
    {
        GmailChannel => "Gmail did not accept the app password. Check the Gmail address, make sure 2-Step Verification is on, " +
                        "and paste a 16-letter app password from myaccount.google.com/apppasswords (not the normal account password).",
        SmtpChannel s when SmtpProvider.Find(s.Provider) is { } provider => provider.AuthFailureHint,
        SmtpChannel s when s.Host.Contains("outlook", StringComparison.OrdinalIgnoreCase) || s.Host.Contains("office365", StringComparison.OrdinalIgnoreCase) =>
            "Microsoft no longer accepts passwords from other programs for Outlook.com, Hotmail and Live accounts. " +
            "Send the alerts from a Gmail, Yahoo or iCloud account instead.",
        _ => $"The mail server rejected the user name or password: {ex.Message}",
    };

    // ------------------------------------------------------------------ chat / push / webhook

    private async Task<string> SendNtfyAsync(NtfyChannel nt, Notification n, CancellationToken cancellationToken)
    {
        var topic = nt.Topic.Trim();
        if (topic.Length == 0) throw new DeliveryException("Enter an ntfy topic.", false);
        var server = string.IsNullOrWhiteSpace(nt.ServerUrl) ? "https://ntfy.sh" : nt.ServerUrl.Trim().TrimEnd('/');
        var token = SecretProtector.Unprotect(nt.AccessToken);
        return await PostJsonAsync(server, NotificationRenderer.Ntfy(n, topic),
            token.Length > 0 ? new AuthenticationHeaderValue("Bearer", token) : null, cancellationToken, $"Push sent to ntfy topic '{topic}'");
    }

    private async Task<string> SendTelegramAsync(TelegramChannel tg, Notification n, CancellationToken cancellationToken)
    {
        var botToken = Secret(tg.BotToken, "Telegram bot token");
        if (string.IsNullOrWhiteSpace(tg.ChatId)) throw new DeliveryException("Enter the Telegram chat ID.", false);
        return await PostJsonAsync($"https://api.telegram.org/bot{botToken}/sendMessage", NotificationRenderer.Telegram(n, tg.ChatId.Trim()),
            null, cancellationToken, "Sent to Telegram");
    }

    private async Task<string> SendWebhookAsync(WebhookChannel w, Notification n, CancellationToken cancellationToken)
    {
        var url = Secret(w.Url, "webhook URL");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = Json(NotificationRenderer.Webhook(n)) };
        if (!string.IsNullOrWhiteSpace(w.HeaderName))
            request.Headers.TryAddWithoutValidation(w.HeaderName.Trim(), SecretProtector.Unprotect(w.HeaderValue));
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Webhook", cancellationToken);
        return $"Webhook delivered (HTTP {(int)response.StatusCode})";
    }

    private async Task<string> PostJsonAsync(string url, JsonObject payload, AuthenticationHeaderValue? auth, CancellationToken cancellationToken, string success)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new DeliveryException("The URL is not valid.", false);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = Json(payload) };
        if (auth is not null) request.Headers.Authorization = auth;
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, uri.Host, cancellationToken);
        return success;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string service, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = body.Length > 400 ? body[..400] : body;
        var code = (int)response.StatusCode;
        var hint = code switch
        {
            401 => " (sign-in rejected – try signing in again)",
            403 => " (permission denied – check the account or webhook)",
            404 => " (URL not found – check the webhook address)",
            _ => "",
        };
        throw new DeliveryException($"{service} returned HTTP {code}{hint}: {detail}", retryable: code is 408 or 429 or >= 500);
    }

    /// <summary>Buffered JSON with a Content-Length header; some webhook receivers reject chunked uploads.</summary>
    private static StringContent Json(JsonObject payload) => new(payload.ToJsonString(), Encoding.UTF8, "application/json");

    private static string Secret(string stored, string what)
    {
        var value = SecretProtector.Unprotect(stored).Trim();
        if (value.Length == 0) throw new DeliveryException($"Enter the {what}.", false);
        return value;
    }
}
