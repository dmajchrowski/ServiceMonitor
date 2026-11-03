using ServiceMonitor.Core.Configuration;
using ServiceMonitor.Core.Security;

namespace ServiceMonitor.Core.Tests;

public class ChannelTransferTests
{
    private static List<ChannelConfig> Channels() =>
    [
        new SmtpChannel { Name = "Mail relay", Host = "mailhost", Port = 25, Username = "alerts@example.com", Password = SecretProtector.Protect("smtp-secret-1") },
        new TelegramChannel { Name = "Telegram", BotToken = SecretProtector.Protect("123:telegram-secret"), ChatId = "-1001" },
        new WebhookChannel { Name = "RMM", Url = SecretProtector.Protect("https://rmm.example/hook?key=url-secret"), HeaderName = "X-Key", HeaderValue = SecretProtector.Protect("header-secret") },
        new GmailChannel { Name = "Gmail sign-in", AuthMode = GmailAuthMode.OAuth, Account = "a@gmail.com", Enabled = false },
    ];

    [Fact]
    public void Export_and_import_restore_channels_and_secrets_without_writing_them_in_plain_text()
    {
        var file = ChannelTransfer.Export(Channels(), "correct horse");

        Assert.DoesNotContain("secret", file);
        Assert.DoesNotContain("Mail relay", file);
        var imported = ChannelTransfer.Import(file, "correct horse");

        Assert.Equal(["Mail relay", "Telegram", "RMM", "Gmail sign-in"], imported.Select(c => c.Name));
        var smtp = Assert.IsType<SmtpChannel>(imported[0]);
        Assert.Equal(("mailhost", 25), (smtp.Host, smtp.Port));
        Assert.StartsWith("dpapi:", smtp.Password); // encrypted again for this computer
        Assert.Equal("smtp-secret-1", SecretProtector.Unprotect(smtp.Password));
        var telegram = Assert.IsType<TelegramChannel>(imported[1]);
        Assert.Equal(("123:telegram-secret", "-1001"), (SecretProtector.Unprotect(telegram.BotToken), telegram.ChatId));
        var webhook = Assert.IsType<WebhookChannel>(imported[2]);
        Assert.Equal("https://rmm.example/hook?key=url-secret", SecretProtector.Unprotect(webhook.Url));
        Assert.Equal(("X-Key", "header-secret"), (webhook.HeaderName, SecretProtector.Unprotect(webhook.HeaderValue)));
        var gmail = Assert.IsType<GmailChannel>(imported[3]);
        Assert.True(gmail.UsesOAuth);
        Assert.False(gmail.Enabled);
    }

    [Fact]
    public void Says_what_the_file_holds_before_the_password_is_entered()
    {
        var info = ChannelTransfer.ReadInfo(ChannelTransfer.Export(Channels(), "correct horse"));

        Assert.Equal(4, info.Count);
        Assert.Equal(Environment.MachineName, info.Machine);
        Assert.True(DateTimeOffset.Now - info.Exported < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Refuses_a_wrong_password()
    {
        var file = ChannelTransfer.Export(Channels(), "correct horse");

        var ex = Assert.Throws<InvalidDataException>(() => ChannelTransfer.Import(file, "battery staple"));
        Assert.Contains("password", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{ \"siteName\": \"a config.json\", \"channels\": [] }")]
    public void Refuses_a_file_that_is_not_an_export(string text)
    {
        Assert.Throws<InvalidDataException>(() => ChannelTransfer.ReadInfo(text));
        Assert.Throws<InvalidDataException>(() => ChannelTransfer.Import(text, "correct horse"));
    }

    [Fact]
    public void Refuses_a_damaged_file()
    {
        var file = ChannelTransfer.Export(Channels(), "correct horse");
        var data = System.Text.Json.Nodes.JsonNode.Parse(file)!;
        var bytes = Convert.FromBase64String(data["data"]!.GetValue<string>());
        bytes[0] ^= 1;
        data["data"] = Convert.ToBase64String(bytes);

        Assert.Throws<InvalidDataException>(() => ChannelTransfer.Import(data.ToJsonString(), "correct horse"));
    }

    [Fact]
    public void Merge_replaces_channels_with_the_same_name_and_adds_the_rest()
    {
        var telegram = new TelegramChannel { Name = "Telegram", ChatId = "old" };
        var ntfy = new NtfyChannel { Name = "Phone push", Topic = "t" };
        var existing = new List<ChannelConfig> { telegram, ntfy };
        var imported = new List<ChannelConfig>
        {
            new TelegramChannel { Name = "telegram", ChatId = "new" },
            new SlackChannel { Name = "Slack", Id = ntfy.Id }, // same id as an existing channel, e.g. imported twice
        };

        var (added, replaced) = ChannelTransfer.Merge(existing, imported);

        Assert.Equal((1, 1), (added, replaced));
        Assert.Equal(["telegram", "Phone push", "Slack"], existing.Select(c => c.Name));
        Assert.Equal(("new", telegram.Id), (((TelegramChannel)existing[0]).ChatId, existing[0].Id)); // keeps the id: a sign-in stays with it
        Assert.NotEqual(ntfy.Id, existing[2].Id);
        Assert.Equal(3, existing.Select(c => c.Id).Distinct().Count());
    }
}
