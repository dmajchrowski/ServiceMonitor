using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ServiceMonitor.Core.Monitoring;

namespace ServiceMonitor.Core.Notifications;

/// <summary>Turns a <see cref="Notification"/> into each channel's native format.</summary>
public static class NotificationRenderer
{
    private static string Footer(Notification n) =>
        $"Sent by {AppInfo.ProductName} {AppInfo.Version} on {n.MachineName} at {TimeText.Local(n.Time.ToUniversalTime())}.";

    // ------------------------------------------------------------------ email

    public static string Html(Notification n)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append($$"""
            <!DOCTYPE html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"></head>
            <body style="margin:0;padding:0;background:#f3f3f3;font-family:'Segoe UI',Arial,sans-serif;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#f3f3f3;padding:24px 0;"><tr><td align="center">
            <table role="presentation" width="600" cellpadding="0" cellspacing="0" style="max-width:600px;width:100%;background:#ffffff;border:1px solid #e0e0e0;border-radius:8px;">
            <tr><td style="background:{{n.ColorHex}};padding:18px 24px;border-radius:8px 8px 0 0;color:#ffffff;">
              <div style="font-size:12px;letter-spacing:1px;text-transform:uppercase;opacity:0.9;">{{E(n.Tag)}} &middot; {{E(n.Origin)}}</div>
              <div style="font-size:20px;font-weight:600;margin-top:4px;line-height:1.3;">{{E(n.Title)}}</div>
            </td></tr>
            """);
        if (!string.IsNullOrWhiteSpace(n.Summary))
            sb.Append($"""<tr><td style="padding:20px 24px 12px;font-size:14px;line-height:1.5;color:#1a1a1a;">{E(n.Summary)}</td></tr>""");

        if (n.Facts.Count > 0)
        {
            sb.Append("""<tr><td style="padding:4px 24px 12px;"><table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="border-collapse:collapse;font-size:13px;">""");
            foreach (var (key, value) in n.Facts)
            {
                sb.Append($"""<tr><td style="padding:7px 16px 7px 0;color:#616161;white-space:nowrap;vertical-align:top;border-top:1px solid #eeeeee;">{E(key)}</td>""");
                sb.Append($"""<td style="padding:7px 0;color:#1a1a1a;vertical-align:top;border-top:1px solid #eeeeee;word-break:break-word;">{E(value)}</td></tr>""");
            }
            sb.Append("</table></td></tr>");
        }

        if (n.Events.Count > 0)
        {
            sb.Append("""<tr><td style="padding:8px 24px 4px;"><div style="font-size:13px;font-weight:600;color:#1a1a1a;margin-bottom:8px;">Related Windows events</div>""");
            foreach (var ev in n.Events)
            {
                sb.Append($"""<div style="font-size:12px;color:#616161;">{E(TimeText.Local(ev.Time.ToUniversalTime()))} &middot; {E(ev.Log)} log &middot; {E(ev.Source)} &middot; Event {ev.EventId}</div>""");
                sb.Append($"""<pre style="white-space:pre-wrap;word-break:break-word;font-family:Consolas,'Courier New',monospace;font-size:12px;line-height:1.4;background:#f7f7f7;border:1px solid #eeeeee;border-radius:4px;padding:8px 10px;margin:4px 0 14px;color:#1a1a1a;">{E(ev.Message)}</pre>""");
            }
            sb.Append("</td></tr>");
        }

        sb.Append($"""
            <tr><td style="padding:14px 24px 18px;font-size:11px;color:#8a8a8a;border-top:1px solid #eeeeee;">{E(Footer(n))}</td></tr>
            </table></td></tr></table></body></html>
            """);
        return sb.ToString();
    }

    public static string PlainText(Notification n, bool includeEvents = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{n.Tag}: {n.Title}");
        sb.AppendLine(n.Origin);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(n.Summary)) sb.AppendLine(n.Summary).AppendLine();
        foreach (var (key, value) in n.Facts) sb.AppendLine($"{key}: {value}");
        if (includeEvents && n.Events.Count > 0)
        {
            sb.AppendLine().AppendLine("Related Windows events");
            foreach (var ev in n.Events)
            {
                sb.AppendLine($"--- {TimeText.Local(ev.Time.ToUniversalTime())} · {ev.Log} · {ev.Source} · Event {ev.EventId}");
                sb.AppendLine(ev.Message);
            }
        }
        sb.AppendLine().AppendLine(Footer(n));
        return sb.ToString();
    }

    private static string EventsText(Notification n, int maxLength)
    {
        if (n.Events.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var ev in n.Events)
        {
            sb.AppendLine($"[{ev.Time:yyyy-MM-dd HH:mm:ss}] {ev.Source} {ev.EventId}");
            sb.AppendLine(ev.Message).AppendLine();
        }
        return Truncate(sb.ToString().Trim(), maxLength);
    }

    internal static string Truncate(string text, int max) => text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";

    // ------------------------------------------------------------------ chat / push

    /// <summary>Teams "Workflows" (Power Automate) webhook payload with an Adaptive Card.</summary>
    public static JsonObject Teams(Notification n)
    {
        var style = n.Severity switch { Severity.Critical => "attention", Severity.Warning => "warning", Severity.Good => "good", _ => "accent" };
        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock", ["text"] = $"{n.Tag} · {n.Origin}", ["size"] = "Small", ["weight"] = "Bolder",
                ["color"] = style, ["wrap"] = true,
            },
            new JsonObject { ["type"] = "TextBlock", ["text"] = n.Title, ["size"] = "Large", ["weight"] = "Bolder", ["wrap"] = true },
        };
        if (!string.IsNullOrWhiteSpace(n.Summary)) body.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = n.Summary, ["wrap"] = true });
        if (n.Facts.Count > 0)
        {
            var facts = new JsonArray();
            foreach (var (key, value) in n.Facts) facts.Add(new JsonObject { ["title"] = key, ["value"] = value });
            body.Add(new JsonObject { ["type"] = "FactSet", ["facts"] = facts });
        }
        if (n.Events.Count > 0)
        {
            body.Add(new JsonObject
            {
                ["type"] = "TextBlock", ["text"] = EventsText(n, 3000), ["fontType"] = "Monospace", ["size"] = "Small",
                ["wrap"] = true, ["isSubtle"] = true, ["separator"] = true,
            });
        }
        body.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = Footer(n), ["size"] = "Small", ["isSubtle"] = true, ["wrap"] = true });

        return new JsonObject
        {
            ["type"] = "message",
            ["summary"] = n.Subject,
            ["attachments"] = new JsonArray
            {
                new JsonObject
                {
                    ["contentType"] = "application/vnd.microsoft.card.adaptive",
                    ["content"] = new JsonObject
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = "1.4",
                        ["msteams"] = new JsonObject { ["width"] = "Full" },
                        ["body"] = body,
                    },
                },
            },
        };
    }

    public static JsonObject Slack(Notification n)
    {
        var blocks = new JsonArray
        {
            new JsonObject { ["type"] = "header", ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = Truncate($"{n.Emoji} {n.Title}", 150) } },
            new JsonObject { ["type"] = "context", ["elements"] = new JsonArray { new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*{n.Tag}* · {SlackEscape(n.Origin)}" } } },
        };
        if (!string.IsNullOrWhiteSpace(n.Summary))
            blocks.Add(new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = SlackEscape(n.Summary) } });
        if (n.Facts.Count > 0)
        {
            var fields = new JsonArray();
            foreach (var (key, value) in n.Facts.Take(10))
                fields.Add(new JsonObject { ["type"] = "mrkdwn", ["text"] = Truncate($"*{SlackEscape(key)}*\n{SlackEscape(value)}", 2000) });
            blocks.Add(new JsonObject { ["type"] = "section", ["fields"] = fields });
        }
        if (n.Events.Count > 0)
            blocks.Add(new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = "```" + EventsText(n, 2800) + "```" } });
        blocks.Add(new JsonObject { ["type"] = "context", ["elements"] = new JsonArray { new JsonObject { ["type"] = "mrkdwn", ["text"] = SlackEscape(Footer(n)) } } });

        return new JsonObject { ["text"] = $"{n.Emoji} {n.Subject}", ["blocks"] = blocks };
    }

    private static string SlackEscape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static JsonObject Discord(Notification n)
    {
        var description = n.Summary;
        if (n.Events.Count > 0) description += "\n```\n" + EventsText(n, 3000).Replace("```", "'''") + "\n```";
        var fields = new JsonArray();
        foreach (var (key, value) in n.Facts.Take(25))
            fields.Add(new JsonObject { ["name"] = Truncate(key, 256), ["value"] = Truncate(value, 1024), ["inline"] = value.Length < 40 });

        return new JsonObject
        {
            ["username"] = AppInfo.ProductName,
            ["embeds"] = new JsonArray
            {
                new JsonObject
                {
                    ["title"] = Truncate($"{n.Emoji} {n.Title}", 256),
                    ["description"] = Truncate(description, 4000),
                    ["color"] = Convert.ToInt32(n.ColorHex.TrimStart('#'), 16),
                    ["author"] = new JsonObject { ["name"] = Truncate($"{n.Tag} · {n.Origin}", 256) },
                    ["fields"] = fields,
                    ["footer"] = new JsonObject { ["text"] = Truncate(Footer(n), 2048) },
                    ["timestamp"] = n.Time.ToUniversalTime().ToString("o"),
                },
            },
        };
    }

    public static JsonObject Ntfy(Notification n, string topic)
    {
        var message = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(n.Summary)) message.AppendLine(n.Summary);
        foreach (var (key, value) in n.Facts.Where(f => f.Key is not ("Executable" or "Start type"))) message.AppendLine($"{key}: {value}");
        var (priority, tag) = n.Severity switch
        {
            Severity.Critical => (5, "rotating_light"),
            Severity.Warning => (4, "warning"),
            Severity.Good => (3, "white_check_mark"),
            _ => (2, "information_source"),
        };
        return new JsonObject
        {
            ["topic"] = topic,
            ["title"] = Truncate($"{n.Origin}: {n.Title}", 250),
            ["message"] = Truncate(message.ToString().Trim(), 4000),
            ["priority"] = priority,
            ["tags"] = new JsonArray { tag, n.Tag.ToLowerInvariant().Replace(' ', '_') },
        };
    }

    public static JsonObject Telegram(Notification n, string chatId)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.AppendLine($"{n.Emoji} <b>{E(n.Title)}</b>");
        sb.AppendLine($"<i>{E(n.Tag)} · {E(n.Origin)}</i>");
        if (!string.IsNullOrWhiteSpace(n.Summary)) sb.AppendLine().AppendLine(E(n.Summary));
        if (n.Facts.Count > 0) sb.AppendLine();
        foreach (var (key, value) in n.Facts) sb.AppendLine($"<b>{E(key)}:</b> {E(value)}");
        var text = sb.ToString();
        if (n.Events.Count > 0) text += "\n<pre>" + E(EventsText(n, Math.Max(200, 3900 - text.Length))) + "</pre>";
        return new JsonObject
        {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["parse_mode"] = "HTML",
            ["disable_web_page_preview"] = true,
        };
    }

    /// <summary>Stable, documented JSON for generic webhooks (see docs).</summary>
    public static JsonObject Webhook(Notification n)
    {
        var facts = new JsonObject();
        foreach (var (key, value) in n.Facts) facts[key] = value;
        var events = new JsonArray();
        foreach (var ev in n.Events)
        {
            events.Add(new JsonObject
            {
                ["time"] = ev.Time.ToString("o"), ["log"] = ev.Log, ["source"] = ev.Source, ["eventId"] = ev.EventId, ["message"] = ev.Message,
            });
        }
        return new JsonObject
        {
            ["id"] = n.Id,
            ["kind"] = n.Kind.ToString().ToLowerInvariant(),
            ["severity"] = n.Severity.ToString().ToLowerInvariant(),
            ["tag"] = n.Tag,
            ["title"] = n.Title,
            ["summary"] = n.Summary,
            ["subject"] = n.Subject,
            ["machine"] = n.MachineName,
            ["site"] = n.SiteName,
            ["service"] = n.ServiceName,
            ["serviceDisplayName"] = n.ServiceDisplayName,
            ["time"] = n.Time.ToString("o"),
            ["facts"] = facts,
            ["events"] = events,
            ["source"] = new JsonObject { ["product"] = AppInfo.ProductName, ["version"] = AppInfo.Version },
        };
    }
}
