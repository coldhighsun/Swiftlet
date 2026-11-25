# CLAUDE.md — Swiftlet

Cross-platform email client: .NET 11 + Avalonia 12, IMAP/SMTP (MailKit 5), SQLite + FTS5, OAuth2 (Google / Microsoft), English and Simplified Chinese UI.
This file lets Claude Code pick the project up in a fresh session. Human-readable progress and the roadmap are in `docs/HANDOFF.md`.

## First steps when taking over

1. Run `git log --oneline` and confirm HEAD is at `b6d25c9` (M4) or newer; run `git status` to see whether anything is uncommitted.
2. Run `dotnet build Swiftlet.slnx`, then run the tests project by project (see below). The baseline is **277 passing tests** (Domain 33 / Application 16 / Infrastructure 112 / Desktop 116) with a 0-warning build. M5a may still be uncommitted in the working tree, so check `git status`.
3. Read "Remaining work" in `docs/HANDOFF.md`. **M5a (unified inbox + labels + server-side delete) is done.** The next planned step is **M5.5 (write read/starred state back to the server)**; confirm with the user before starting. The rules feature was cancelled by the user: do not build it.
4. M5a changed the schema (new `Labels` and `MessageLabels` tables) and there are no EF migrations: the local dev database `%LocalAppData%\Swiftlet\swiftlet.db` must be deleted and recreated, otherwise every message query (they all include labels) fails to load.

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
- **Database**: `SqliteSchemaInitializer` creates tables with `EnsureCreated` plus hand-written FTS5 DDL and triggers; **there are no EF migrations**. After changing entities, delete the local `%LocalAppData%\Swiftlet\swiftlet.db` during development. All time fields are stored as Unix milliseconds (`UnixMillisecondsConverter`) so they sort correctly.
- **Search**: FTS5 trigram tokenizer; terms shorter than 3 characters fall back to `LIKE`; query terms are escaped into literals so user input is never treated as FTS syntax.
- **Attachments**: only metadata is stored locally (including `Ordinal`, the attachment's position in the server message); content is fetched from IMAP on demand via `DownloadAttachmentQuery`.
- **Local state**: deleting a message is written back to the server; read state, starred state and labels are still local only and are not written back.
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

- IMAP delete (MOVE to Trash, UID EXPUNGE for permanent delete, the COPY fallback; on Gmail, moving to `[Gmail]/Trash` removes labels and whether EXPUNGE inside Trash is final depends on Gmail's IMAP settings; servers that do not advertise a `\Trash` special-use folder make every delete permanent; MOVE/UIDPLUS support of QQ/163 and similar providers is unknown)
- How the delete confirmation dialog, the context menu and the label flyout behave in a real window (only headless snapshots were viewed; whether a right-click selects the item under the pointer is unverified)
- IMAP / SMTP sending and receiving, attachment download, sent copies (`SavesSentCopyAutomatically` is a conservative guess for every provider except Gmail/Outlook)
- Google / Microsoft OAuth sign-in and token refresh
- `MacKeychainSecretStore` (`security` command), `LibSecretStore` (`secret-tool`)
- The Avalonia UI on macOS / Linux, Chinese font fallback, file pickers, the system browser

## Remaining work (summary; details in docs/HANDOFF.md)

- **M5.5**: server state sync (write read/starred state back, pull server-side changes). Manual delete is already written back; read/starred/labels are not.
- **M6**: search `from:` / `subject:` prefix syntax, notifications, tray icon, settings page (sync interval, theme), packaging (MSIX or Inno Setup, `.app`, AppImage/deb), three-platform GitHub Actions CI.
- **Gaps**: account management UI (delete/edit), OAuth re-sign-in entry point, IMAP IDLE and reconnect handling, server state sync (read/starred/delete), inline HTML mail rendering (needs a WebView spike first), drafts, reply all, forwarding with attachments, recipient parsing (display names containing commas), message list paging, manual light/dark theme switch, file logging, EF migrations, README.
