# AtlassianPilot

**Your Work. Your Agents. Your Control.**

A single-user Windows WPF desktop workspace for GitHub Copilot-powered Jira and Confluence agents.

## Current build: Jira and Confluence agents with approved writes

Implemented:
- .NET 10 WPF shell, original branding, light/dark themes and personal-use attribution.
- SQLite settings and Windows DPAPI-encrypted Atlassian credentials.
- Automatic integration-form switching with confirmation before discarding unsaved edits.
- Real read-only Jira/Confluence account connection tests using saved settings only.
- Authentication/permission errors, bounded response validation, transient read retries, rate-limit handling, timeouts and cancellation.
- Official `GitHub.Copilot.SDK` 1.0.32 with its bundled, checksum-verified Copilot runtime 1.0.110.
- GitHub device authorization UI, secure-storage enforcement, sign-out and actual model discovery.
- A read-only Jira issue-search service, a Jira Agent screen, actual issue cards and continuation pagination. Search results are taken only from Jira responses; the UI does not present model-generated issue facts as API results.
- Read-only issue details by issue key, including a readable description and raw Jira fields.
- A dedicated **Create Jira** chat: refine rough notes into a summary, user story and acceptance criteria; request revisions or edit content directly.
- Project-specific Jira issue types and create fields, optional compatible reference defaults, required-field checks, and an exact payload preview. Creation requires a separate explicit confirmation and returns the Jira issue link.
- A **Jira Actions** screen for issue summary/description updates, comments, assignment by account ID, available workflow transitions, issue links, and clone previews. All writes require explicit approval of the exact payload and are submitted once without automatic retry. Clone copies summary/description only and is blocked when unsupported required fields lack defaults.
- A Confluence Agent chat for page search, reading, analysis, and Copilot-assisted create/update drafts. Chat proposals flow into the editable page form; pages fetched for analysis are also shown with a **Load page for editing** action. New pages use the configured default space when the prompt omits one. Every page write requires an exact preview and separate explicit approval.
- Local Agent History and Activity Logs, with transcript and audit retention limits, deletion controls, and activity entries restricted to non-sensitive labels.
- Offline regression tests using mocked HTTP/runtime services and real SQLite/DPAPI.

**Navigation:** the sidebar has five items: **Home**, **Jira** (tabs: Ask Jira, Create issue, Change issue), **Confluence**, **History** (tabs: Conversations, Activity log) and **Settings** (tabs: Accounts, AI model). Advanced options (saved searches, the Confluence page editor, reference fields, and the individual Jira actions) are collapsed until you open them.

**Verification:** 175 automated tests pass, including Confluence chat-to-editor create/update paths and model-list empty-state coverage. Jira writes and clone safety are covered with mocked API tests; no new live Jira write has been performed. A live read-only Confluence search returned five results and a continuation token; no page content was printed and no writes were performed. Security review found no substantiated vulnerabilities. API responses and result lists are bounded, network calls have timeouts, and the complete regression suite ran in 2.3 seconds in this environment. Saved configuration is not a verified connection; account verification does not establish every project/space permission or model inference.

**Still planned:** Jira search remains read-only, and issue details do not include complete paginated nested collections or issue history. Jira Actions are deliberate manual workflows rather than natural-language write commands; cloning intentionally copies only summary and description, and updates currently edit only summary/description. Confluence page writes require exact-preview approval.

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
dotnet publish src\AtlassianPilot.Desktop\AtlassianPilot.Desktop.csproj -c Release -r win-x64 --self-contained true -o artifacts\win-x64-agent-ready
```

Launch `artifacts\win-x64-agent-ready\AtlassianPilot.Desktop.exe`. The desktop app does not require a local web server, administrator privileges or cloud storage. GitHub device sign-in uses a browser. Workplace execution/installation policies still apply. A signed installer and shortcut packaging remain future distribution work.

## Beginner setup guide

You do not need to know programming or run the developer commands above to configure the published app.

### 1. Open the app

On this development PC, open:

```text
C:\Repo\AtlasianFriend\artifacts\win-x64-agent-ready\AtlassianPilot.Desktop.exe
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

1. Click **Settings** in the sidebar (Accounts tab) (or **Configure integrations** on the dashboard).
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

Once GitHub Copilot sign-in succeeds, open **Jira** (Ask Jira tab), enter a request such as `Find my issues that are In Progress`, and select **Search with Copilot**. The JQL generated by Copilot and issue cards are shown after Jira responds; select an issue key to open it in your browser. Select **Load more** when additional pages are available. The default project key does not automatically restrict searches; include the project in your request when needed. Jira conversations are saved locally for 30 days and can be reviewed or deleted under **History**. Starting a new conversation clears the visible transcript for the next session; it does not guarantee deletion of data retained by the Copilot runtime. Searches cannot create or modify issues.

To reuse a search, select an example or saved search in **Saved searches and examples**, then click **Use selected**. This fills the request without running it. Replace bracketed placeholders such as `[project key]` and review the scope before searching. To save your own, enter the request and a name, then click **Save as new search**. To rename or change a saved search, select it, click **Use selected**, edit its name or request, and click **Update selected search**. Use **Delete saved search** to remove a saved search after confirmation; examples cannot be deleted or changed, but you can save a customized copy. Saved search names and requests are stored unencrypted in `%LOCALAPPDATA%\AtlassianPilot\workspace.db`, so do not include tokens or other secrets. Use one app instance when editing saved searches to avoid overwriting changes from another instance.

To inspect an issue, ask **Show details for ABC-123**, replacing the example with your issue key. The app displays a readable summary and description, plus the raw Jira fields. The description rendering is simplified; consult the raw fields for formatting and non-text content. Jira may paginate nested collections such as comments, and issue history is not included. Open the issue in Jira for complete collections and history. The request and issue key go to Copilot, but the full details response stays local and is not available for Copilot to summarize. Results depend on your Jira permissions.

The search agent can still prepare read-only draft previews. To refine rough notes and create an issue after approval, use **Create Jira**, described below. To inspect create-screen fields separately, ask **Which fields are available when creating a Task in project WOR?** Fields vary by project, issue type and permissions.

### 9. Refine rough notes and create a Jira issue

1. Open **Jira** in the sidebar, then the **Create issue** tab. Paste your rough content into **Rough content or revision instructions**, then click **Refine story with Copilot**. You do not need to supply a title or a special prompt.
2. Review the proposed summary and description, including the user story, background, scope and acceptance criteria. Edit them directly, or type further chat instructions and refine again. Technical names should be preserved; check all facts and resolve open questions before approval.
3. Enter the project key and click **Load issue types**. Select an actual type returned by Jira. For example, a project may offer Task but not Story; you can use a story-formatted description in a Task, but the app will not silently substitute a type.
4. Optionally enter a reference key, such as `WOR-742`, then click **Load fields / reference defaults**. The reference must have the same project and issue type. Compatible fields such as assignee, priority, labels, dates, teams and options can be prefilled for your review. No reference is loaded automatically. Summary, description, parent, reporter, status, history, attachments, links, restrictions and unsupported/computed fields are not copied. Enter an intended parent/epic separately.
5. Review the field editors. Use dropdown options where offered, account IDs for user fields, and `yyyy-MM-dd` for dates. Less common complex fields use JSON and show format guidance. Blank optional fields are omitted and may receive Jira defaults. Unsupported required fields without defaults block creation rather than being ignored.
6. Click **Prepare approval preview**. The app reloads Jira create metadata and checks required fields, supported formats and supplied options. Review the destination and exact field payload, including the description in Atlassian Document Format. Jira still performs final permission, hierarchy and workflow validation.
7. Click **Approve and create Jira**, then confirm the dialog. Only this action sends one create request. The resulting issue key opens Jira in your browser. Declining confirmation makes no Jira changes.

Any content, configuration, parent or field edit invalidates the prepared approval. Approvals expire after 15 minutes and can be used only once. Changing saved Jira settings or credentials also invalidates a prepared submission. The app never automatically retries creation. If a timeout, lost response or unexpected response makes the outcome uncertain, check Jira before preparing another approval to avoid duplicates. After a successful creation, start a new draft before creating another issue.

The editable Create Jira form values and prepared preview are temporary and are not restored when reopening the app. Conversation messages are saved locally for 30 days and can be reviewed or deleted under **Agent History**; closing the app warns before discarding unsaved form content. The Copilot runtime may retain local session data. Rough notes and the current summary/description are sent to Copilot, but reference field values and creation payloads are not sent by this drafting workflow. Do not include secrets. Automated creation tests use mocked Jira responses; live validation deliberately stops before approval and does not create test issues in your workspace.

### 10. Update, comment, assign, transition, link or clone a Jira issue

Open **Jira**, then the **Change issue** tab. Enter the source issue key, then use the relevant action card. Updates support summary and description; comments are converted to plain-text paragraphs. Assignment requires the target user's Jira account ID (not a display name or email). Load available transitions and link types from Jira, choose an exact returned option, and review the link direction labels. Cloning asks for a destination project and issue type, loads the source issue and destination create metadata, and previews a new issue containing only the source summary and description. Required destination fields without defaults block clone preparation; use **Create Jira** to provide additional fields. Comments, attachments, links, history, and other fields are not cloned.

Every action first creates an exact payload preview; editing inputs discards it. Confirm the preview on screen, then select **Approve and execute** and confirm the warning. Jira applies final permissions and workflow rules. Each approval expires after 15 minutes and is single-use; requests are not retried. If a timeout or interrupted response makes the outcome uncertain, check Jira before trying again. All actions and outcomes use fixed labels in Activity Logs without recording prompts or payloads.

### 11. Use the Confluence Agent, history and activity log

Open **Confluence Agent** and make requests in chat to search/read pages, summarize or compare results, create a page, or change an existing page. For a requested edit, Copilot should retrieve the current page and propose the complete revised title and body; title-only changes preserve the body. The proposal appears in the editable form. A page fetched for analysis is also shown in the results with **Load page for editing** if you need to make or complete an edit manually. New pages use the configured default space if your prompt omits one; otherwise specify the space. Search and page reads use your saved Confluence account. Page titles and content sent to Copilot are online business data; follow your organization's data-handling rules. Page content is treated as untrusted input, and Copilot cannot publish directly.

Review the complete exact payload in **Prepare exact save preview**. For updates, the current page is replaced with escaped plain-text paragraphs; existing rich formatting is not preserved. Any edit invalidates the preview, which expires after 15 minutes. Only **Approve and save page** followed by confirmation sends the write request; ambiguous results are not retried. Use **Open saved Confluence page** to inspect the result.

**History > Conversations** stores Jira and Confluence chat messages in the local SQLite database for 30 days (up to 200 messages per conversation). Use its agent filter to review transcripts, delete one, or clear all. **History > Activity log** records only fixed agent/action/outcome labels, not prompts, page text, issue content, or request payloads; records are retained for 90 days and capped at 5,000. The database is not encrypted, so avoid secrets and sensitive content, and delete records when no longer needed. Confluence page drafts and the Jira creation form values are not restored from history.

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
| Confluence Agent cannot find or save a page | Check the saved Confluence connection, space key, page ID, and the account's space permissions. Updates replace rich formatting with plain paragraphs; review the exact preview before approving. |
| Navigation is blocked while editing settings | Save your changes or reload the integration to discard them before leaving. Closing with unsaved changes also asks for confirmation. |
| Local storage operation fails | Check that your Windows account can write to `%LOCALAPPDATA%\AtlassianPilot`; contact workplace IT if access is restricted. |
| Credentials fail after moving to another Windows profile/PC | Re-enter tokens on that profile/PC. DPAPI-encrypted tokens are not portable credentials. |

The direct Jira search API, pagination and Copilot-assisted natural-language searches have been exercised read-only with an authorized account. Each user's Copilot access and network availability still need to be verified.

## Local data and security

Data resides in `%LOCALAPPDATA%\AtlassianPilot\workspace.db`. Non-secret configuration and locally retained conversation messages/activity labels are stored in SQLite. Credential columns contain only DPAPI ciphertext; credentials and settings are updated in one atomic database statement. This intentionally keeps encrypted credentials in SQLite rather than separate secret files, avoiding partial updates across two stores.

DPAPI binds ciphertext to your Windows user profile. It does not protect against a compromised process running as that same user. Copying the database to another computer/profile is not a credential migration strategy; re-enter credentials there. The database is not encrypted: transcripts can include business content and are retained for 30 days; activity entries use fixed labels and are retained for 90 days (up to 5,000 records). Use the in-app delete/clear controls and follow workplace data-handling rules.

Tokens are not data-bound, returned in settings models, written to logs or stored in browser storage. The automated regression suite requires no credentials and uses only dummy tokens and temporary databases. Using the application with real Jira or Copilot requires your configured credentials and access.

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
2. **Integrations:** Jira/Confluence connection tests and official Copilot runtime/sign-in/model discovery (implemented; verify with your own accounts).
3. **Jira Agent:** search, issue details, saved searches, approval-gated issue creation, and manual approval-gated updates/comments/assignment/transitions/links/limited cloning (implemented). Search remains read-only.
4. **Confluence Agent:** real search/read, Copilot-assisted analysis and draft proposals, plus approval-gated page creation/updates (implemented; formatting is simplified on update).
5. **Agent history and activity:** local transcript browsing/deletion, restricted activity labels and retention controls (implemented; SQLite is not encrypted).
6. **Packaging and release assurance:** a self-contained Windows x64 build and portable archive are published locally. Security review, bounded-response/performance checks and live read-only Confluence search smoke test are complete. A trusted signed installer still requires an organization-issued code-signing certificate and identity; no certificate was available, so the release remains unsigned and portable.

Each capability will include backend/service logic, desktop UI, validation and tests before moving on. Initial Atlassian compatibility targets **Cloud**, not Data Center. Non-idempotent modifications will never be blindly retried.

### Possible Jira enhancements

The [Jira Cloud REST API v3 issue documentation](https://developer.atlassian.com/cloud/jira/platform/rest/v3/api-group-issues/) can guide future enhancements, including complete comment/history pagination, richer custom-field editing for updates and clone, copying compatible fields/attachments/links when cloning, and Copilot-assisted natural-language preparation of Jira Actions. Current operations depend on Jira permissions and project configuration; every write must remain previewed and explicitly approved.

## Help, authorship and copyright

The sidebar displays **Developed by Dawood Ali** and **Personal use only**. Open **Help & About** for a short configuration guide and the application's copyright notice. The full beginner setup instructions are above in this README.

**Copyright (c) 2026 Dawood Ali. All rights reserved.**

Developed by Dawood Ali for personal use only. No permission to redistribute or commercially use the original application code or branding is granted by this notice. Third-party components remain subject to their respective licenses; this notice does not claim ownership of those components or override their license terms.

AtlassianPilot is an independent personal project, not affiliated with or endorsed by Atlassian or GitHub. Their names and trademarks belong to their respective owners.
