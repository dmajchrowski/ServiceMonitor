namespace ServiceMonitor.Core.OAuth;

/// <summary>Endpoints and scopes for the two consumer mail providers technicians use.</summary>
public sealed record OAuthProvider(
    string Id,
    string DisplayName,
    string AuthorizeEndpoint,
    string TokenEndpoint,
    string? DeviceCodeEndpoint,
    string Scopes,
    bool RedirectUsesLocalhostName)
{
    public const string MicrosoftId = "microsoft";
    public const string GoogleId = "google";

    /// <summary>
    /// Microsoft identity platform. "common" accepts both personal (outlook.com/hotmail/live) and work accounts
    /// when the app registration allows both. Mail is sent through Microsoft Graph (Mail.Send).
    /// </summary>
    public static OAuthProvider Microsoft(string tenant)
    {
        var t = string.IsNullOrWhiteSpace(tenant) ? "common" : tenant.Trim();
        return new OAuthProvider(
            MicrosoftId,
            "Microsoft",
            $"https://login.microsoftonline.com/{t}/oauth2/v2.0/authorize",
            $"https://login.microsoftonline.com/{t}/oauth2/v2.0/token",
            $"https://login.microsoftonline.com/{t}/oauth2/v2.0/devicecode",
            "offline_access openid profile email https://graph.microsoft.com/Mail.Send",
            RedirectUsesLocalhostName: true);
    }

    /// <summary>Google. Only the narrow gmail.send scope is requested: the app can send mail but never read it.</summary>
    public static OAuthProvider Google { get; } = new(
        GoogleId,
        "Google",
        "https://accounts.google.com/o/oauth2/v2/auth",
        "https://oauth2.googleapis.com/token",
        DeviceCodeEndpoint: null, // Google's device flow does not allow Gmail scopes
        "openid email https://www.googleapis.com/auth/gmail.send",
        RedirectUsesLocalhostName: false);
}

/// <summary>Thrown when a stored sign-in no longer works and a person has to sign in again.</summary>
public sealed class ReauthorizationRequiredException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class OAuthException(string message) : Exception(message);
