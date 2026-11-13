# AtlassianPilot

**Your Work. Your Agents. Your Control.**

A single-user Windows WPF desktop workspace for GitHub Copilot-powered Jira and Confluence agents.

## Current build: early Jira search

Implemented:
- .NET 10 WPF shell, original branding, light/dark themes and personal-use attribution.
- SQLite settings and Windows DPAPI-encrypted Atlassian credentials, preserved across this update.
- Automatic integration-form switching with confirmation before discarding unsaved edits.
- Real read-only Jira/Confluence account connection tests using saved settings only.
- Authentication/permission errors, bounded response validation, transient read retries, rate-limit handling, timeouts and cancellation.
- Official `GitHub.Copilot.SDK` 1.0.32 with its bundled, checksum-verified Copilot runtime 1.0.110.
- GitHub device authorization UI, secure-storage enforcement, sign-out and actual model discovery.
- A read-only Jira issue-search service, a Jira Agent screen, actual issue cards and continuation pagination. Search results are taken only from Jira responses; the UI does not present model-generated issue facts as API results.
- Offline regression tests using mocked HTTP/runtime services and real SQLite/DPAPI.

**Verification limitation:** the prior native-runtime device-code network failure was resolved in a live, cancelled test by passing required Windows/network environment settings to the runtime. No TLS verification or workplace security controls were disabled. Completing GitHub sign-in, model discovery and Copilot-assisted Jira search still require your authorization and remain unverified.

**Not enabled yet:** Jira creation/updates/comments/cloning, Confluence agent operations, persistent conversation browsing and agent audit logs. Jira search and continuation have been verified directly against the saved Jira account; Copilot-assisted end-to-end search remains unverified until GitHub sign-in works. Saved configuration is not a verified connection; account verification does not establish every project/space permission or model inference.

## Run on Windows

Building requires Windows 10/11 and the .NET 10 SDK with WPF support. `global.json` pins SDK 10.0.404 (latest patch roll-forward). The published self-contained application needs no installed .NET SDK/runtime. Its Copilot runtime is bundled; do not separately install a CLI for ordinary app use. A first build downloads the pinned runtime from GitHub and verifies its SHA-256 checksum, so build-time GitHub network access is required.

From PowerShell in the solution directory:

```powershell
dotnet restore AtlassianPilot.sln
dotnet build AtlassianPilot.sln
dotnet test tests\AtlassianPilot.UnitTests\AtlassianPilot.UnitTests.csproj
dotnet run --project src\AtlassianPilot.Desktop\AtlassianPilot.Desktop.csproj
```

In Visual Studio, open `AtlassianPilot.sln`, select `AtlassianPilot.Desktop` as the startup project and press F5.

For a self-contained Windows x64 release:

```powershell
dotnet publish src\AtlassianPilot.Desktop\AtlassianPilot.Desktop.csproj -c Release -r win-x64 --self-contained true -o artifacts\win-x64
```

Launch `artifacts\win-x64\AtlassianPilot.Desktop.exe`. The desktop app does not require a local web server, administrator privileges or cloud storage. GitHub device sign-in uses a browser. Workplace execution/installation policies still apply. A signed installer and shortcut packaging are planned for Phase 6.

## Beginner setup guide

You do not need to know programming or run the developer commands above to configure the published app.

### 1. Open the app

On this development PC, open:

```text
C:\Repo\AtlasianFriend\artifacts\win-x64-status-fix\AtlassianPilot.Desktop.exe
```

You can right-click the executable and use **Show more options > Send to > Desktop (create shortcut)** if your Windows configuration offers that option. Keep the entire published folder together: the executable needs the accompanying files. On another PC, copy that whole folder to a location your workplace permits, then open the executable there.

On a fresh installation, the dashboard shows **Not configured** for Jira/Confluence and **Not checked** for Copilot. This is expected.

> **Current limitation:** the Jira Agent search screen requires a successful Copilot sign-in. The device-code request now works in a live test, but you must complete authorization in the browser; see the verification note above. Your already-saved Atlassian settings do not need to be re-entered.

### 2. Gather your connection details

Use the Atlassian account you normally use at work. All URLs and values below are examples: replace them with your own.

| Detail | Where to find it | Example only |
|---|---|---|
| Jira base URL | Open Jira in your browser. Keep the HTTPS scheme and site hostname, not the issue/project path. | `https://your-company.atlassian.net` |
| Confluence base URL | Open Confluence in your browser. For a standard Cloud site, keep the same site hostname plus `/wiki`. | `https://your-company.atlassian.net/wiki` |
| Atlassian email | The email shown in your Atlassian account profile. Use the account that owns the API token, not your GitHub username. | `your.name@company.com` |
| Jira project key (optional) | The prefix of an issue ID in the target project. For issue `ABC-123`, the project key is `ABC`. | `ABC` |
| Confluence space key (optional) | Open the target space. In a URL containing `/wiki/spaces/ENG/`, the key is `ENG`. It is not necessarily the space's displayed name. | `ENG` |
| API token | Your Atlassian account's API token management screen. Treat it like a password. | Paste your token only into the app's masked token input. |

For example:
- Jira issue URL `https://your-company.atlassian.net/browse/ABC-123` becomes Jira base URL `https://your-company.atlassian.net` and optional project key `ABC`.
- Confluence page URL `https://your-company.atlassian.net/wiki/spaces/ENG/pages/123456/Page+Title` becomes Confluence base URL `https://your-company.atlassian.net/wiki` and optional space key `ENG`.

Do not paste a full issue/page URL, login URL or a URL containing a token into **Base URL**. If your company uses a custom domain, an internal server or a scoped API token that requires an Atlassian API gateway URL, do not assume these Cloud examples apply. Check with your Atlassian administrator; the connection implementation must support that URL and token type. Initial integration development targets Atlassian Cloud, not Data Center.

You can manage Atlassian tokens at [Atlassian API tokens](https://id.atlassian.com/manage-profile/security/api-tokens). A token does not grant more permissions than its owning account; expiry, scopes and your organization's policies can also limit access. Click **Test connection** after saving to verify account API access. Tests use basic authentication with your email and token; scoped tokens may require the appropriate `api.atlassian.com` gateway base URL supplied by your administrator.

### 3. Save Jira settings

1. Click **Integrations & Settings** in the sidebar (or **Configure integrations** on the dashboard).
2. In the **Integration** dropdown, select **Jira**.
3. The form switches automatically. Check that its heading says **Jira**. If you have unsaved changes, confirm discarding them or cancel and save first.
4. Enter your Jira **Base URL (HTTPS)**.
5. Enter your **Atlassian email**.
6. Enter the **Default project key**, or leave it blank if you are unsure. Do not enter an issue ID such as `ABC-123` here.
7. Paste your token into **API token (write-only)**. The characters are masked.
8. Click **Save securely**.
9. Check the bottom status message for **Settings saved locally. Connection has not yet been tested.** The token input clears, and the form indicates that a token is securely stored.

The dashboard should now show **Configured / untested** for Jira. Click **Test connection** in the Jira form. A successful account response changes the dashboard to **Connection verified**; errors appear in the form and bottom status bar. Use **Cancel operation** to stop waiting. No issue is created or changed by the test.

Checks expire when you restart the app or change/delete saved configuration. Verification covers the account endpoint only; it does not test the optional default project's permissions.

### 4. Save Confluence settings

1. Stay in **Integrations & Settings**.
2. Select **Confluence** from the dropdown. It loads automatically. Save Jira edits first, or explicitly confirm discarding them when prompted.
3. Check that the form heading says **Confluence**.
4. Enter the Confluence **Base URL (HTTPS)**, normally `https://your-company.atlassian.net/wiki` for Cloud.
5. Enter the **Atlassian email** belonging to your token.
6. Enter the **Default space key**, or leave it blank if you are unsure. Do not enter a page ID, page URL or space name unless that name is also its key.
7. Paste the API token into the masked field and click **Save securely**.
8. Click **Test connection** and check the result. A successful authenticated account response reports **Connection verified**. This does not test every space's permissions or modify pages.

Jira and Confluence credentials are saved separately. You may use the same token in both only if its account, permissions and token scope support both products; saving it for Jira does not automatically save it for Confluence.

### 5. Connect GitHub Copilot

1. Open **AI Configuration**; check that the heading says **Copilot**.
2. Leave **Preferred model (optional)** blank if unsure. Save any edited preference before testing or signing in.
3. Click **Sign in with GitHub**. The app starts the official runtime's github.com device authorization flow.
4. When a one-time code appears, click **Open GitHub verification page**. Verify that your browser is on `github.com`, sign in with the GitHub account that has Copilot access, and enter the displayed code. Do not share the code.
5. Follow GitHub's authorization prompts. The app waits for the runtime to complete sign-in. Use **Cancel operation** to retire the pending flow if necessary; an expired code requires a new sign-in.
6. On successful sign-in and catalog retrieval, the header reports **Connected as ...**, and **Available models** is populated from actual Copilot responses. Select a model to fill the preference, then click **Save model preference**, or leave it blank for the runtime default.
7. On later app launches, use **Test connection** to check authentication and refresh models. Sign-in/cached credentials are resolved by the official runtime, not by copying this Visual Studio chat.
8. **Sign out** asks for confirmation and signs out the active account through the runtime. It does not delete Jira or Confluence credentials.

If the native runtime cannot display a device code, check with IT whether it is permitted to reach GitHub OAuth/Copilot endpoints through the approved VPN/proxy and certificate configuration. Do not disable TLS verification or endpoint security. See [GitHub's network troubleshooting guide](https://docs.github.com/en/copilot/how-tos/troubleshoot-copilot/troubleshoot-network-errors).

You need suitable GitHub Copilot access and organization approval. This initial sign-in UI supports a single github.com account, not an Enterprise Cloud custom host/Entra account-selection flow. The SDK account APIs are experimental and isolated behind the runtime adapter. Unsupported authorization steps fail explicitly. If the runtime requests consent to plaintext credential storage, the app refuses and cancels the flow; secure OS credential storage must be available.

No OpenAI key, GitHub personal access token or Atlassian token belongs in AI Configuration. A successful check verifies authentication and the model catalog, not an inference request or agent tools. The header starts as **Not checked** after opening the app; a successful Jira Agent response changes it to **Connected (last request)**. A failed or cancelled request changes it to **Status unknown**, because a Jira or network failure does not necessarily mean Copilot signed out. Use **Test connection** in AI Configuration for a fresh authentication and model check.

When enabled, Copilot will receive your instructions and relevant issue/page content to perform its analysis. Obtain any required workplace approval before sending business content. Atlassian authentication tokens will remain in the local integration services, not agent prompts.

### 6. Update, remove or keep a token

- **Keep it:** load the integration, edit other fields, leave the token input blank and save. The existing token is preserved. Blank does not mean delete.
- **Replace it:** load the integration, paste the new token and click **Save securely**. The old stored token is replaced.
- **Remove it:** click **Delete stored token**, then confirm. This removes the app's stored copy only; it does not revoke the token in Atlassian. Revoke it separately in Atlassian's token management screen if needed.
- **Discard unsaved edits:** click **Reload saved settings** and confirm. This reloads saved settings and clears the token input. Changing integrations also asks before discarding unsaved edits.
- **Paste the token value only:** for copied text such as `token:mytoken`, paste `mytoken` without the prefix, quotes or surrounding spaces. The connection test rejects such wrappers instead of sending them.

Tokens are write-only: the app will not show a saved token again. Keep track of token expiry using your company's approved process. Do not send tokens in screenshots, chats, email or this README.

### 7. Check persistence and change appearance

After saving, close and reopen the app using the same Windows account. Load each integration again: its non-secret fields should remain, and its credential status should indicate a stored token without revealing it.

Use **Switch light / dark** in the sidebar to change appearance. The selected theme is remembered on this PC.

### 8. Search Jira issues

Once GitHub Copilot sign-in succeeds, open **Jira Agent**, enter a request such as `Find my issues that are In Progress`, and select **Search with Copilot**. The JQL generated by Copilot and issue cards are shown after Jira responds; select an issue key to open it in your browser. Select **Load more** when additional pages are available. The default project key does not automatically restrict searches; include the project in your request when needed. This release shows only the latest search's results and uses a temporary visible chat; starting a new conversation clears that display, not necessarily the Copilot runtime's local files. Searches cannot create or modify issues.

To reuse a search, select an example or saved search in **Saved searches and examples**, then click **Use selected**. This fills the request without running it. Replace bracketed placeholders such as `[project key]` and review the scope before searching. To save your own, enter the request and a name, then click **Save as new search**. To rename or change a saved search, select it, click **Use selected**, edit its name or request, and click **Update selected search**. Use **Delete saved search** to remove a saved search after confirmation; examples cannot be deleted or changed, but you can save a customized copy. Saved search names and requests are stored unencrypted in `%LOCALAPPDATA%\AtlassianPilot\workspace.db`, so do not include tokens or other secrets. Use one app instance when editing saved searches to avoid overwriting changes from another instance.

### Troubleshooting

| What you see | What it means / what to do |
|---|---|
| Form heading does not match the dropdown | Reopen the updated executable; selecting an integration now loads it automatically. If you cancel discarding edits, the original selection remains. |
| An HTTPS validation message | Use a base URL starting with `https://`, without embedded credentials, query parameters or a fragment. |
| An email validation message | Enter the actual Atlassian account email, not a display name. |
| Token field is empty after saving | Expected: it clears deliberately. Check the stored-credential status instead. |
| **Configured / untested** | Settings exist locally; no successful API connection has been established yet. |
| Copilot shows **Not checked** or **Status unknown** | Test the connection in **AI Configuration**. If the test says you are not signed in, use **Sign in with GitHub**. If the runtime cannot reach GitHub, contact IT about approved network access. |
| Authentication failed (401) | Check email/token, expiry and whether the token needs a scoped-token gateway URL. |
| Access denied (403) | Check product access, scopes and organization policy with your administrator. |
| Rate limited (429) | Wait and retry. Long server Retry-After periods are not ignored. |
| Server returned HTML instead of JSON | Check the base URL; a login page or redirect is not a successful API response. |
| A test will not start | Save edits first. Tests intentionally use saved settings and tokens only. |
| GitHub secure credential storage failed | The app refuses plaintext storage. Ask IT to check Windows Credential Manager access. |
| Jira Agent asks you to sign in | Sign in through **AI Configuration** first. If the device-code request fails, consult IT about the native runtime's approved network access. |
| Confluence Agent shows an availability message | Confluence agent operations are not implemented yet. |
| Navigation is blocked while editing settings | Save your changes or reload the integration to discard them before leaving. Closing with unsaved changes also asks for confirmation. |
| Local storage operation fails | Check that your Windows account can write to `%LOCALAPPDATA%\AtlassianPilot`; contact workplace IT if access is restricted. |
| Credentials fail after moving to another Windows profile/PC | Re-enter tokens on that profile/PC. DPAPI-encrypted tokens are not portable credentials. |

The direct Jira search API, pagination and Copilot-assisted natural-language searches have been exercised read-only with an authorized account. Each user's Copilot access and network availability still need to be verified.

## Local data and security

Data resides in `%LOCALAPPDATA%\AtlassianPilot\workspace.db`. Non-secret configuration is stored as SQLite columns. Credential columns contain only DPAPI ciphertext; credentials and settings are updated in one atomic database statement. This intentionally keeps encrypted credentials in SQLite rather than separate secret files, avoiding partial updates across two stores.

DPAPI binds ciphertext to your Windows user profile. It does not protect against a compromised process running as that same user. Copying the database to another computer/profile is not a credential migration strategy; re-enter credentials there. The entire database is not encrypted. Future conversation/audit content can contain sensitive business data and will need appropriate retention controls.

Tokens are not data-bound, returned in settings models, written to logs or stored in browser storage. No credentials are needed to run this phase. Tests use only dummy tokens and temporary databases, never real Atlassian access.

Local storage does **not** mean offline AI: when searching with Copilot, your instructions and returned Jira issue details are sent to GitHub Copilot. Your account needs appropriate Copilot access and organization policy approval. This app will not inherit Visual Studio conversations, tools or credentials automatically. The official Copilot runtime may retain session data (including instructions and tool results) under `%LOCALAPPDATA%\AtlassianPilot\copilot`; starting a new visible conversation or closing the app does not guarantee deletion of those files. Do not enter secrets into the chat, and follow your employer's data handling rules.

## Architecture and next phases

```text
src\
  AtlassianPilot.Desktop\        WPF views, view models, themes and branding
  AtlassianPilot.Core\           Domain configuration and documented service interfaces
  AtlassianPilot.Infrastructure\ SQLite and Windows credential protection
  AtlassianPilot.Agents\         Reserved project for agent policies and tools
 tests\
  AtlassianPilot.UnitTests\      Foundation validation and real DPAPI/SQLite tests
```

Direct SQLite is used for the small foundation schema instead of introducing EF Core. The schema is versioned and future migrations will preserve existing data.

1. **Foundation:** runnable branded desktop shell and secure local settings (completed).
2. **Integrations:** Jira/Confluence tests and official Copilot runtime/sign-in/model-discovery implementation (device-code request verified; complete authorization remains to be tested).
3. **Jira Agent:** read-only issue search implemented; create, comment, update and clone remain planned, with explicit approvals for writes.
4. **Confluence Agent:** real search, create/update, content improvement and explainable duplicate analysis.
5. **Advanced features:** persisted conversations, activity reporting, filters and retention controls.
6. **Packaging:** regression/security review, performance checks and Windows distribution.

Each capability will include backend/service logic, desktop UI, validation and tests before moving on. Initial Atlassian compatibility targets **Cloud**, not Data Center. Non-idempotent modifications will never be blindly retried.

## Help, authorship and copyright

The sidebar displays **Developed by Dawood Ali** and **Personal use only**. Open **Help & About** for a short configuration guide and the application's copyright notice. The full beginner setup instructions are above in this README.

**Copyright (c) 2026 Dawood Ali. All rights reserved.**

Developed by Dawood Ali for personal use only. No permission to redistribute or commercially use the original application code or branding is granted by this notice. Third-party components remain subject to their respective licenses; this notice does not claim ownership of those components or override their license terms.

AtlassianPilot is an independent personal project, not affiliated with or endorsed by Atlassian or GitHub. Their names and trademarks belong to their respective owners.
