using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    /// The contact shown on the card or in the form; <see langword="null"/> without one and while a new one is entered.
    /// </summary>
    private Guid? _shownId;

    /// <summary>
    /// The content of the form when editing started, to tell whether something was changed.
    /// </summary>
    private string _baseline = string.Empty;

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
        Emails.CollectionChanged += OnEmailsChanged;
        SelectedCollected.CollectionChanged += OnSelectedCollectedChanged;
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
    /// Gets the selected collected addresses.
    /// </summary>
    public ObservableCollection<CollectedItemViewModel> SelectedCollected { get; } = [];

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
    [NotifyPropertyChangedFor(nameof(IsViewing))]
    [NotifyPropertyChangedFor(nameof(IsFormShown))]
    [NotifyPropertyChangedFor(nameof(IsNoContactShown))]
    [NotifyPropertyChangedFor(nameof(IsNoCollectedShown))]
    [NotifyPropertyChangedFor(nameof(IsOneCollectedShown))]
    [NotifyPropertyChangedFor(nameof(IsManyCollectedShown))]
    public partial int SelectedPage { get; set; }

    /// <summary>
    /// Gets or sets the selected contact row.
    /// </summary>
    [ObservableProperty]
    public partial ContactItemViewModel? SelectedContact { get; set; }

    /// <summary>
    /// Gets or sets the contact of the card, as saved.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewing))]
    [NotifyPropertyChangedFor(nameof(IsNoContactShown))]
    public partial ContactItemViewModel? ShownContact { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the form is in use.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewing))]
    [NotifyPropertyChangedFor(nameof(IsFormShown))]
    [NotifyPropertyChangedFor(nameof(IsNoContactShown))]
    public partial bool IsEditing { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a saved contact is shown, which can be edited, deleted and written to.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
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
    /// Gets a value indicating whether the card of a contact is shown.
    /// </summary>
    public bool IsViewing => IsContactsPage && !IsEditing && ShownContact is not null;

    /// <summary>
    /// Gets a value indicating whether the form is shown.
    /// </summary>
    public bool IsFormShown => IsContactsPage && IsEditing;

    /// <summary>
    /// Gets a value indicating whether the contacts page shows neither a card nor the form.
    /// </summary>
    public bool IsNoContactShown => IsContactsPage && !IsEditing && ShownContact is null;

    /// <summary>
    /// Gets a value indicating whether the collected page has no selected address.
    /// </summary>
    public bool IsNoCollectedShown => !IsContactsPage && SelectedCollected.Count == 0;

    /// <summary>
    /// Gets a value indicating whether the collected page shows one selected address.
    /// </summary>
    public bool IsOneCollectedShown => !IsContactsPage && SelectedCollected.Count == 1;

    /// <summary>
    /// Gets a value indicating whether the collected page shows the actions for several selected addresses.
    /// </summary>
    public bool IsManyCollectedShown => !IsContactsPage && SelectedCollected.Count > 1;

    /// <summary>
    /// Gets the selected collected address when exactly one is selected.
    /// </summary>
    public CollectedItemViewModel? SingleCollected => SelectedCollected.Count == 1 ? SelectedCollected[0] : null;

    /// <summary>
    /// Gets the localized number of selected collected addresses.
    /// </summary>
    public string SelectedCollectedText => _localizer.Format("Contacts_SelectedCount", SelectedCollected.Count);

    /// <summary>
    /// Gets a value indicating whether the form holds changes that have not been saved.
    /// </summary>
    public bool HasUnsavedChanges => IsEditing && FormContent() != _baseline;

    /// <summary>
    /// Gets the question about unsaved changes started by selecting another contact, so tests can wait for the answer.
    /// </summary>
    internal Task PendingSwitch { get; private set; } = Task.CompletedTask;

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

            StartNew();
            (FirstName, LastName) = PersonName.Split(request.Name);
            Fill(Emails, [request.Address], CreateEmailField);

            // What the window filled in itself is not a change worth asking about.
            _baseline = FormContent();
        });
    }

    /// <summary>
    /// Shows an empty form for a new contact.
    /// </summary>
    /// <returns>A task that completes when the form is shown or the user kept the previous one.</returns>
    [RelayCommand]
    private async Task NewAsync()
    {
        if (await ConfirmDiscardAsync())
        {
            StartNew();
        }
    }

    /// <summary>
    /// Opens the form for the shown contact.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsExisting))]
    private void Edit()
    {
        if (IsEditing || FindContact(_shownId) is not { } contact)
        {
            return;
        }

        ErrorText = null;
        StatusText = null;
        FirstName = contact.FirstName;
        LastName = contact.LastName;
        Fill(Emails, contact.Emails, CreateEmailField);
        Fill(Phones, contact.Phones, CreatePhoneField);
        Company = contact.Company;
        Note = contact.Note;
        _baseline = FormContent();
        IsEditing = true;
    }

    /// <summary>
    /// Leaves the form without saving, asking first when something was changed.
    /// </summary>
    /// <returns>A task that completes when the form is closed or the user kept it.</returns>
    [RelayCommand]
    private async Task CancelAsync()
    {
        if (!IsEditing || !await ConfirmDiscardAsync())
        {
            return;
        }

        ErrorText = null;
        IsEditing = false;
        if (_shownId is { } id)
        {
            Select(id);
        }
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
            var id = await _dispatcher.SendAsync(new SaveContactCommand(_shownId, details));
            IsEditing = false;
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
        if (_shownId is not { } id || FindContact(id) is not { } contact)
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
    /// Closes the window to write to the shown contact, as saved.
    /// </summary>
    /// <param name="address">The address to write to, or <see langword="null"/> for the preferred one.</param>
    [RelayCommand(CanExecute = nameof(IsExisting))]
    private void Write(string? address)
    {
        if (FindContact(_shownId) is { } contact)
        {
            CloseRequested?.Invoke(this, RecipientList.Format(contact.DisplayName, address ?? contact.Emails[0]));
        }
    }

    /// <summary>
    /// Copies an address or phone number to the clipboard.
    /// </summary>
    /// <param name="value">The text to copy.</param>
    /// <returns>A task that completes when the text has been copied or the failure is shown.</returns>
    [RelayCommand]
    private async Task CopyAsync(string? value)
    {
        ArgumentNullException.ThrowIfNull(value);
        await RunAsync(async () =>
        {
            await _dialogs.CopyTextAsync(value);
            StatusText = _localizer.Format("Contacts_Copied", value);
        });
    }

    /// <summary>
    /// Turns the selected collected addresses into contacts. A single new contact is shown right away.
    /// </summary>
    /// <returns>A task that completes when the contacts have been created or the failure is shown.</returns>
    [RelayCommand]
    private async Task AddSelectedCollectedAsync()
    {
        var items = SelectedCollected.ToList();

        // Showing the new contact leaves the form, so unsaved changes are asked about first.
        if (items.Count == 0 || (items.Count == 1 && !await ConfirmDiscardAsync()))
        {
            return;
        }

        await RunAsync(async () =>
        {
            var created = Guid.Empty;
            try
            {
                foreach (var item in items)
                {
                    var (firstName, lastName) = PersonName.Split(item.Entry.Name);
                    var details = new ContactDetails(
                        firstName, lastName, [item.Entry.Address], [], string.Empty, string.Empty);
                    created = await _dispatcher.SendAsync(new SaveContactCommand(null, details));
                }
            }
            finally
            {
                // Also after a failure part of the way: the contacts created before it exist.
                await ReloadAsync();
            }

            if (items.Count > 1)
            {
                StatusText = _localizer.Format("Contacts_AddedCount", items.Count);
                return;
            }

            IsEditing = false;
            SearchText = string.Empty;
            SelectedPage = ContactsPage;
            Select(created);
        });
    }

    /// <summary>
    /// Removes the selected collected addresses for good, after a confirmation when there are several.
    /// </summary>
    /// <returns>A task that completes when the addresses have been removed or the removal was cancelled.</returns>
    [RelayCommand]
    private async Task RemoveSelectedCollectedAsync()
    {
        var items = SelectedCollected.ToList();
        if (items.Count == 0)
        {
            return;
        }

        if (items.Count > 1
            && !await _dialogs.ConfirmAsync(
                _localizer["Contacts_RemoveManyTitle"],
                _localizer.Format("Contacts_RemoveManyMessage", items.Count),
                _localizer["Contacts_Remove"]))
        {
            return;
        }

        await RunAsync(async () =>
        {
            try
            {
                foreach (var item in items)
                {
                    await _dispatcher.SendAsync(new DismissCollectedAddressCommand(item.Entry.Address));
                }
            }
            finally
            {
                await ReloadAsync();
            }
        });
    }

    /// <summary>
    /// Closes the window to write to the selected collected address.
    /// </summary>
    [RelayCommand]
    private void WriteCollected()
    {
        if (SingleCollected is { } item)
        {
            CloseRequested?.Invoke(this, RecipientList.Format(item.Entry.Name, item.Entry.Address));
        }
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
    /// Asks the window to close, after asking about unsaved changes.
    /// </summary>
    /// <returns>A task that completes when the window was asked to close or the user kept it open.</returns>
    [RelayCommand]
    private async Task CloseAsync()
    {
        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        // Discarded changes must not be asked about again while the window closes.
        IsEditing = false;
        CloseRequested?.Invoke(this, null);
    }

    /// <summary>
    /// Handles the Escape key: leaves the visible form, otherwise closes the window.
    /// </summary>
    /// <returns>A task that completes when the form or the window was left, or the user kept it.</returns>
    [RelayCommand]
    private Task EscapeAsync() => IsFormShown ? CancelAsync() : CloseAsync();

    /// <summary>
    /// Gets a value indicating whether the contacts page is shown.
    /// </summary>
    private bool IsContactsPage => SelectedPage == ContactsPage;

    /// <summary>
    /// Gets a value indicating whether the UI language puts the family name before the given name.
    /// </summary>
    private bool IsFamilyNameFirst => bool.TryParse(_localizer["Contacts_FamilyNameFirst"], out var first) && first;

    /// <summary>
    /// Adds an empty email address field.
    /// </summary>
    [RelayCommand]
    private void AddEmail() => Emails.Add(CreateEmailField(string.Empty));

    /// <summary>
    /// Adds an empty phone number field.
    /// </summary>
    [RelayCommand]
    private void AddPhone() => Phones.Add(CreatePhoneField(string.Empty));

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
    /// Creates an email field, which can remove itself and become the preferred address.
    /// </summary>
    /// <param name="value">The initial value.</param>
    /// <returns>The field.</returns>
    private ContactValueViewModel CreateEmailField(string value)
    {
        return new ContactValueViewModel(
            value,
            field => Emails.Remove(field),
            field => Emails.Move(Emails.IndexOf(field), 0));
    }

    /// <summary>
    /// Creates a phone field, which can remove itself.
    /// </summary>
    /// <param name="value">The initial value.</param>
    /// <returns>The field.</returns>
    private ContactValueViewModel CreatePhoneField(string value)
    {
        return new ContactValueViewModel(value, field => Phones.Remove(field));
    }

    /// <summary>
    /// Replaces the fields of a list; without values one empty field is shown, ready to type into.
    /// </summary>
    /// <param name="fields">The list of fields.</param>
    /// <param name="values">The values.</param>
    /// <param name="create">Creates one field of the list.</param>
    private static void Fill(
        ObservableCollection<ContactValueViewModel> fields,
        IReadOnlyList<string> values,
        Func<string, ContactValueViewModel> create)
    {
        fields.Clear();
        foreach (var value in values.Count > 0 ? values : [string.Empty])
        {
            fields.Add(create(value));
        }
    }

    /// <summary>
    /// Gets what saving the form would store, as one comparable text.
    /// </summary>
    /// <returns>The content, ignoring surrounding white space and empty fields.</returns>
    private string FormContent()
    {
        return string.Join(
            '\u0001',
            FirstName.Trim(),
            LastName.Trim(),
            string.Join('\u0002', Values(Emails)),
            string.Join('\u0002', Values(Phones)),
            Company.Trim(),
            Note.Trim());
    }

    /// <summary>
    /// Asks whether unsaved changes may be dropped.
    /// </summary>
    /// <returns><see langword="true"/> when there are none or the user confirmed dropping them.</returns>
    private async Task<bool> ConfirmDiscardAsync()
    {
        return !HasUnsavedChanges
            || await _dialogs.ConfirmAsync(
                _localizer["Contacts_DiscardTitle"],
                _localizer["Contacts_DiscardMessage"],
                _localizer["Contacts_Discard"]);
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
    /// Shows the card of a newly selected contact. Losing the selection, for example because the search hides the row,
    /// keeps what is shown.
    /// </summary>
    /// <param name="value">The selected row.</param>
    partial void OnSelectedContactChanged(ContactItemViewModel? value)
    {
        // Rebuilding the list selects the shown contact again, which must not close its form.
        if (value is null || value.Contact.Id == _shownId)
        {
            return;
        }

        if (HasUnsavedChanges)
        {
            PendingSwitch = SwitchAsync(value.Contact.Id);
            return;
        }

        ErrorText = null;
        StatusText = null;
        Show(value.Contact);
    }

    /// <summary>
    /// Asks about unsaved changes before showing another contact, and selects the previous row again when the user
    /// keeps them.
    /// </summary>
    /// <param name="id">The contact to show.</param>
    /// <returns>A task that completes when the question has been answered.</returns>
    private async Task SwitchAsync(Guid id)
    {
        // The list is still changing its selection; it must finish before the selection is changed again.
        await Task.Yield();
        if (await ConfirmDiscardAsync())
        {
            ErrorText = null;
            IsEditing = false;
            Select(id);
            return;
        }

        SelectedContact = Contacts.FirstOrDefault(c => c.Contact.Id == _shownId);
    }

    /// <summary>
    /// Shows the card of a contact.
    /// </summary>
    /// <param name="contact">The contact.</param>
    private void Show(Contact contact)
    {
        _shownId = contact.Id;
        IsEditing = false;
        ShownContact = new ContactItemViewModel(contact);
        IsExisting = true;
    }

    /// <summary>
    /// Shows an empty form for a new contact.
    /// </summary>
    private void StartNew()
    {
        ErrorText = null;
        StatusText = null;
        SelectedPage = ContactsPage;
        SelectedContact = null;
        ClearForm();
        _baseline = FormContent();
        IsEditing = true;
    }

    /// <summary>
    /// Empties and hides the form and the card.
    /// </summary>
    private void ClearForm()
    {
        _shownId = null;
        ShownContact = null;
        IsExisting = false;
        IsEditing = false;
        FirstName = LastName = Company = Note = string.Empty;
        Fill(Emails, [], CreateEmailField);
        Fill(Phones, [], CreatePhoneField);
    }

    /// <summary>
    /// Gets a contact of the loaded address book.
    /// </summary>
    /// <param name="id">The contact.</param>
    /// <returns>The contact, or <see langword="null"/>.</returns>
    private Contact? FindContact(Guid? id) => _book.Contacts.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Selects a contact and shows its card.
    /// </summary>
    /// <param name="id">The contact.</param>
    private void Select(Guid id)
    {
        if (FindContact(id) is { } contact)
        {
            Show(contact);
            SelectedContact = Contacts.FirstOrDefault(c => c.Contact.Id == id);
        }
    }

    /// <summary>
    /// Marks the first email field as the preferred address.
    /// </summary>
    /// <param name="sender">The fields.</param>
    /// <param name="e">The change.</param>
    private void OnEmailsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        for (var i = 0; i < Emails.Count; i++)
        {
            Emails[i].IsPrimary = i == 0;
        }
    }

    /// <summary>
    /// Updates the pane of the collected page when its selection changes.
    /// </summary>
    /// <param name="sender">The selection.</param>
    /// <param name="e">The change.</param>
    private void OnSelectedCollectedChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsNoCollectedShown));
        OnPropertyChanged(nameof(IsOneCollectedShown));
        OnPropertyChanged(nameof(IsManyCollectedShown));
        OnPropertyChanged(nameof(SingleCollected));
        OnPropertyChanged(nameof(SelectedCollectedText));
    }

    /// <summary>
    /// Loads the address book and rebuilds the lists.
    /// </summary>
    /// <returns>A task that completes when the lists are rebuilt.</returns>
    private async Task ReloadAsync()
    {
        _book = await _dispatcher.SendAsync(new GetAddressBookQuery());
        ApplyFilter();

        // An import may have changed the contact of the card.
        if (FindContact(_shownId) is { } shown)
        {
            ShownContact = new ContactItemViewModel(shown);
        }
    }

    /// <summary>
    /// Rebuilds both lists with the entries matching the search.
    /// </summary>
    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        bool Matches(string value) => value.Contains(text, StringComparison.CurrentCultureIgnoreCase);

        var found = _book.Contacts
            .Where(c =>
                Matches(c.DisplayName) || Matches($"{c.FirstName} {c.LastName}") || Matches(c.Company)
                || c.Emails.Any(Matches) || c.Phones.Any(Matches))
            .Select(c => new ContactItemViewModel(c))
            .OrderBy(c => c.GroupKey == ContactItemViewModel.OtherGroup)
            .ThenBy(c => c.GroupKey, StringComparer.Ordinal)
            .ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Contacts.Clear();
        string? group = null;
        foreach (var item in found)
        {
            Contacts.Add(new ContactItemViewModel(item.Contact) { IsGroupStart = item.GroupKey != group });
            group = item.GroupKey;
        }

        SelectedCollected.Clear();
        Collected.Clear();
        foreach (var entry in _book.Collected.Where(a => Matches(a.Name) || Matches(a.Address)))
        {
            Collected.Add(new CollectedItemViewModel(entry));
        }

        SelectedContact = Contacts.FirstOrDefault(c => c.Contact.Id == _shownId);
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
