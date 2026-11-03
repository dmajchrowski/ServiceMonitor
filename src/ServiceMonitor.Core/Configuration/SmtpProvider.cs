namespace ServiceMonitor.Core.Configuration;

/// <summary>
/// Free mail providers that still accept an app password over SMTP. Picking one fills in the server settings,
/// so a technician only has to enter the address and the app password.
/// </summary>
public sealed record SmtpProvider(
    string Id,
    string Name,
    string Host,
    int Port,
    SmtpSecurity Security,
    string AddressExample,
    string Instructions,
    string AccountPageUrl,
    string AccountPageButton,
    string AuthFailureHint)
{
    public static SmtpProvider Yahoo { get; } = new(
        "yahoo", "Yahoo Mail", "smtp.mail.yahoo.com", 465, SmtpSecurity.SslOnConnect, "site.alerts@yahoo.com",
        "1. In a browser, sign in to the Yahoo account that will send the alerts.\n" +
        "2. Open Account security, turn on 2-step verification, then under External connections choose Create app password.\n" +
        "3. Type a name such as \"Service Monitor\", click Generate and paste the password below.\n" +
        "Brand-new Yahoo accounts usually can't create app passwords yet: Yahoo only allows it after the account has been " +
        "used in the same browser for several days. For an account created today, use Gmail instead.",
        "https://login.yahoo.com/account/security", "Open Yahoo account security",
        "Yahoo did not accept the app password. Use the full Yahoo address and an app password from Account security → " +
        "External connections → Create app password, not the normal account password.");

    public static SmtpProvider ICloud { get; } = new(
        "icloud", "iCloud Mail", "smtp.mail.me.com", 587, SmtpSecurity.StartTls, "site.alerts@icloud.com",
        "1. The Apple Account must have two-factor authentication on and iCloud Mail set up (an @icloud.com address).\n" +
        "2. Sign in at account.apple.com and open Sign-In and Security → App-Specific Passwords.\n" +
        "3. Create one named \"Service Monitor\" and paste it below.",
        "https://account.apple.com", "Open Apple Account",
        "iCloud did not accept the password. Use the full @icloud.com address and an app-specific password from " +
        "account.apple.com, not the Apple Account password.");

    public static IReadOnlyList<SmtpProvider> All { get; } = [Yahoo, ICloud];

    public static SmtpProvider? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
