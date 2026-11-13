# AtlassianPilot

**Your Work. Your Agents. Your Control.**

A single-user Windows WPF desktop workspace for GitHub Copilot-powered Jira and Confluence agents.

## Current release: Phase 1 foundation

Implemented:
- .NET 10 WPF shell using MVVM and dependency injection.
- Original compass/spark branding, SVG logo, vector window/taskbar logo, generated executable ICO and startup splash.
- Dashboard and seven navigation destinations with clear phase availability.
- Light/dark themes persisted locally.
- Jira/Confluence configuration and optional Copilot model preference.
- API token replacement, preservation and explicitly confirmed deletion.
- SQLite persistence; tokens encrypted using Windows DPAPI for the current Windows user.
- HTTPS URL/email validation, startup error reporting and storage/credential tests.

**Not enabled yet:** GitHub sign-in, Copilot SDK runtime, connection tests, agent chat/tool execution, conversation history and agent audit logs. These sections explain their availability; they do not generate sample results or make integration requests. A saved configuration is not a verified connection.

## Run on Windows

Requires Windows 10/11 and the .NET 10 SDK with WPF support. `global.json` pins SDK 10.0.404 (latest patch roll-forward).

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

Launch `artifacts\win-x64\AtlassianPilot.Desktop.exe`. No browser, local web server, administrator privileges or cloud storage are required by the app. Workplace execution/installation policies still apply. A signed installer and shortcut packaging are planned for Phase 6.

## Beginner setup guide

You do not need to know programming or run the developer commands above to configure the published app.

### 1. Open the app

On this development PC, open:

```text
C:\Repo\AtlasianFriend\artifacts\win-x64\AtlassianPilot.Desktop.exe
```

You can right-click the executable and use **Show more options > Send to > Desktop (create shortcut)** if your Windows configuration offers that option. Keep the entire `win-x64` folder together: the executable needs the accompanying files. On another PC, copy that whole folder to a location your workplace permits, then open the executable there.

The initial dashboard shows **Not configured** for Jira/Confluence and **Not connected** for Copilot. This is expected.

> **Current limitation:** Phase 1 can save configuration, protect tokens and change themes. It cannot yet test connections, sign in to Copilot, search Jira/Confluence or execute agent instructions. Configuring it now prepares for the next phase; it does not activate those features.

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

You can manage Atlassian tokens at [Atlassian API tokens](https://id.atlassian.com/manage-profile/security/api-tokens). A token does not grant more permissions than its owning account; expiry, scopes and your organization's policies can also limit access. Phase 1 does not validate any token against Atlassian yet.

### 3. Save Jira settings

1. Click **Integrations & Settings** in the sidebar (or **Configure integrations** on the dashboard).
2. In the **Integration** dropdown, select **Jira**.
3. Click **Load / discard edits**. Selecting the dropdown alone does not load that integration. Check that the form heading now says **Jira**.
4. Enter your Jira **Base URL (HTTPS)**.
5. Enter your **Atlassian email**.
6. Enter the **Default project key**, or leave it blank if you are unsure. Do not enter an issue ID such as `ABC-123` here.
7. Paste your token into **API token (write-only)**. The characters are masked.
8. Click **Save securely**.
9. Check the bottom status message for **Settings saved locally. Connection has not yet been tested.** The token input clears, and the form indicates that a token is securely stored.

The dashboard should now show **Configured / untested** for Jira. That means the token was stored, not that Jira authentication succeeded.

### 4. Save Confluence settings

1. Stay in **Integrations & Settings**.
2. Select **Confluence** from the dropdown, then click **Load / discard edits**. Save any Jira edits first: loading a form discards unsaved inputs.
3. Check that the form heading says **Confluence**.
4. Enter the Confluence **Base URL (HTTPS)**, normally `https://your-company.atlassian.net/wiki` for Cloud.
5. Enter the **Atlassian email** belonging to your token.
6. Enter the **Default space key**, or leave it blank if you are unsure. Do not enter a page ID, page URL or space name unless that name is also its key.
7. Paste the API token into the masked field and click **Save securely**.
8. Check the saved/untested status message.

Jira and Confluence credentials are saved separately. You may use the same token in both only if its account, permissions and token scope support both products; saving it for Jira does not automatically save it for Confluence.

### 5. Configure GitHub Copilot: what to do now

1. Click **AI Configuration** in the sidebar.
2. Check that the form heading says **Copilot**.
3. Leave **Preferred model (optional)** blank for now. This is the recommended choice if you do not know an available model identifier.
4. Click **Save model preference** if you changed the preference. Saving an empty preference keeps the default-model choice.

**There is no Copilot sign-in button in this release.** The header will continue to say **GitHub Copilot / Not connected**. You do not need to obtain an OpenAI key, paste a GitHub personal access token or supply an Atlassian token in this section.

The next integration phase will add the official [GitHub Copilot SDK](https://github.com/github/copilot-sdk), supported GitHub sign-in and model discovery. You will need a GitHub account with suitable Copilot access and workplace permission to use it. Signing into Copilot in Visual Studio does not automatically connect this app. Exact sign-in steps will be documented when that feature is implemented; no CLI installation or sign-in is required just to use Phase 1.

When enabled, Copilot will receive your instructions and relevant issue/page content to perform its analysis. Obtain any required workplace approval before sending business content. Atlassian authentication tokens will remain in the local integration services, not agent prompts.

### 6. Update, remove or keep a token

- **Keep it:** load the integration, edit other fields, leave the token input blank and save. The existing token is preserved. Blank does not mean delete.
- **Replace it:** load the integration, paste the new token and click **Save securely**. The old stored token is replaced.
- **Remove it:** click **Delete stored token**, then confirm. This removes the app's stored copy only; it does not revoke the token in Atlassian. Revoke it separately in Atlassian's token management screen if needed.
- **Discard unsaved edits:** select the integration and click **Load / discard edits**. This reloads its saved settings and clears the token input.

Tokens are write-only: the app will not show a saved token again. Keep track of token expiry using your company's approved process. Do not send tokens in screenshots, chats, email or this README.

### 7. Check persistence and change appearance

After saving, close and reopen the app using the same Windows account. Load each integration again: its non-secret fields should remain, and its credential status should indicate a stored token without revealing it.

Use **Switch light / dark** in the sidebar to change appearance. The selected theme is remembered on this PC.

### Troubleshooting

| What you see | What it means / what to do |
|---|---|
| Form heading does not match the dropdown | Click **Load / discard edits** to load the chosen integration. Unsaved edits will be discarded. |
| An HTTPS validation message | Use a base URL starting with `https://`, without embedded credentials, query parameters or a fragment. |
| An email validation message | Enter the actual Atlassian account email, not a display name. |
| Token field is empty after saving | Expected: it clears deliberately. Check the stored-credential status instead. |
| **Configured / untested** | Settings exist locally; no successful API connection has been established yet. |
| Copilot remains **Not connected** | Expected in Phase 1. Sign-in/runtime integration has not been implemented. |
| Jira Agent or Confluence Agent shows an availability message | Agent execution is not implemented yet; there is no chat action to enable by changing settings. |
| Navigation is blocked while editing settings | Save your changes or reload the integration to discard them before leaving. Closing with unsaved changes also asks for confirmation. |
| Local storage operation fails | Check that your Windows account can write to `%LOCALAPPDATA%\AtlassianPilot`; contact workplace IT if access is restricted. |
| Credentials fail after moving to another Windows profile/PC | Re-enter tokens on that profile/PC. DPAPI-encrypted tokens are not portable credentials. |

A successful Phase 1 setup means the app opens, your settings survive restart, tokens remain masked, and configured integrations are explicitly **untested**. Actual connection tests and natural-language agent operations come in later phases.

## Local data and security

Data resides in `%LOCALAPPDATA%\AtlassianPilot\workspace.db`. Non-secret configuration is stored as SQLite columns. Credential columns contain only DPAPI ciphertext; credentials and settings are updated in one atomic database statement. This intentionally keeps encrypted credentials in SQLite rather than separate secret files, avoiding partial updates across two stores.

DPAPI binds ciphertext to your Windows user profile. It does not protect against a compromised process running as that same user. Copying the database to another computer/profile is not a credential migration strategy; re-enter credentials there. The entire database is not encrypted. Future conversation/audit content can contain sensitive business data and will need appropriate retention controls.

Tokens are not data-bound, returned in settings models, written to logs or stored in browser storage. No credentials are needed to run this phase. Tests use only dummy tokens and temporary databases, never real Atlassian access.

Local storage does **not** mean offline AI: once Copilot is enabled, instructions and relevant Atlassian content will be processed by GitHub Copilot. Your account needs appropriate Copilot access and organization policy approval. This app will not inherit Visual Studio conversations, tools or credentials automatically.

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

1. **Foundation:** runnable branded desktop shell and secure local settings (current).
2. **Integrations:** official Copilot SDK authentication/runtime, model discovery and Jira/Confluence connection tests.
3. **Jira Agent:** separate tool registry, real search, create, comment, update and clone; explicit approvals for writes.
4. **Confluence Agent:** real search, create/update, content improvement and explainable duplicate analysis.
5. **Advanced features:** persisted conversations, activity reporting, filters and retention controls.
6. **Packaging:** regression/security review, performance checks and Windows distribution.

Each capability will include backend/service logic, desktop UI, validation and tests before moving on. Initial Atlassian compatibility targets **Cloud**, not Data Center. Non-idempotent modifications will never be blindly retried.

## Help, authorship and copyright

The sidebar displays **Developed by Dawood Ali** and **Personal use only**. Open **Help & About** for a short configuration guide and the application's copyright notice. The full beginner setup instructions are above in this README.

**Copyright (c) 2026 Dawood Ali. All rights reserved.**

Developed by Dawood Ali for personal use only. No permission to redistribute or commercially use the original application code or branding is granted by this notice. Third-party components remain subject to their respective licenses; this notice does not claim ownership of those components or override their license terms.

AtlassianPilot is an independent personal project, not affiliated with or endorsed by Atlassian or GitHub. Their names and trademarks belong to their respective owners.
