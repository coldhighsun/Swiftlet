using Mediator;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Contacts;

namespace Swiftlet.Application.Contacts;

/// <summary>
/// The editable details of a contact.
/// </summary>
/// <param name="FirstName">The given name; may be empty.</param>
/// <param name="LastName">The family name; may be empty.</param>
/// <param name="Emails">The email addresses; at least one.</param>
/// <param name="Phones">The phone numbers.</param>
/// <param name="Company">The company.</param>
/// <param name="Note">The note.</param>
public sealed record ContactDetails(
    string FirstName,
    string LastName,
    IReadOnlyList<string> Emails,
    IReadOnlyList<string> Phones,
    string Company,
    string Note);

/// <summary>
/// Thrown when an email address already belongs to another contact.
/// </summary>
/// <param name="address">The address.</param>
public sealed class DuplicateContactEmailException(string address)
    : InvalidOperationException("The email address already belongs to another contact.")
{
    /// <summary>
    /// Gets the address that is taken.
    /// </summary>
    public string Address { get; } = address;
}

/// <summary>
/// Creates or changes a contact.
/// </summary>
/// <param name="Id">The contact to change, or <see langword="null"/> to create one.</param>
/// <param name="Details">The new details.</param>
public sealed record SaveContactCommand(Guid? Id, ContactDetails Details) : ICommand<Guid>, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names) => Id is null ? "new contact" : $"contact {Id}";
}

/// <summary>
/// Handles <see cref="SaveContactCommand"/>.
/// </summary>
/// <param name="contacts">The contact repository.</param>
public sealed class SaveContactCommandHandler(IContactRepository contacts) : ICommandHandler<SaveContactCommand, Guid>
{
    /// <inheritdoc />
    public async ValueTask<Guid> Handle(SaveContactCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var details = command.Details;
        var all = await contacts.GetAllAsync(cancellationToken);
        var taken = details.Emails
            .Select(e => e.Trim())
            .FirstOrDefault(e => all.Any(c => c.Id != command.Id && c.Emails.Contains(e, StringComparer.OrdinalIgnoreCase)));
        if (taken is not null)
        {
            throw new DuplicateContactEmailException(taken);
        }

        if (command.Id is not { } id)
        {
            var created = Contact.Create(
                details.FirstName, details.LastName, details.Emails, details.Phones, details.Company, details.Note);
            await contacts.AddRangeAsync([created], cancellationToken);
            return created.Id;
        }

        var contact = await contacts.GetAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Contact {id} was not found.");
        contact.Update(details.FirstName, details.LastName, details.Emails, details.Phones, details.Company, details.Note);
        await contacts.SaveChangesAsync(cancellationToken);
        return id;
    }
}

/// <summary>
/// Deletes a contact.
/// </summary>
/// <param name="Id">The contact.</param>
public sealed record DeleteContactCommand(Guid Id) : ICommand<Unit>, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names) => $"contact {Id}";
}

/// <summary>
/// Handles <see cref="DeleteContactCommand"/>.
/// </summary>
/// <param name="contacts">The contact repository.</param>
public sealed class DeleteContactCommandHandler(IContactRepository contacts) : ICommandHandler<DeleteContactCommand, Unit>
{
    /// <inheritdoc />
    public async ValueTask<Unit> Handle(DeleteContactCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await contacts.DeleteAsync(command.Id, cancellationToken);
        return Unit.Value;
    }
}

/// <summary>
/// Removes an address from the collected ones for good.
/// </summary>
/// <param name="Address">The address.</param>
public sealed record DismissCollectedAddressCommand(string Address) : ICommand<Unit>;

/// <summary>
/// Handles <see cref="DismissCollectedAddressCommand"/>.
/// </summary>
/// <param name="contacts">The contact repository.</param>
public sealed class DismissCollectedAddressCommandHandler(IContactRepository contacts)
    : ICommandHandler<DismissCollectedAddressCommand, Unit>
{
    /// <inheritdoc />
    public async ValueTask<Unit> Handle(DismissCollectedAddressCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await contacts.DismissAddressAsync(command.Address, cancellationToken);
        return Unit.Value;
    }
}
