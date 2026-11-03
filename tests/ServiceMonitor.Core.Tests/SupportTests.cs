using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ServiceMonitor.Core.Configuration;
using ServiceMonitor.Core.Monitoring;
using ServiceMonitor.Core.Notifications;
using ServiceMonitor.Core.OAuth;
using ServiceMonitor.Core.Security;
using ServiceMonitor.Core.Windows;

namespace ServiceMonitor.Core.Tests;

public class ImagePathParserTests
{
    [Theory]
    [InlineData("\"C:\\Program Files (x86)\\Contoso\\Billing\\BillingSvc.exe\"", @"C:\Program Files (x86)\Contoso\Billing\BillingSvc.exe")]
    [InlineData(@"C:\Program Files (x86)\Contoso\Sync Tool\ContosoSync.exe", @"C:\Program Files (x86)\Contoso\Sync Tool\ContosoSync.exe")]
    [InlineData("\"C:\\Program Files\\Service Monitor\\ServiceMonitor.exe\" --service", @"C:\Program Files\Service Monitor\ServiceMonitor.exe")]
    [InlineData(@"C:\Tools\agent.exe -run fast", @"C:\Tools\agent.exe")]
    public void Extracts_the_executable(string imagePath, string expected) =>
        Assert.Equal(expected, ImagePathParser.GetExecutable(imagePath));

    [Fact]
    public void Expands_environment_variables() =>
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\svchost.exe"),
            ImagePathParser.GetExecutable(@"%SystemRoot%\System32\svchost.exe -k netsvcs -p"), ignoreCase: true);
}

public class ConfigTests
{
    [Fact]
    public void Channels_round_trip_through_json_with_their_type()
    {
        var config = new MonitorConfig { SiteName = "Site" };
        config.Services.Add(new MonitoredService { ServiceName = "ContosoBilling", AutoRestart = true, PortCheck = new PortCheck { Port = 8080 } });
        config.Channels.Add(new GmailChannel { Account = "a@gmail.com", AppPassword = SecretProtector.Protect("abcdabcdabcdabcd"), Recipients = "x@y.com" });
        config.Channels.Add(new MicrosoftChannel { Account = "b@outlook.com" });
        config.Channels.Add(new NtfyChannel { Topic = "t" });

        var json = ConfigStore.Serialize(config);
        Assert.DoesNotContain("abcdabcdabcdabcd", json);
        var back = ConfigStore.Deserialize(json);

        Assert.Equal(["Gmail", "Microsoft", "Ntfy"], back.Channels.Select(c => c.Kind.ToString()));
        Assert.Equal("abcdabcdabcdabcd", SecretProtector.Unprotect(((GmailChannel)back.Channels[0]).AppPassword));
        Assert.Equal(8080, back.Services[0].PortCheck!.Port);
    }

    [Fact]
    public void Hand_written_config_with_type_anywhere_and_plaintext_secret_is_accepted()
    {
        const string json = """
            {
              // written by a deployment script
              "siteName": "Clinic",
              "services": [ { "serviceName": "ContosoSync" } ],
              "channels": [ { "name": "Relay", "host": "smtp.local", "type": "smtp", "password": "plain", "security": "None" } ],
            }
            """;
        var config = ConfigStore.Deserialize(json);
        var smtp = Assert.IsType<SmtpChannel>(Assert.Single(config.Channels));
        Assert.Equal("plain", SecretProtector.Unprotect(smtp.Password));
        Assert.Equal(SmtpSecurity.None, smtp.Security);
        Assert.Equal(10, config.Settings.PollIntervalSeconds);
    }

    [Fact]
    public void Recipient_list_falls_back_to_the_sender()
    {
        var gmail = new GmailChannel { Recipients = " a@x.com; b@x.com,a@x.com " };
        Assert.Equal(["a@x.com", "b@x.com"], gmail.RecipientList("me@gmail.com"));
        Assert.Equal(["me@gmail.com"], new GmailChannel().RecipientList("me@gmail.com"));
    }
}

public class SmtpProviderTests
{
    [Fact]
    public void Preset_fills_in_the_server_and_round_trips()
    {
        var smtp = new SmtpChannel { Host = "relay.local", Port = 25, Security = SmtpSecurity.None, From = "old@relay.local" };
        smtp.ApplyProvider(SmtpProvider.Yahoo);

        Assert.Equal(("yahoo", "smtp.mail.yahoo.com", 465, SmtpSecurity.SslOnConnect, ""), (smtp.Provider, smtp.Host, smtp.Port, smtp.Security, smtp.From));

        var back = (SmtpChannel)ConfigStore.Deserialize(ConfigStore.Serialize(new MonitorConfig { Channels = { smtp } })).Channels[0];
        Assert.Same(SmtpProvider.Yahoo, SmtpProvider.Find(back.Provider));
    }

    [Fact]
    public void Switching_back_to_a_custom_server_keeps_the_settings()
    {
        var smtp = new SmtpChannel();
        smtp.ApplyProvider(SmtpProvider.ICloud);
        smtp.ApplyProvider(null);

        Assert.Equal(("", "smtp.mail.me.com", 587), (smtp.Provider, smtp.Host, smtp.Port));
        Assert.Null(SmtpProvider.Find(smtp.Provider));
        Assert.Same(SmtpProvider.ICloud, SmtpProvider.Find("ICLOUD"));
    }

    [Fact]
    public async Task Preset_without_an_address_fails_before_connecting()
    {
        using var http = new HttpClient();
        var sender = new ChannelSender(http, new OAuthClient(http));
        var smtp = new SmtpChannel { Provider = "yahoo" }; // as in a hand-written config: no server settings
        var config = new MonitorConfig();

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => sender.SendAsync(smtp, config, SystemNotifications.Test(config, "Yahoo"), CancellationToken.None));
        Assert.Equal("Enter the Yahoo Mail address.", ex.Message);
        Assert.False(ex.Retryable);
    }
}

public class TcpConnectorTests
{
    [Fact]
    public void Alternates_address_families_starting_with_the_preferred_one()
    {
        IPAddress v6a = IPAddress.Parse("fe80::1"), v6b = IPAddress.Parse("2001:db8::1");
        IPAddress v4a = IPAddress.Parse("192.0.2.10"), v4b = IPAddress.Parse("10.0.0.5");
        Assert.Equal(new[] { v6a, v4a, v6b, v4b }, TcpConnector.Interleave([v6a, v6b, v4a, v4b]));
        Assert.Equal(new[] { v4a, v6a, v4b }, TcpConnector.Interleave([v4a, v4b, v6a]));
    }

    [Fact]
    public async Task Uses_the_next_address_when_the_first_never_answers()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var timer = Stopwatch.StartNew();
        // 192.0.2.1 is reserved for documentation, so nothing there answers (like an IPv6 link-local address a mail server does not listen on).
        using var socket = await TcpConnector.ConnectAsync([IPAddress.Parse("192.0.2.1"), IPAddress.Loopback], port, CancellationToken.None);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, port), socket.RemoteEndPoint);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"Took {timer.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public async Task Connects_by_name_when_only_one_family_listens()
    {
        // IPv4 only, while "localhost" resolves to ::1 first.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var socket = await TcpConnector.ConnectAsync("localhost", ((IPEndPoint)listener.LocalEndpoint).Port, CancellationToken.None);
        Assert.True(socket.Connected);
    }

    [Fact]
    public async Task Reports_a_socket_error_when_no_address_answers()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await Assert.ThrowsAsync<SocketException>(() => TcpConnector.ConnectAsync([IPAddress.Loopback], port, CancellationToken.None));
    }
}

public class RendererTests
{
    private static Notification Sample() => new Notification
    {
        Kind = NotificationKind.Alert,
        Severity = Severity.Critical,
        Title = "Contoso <Sync> Service crashed",
        Summary = "The service terminated unexpectedly & needs attention.",
        SiteName = "Contoso",
        Events = [new EventSummary(DateTimeOffset.Now, "Application", ".NET Runtime", 1026, "Exception Info: System.InvalidOperationException")],
    }.AddFact("Service", "Contoso Sync Service (ContosoSync)");

    [Fact]
    public void Html_is_encoded_and_contains_the_details()
    {
        var html = NotificationRenderer.Html(Sample());
        Assert.Contains("Contoso &lt;Sync&gt; Service crashed", html);
        Assert.Contains("terminated unexpectedly &amp; needs attention", html);
        Assert.Contains("System.InvalidOperationException", html);
        Assert.Contains("#C42B1C", html);
    }

    [Fact]
    public void Teams_payload_is_an_adaptive_card_message()
    {
        var json = NotificationRenderer.Teams(Sample());
        Assert.Equal("message", (string?)json["type"]);
        var card = json["attachments"]![0]!;
        Assert.Equal("application/vnd.microsoft.card.adaptive", (string?)card["contentType"]);
        Assert.Equal("AdaptiveCard", (string?)card["content"]!["type"]);
    }

    [Fact]
    public void Ntfy_uses_urgent_priority_for_critical_alerts()
    {
        var json = NotificationRenderer.Ntfy(Sample(), "topic1");
        Assert.Equal(5, (int)json["priority"]!);
        Assert.Equal("topic1", (string?)json["topic"]);
    }

    [Fact]
    public void Discord_and_slack_respect_size_limits()
    {
        var n = Sample();
        n.Events.Add(new EventSummary(DateTimeOffset.Now, "System", ScmEvents.Provider, 7034, new string('x', 10_000)));
        Assert.True(((string)NotificationRenderer.Discord(n)["embeds"]![0]!["description"]!).Length <= 4096);
        var slack = NotificationRenderer.Slack(n).ToJsonString();
        Assert.DoesNotContain(new string('x', 3000), slack);
    }

    [Fact]
    public void Webhook_payload_has_stable_field_names()
    {
        var json = NotificationRenderer.Webhook(Sample());
        foreach (var key in new[] { "id", "kind", "severity", "title", "summary", "machine", "site", "facts", "events", "time" })
            Assert.True(json.ContainsKey(key), key);
        Assert.Equal("alert", (string?)json["kind"]);
    }
}

public class OAuthTests
{
    [Fact]
    public void Reads_the_account_from_an_id_token()
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var ms = $"{B64("{\"alg\":\"none\"}")}.{B64("{\"preferred_username\":\"tech@outlook.com\"}")}.";
        var google = $"{B64("{}")}.{B64("{\"email\":\"alerts@gmail.com\",\"sub\":\"1\"}")}.sig";
        Assert.Equal("tech@outlook.com", OAuthClient.ReadAccount(ms));
        Assert.Equal("alerts@gmail.com", OAuthClient.ReadAccount(google));
        Assert.Null(OAuthClient.ReadAccount("garbage"));
    }

    [Fact]
    public void Microsoft_requests_only_mail_send_and_identity_scopes()
    {
        var scopes = OAuthProvider.Microsoft("common").Scopes.Split(' ');
        Assert.Contains("https://graph.microsoft.com/Mail.Send", scopes);
        Assert.Contains("offline_access", scopes);
        Assert.DoesNotContain(scopes, s => s.Contains("Mail.Read"));
        Assert.Contains("gmail.send", OAuthProvider.Google.Scopes);
    }
}

public class TimeTextTests
{
    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(185, "3 min")]
    [InlineData(3900, "1 h 05 min")]
    [InlineData(7200, "2 h")]
    [InlineData(90000, "1 d 1 h")]
    public void Durations_read_naturally(int seconds, string expected) =>
        Assert.Equal(expected, TimeText.Duration(TimeSpan.FromSeconds(seconds)));
}
