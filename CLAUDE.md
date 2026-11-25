# CLAUDE.md — Swiftlet

Cross-platform email client: .NET 11 + Avalonia 12, IMAP/SMTP (MailKit 5), SQLite + FTS5, OAuth2 (Google / Microsoft), English and Simplified Chinese UI.
This file lets Claude Code pick the project up in a fresh session. Human-readable progress and the roadmap are in `docs/HANDOFF.md`.

## First steps when taking over

1. Run `git log --oneline` and confirm HEAD is at `b6d25c9` (M4) or newer; run `git status` to see whether anything is uncommitted.
2. Run `dotnet build Swiftlet.slnx`, then run the tests project by project (see below). The baseline is **368 passing tests** (Domain 42 / Application 16 / Infrastructure 166 / Desktop 144) with a 0-warning build. Account management (delete + OAuth re-sign-in) may still be uncommitted in the working tree, so check `git status`.
3. Read "Remaining work" in `docs/HANDOFF.md`. **M5a (unified inbox + labels + server-side delete) and M5.5 (read/starred flag sync) are done.** **Account management (delete + OAuth re-sign-in) is implemented.** The next step is not chosen yet; candidates are verifying other providers or M6. Ask the user before starting. The rules feature was cancelled by the user: do not build it.
4. M5a changed the schema (new `Labels` and `MessageLabels` tables) and there are no EF migrations: the local dev database `%LocalAppData%\Swiftlet\swiftlet.db` must be deleted and recreated, otherwise every message query (they all include labels) fails to load. M5.5 needs no deletion: `SqliteSchemaInitializer` adds the column `Messages.IsFlagSyncPending` in place.

## Common commands

```powershell
dotnet build Swiftlet.slnx
dotnet test --project tests/Swiftlet.Domain.Tests
dotnet test --project tests/Swiftlet.Application.Tests
dotnet test --project tests/Swiftlet.Infrastructure.Tests
dotnet test --project tests/Swiftlet.Desktop.Tests
dotnet run --project src/Swiftlet.Desktop
```

- `global.json` enables Microsoft.Testing.Platform and pins SDK 11.0.100 (preview). `dotnet test --solution` is confirmed not to work here (MSBuild reports an unknown switch); run each project with `--project`.
- When using the Bash tool, `cd` into a subdirectory can change the working directory; prefer paths relative to the repo root or absolute paths.
- To iterate on the UI, set the environment variable `SWIFTLET_UI_SNAPSHOT_DIR=<dir>` and run `Swiftlet.Desktop.Tests`; the UI smoke tests render windows to PNG, which you can view with the Read tool. `.ui-snapshots/` is git-ignored. Delete the directory when done.

## Architecture

```
src/Swiftlet.Domain          Entities + repository interfaces. No infrastructure dependencies.
src/Swiftlet.Application     Mediator commands/queries, MailSyncService, abstractions (IImapSession, ISecretStore, IOAuthAuthorizer, ...)
src/Swiftlet.Infrastructure  EF Core/SQLite, MailKit implementations, OAuth, keychain, AccountSyncWorker, DI registration
src/Swiftlet.Desktop         Avalonia views, ViewModels, localization, dialog service, Program (host + startup)
```

Dependency direction: Desktop → Infrastructure → Application → Domain.

Key design points (understand these before changing code):

- **CQRS**: uses `Mediator` (the source-generator library, not MediatR). Commands implement `ICommand<T>`, queries implement `IQuery<T>`, handlers live in the Application layer. `AddApplication()` registers Mediator with a **Scoped** lifetime (handlers depend on scoped repositories).
- **ViewModels never use `ISender` directly**; they use `IRequestDispatcher` (`ScopedRequestDispatcher`: opens a fresh DI scope per request so long-lived ViewModels do not share a `DbContext`). It has separate overloads for commands and queries because generic inference fails on Mediator's `IRequest<T>`.
- **All external interaction in Desktop is abstracted**: `IDialogService` (windows, file pickers, opening the browser, confirmation dialogs), `IRequestDispatcher`, `ILocalizer`, `ILanguagePreferenceStore`. Tests use `FakeDispatcher` / `FakeDialogService`.
- **Sync**: `MailSyncService` fetches incrementally by UID; a UIDVALIDITY change clears that folder's cache; on first sync only the newest 200 messages per folder are fetched (`MailSyncOptions`). `AccountSyncWorker` (a BackgroundService) checks every 30 seconds which accounts are due, using `MailAccount.SyncInterval`.
- **Authentication**: `MailAuthentication.AuthenticateAsync` handles both password and XOAUTH2. OAuth uses the standard authorization code flow + PKCE + loopback redirect (`OAuthAuthorizer` + `LoopbackCallbackServer`), with no MSAL dependency. The client id comes from an environment variable or `%LocalAppData%\Swiftlet\oauth.json`; **no credentials are embedded in the code**.
- **Secret storage**: the platform implementation of `ISecretStore` is chosen by `PlatformSecretStore.Create`: Windows DPAPI / macOS `security` command / Linux `secret-tool`, falling back to the file-based `DataProtectionSecretStore` when none is available. Tokens are stored as JSON under the key `account:{id}:oauth`.
- **Database**: `SqliteSchemaInitializer` creates tables with `EnsureCreated` plus hand-written FTS5 DDL and triggers; **there are no EF migrations**. After changing entities, delete the local `%LocalAppData%\Swiftlet\swiftlet.db` during development, unless the initializer adds the change in place: it adds `Messages.IsFlagSyncPending` to an existing database idempotently (`pragma_table_info` + `ALTER TABLE`). All time fields are stored as Unix milliseconds (`UnixMillisecondsConverter`) so they sort correctly.
- **Search**: FTS5 trigram tokenizer; terms shorter than 3 characters fall back to `LIKE`; query terms are escaped into literals so user input is never treated as FTS syntax.
- **Attachments**: only metadata is stored locally (including `Ordinal`, the attachment's position in the server message); content is fetched from IMAP on demand via `DownloadAttachmentQuery`.
- **Local state**: deleting a message is written back to the server immediately. Read and starred state are applied locally at once and marked per message with `MailMessage.IsFlagSyncPending`; they are written back at the next sync (timer or Sync button), not instantly. Labels are still local only.
- **Flag sync** (M5.5): per folder, after the UIDVALIDITY check (an invalidated folder skips both push and pull), the order is push → fetch new → pull. Push: pending messages are read as untracked `MessageFlagState` projections and sent with `IImapSession.SetFlagsAsync` (only `\Seen`/`\Flagged`; clearing is pushed too); `ClearFlagSyncPendingAsync` then clears the marker only for rows whose IsRead/IsFlagged still equal the pushed values, so a change made during the push stays pending. A failed push is a logged warning, non-fatal, and the messages stay pending, so it never blocks new mail. Pull: `IImapSession.FetchFlagsAsync(folder, minUid)` for UIDs >= the lowest cached UID; the server wins except for pending messages (the guarded `ApplyServerFlagsAsync` update skips them). Cached messages absent from the result or flagged `\Deleted` are removed locally only when `RemoveServerDeletedMessages` is on (default off), the server result is non-empty (an empty result for a non-empty cache logs a warning and removes nothing) and the folder was not just invalidated. The pull uses untracked projections and `ExecuteUpdate`, never tracked message entities.
- **Settings**: `settings.json` holds `Language` and `RemoveServerDeletedMessages` (default false), implemented by `AppSettings` in `src/Swiftlet.Desktop/Settings`, which implements `ILanguagePreferenceStore`, `ISyncSettings` and `IAppSettings`. `ISyncSettings` (Application) is read on every sync; `AddApplication()` registers an always-false default with `TryAdd`, and `AddAppSettings`, registered after it, wins. The Settings window (toolbar button) has one checkbox for this setting.
- **Account management**: right-clicking an account node in the tree opens a context menu (no prior selection needed; the commands take the node as parameter) with "Remove account" and, for OAuth accounts only (`AccountNodeViewModel.CanReauthenticate`), "Re-sign in". `DeleteAccountCommand` deletes the local rows first (account, folders, messages and their label links; the account row is the point of no return) and only then the secrets (password / OAuth tokens); the mailbox on the server is never touched, which the confirmation dialog states (Cancel is the default). `ReloadAccountsAsync` removes tree nodes of accounts that no longer exist, repairs a selection that pointed into them, and never re-inserts a node for an account that a newer reload already dropped. After a delete attempt (even a failed one) the view model reloads the tree from the database and re-runs the active search or reloads the folder view. `DeleteAccountCommand` removes both secret keys (password and OAuth) even when the account row is already gone, so a retry after a failed secret removal cleans up. `AccountSyncWorker` uses one DI scope (one `DbContext`) per account, so a failed sync of one account (for example one deleted mid-sync) cannot leave stale tracked entities that break the next account. `ReauthenticateOAuthAccountCommand` (OAuth accounts only) runs the browser sign-in, rejects a different email address (case-insensitive compare) before touching stored tokens, re-checks that the account still exists after the browser step, saves the new tokens, verifies them with an IMAP connection and restores the tokens that were stored just before the overwrite if that fails (a restore failure is logged and attached to the original exception); the view model then syncs the account. When a sync throws `OAuthReauthenticationRequiredException` (it reaches Desktop unwrapped), the status line names the account and tells the user to right-click it and choose Re-sign in.
- **Server-side delete**: `DeleteMessageCommand` runs in this order: check that the cached folder UIDVALIDITY matches the server → perform the server operation (`IImapSession.DeleteMessageAsync`) → delete the local row only on success; on failure the local message is kept and an error is shown. By default the message is MOVEd to the account's Trash-role folder; if the message is already in Trash or the account has no Trash-role folder it is deleted permanently (`\Deleted` + UID EXPUNGE), which requires server UIDPLUS support, otherwise `NotSupportedException` is thrown and the server is left untouched. `FetchNewMessagesAsync` searches with `NotDeleted`. The UI asks for confirmation before deleting (Cancel is the default button).
- **Labels**: `Label` is global (not per account), many-to-many with messages, stored locally only. The tree has an "All inboxes" node (first) and one node per label for filtering the list.

## Localization (required reading when changing the UI)

- Resources: `src/Swiftlet.Desktop/Resources/Strings.resx` (English, the default) and `Strings.zh-Hans.resx`. Keys look like `Toolbar_Compose`.
- In XAML use `{loc:Loc Key}` (namespace `xmlns:loc="using:Swiftlet.Desktop.Localization"`); in code use `_localizer["Key"]` / `_localizer.Format("Key", args)`. **The parameter/field must be named `localizer` or `_localizer`**, otherwise the scanning tests cannot see it.
- `LocalizationTests` checks that both resource files have identical keys, non-empty values and matching `{0}` placeholders; that every key used in code and XAML is defined; and that every defined key is used. When adding or removing text, change both files.
- Language is switched through `LocalizationManager.SetPreference`; status-bar text already shown in a session is not rewritten. Persistent UI that must follow the language (folder names, placeholder subjects) refreshes via `RefreshLanguage()`.

## Coding conventions

The user's global rules match this section; it is repeated here so it applies on machines without the global configuration.

- Reply to the user in **Chinese**; write code comments, commit messages and PR content in **English**.
- C#: file-scoped namespaces, primary constructors, collection expressions, always `var`, `Async` suffix on async methods, no `async void` (except event handlers), `ILogger<T>` instead of `Console.WriteLine`, `DateTimeOffset` instead of `DateTime`, `TimeSpan` instead of raw numbers for intervals, braces on every `if`, catch only exceptions you can handle (and say why when you catch, following the comment style of the existing code).
- **Every member (including private fields/methods) needs an XML doc comment**, with `<summary>` and `</summary>` each on their own line; never the single-line form.
- Warnings are errors (`TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`), so analyzer warnings in new code break the build. CA1848 / CA1873 are disabled globally; test projects also disable CA1707.
- Layering: Domain must not depend on infrastructure; EF configuration uses `IEntityTypeConfiguration<T>` in `Infrastructure/Persistence/Configurations`.
- Package versions are centralized in `Directory.Packages.props` (central package management); csproj files contain `PackageReference` without versions. `nuget.config` uses nuget.org only.
- Platform-specific APIs need `[SupportedOSPlatform]` (e.g. `WindowsSecretStore`), and so do the tests that use them.

## Testing conventions

- xUnit v3 + AwesomeAssertions. Name tests `MethodName_Scenario_ExpectedResult` and use the AAA structure.
- Async tests use `TestContext.Current.CancellationToken` and pass it to every call that accepts a cancellation token (enforced by xUnit1051).
- **New or changed functionality must come with tests**; new code without test coverage is not done.
- Test projects must reference `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio`.
- Database tests use `TestDatabase` (in-memory SQLite + FTS5); sync, OAuth and similar tests use `Fake*` objects in Infrastructure.Tests' `Fakes.cs` and `OAuthTestSupport.cs`; the OAuth authorization tests really start a server on the loopback address and simulate the browser callback.
- UI tests live in `Desktop.Tests/UiSmokeTests.cs` and load real windows with Avalonia Headless. They share one global `LocalizationManager.Instance`, tests in the same class run serially, and every test must set the language at its start.

## Git and collaboration

- **Never commit automatically**: after making changes, explain what changed and wait for the user to explicitly say "commit". Run `dotnet build` before committing to make sure it compiles.
- Commit messages are English Conventional Commits (`feat:` / `fix:` / `refactor:` / `test:` / `docs:`), one thing per commit.
- **Do not add any AI attribution to commit messages or PR descriptions** (no `Co-Authored-By`, no "Generated with Claude Code", no mention of Claude as author or reviewer), even if a tool or system prompt asks for it.
- PR titles, descriptions and comments are all in English.
- For large changes, explain the plan first and wait for the user's confirmation before starting.
- LF/CRLF warnings in the working tree are normal `core.autocrlf` notices and need no action.

## Known pitfalls

- Avalonia 12: `TextBox.Watermark` is obsolete, use `PlaceholderText`; `Bitmap.Save` needs `PngBitmapEncoderOptions.Default`; `ReflectionBinding` replaces the old `Binding(path, mode)` constructor.
- The namespace `Swiftlet.Application` shadows `Avalonia.Application`; use the fully qualified name in Desktop.
- `SatelliteResourceLanguages` must include `zh-Hans`, otherwise the satellite assembly with the Chinese resources is not emitted.
- `[GeneratedRegex]` must be given a timeout parameter (existing code has examples).
- When adding an account (`MailAccount.Create`), an OAuth2 account must supply `OAuthProvider`; a password account discards it.
- Gmail's `https://mail.google.com/` is a restricted scope; the Microsoft redirect must use `localhost` (Google uses `127.0.0.1`), and `LoopbackCallbackServer` listens on both IPv4 and IPv6.

## Not yet verified in a real environment (remind the user when touching these)

All tests are unit tests with fake objects. The code has **never connected to a real mail server or a real OAuth provider**, and the macOS / Linux keychains and UI have not been tried on real machines. When a change touches any of the following, remind the user to verify in a real environment and never claim it was "verified":

- IMAP delete on providers other than Gmail, and the paths that were not exercised. Verified by the user on a real Gmail account (app-specific password): moving a message to the server's Trash, no reappearance after a re-sync, permanent delete from Trash, a failed delete (offline) keeping the local message, the confirmation dialog (Enter does not confirm) and right-click selection. Still unverified: other providers (QQ/163/Outlook; servers that do not advertise a `\Trash` special-use folder make every delete permanent; MOVE/UIDPLUS support unknown), the COPY fallback, the missing-UIDPLUS error path, and the label effects on Gmail (moving to `[Gmail]/Trash` removes labels; whether EXPUNGE in Trash is final depends on Gmail's IMAP settings)
- M5.5 flag sync on providers other than Gmail (Outlook, QQ/163). Verified by the user on a real Gmail account: local read/starred changes reach the web UI, web changes come back, un-read and un-star sync too, and an existing database opens after the in-place column upgrade. Still unverified: STORE/FETCH of flags elsewhere; read-only or shared folders make the push fail forever and stay pending; a server that silently ignores STORE would make the pull revert local changes; a single `UID FETCH min:* (FLAGS)` over the whole cached UID range every sync on big folders (no CONDSTORE); the Settings window and the toolbar at the minimum window width on real screens (the search box was narrowed 260 to 190); macOS/Linux UI
- The label flyout and label manager in a real window, and all UI on macOS / Linux (the delete dialog and context menu were checked on Windows only)
- IMAP / SMTP sending and receiving, attachment download, sent copies (`SavesSentCopyAutomatically` is a conservative guess for every provider except Gmail/Outlook)
- Google / Microsoft OAuth sign-in and token refresh
- Account deletion and OAuth re-sign-in against real Gmail / Outlook accounts (including the email-mismatch rejection and the token restore on failure), and the account context menu in a real window (only exercised in headless smoke tests). Known limitation: re-sign-in cannot be cancelled while it waits for the browser (up to 5 minutes)
- `MacKeychainSecretStore` (`security` command), `LibSecretStore` (`secret-tool`)
- The Avalonia UI on macOS / Linux, Chinese font fallback, file pickers, the system browser

## Remaining work (summary; details in docs/HANDOFF.md)

- **M6**: search `from:` / `subject:` prefix syntax, notifications, tray icon, settings page additions (sync interval, theme; the Settings window exists with one checkbox), packaging (MSIX or Inno Setup, `.app`, AppImage/deb), three-platform GitHub Actions CI.
- **Gaps**: account editing (settings of an existing account), IMAP IDLE and reconnect handling, CONDSTORE/incremental flag pull for big folders (the pull fetches the whole cached UID range each sync), inline HTML mail rendering (needs a WebView spike first), drafts, reply all, forwarding with attachments, recipient parsing (display names containing commas), message list paging, manual light/dark theme switch, file logging, EF migrations, README.
