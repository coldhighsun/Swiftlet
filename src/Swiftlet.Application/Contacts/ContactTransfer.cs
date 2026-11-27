using System.Text;
using Mediator;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Contacts;

namespace Swiftlet.Application.Contacts;

/// <summary>
/// A file format contacts can be exchanged in.
/// </summary>
public enum ContactFileFormat
{
    /// <summary>
    /// vCard (<c>.vcf</c>).
    /// </summary>
    VCard = 0,

    /// <summary>
    /// Comma-separated values (<c>.csv</c>).
    /// </summary>
    Csv = 1,
}

/// <summary>
/// What an import did.
/// </summary>
/// <param name="Added">The number of new contacts.</param>
/// <param name="Updated">The number of existing contacts that received more details.</param>
/// <param name="Skipped">The number of file entries without a usable email address or with nothing new.</param>
public sealed record ImportContactsResult(int Added, int Updated, int Skipped);

/// <summary>
/// Imports contacts from a file. An entry sharing an email address with a contact only fills in what that contact lacks.
/// </summary>
/// <param name="Content">The file content.</param>
/// <param name="Format">The file format.</param>
public sealed record ImportContactsCommand(byte[] Content, ContactFileFormat Format) : ICommand<ImportContactsResult>, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names) => $"{Format}, {Content.Length} bytes";
}

/// <summary>
/// Handles <see cref="ImportContactsCommand"/>.
/// </summary>
/// <param name="contacts">The contact repository.</param>
public sealed class ImportContactsCommandHandler(IContactRepository contacts)
    : ICommandHandler<ImportContactsCommand, ImportContactsResult>
{
    /// <inheritdoc />
    public async ValueTask<ImportContactsResult> Handle(ImportContactsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var text = ContactFileText.Decode(command.Content);
        var entries = command.Format == ContactFileFormat.VCard ? VCardFormat.Read(text) : CsvContactFormat.Read(text);

        var byAddress = new Dictionary<string, Contact>(StringComparer.OrdinalIgnoreCase);
        foreach (var contact in await contacts.GetAllAsync(cancellationToken))
        {
            foreach (var email in contact.Emails)
            {
                byAddress.TryAdd(email, contact);
            }
        }

        var added = new List<Contact>();
        var updated = new HashSet<Contact>();
        var skipped = 0;
        foreach (var entry in entries)
        {
            var emails = entry.Emails.Select(e => e.Trim()).Where(Contact.IsValidEmail).ToList();
            if (emails.Count == 0)
            {
                skipped++;
                continue;
            }

            var target = emails.Select(e => byAddress.GetValueOrDefault(e)).FirstOrDefault(c => c is not null);
            if (target is null)
            {
                target = Contact.Create(entry.FirstName, entry.LastName, emails, entry.Phones, entry.Company, entry.Note);
                added.Add(target);
            }
            else if (!TryFill(target, entry, emails.Where(e => byAddress.GetValueOrDefault(e) is null || byAddress[e] == target)))
            {
                skipped++;
                continue;
            }
            else if (!added.Contains(target))
            {
                updated.Add(target);
            }

            foreach (var email in target.Emails)
            {
                byAddress.TryAdd(email, target);
            }
        }

        await contacts.AddRangeAsync(added, cancellationToken);
        return new ImportContactsResult(added.Count, updated.Count, skipped);
    }

    /// <summary>
    /// Adds the addresses and phone numbers a contact does not have yet and fills its empty texts.
    /// </summary>
    /// <param name="contact">The contact.</param>
    /// <param name="entry">The imported entry.</param>
    /// <param name="emails">The imported addresses that no other contact owns.</param>
    /// <returns><see langword="true"/> if the contact changed.</returns>
    private static bool TryFill(Contact contact, ContactDetails entry, IEnumerable<string> emails)
    {
        var mergedEmails = contact.Emails.Concat(emails).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var mergedPhones = contact.Phones
            .Concat(entry.Phones.Select(p => p.Trim()).Where(p => p.Length > 0))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        // The two name parts belong together: they are only taken over when the contact has no name at all.
        var named = contact.DisplayName.Length > 0;
        var firstName = named ? contact.FirstName : entry.FirstName.Trim();
        var lastName = named ? contact.LastName : entry.LastName.Trim();
        var company = contact.Company.Length > 0 ? contact.Company : entry.Company.Trim();
        var note = contact.Note.Length > 0 ? contact.Note : entry.Note.Trim();

        if (mergedEmails.Count == contact.Emails.Count
            && mergedPhones.Count == contact.Phones.Count
            && firstName == contact.FirstName
            && lastName == contact.LastName
            && company == contact.Company
            && note == contact.Note)
        {
            return false;
        }

        contact.Update(firstName, lastName, mergedEmails, mergedPhones, company, note);
        return true;
    }
}

/// <summary>
/// Writes all contacts to a file format. Collected addresses are not included.
/// </summary>
/// <param name="Format">The file format.</param>
public sealed record ExportContactsQuery(ContactFileFormat Format) : IQuery<byte[]>;

/// <summary>
/// Handles <see cref="ExportContactsQuery"/>.
/// </summary>
/// <param name="contacts">The contact repository.</param>
public sealed class ExportContactsQueryHandler(IContactRepository contacts) : IQueryHandler<ExportContactsQuery, byte[]>
{
    /// <inheritdoc />
    public async ValueTask<byte[]> Handle(ExportContactsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var details = (await contacts.GetAllAsync(cancellationToken))
            .Select(c => new ContactDetails(c.FirstName, c.LastName, c.Emails, c.Phones, c.Company, c.Note))
            .ToList();
        if (query.Format == ContactFileFormat.VCard)
        {
            return Encoding.UTF8.GetBytes(VCardFormat.Write(details));
        }

        // Spreadsheet programs need the byte order mark to read the file as UTF-8.
        return [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(CsvContactFormat.Write(details))];
    }
}
