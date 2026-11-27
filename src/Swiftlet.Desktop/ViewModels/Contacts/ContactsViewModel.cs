using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Contacts;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Domain.Contacts;

namespace Swiftlet.Desktop.ViewModels.Contacts;

/// <summary>
/// The view model of the contacts window: search, edit and delete contacts, turn collected addresses into contacts, and
/// import or export contact files.
/// </summary>
public sealed partial class ContactsViewModel : ObservableObject
{
    /// <summary>
    /// The index of the page listing the contacts.
    /// </summary>
    public const int ContactsPage = 0;

    /// <summary>
    /// The largest contact file that is imported.
    /// </summary>
    internal const long MaxImportBytes = 10 * 1024 * 1024;

    /// <summary>
    /// The request dispatcher.
    /// </summary>
    private readonly IRequestDispatcher _dispatcher;

    /// <summary>
    /// The dialog service.
    /// </summary>
    private readonly IDialogService _dialogs;

    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// The logger, if any.
    /// </summary>
    private readonly ILogger? _logger;

    /// <summary>
    /// The address book as loaded last.
    /// </summary>
    private AddressBook _book = new([], []);

    /// <summary>
    /// The contact shown in the form; <see langword="null"/> while a new one is entered.
    /// </summary>
    private Guid? _editingId;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContactsViewModel"/> class.
    /// </summary>
    /// <param name="dispatcher">The request dispatcher.</param>
    /// <param name="dialogs">The dialog service.</param>
    /// <param name="localizer">The localizer.</param>
    /// <param name="logger">The logger; optional.</param>
    public ContactsViewModel(
        IRequestDispatcher dispatcher,
        IDialogService dialogs,
        ILocalizer localizer,
        ILogger<ContactsViewModel>? logger = null)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _localizer = localizer;
        _logger = logger;
        SearchText = string.Empty;
        ClearForm();
    }

    /// <summary>
    /// Raised when the window should close; the argument is the recipient to write to, or <see langword="null"/>.
    /// </summary>
    public event EventHandler<string?>? CloseRequested;

    /// <summary>
    /// Gets the contacts matching the search.
    /// </summary>
    public ObservableCollection<ContactItemViewModel> Contacts { get; } = [];

    /// <summary>
    /// Gets the collected addresses matching the search.
    /// </summary>
    public ObservableCollection<CollectedItemViewModel> Collected { get; } = [];

    /// <summary>
    /// Gets the email address fields of the form.
    /// </summary>
    public ObservableCollection<ContactValueViewModel> Emails { get; } = [];

    /// <summary>
    /// Gets the phone number fields of the form.
    /// </summary>
    public ObservableCollection<ContactValueViewModel> Phones { get; } = [];

    /// <summary>
    /// Gets the column of the given name: the languages that say the family name first show it second.
    /// </summary>
    public int FirstNameColumn => IsFamilyNameFirst ? 1 : 0;

    /// <summary>
    /// Gets the column of the family name.
    /// </summary>
    public int LastNameColumn => IsFamilyNameFirst ? 0 : 1;

    /// <summary>
    /// Gets or sets the search text.
    /// </summary>
    [ObservableProperty]
    public partial string SearchText { get; set; }

    /// <summary>
    /// Gets or sets the index of the shown page (contacts or collected addresses).
    /// </summary>
    [ObservableProperty]
    public partial int SelectedPage { get; set; }

    /// <summary>
    /// Gets or sets the selected contact row.
    /// </summary>
    [ObservableProperty]
    public partial ContactItemViewModel? SelectedContact { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the form is shown.
    /// </summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the form shows a saved contact, which can be deleted and written to.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    public partial bool IsExisting { get; set; }

    /// <summary>
    /// Gets or sets the given name in the form.
    /// </summary>
    [ObservableProperty]
    public partial string FirstName { get; set; }

    /// <summary>
    /// Gets or sets the family name in the form.
    /// </summary>
    [ObservableProperty]
    public partial string LastName { get; set; }

    /// <summary>
    /// Gets or sets the company in the form.
    /// </summary>
    [ObservableProperty]
    public partial string Company { get; set; }

    /// <summary>
    /// Gets or sets the note in the form.
    /// </summary>
    [ObservableProperty]
    public partial string Note { get; set; }

    /// <summary>
    /// Gets or sets the localized error of the last failed operation, or <see langword="null"/>.
    /// </summary>
    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    /// <summary>
    /// Gets or sets the localized result of the last import or export, or <see langword="null"/>.
    /// </summary>
    [ObservableProperty]
    public partial string? StatusText { get; set; }

    /// <summary>
    /// Gets a value indicating whether there is no collected address to show.
    /// </summary>
    public bool IsCollectedEmpty => Collected.Count == 0;

    /// <summary>
    /// Loads the address book and, when asked for, shows the contact of an address or a new form filled with it.
    /// </summary>
    /// <param name="request">The address to show, or <see langword="null"/>.</param>
    /// <returns>A task that completes when the window is filled.</returns>
    public async Task InitializeAsync(ContactsRequest? request = null)
    {
        await RunAsync(async () =>
        {
            await ReloadAsync();
            if (request is null)
            {
                return;
            }

            var known = _book.Contacts.FirstOrDefault(c => c.Emails.Contains(request.Address, StringComparer.OrdinalIgnoreCase));
            if (known is not null)
            {
                Select(known.Id);
                return;
            }

            New();
            (FirstName, LastName) = PersonName.Split(request.Name);
            Fill(Emails, [request.Address]);
        });
    }

    /// <summary>
    /// Shows an empty form for a new contact.
    /// </summary>
    [RelayCommand]
    private void New()
    {
        ErrorText = null;
        SelectedPage = ContactsPage;
        SelectedContact = null;
        ClearForm();
        IsEditing = true;
    }

    /// <summary>
    /// Saves the form.
    /// </summary>
    /// <returns>A task that completes when the contact has been saved or the failure is shown.</returns>
    [RelayCommand]
    private async Task SaveAsync()
    {
        var emails = Values(Emails);
        await RunAsync(async () =>
        {
            if (emails.Count == 0)
            {
                ErrorText = _localizer["Contacts_ErrorNoEmail"];
                return;
            }

            if (emails.Find(e => !Contact.IsValidEmail(e)) is { } invalid)
            {
                ErrorText = _localizer.Format("Contacts_ErrorInvalidEmail", invalid);
                return;
            }

            var details = new ContactDetails(FirstName, LastName, emails, Values(Phones), Company, Note);
            var id = await _dispatcher.SendAsync(new SaveContactCommand(_editingId, details));
            SearchText = string.Empty;
            await ReloadAsync();
            Select(id);
        });
    }

    /// <summary>
    /// Deletes the shown contact after the user confirmed it.
    /// </summary>
    /// <returns>A task that completes when the contact has been deleted or the deletion was cancelled.</returns>
    [RelayCommand(CanExecute = nameof(IsExisting))]
    private async Task DeleteAsync()
    {
        if (_editingId is not { } id || _book.Contacts.FirstOrDefault(c => c.Id == id) is not { } contact)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            _localizer["Contacts_DeleteTitle"],
            _localizer.Format("Contacts_DeleteMessage", new ContactItemViewModel(contact).Title),
            _localizer["Contacts_Delete"]);
        if (!confirmed)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _dispatcher.SendAsync(new DeleteContactCommand(id));
            SelectedContact = null;
            ClearForm();
            await ReloadAsync();
        });
    }

    /// <summary>
    /// Closes the window to write to the first address of the shown contact, as saved.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsExisting))]
    private void Write()
    {
        if (_book.Contacts.FirstOrDefault(c => c.Id == _editingId) is { } contact)
        {
            CloseRequested?.Invoke(this, RecipientList.Format(contact.DisplayName, contact.Emails[0]));
        }
    }

    /// <summary>
    /// Turns a collected address into a contact and shows it.
    /// </summary>
    /// <param name="item">The collected row.</param>
    /// <returns>A task that completes when the contact has been created.</returns>
    [RelayCommand]
    private async Task AddCollectedAsync(CollectedItemViewModel? item)
    {
        ArgumentNullException.ThrowIfNull(item);
        await RunAsync(async () =>
        {
            var (firstName, lastName) = PersonName.Split(item.Entry.Name);
            var details = new ContactDetails(firstName, lastName, [item.Entry.Address], [], string.Empty, string.Empty);
            var id = await _dispatcher.SendAsync(new SaveContactCommand(null, details));
            SearchText = string.Empty;
            await ReloadAsync();
            SelectedPage = ContactsPage;
            Select(id);
        });
    }

    /// <summary>
    /// Removes a collected address for good.
    /// </summary>
    /// <param name="item">The collected row.</param>
    /// <returns>A task that completes when the address has been removed.</returns>
    [RelayCommand]
    private async Task RemoveCollectedAsync(CollectedItemViewModel? item)
    {
        ArgumentNullException.ThrowIfNull(item);
        await RunAsync(async () =>
        {
            await _dispatcher.SendAsync(new DismissCollectedAddressCommand(item.Entry.Address));
            _book = _book with { Collected = [.. _book.Collected.Where(a => a != item.Entry)] };
            Collected.Remove(item);
            OnPropertyChanged(nameof(IsCollectedEmpty));
        });
    }

    /// <summary>
    /// Closes the window to write to a collected address.
    /// </summary>
    /// <param name="item">The collected row.</param>
    [RelayCommand]
    private void WriteCollected(CollectedItemViewModel? item)
    {
        ArgumentNullException.ThrowIfNull(item);
        CloseRequested?.Invoke(this, RecipientList.Format(item.Entry.Name, item.Entry.Address));
    }

    /// <summary>
    /// Imports a vCard or CSV file the user picks.
    /// </summary>
    /// <returns>A task that completes when the file has been imported or the failure is shown.</returns>
    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = await _dialogs.PickContactFileAsync();
        if (path is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            if (new FileInfo(path).Length > MaxImportBytes)
            {
                ErrorText = _localizer["Contacts_ErrorFileTooLarge"];
                return;
            }

            var format = Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)
                ? ContactFileFormat.Csv
                : ContactFileFormat.VCard;
            var result = await _dispatcher.SendAsync(new ImportContactsCommand(await File.ReadAllBytesAsync(path), format));
            await ReloadAsync();
            StatusText = _localizer.Format("Contacts_ImportDone", result.Added, result.Updated, result.Skipped);
        });
    }

    /// <summary>
    /// Exports all contacts as vCard.
    /// </summary>
    /// <returns>A task that completes when the file has been written.</returns>
    [RelayCommand]
    private Task ExportVCardAsync() => ExportAsync(ContactFileFormat.VCard, "contacts.vcf");

    /// <summary>
    /// Exports all contacts as CSV.
    /// </summary>
    /// <returns>A task that completes when the file has been written.</returns>
    [RelayCommand]
    private Task ExportCsvAsync() => ExportAsync(ContactFileFormat.Csv, "contacts.csv");

    /// <summary>
    /// Asks the window to close.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this, null);
    }

    /// <summary>
    /// Gets a value indicating whether the UI language puts the family name before the given name.
    /// </summary>
    private bool IsFamilyNameFirst => bool.TryParse(_localizer["Contacts_FamilyNameFirst"], out var first) && first;

    /// <summary>
    /// Adds an empty email address field.
    /// </summary>
    [RelayCommand]
    private void AddEmail() => Emails.Add(CreateField(Emails, string.Empty));

    /// <summary>
    /// Adds an empty phone number field.
    /// </summary>
    [RelayCommand]
    private void AddPhone() => Phones.Add(CreateField(Phones, string.Empty));

    /// <summary>
    /// Gets the filled-in values of a list of fields.
    /// </summary>
    /// <param name="fields">The fields.</param>
    /// <returns>The trimmed, non-empty values.</returns>
    private static List<string> Values(IEnumerable<ContactValueViewModel> fields)
    {
        return fields.Select(f => f.Value.Trim()).Where(v => v.Length > 0).ToList();
    }

    /// <summary>
    /// Creates a field that removes itself from its list.
    /// </summary>
    /// <param name="fields">The list the field belongs to.</param>
    /// <param name="value">The initial value.</param>
    /// <returns>The field.</returns>
    private static ContactValueViewModel CreateField(ObservableCollection<ContactValueViewModel> fields, string value)
    {
        return new ContactValueViewModel(value, field => fields.Remove(field));
    }

    /// <summary>
    /// Replaces the fields of a list; without values one empty field is shown, ready to type into.
    /// </summary>
    /// <param name="fields">The list of fields.</param>
    /// <param name="values">The values.</param>
    private static void Fill(ObservableCollection<ContactValueViewModel> fields, IReadOnlyList<string> values)
    {
        fields.Clear();
        foreach (var value in values.Count > 0 ? values : [string.Empty])
        {
            fields.Add(CreateField(fields, value));
        }
    }

    /// <summary>
    /// Writes all contacts to a file the user chooses.
    /// </summary>
    /// <param name="format">The file format.</param>
    /// <param name="suggestedName">The suggested file name.</param>
    /// <returns>A task that completes when the file has been written or the failure is shown.</returns>
    private async Task ExportAsync(ContactFileFormat format, string suggestedName)
    {
        var path = await _dialogs.PickSavePathAsync(suggestedName);
        if (path is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await File.WriteAllBytesAsync(path, await _dispatcher.SendAsync(new ExportContactsQuery(format)));
            StatusText = _localizer.Format("Contacts_ExportDone", _book.Contacts.Count);
        });
    }

    /// <summary>
    /// Filters the lists when the search text changes.
    /// </summary>
    /// <param name="value">The new search text.</param>
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>
    /// Shows a newly selected contact in the form. Losing the selection, for example because the search hides the row,
    /// keeps the form.
    /// </summary>
    /// <param name="value">The selected row.</param>
    partial void OnSelectedContactChanged(ContactItemViewModel? value)
    {
        // Rebuilding the list selects the shown contact again, which must not overwrite what is being typed.
        if (value is null || value.Contact.Id == _editingId)
        {
            return;
        }

        ErrorText = null;
        Show(value.Contact);
    }

    /// <summary>
    /// Fills the form with a contact.
    /// </summary>
    /// <param name="contact">The contact.</param>
    private void Show(Contact contact)
    {
        _editingId = contact.Id;
        FirstName = contact.FirstName;
        LastName = contact.LastName;
        Fill(Emails, contact.Emails);
        Fill(Phones, contact.Phones);
        Company = contact.Company;
        Note = contact.Note;
        IsExisting = true;
        IsEditing = true;
    }

    /// <summary>
    /// Empties and hides the form.
    /// </summary>
    private void ClearForm()
    {
        _editingId = null;
        FirstName = LastName = Company = Note = string.Empty;
        Fill(Emails, []);
        Fill(Phones, []);
        IsExisting = false;
        IsEditing = false;
    }

    /// <summary>
    /// Selects a contact and shows its saved details.
    /// </summary>
    /// <param name="id">The contact.</param>
    private void Select(Guid id)
    {
        if (_book.Contacts.FirstOrDefault(c => c.Id == id) is { } contact)
        {
            Show(contact);
            SelectedContact = Contacts.FirstOrDefault(c => c.Contact.Id == id);
        }
    }

    /// <summary>
    /// Loads the address book and rebuilds the lists.
    /// </summary>
    /// <returns>A task that completes when the lists are rebuilt.</returns>
    private async Task ReloadAsync()
    {
        _book = await _dispatcher.SendAsync(new GetAddressBookQuery());
        ApplyFilter();
    }

    /// <summary>
    /// Rebuilds both lists with the entries matching the search.
    /// </summary>
    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        bool Matches(string value) => value.Contains(text, StringComparison.CurrentCultureIgnoreCase);

        Contacts.Clear();
        foreach (var contact in _book.Contacts.Where(c =>
            Matches(c.DisplayName) || Matches($"{c.FirstName} {c.LastName}") || Matches(c.Company)
            || c.Emails.Any(Matches) || c.Phones.Any(Matches)))
        {
            Contacts.Add(new ContactItemViewModel(contact));
        }

        Collected.Clear();
        foreach (var entry in _book.Collected.Where(a => Matches(a.Name) || Matches(a.Address)))
        {
            Collected.Add(new CollectedItemViewModel(entry));
        }

        SelectedContact = Contacts.FirstOrDefault(c => c.Contact.Id == _editingId);
        OnPropertyChanged(nameof(IsCollectedEmpty));
    }

    /// <summary>
    /// Runs an operation, clearing the previous messages and turning failures into localized error text.
    /// </summary>
    /// <param name="action">The operation.</param>
    /// <returns>A task that completes when the operation has finished or failed.</returns>
    private async Task RunAsync(Func<Task> action)
    {
        ErrorText = null;
        StatusText = null;
        try
        {
            await action();
        }
        catch (DuplicateContactEmailException ex)
        {
            ErrorText = _localizer.Format("Contacts_ErrorDuplicate", ex.Address);
        }
        catch (FormatException ex)
        {
            // The file readers reject content that is not a contact file.
            _logger?.LogWarning(ex, "A contact file could not be read");
            ErrorText = _localizer["Contacts_ErrorFileFormat"];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: report storage and file failures to the user instead of crashing the window.
            _logger?.LogError(ex, "A contacts operation failed");
            ErrorText = _localizer.Format("Error_Format", _localizer["Contacts_ErrorFailed"], ex.Message);
        }
    }
}
