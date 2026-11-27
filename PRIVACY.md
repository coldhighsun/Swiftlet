# Swiftlet Privacy Statement

Effective date: November 27, 2026

Swiftlet is a desktop email client. It runs on your computer and connects directly to your mail provider. This statement describes what data the app handles, where it goes and how you can remove it.

## Summary

- Swiftlet has no server of its own. The project and its maintainers do not receive your mail, passwords, tokens or usage data.
- The app contains no telemetry, analytics, advertising or crash reporting.
- Your data stays on your computer, except for what the app exchanges with the services described under [Network connections](#network-connections).

## Data stored on your computer

| Data | Purpose |
| --- | --- |
| Account settings (email address, display name, server settings) | Connecting to your mail accounts |
| Passwords, app passwords or OAuth tokens | Signing in |
| Cached folders, messages and attachment information | Showing and searching your mail |
| Labels, remote image rules (sender addresses or domains) and app settings | Features you configure |
| Log files (at most 7) | Troubleshooting |

Attachment contents are downloaded when you ask for them and saved where you choose.

### Protection

- Passwords and tokens are kept with the secret storage of your operating system (Windows DPAPI, macOS Keychain or the Linux secret service). If none is available, Swiftlet falls back to a file-based store whose key is kept beside the data, which offers much weaker protection.
- The mail databases are encrypted when a system secret store is available. They are not encrypted without one, or if you choose to continue unencrypted after encryption fails.
- Log files, the settings file, saved attachments and the temporary files created by "View in browser" are **not** encrypted. The temporary preview files are removed when the app exits, or at its next start.
- Encryption protects database files that are copied off your computer or user account. It does not protect against other programs running as the same user.

### Logs

Logs can contain your email addresses, mail folder names, remote image rules, server error messages and technical details. Swiftlet is designed not to write passwords, tokens, message subjects, message bodies or attachment contents to logs. Logs are never sent anywhere automatically. Review them before you share them.

## Network connections

| Destination | When | What is exchanged |
| --- | --- | --- |
| Your mail servers (IMAP and SMTP) | Synchronizing, searching for new mail, sending and managing messages | Your sign-in data and mail |
| Microsoft or Google sign-in services | When you sign in with them, and to renew the sign-in | Authorization codes and tokens |
| Servers hosting images referenced in a message | Only when you load remote images, or a saved rule allows the sender | The image request, including your IP address |
| Web sites | When you open a link | Handled by your web browser |

Remote images are blocked by default. Loading them can tell the sender that you opened the message.

Swiftlet displays mail with the web view component of your operating system and relies on your operating system for other services. Their vendors' privacy terms apply to them. Your mail provider and your organization process your data under their own terms.

## Sign in with Microsoft

Swiftlet requests these delegated permissions:

| Permission | Use |
| --- | --- |
| `IMAP.AccessAsUser.All` | Reading and managing your mail |
| `SMTP.Send` | Sending mail you write |
| `offline_access` | Staying signed in |
| `openid`, `email`, `profile` | Reading your email address, name and account type to set up the account |

The access is used only on your computer to provide the features of the app. You can withdraw it for a personal account in your Microsoft account privacy settings, and for a work or school account in the My Apps portal or through your administrator.

## Sign in with Google

If a Google sign-in is configured, Swiftlet requests access to your Gmail mail (`https://mail.google.com/`) and your basic profile. Swiftlet's use and transfer of information received from Google APIs adheres to the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including the Limited Use requirements. You can withdraw the access in your Google Account settings.

## Removing your data

- Removing an account in Swiftlet deletes its local mail, settings, passwords and tokens. The mailbox on the server is not changed.
- To remove everything, quit Swiftlet and delete its data directory (on Windows, `%LocalAppData%\Swiftlet`). Entries that Swiftlet created in the macOS Keychain or the Linux secret service must be deleted there. Uninstalling the app may leave this data behind.
- Removing local data does not withdraw the permissions you granted to Microsoft or Google; see the sections above.

## Changes

Changes to this statement are published in this file, with a new effective date. The history is visible in the repository.

## Contact

Open an issue at <https://github.com/coldhighsun/Swiftlet/issues>. Issues are public, so do not include personal data, mail contents, passwords or tokens.
