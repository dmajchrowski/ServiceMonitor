using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ServiceMonitor.Core.Configuration;

namespace ServiceMonitor.Core.OAuth;

public sealed record DeviceCodePrompt(string UserCode, string VerificationUri, string Message, DateTimeOffset ExpiresAt);

/// <summary>
/// OAuth 2.0 for a desktop app: authorization code + PKCE through the system browser and a loopback redirect
/// (RFC 8252), the device code flow for Microsoft, and refresh-token handling for the background service.
/// </summary>
public sealed class OAuthClient(HttpClient http, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public static OAuthProvider ProviderFor(ChannelConfig channel, OAuthAppSettings app) => channel switch
    {
        MicrosoftChannel => OAuthProvider.Microsoft(app.EffectiveMicrosoftTenant),
        GmailChannel { AuthMode: GmailAuthMode.OAuth } => OAuthProvider.Google,
        _ => throw new InvalidOperationException($"{channel.Kind} channels do not use OAuth."),
    };

    // ---------------------------------------------------------------- interactive sign-in

    public async Task<StoredToken> SignInWithBrowserAsync(OAuthProvider provider, OAuthAppSettings app, string? loginHint,
        Action<string> openBrowser, CancellationToken cancellationToken)
    {
        var (clientId, clientSecret) = Client(provider, app);
        var verifier = RandomString(64);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = RandomString(24);

        using var listener = new LoopbackListener();
        var redirectUri = $"http://{(provider.RedirectUsesLocalhostName ? "localhost" : "127.0.0.1")}:{listener.Port}";

        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = provider.Scopes,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        if (provider.Id == OAuthProvider.GoogleId)
        {
            query["access_type"] = "offline";
            query["prompt"] = "consent"; // guarantees a refresh token
        }
        else
        {
            query["prompt"] = "select_account";
        }
        if (!string.IsNullOrWhiteSpace(loginHint)) query["login_hint"] = loginHint.Trim();

        openBrowser(provider.AuthorizeEndpoint + "?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}")));

        var callback = await listener.WaitForCallbackAsync(cancellationToken);
        if (callback.TryGetValue("error", out var error))
        {
            callback.TryGetValue("error_description", out var description);
            throw new OAuthException(error == "access_denied" ? "Sign-in was cancelled or consent was not granted." : $"{error}: {description}");
        }
        if (!callback.TryGetValue("state", out var returnedState) || returnedState != state)
            throw new OAuthException("The sign-in response did not match the request. Please try again.");
        if (!callback.TryGetValue("code", out var code)) throw new OAuthException("The sign-in response did not contain an authorization code.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        };
        if (!string.IsNullOrEmpty(clientSecret)) form["client_secret"] = clientSecret;
        if (provider.Id == OAuthProvider.MicrosoftId) form["scope"] = provider.Scopes;

        var json = await PostTokenAsync(provider, form, cancellationToken);
        return ToStoredToken(provider, app, json, previous: null);
    }

    /// <summary>Microsoft only: sign in on any other device (phone, laptop) by entering a short code.</summary>
    public async Task<StoredToken> SignInWithDeviceCodeAsync(OAuthProvider provider, OAuthAppSettings app,
        Action<DeviceCodePrompt> showPrompt, CancellationToken cancellationToken)
    {
        if (provider.DeviceCodeEndpoint is null) throw new OAuthException($"{provider.DisplayName} does not support signing in with a code.");
        var (clientId, _) = Client(provider, app);

        using var response = await http.PostAsync(provider.DeviceCodeEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = provider.Scopes }), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var start = JsonNode.Parse(body)?.AsObject() ?? throw new OAuthException("Empty response from the sign-in service.");
        if (!response.IsSuccessStatusCode) throw new OAuthException(DescribeError(start, body));

        var deviceCode = (string)start["device_code"]!;
        var interval = TimeSpan.FromSeconds((int?)start["interval"] ?? 5);
        var expires = _time.GetUtcNow() + TimeSpan.FromSeconds((int?)start["expires_in"] ?? 900);
        showPrompt(new DeviceCodePrompt((string)start["user_code"]!, (string)start["verification_uri"]!, (string?)start["message"] ?? "", expires));

        while (_time.GetUtcNow() < expires)
        {
            await Task.Delay(interval, cancellationToken);
            using var poll = await http.PostAsync(provider.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = clientId,
                ["device_code"] = deviceCode,
            }), cancellationToken);
            var pollBody = await poll.Content.ReadAsStringAsync(cancellationToken);
            var json = JsonNode.Parse(pollBody)?.AsObject() ?? new JsonObject();
            if (poll.IsSuccessStatusCode) return ToStoredToken(provider, app, json, previous: null);

            switch ((string?)json["error"])
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "authorization_declined":
                    throw new OAuthException("Sign-in was declined.");
                case "expired_token":
                    throw new OAuthException("The code expired. Please try again.");
                default:
                    throw new OAuthException(DescribeError(json, pollBody));
            }
        }
        throw new OAuthException("The code expired. Please try again.");
    }

    // ---------------------------------------------------------------- background use

    /// <summary>Returns a valid access token for the channel, refreshing (and re-saving) it when needed.</summary>
    public async Task<StoredToken> GetValidTokenAsync(ChannelConfig channel, OAuthAppSettings app, bool forceRefresh, CancellationToken cancellationToken)
    {
        using var _ = await TokenStore.LockAsync(cancellationToken);
        var token = TokenStore.Load(channel.Id)
                    ?? throw new ReauthorizationRequiredException($"'{channel.Name}' is not signed in. Open {AppInfo.ProductName} and click Sign in.");

        if (!forceRefresh && token.AccessToken.Length > 0 && token.AccessTokenExpiresAt > _time.GetUtcNow() + TimeSpan.FromMinutes(5))
            return token;

        var provider = token.Provider == OAuthProvider.MicrosoftId ? OAuthProvider.Microsoft(token.Tenant) : OAuthProvider.Google;
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = token.RefreshToken,
            ["client_id"] = token.ClientId,
        };
        if (provider.Id == OAuthProvider.MicrosoftId) form["scope"] = provider.Scopes;
        else form["client_secret"] = app.EffectiveGoogleClientSecret;

        JsonObject json;
        try
        {
            json = await PostTokenAsync(provider, form, cancellationToken);
        }
        catch (OAuthException ex) when (ex.Message.StartsWith("invalid_grant", StringComparison.Ordinal)
                                        || ex.Message.StartsWith("invalid_client", StringComparison.Ordinal)
                                        || ex.Message.StartsWith("unauthorized_client", StringComparison.Ordinal))
        {
            throw new ReauthorizationRequiredException(
                $"The {provider.DisplayName} sign-in for '{channel.Name}' ({token.Account}) has expired or was revoked. Open {AppInfo.ProductName} and sign in again.", ex);
        }

        var refreshed = ToStoredToken(provider, app, json, token);
        TokenStore.Save(channel.Id, refreshed);
        return refreshed;
    }

    // ---------------------------------------------------------------- helpers

    private static (string ClientId, string? ClientSecret) Client(OAuthProvider provider, OAuthAppSettings app)
    {
        if (provider.Id == OAuthProvider.MicrosoftId)
        {
            if (!app.MicrosoftConfigured)
                throw new OAuthException("No Microsoft app registration is configured. See docs\\OAUTH-APP-REGISTRATION.md (one-time setup by your IT admin).");
            return (app.EffectiveMicrosoftClientId, null);
        }
        if (!app.GoogleConfigured)
            throw new OAuthException("No Google OAuth client is configured. Use the Gmail app-password option, or see docs\\OAUTH-APP-REGISTRATION.md.");
        return (app.EffectiveGoogleClientId, app.EffectiveGoogleClientSecret);
    }

    private async Task<JsonObject> PostTokenAsync(OAuthProvider provider, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(provider.TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonObject? json = null;
        try { json = JsonNode.Parse(body)?.AsObject(); } catch (JsonException) { }
        if (!response.IsSuccessStatusCode || json is null) throw new OAuthException(DescribeError(json, body));
        return json;
    }

    private StoredToken ToStoredToken(OAuthProvider provider, OAuthAppSettings app, JsonObject json, StoredToken? previous)
    {
        var now = _time.GetUtcNow();
        var refreshToken = (string?)json["refresh_token"];
        if (string.IsNullOrEmpty(refreshToken))
        {
            refreshToken = previous?.RefreshToken; // Google keeps the original refresh token
            if (string.IsNullOrEmpty(refreshToken))
                throw new OAuthException("The provider did not return a refresh token, so the monitor could not keep sending in the background.");
        }

        var account = ReadAccount((string?)json["id_token"]) ?? previous?.Account ?? "";
        return new StoredToken
        {
            Provider = provider.Id,
            Account = account,
            ClientId = previous?.ClientId ?? (provider.Id == OAuthProvider.MicrosoftId ? app.EffectiveMicrosoftClientId : app.EffectiveGoogleClientId),
            Tenant = previous?.Tenant ?? (provider.Id == OAuthProvider.MicrosoftId ? app.EffectiveMicrosoftTenant : ""),
            RefreshToken = refreshToken,
            AccessToken = (string?)json["access_token"] ?? "",
            AccessTokenExpiresAt = now + TimeSpan.FromSeconds(ReadInt(json["expires_in"]) ?? 3600),
            SignedInAt = previous?.SignedInAt ?? now,
            LastRefreshedAt = now,
        };
    }

    private static int? ReadInt(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.Number => (int)node,
        JsonValueKind.String when int.TryParse((string?)node, out var v) => v,
        _ => null,
    };

    /// <summary>Reads the e-mail address from the ID token (it comes straight from the token endpoint over TLS).</summary>
    internal static string? ReadAccount(string? idToken)
    {
        if (string.IsNullOrEmpty(idToken)) return null;
        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var claims = JsonNode.Parse(Convert.FromBase64String(payload))?.AsObject();
            return (string?)claims?["email"] ?? (string?)claims?["preferred_username"] ?? (string?)claims?["upn"];
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string DescribeError(JsonObject? json, string body)
    {
        var error = (string?)json?["error"];
        var description = (string?)json?["error_description"];
        if (error is null) return body.Length > 300 ? body[..300] : body;
        if (description is not null)
        {
            // Microsoft appends trace IDs and timestamps; keep the human part.
            var cut = description.IndexOf("Trace ID", StringComparison.Ordinal);
            if (cut > 0) description = description[..cut].Trim();
        }
        return description is null ? error : $"{error}: {description}";
    }

    private static string RandomString(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Minimal HTTP listener on 127.0.0.1 and ::1 that captures the OAuth redirect.</summary>
    private sealed class LoopbackListener : IDisposable
    {
        private readonly TcpListener _v4;
        private readonly TcpListener? _v6;

        public int Port { get; }

        public LoopbackListener()
        {
            _v4 = new TcpListener(IPAddress.Loopback, 0);
            _v4.Start();
            Port = ((IPEndPoint)_v4.LocalEndpoint).Port;
            try
            {
                // Browsers may resolve "localhost" to ::1 first.
                _v6 = new TcpListener(IPAddress.IPv6Loopback, Port);
                _v6.Start();
            }
            catch (SocketException)
            {
                _v6 = null;
            }
        }

        public async Task<Dictionary<string, string>> WaitForCallbackAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var accepts = new List<Task<TcpClient>> { _v4.AcceptTcpClientAsync(timeout.Token).AsTask() };
            if (_v6 is not null) accepts.Add(_v6.AcceptTcpClientAsync(timeout.Token).AsTask());

            while (true)
            {
                var done = await Task.WhenAny(accepts);
                var index = accepts.IndexOf(done);
                using var client = await done; // throws OperationCanceledException on cancel/timeout
                accepts[index] = (index == 0 ? _v4 : _v6!).AcceptTcpClientAsync(timeout.Token).AsTask();

                var stream = client.GetStream();
                var requestLine = await ReadRequestLineAsync(stream, timeout.Token);
                var target = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
                var queryIndex = target.IndexOf('?');
                var query = ParseQuery(queryIndex >= 0 ? target[(queryIndex + 1)..] : "");

                if (!query.ContainsKey("code") && !query.ContainsKey("error"))
                {
                    await WriteResponseAsync(stream, "404 Not Found", "", timeout.Token); // favicon.ico etc.
                    continue;
                }

                var ok = query.ContainsKey("code");
                await WriteResponseAsync(stream, "200 OK", Page(ok), timeout.Token);
                return query;
            }
        }

        private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
                if (read == 0) break;
                total += read;
                var text = Encoding.ASCII.GetString(buffer, 0, total);
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) break;
            }
            var request = Encoding.ASCII.GetString(buffer, 0, total);
            var end = request.IndexOf("\r\n", StringComparison.Ordinal);
            return end >= 0 ? request[..end] : request;
        }

        private static async Task WriteResponseAsync(NetworkStream stream, string status, string html, CancellationToken cancellationToken)
        {
            var body = Encoding.UTF8.GetBytes(html);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString((eq >= 0 ? pair[..eq] : pair).Replace('+', ' '));
                var value = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
                result[key] = value;
            }
            return result;
        }

        private static string Page(bool ok) =>
            $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>{{AppInfo.ProductName}}</title>
            <style>body{font-family:'Segoe UI',sans-serif;background:#f3f3f3;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
            .card{background:#fff;border-radius:8px;padding:32px 40px;box-shadow:0 2px 8px rgba(0,0,0,.08);text-align:center;max-width:420px}
            h1{font-size:20px;font-weight:600;margin:0 0 8px;color:{{(ok ? "#0F7B0F" : "#C42B1C")}}}p{color:#555;margin:0}</style></head>
            <body><div class="card"><h1>{{(ok ? "You're signed in" : "Sign-in did not complete")}}</h1>
            <p>{{(ok ? "You can close this tab and return to " + AppInfo.ProductName + "." : "Return to " + AppInfo.ProductName + " for details.")}}</p></div></body></html>
            """;

        public void Dispose()
        {
            _v4.Stop();
            _v6?.Stop();
        }
    }
}
