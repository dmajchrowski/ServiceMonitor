# One-time setup: "Sign in with Microsoft" and "Sign in with Google"

**Optional.** Gmail, Yahoo Mail and iCloud Mail work with app passwords and need none of this.

**Who:** whoever builds and distributes `ServiceMonitor.exe` (IT / engineering), **once** for the whole company.
**Why:** Microsoft has switched off password sign-in for Outlook.com / Hotmail / Live mailboxes in third-party
programs (app passwords included), so the only way to send from those accounts is OAuth. OAuth requires the
program to be registered with Microsoft, and that registration's ID is built into the exe. Technicians then
just click **Sign in** – they never see any of this. Until an ID is built in (or entered under Settings →
Advanced), the Outlook.com option is hidden from **Add a channel**.

| Provider | Needed? | Cost | Time |
|---|---|---|---|
| Microsoft (Outlook.com, Hotmail, Live, Microsoft 365) | Only if alerts must be sent from Outlook.com / Microsoft 365 accounts | Free | ~5 min |
| Google | **No** – Gmail works with app passwords. Optional convenience. | Free | ~15 min + Google's review if you exceed 100 users |

---

## Microsoft (Entra ID app registration)

You need any Microsoft Entra tenant – your company's Microsoft 365 tenant is fine (an ordinary user can
usually register apps; otherwise ask an admin). The registration does **not** give the app access to your
company's data; each technician grants access only to the one mailbox they sign in with.

Sign in to the portal with a **work account**, not the technicians' outlook.com mailbox: since June 2024 a
personal Microsoft account on its own can no longer register apps. If the company has no Microsoft 365, sign
up for a free Azure account first (that creates a tenant). The outlook.com mailboxes are only used later, when
technicians click **Sign in with Microsoft**.

1. Go to <https://entra.microsoft.com>, type **App registrations** in the top search box, open it and click
   **New registration**.
2. **Name:** `Service Monitor`
3. **Supported account types:** *Accounts in any organizational directory and personal Microsoft accounts*
   (this is what allows outlook.com / hotmail.com addresses).
4. **Redirect URI:** platform **Public client/native (mobile & desktop)**, value `http://localhost`
5. Click **Register**. The app's **Overview** page opens; copy the **Application (client) ID** (a GUID
   like `11111111-2222-3333-4444-555555555555`). The Directory (tenant) ID next to it is not needed.
6. **Authentication** page → **Advanced settings** → *Allow public client flows* = **Yes**, then **Save**.
   (Enables the "sign in on your phone with a code" option.)
7. **API permissions** → *Add a permission* → **Microsoft Graph** → **Delegated** → tick `Mail.Send`
   (plus `offline_access`, `openid`, `email`, `profile` under *OpenId permissions*). No admin consent is needed;
   each user consents for their own mailbox.
8. Optional: **Branding & properties** → add a logo and publisher domain so the consent screen looks familiar.

Build it in:

```powershell
.\build\publish.ps1 -MicrosoftClientId 11111111-2222-3333-4444-555555555555
```

(or, for a single machine, paste the ID into **Settings → Advanced: OAuth app registrations**).

What the app can do with a technician's sign-in: **send mail as that account** (`Mail.Send`) – it can't read the
mailbox. Mail is sent through Microsoft Graph (`POST /me/sendMail`) and is saved in Sent Items.

---

## Google (optional)

Only needed if you prefer **Sign in with Google** over Gmail app passwords.

1. <https://console.cloud.google.com> → create a project, e.g. `Service Monitor`.
2. **APIs & Services → Library** → enable **Gmail API**.
3. **Google Auth Platform → Branding / Audience**: user type **External**, app name, support e-mail.
   Under **Data access** add the scope `https://www.googleapis.com/auth/gmail.send`.
4. **Audience → Publish app** (set publishing status to **In production**).
   ⚠ In *Testing* status Google expires sign-ins after 7 days, which would silently break alerts.
5. **Clients → Create client → Desktop app**. Copy the **client ID** and **client secret**
   (for desktop apps Google does not treat the secret as confidential).
6. Build it in:

   ```powershell
   .\build\publish.ps1 -MicrosoftClientId <id> -GoogleClientId <id>.apps.googleusercontent.com -GoogleClientSecret <secret>
   ```

`gmail.send` is a *sensitive* scope: until Google verifies the app, users see an "unverified app" warning
(click *Advanced → Go to …*) and the app is limited to 100 users. For more, submit the app for verification
in the Google Cloud console.

---

## How sign-ins are kept working

* Tokens are stored per channel under `C:\ProgramData\ServiceMonitor\tokens`, encrypted with Windows
  DPAPI (machine scope) in a folder only SYSTEM and Administrators can read. Copying them to another computer
  makes them useless.
* The background monitor refreshes every sign-in once a day. Microsoft sign-ins otherwise expire after 90 days
  without use and Google's after 6 months – a monitor that only sends mail when something breaks would
  otherwise go quiet without anyone noticing.
* If a sign-in is revoked (password change, account locked), the monitor writes event 302 to the Application log
  and sends a *"channel needs attention"* message through the other channels.
