using AwesomeAssertions;
using Mediator;
using Swiftlet.Application.Contacts;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels.Compose;
using Swiftlet.Desktop.ViewModels.Contacts;
using Swiftlet.Domain.Contacts;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests the contacts window view model and the recipient suggestions.
/// </summary>
public sealed class ContactsViewModelTests
{
    /// <summary>
    /// The contact the fake address book starts with.
    /// </summary>
    private readonly Contact _contact = Contact.Create("三", "张", ["zhang@x.com"], ["123"], "Acme", "note");

    /// <summary>
    /// The collected address the fake address book starts with.
    /// </summary>
    private readonly CollectedAddress _collected = new("bob@x.com", "Bob", DateTimeOffset.UnixEpoch);

    /// <summary>
    /// The fake dispatcher.
    /// </summary>
    private readonly FakeDispatcher _dispatcher = new();

    /// <summary>
    /// The fake dialogs.
    /// </summary>
    private readonly FakeDialogService _dialogs = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ContactsViewModelTests"/> class.
    /// </summary>
    public ContactsViewModelTests()
    {
        _dispatcher.On<GetAddressBookQuery>(_ => new AddressBook([_contact], [_collected]));
    }

    /// <summary>
    /// Verifies both lists are loaded and filtered by the search.
    /// </summary>
    [Fact]
    public async Task Search_Text_FiltersBothLists()
    {
        var viewModel = await CreateAsync();
        viewModel.Contacts.Should().ContainSingle().Which.Title.Should().Be("张三");
        viewModel.Collected.Should().ContainSingle().Which.Title.Should().Be("Bob");

        viewModel.SearchText = "acme";

        viewModel.Contacts.Should().ContainSingle();
        viewModel.Collected.Should().BeEmpty();
        viewModel.IsCollectedEmpty.Should().BeTrue();
    }

    /// <summary>
    /// Verifies selecting a contact shows its card, and editing fills a form that a search keeps.
    /// </summary>
    [Fact]
    public async Task Edit_ThenSearch_KeepsEditedForm()
    {
        var viewModel = await CreateAsync();

        viewModel.SelectedContact = viewModel.Contacts[0];
        viewModel.IsViewing.Should().BeTrue();
        viewModel.IsFormShown.Should().BeFalse();
        viewModel.ShownContact!.EmailRows.Should().ContainSingle().Which.Should().Be(new ContactDetailRow("zhang@x.com", false));
        viewModel.EditCommand.Execute(null);
        viewModel.Emails.Select(f => f.Value).Should().Equal("zhang@x.com");
        viewModel.FirstName = "typed";
        viewModel.SearchText = "nothing";
        viewModel.SearchText = string.Empty;

        viewModel.FirstName.Should().Be("typed");
        viewModel.IsFormShown.Should().BeTrue();
        viewModel.SelectedContact.Should().NotBeNull();
    }

    /// <summary>
    /// Verifies cancelling an unchanged form returns to the card without asking.
    /// </summary>
    [Fact]
    public async Task Cancel_Unchanged_ShowsCardWithoutAsking()
    {
        var viewModel = await CreateEditingAsync();
        viewModel.AddPhoneCommand.Execute(null);
        viewModel.Company = " Acme ";

        await viewModel.CancelCommand.ExecuteAsync(null);

        _dialogs.Confirmations.Should().BeEmpty();
        viewModel.IsViewing.Should().BeTrue();
    }

    /// <summary>
    /// Verifies cancelling a changed form asks, and only a confirmation drops the change.
    /// </summary>
    /// <param name="confirmed">Whether the user confirms.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancel_Changed_AsksFirst(bool confirmed)
    {
        _dialogs.ConfirmResult = confirmed;
        var viewModel = await CreateEditingAsync();
        viewModel.FirstName = "typed";

        await viewModel.CancelCommand.ExecuteAsync(null);

        _dialogs.Confirmations.Should().ContainSingle();
        viewModel.IsViewing.Should().Be(confirmed);
        viewModel.IsFormShown.Should().Be(!confirmed);
        viewModel.HasUnsavedChanges.Should().Be(!confirmed);
        viewModel.ShownContact!.Title.Should().Be("张三");
    }

    /// <summary>
    /// Verifies selecting another contact over unsaved changes asks, and keeping them selects the edited one again.
    /// </summary>
    /// <param name="confirmed">Whether the user drops the changes.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectedContact_UnsavedChanges_AsksFirst(bool confirmed)
    {
        var other = Contact.Create("Amy", string.Empty, ["amy@x.com"]);
        _dispatcher.On<GetAddressBookQuery>(_ => new AddressBook([_contact, other], []));
        _dialogs.ConfirmResult = confirmed;
        var viewModel = await CreateAsync();
        viewModel.SelectedContact = viewModel.Contacts.Single(c => c.Contact == _contact);
        viewModel.EditCommand.Execute(null);
        viewModel.Note = "typed";

        viewModel.SelectedContact = viewModel.Contacts.Single(c => c.Contact == other);
        await viewModel.PendingSwitch;

        _dialogs.Confirmations.Should().ContainSingle();
        viewModel.SelectedContact!.Contact.Should().Be(confirmed ? other : _contact);
        viewModel.IsFormShown.Should().Be(!confirmed);
        viewModel.Note.Should().Be("typed");
        viewModel.ShownContact!.Contact.Should().Be(confirmed ? other : _contact);
    }

    /// <summary>
    /// Verifies closing and Escape ask about unsaved changes, and Escape leaves the form before the window.
    /// </summary>
    [Fact]
    public async Task CloseAndEscape_UnsavedChanges_AskFirst()
    {
        var viewModel = await CreateEditingAsync();
        var closed = 0;
        viewModel.CloseRequested += (_, _) => closed++;
        viewModel.FirstName = "typed";

        await viewModel.CloseCommand.ExecuteAsync(null);
        await viewModel.EscapeCommand.ExecuteAsync(null);
        closed.Should().Be(0);
        viewModel.IsFormShown.Should().BeTrue();

        _dialogs.ConfirmResult = true;
        await viewModel.EscapeCommand.ExecuteAsync(null);
        closed.Should().Be(0);
        viewModel.IsViewing.Should().BeTrue();

        await viewModel.EscapeCommand.ExecuteAsync(null);
        closed.Should().Be(1);
        _dialogs.Confirmations.Should().HaveCount(3);
    }

    /// <summary>
    /// Verifies the chosen address becomes the first one that is saved.
    /// </summary>
    [Fact]
    public async Task MakePrimary_SecondAddress_SavesItFirst()
    {
        _dispatcher.On<SaveContactCommand>(_ => _contact.Id);
        var viewModel = await CreateAsync();
        await viewModel.NewCommand.ExecuteAsync(null);
        Type(viewModel, "a@x.com", "b@x.com");
        viewModel.Emails.Select(f => f.IsPrimary).Should().Equal(true, false);

        viewModel.Emails[1].MakePrimaryCommand.Execute(null);

        viewModel.Emails.Select(f => f.IsPrimary).Should().Equal(true, false);
        await viewModel.SaveCommand.ExecuteAsync(null);
        _dispatcher.Sent<SaveContactCommand>().Single().Details.Emails.Should().Equal("b@x.com", "a@x.com");
        viewModel.IsViewing.Should().BeTrue();
    }

    /// <summary>
    /// Verifies one address of the card can be written to and copied.
    /// </summary>
    [Fact]
    public async Task WriteAndCopy_ChosenValue_UsesIt()
    {
        var viewModel = await CreateAsync();
        viewModel.SelectedContact = viewModel.Contacts[0];
        string? recipient = null;
        viewModel.CloseRequested += (_, value) => recipient = value;

        viewModel.WriteCommand.Execute("work@x.com");
        await viewModel.CopyCommand.ExecuteAsync("123");

        recipient.Should().Be("张三 <work@x.com>");
        _dialogs.CopiedTexts.Should().Equal("123");
        viewModel.StatusText.Should().Contain("123");
    }

    /// <summary>
    /// Verifies the list is grouped by the first letter, with other names last.
    /// </summary>
    [Fact]
    public async Task Contacts_Several_GroupedByLetter()
    {
        _dispatcher.On<GetAddressBookQuery>(_ => new AddressBook(
            [
                _contact,
                Contact.Create("bob", string.Empty, ["bob@x.com"]),
                Contact.Create("Émile", string.Empty, ["emile@x.com"]),
                Contact.Create(string.Empty, string.Empty, ["9@x.com"]),
                Contact.Create("Eve", string.Empty, ["eve@x.com"]),
            ],
            []));

        var viewModel = await CreateAsync();

        viewModel.Contacts.Select(c => c.GroupKey).Should().Equal("B", "E", "E", "#", "#");
        viewModel.Contacts.Select(c => c.IsGroupStart).Should().Equal(true, true, false, true, false);
    }

    /// <summary>
    /// Verifies saving sends the values of the form.
    /// </summary>
    [Fact]
    public async Task Save_NewContact_SendsDetails()
    {
        _dispatcher.On<SaveContactCommand>(_ => _contact.Id);
        var viewModel = await CreateAsync();
        await viewModel.NewCommand.ExecuteAsync(null);
        viewModel.FirstName = "New";
        viewModel.LastName = "Person";
        Type(viewModel, "a@x.com", " b@x.com ", "  ");
        viewModel.Phones.Single().Value = "1";
        viewModel.AddPhoneCommand.Execute(null);
        viewModel.Phones[1].Value = "2";

        await viewModel.SaveCommand.ExecuteAsync(null);

        var sent = _dispatcher.Sent<SaveContactCommand>().Should().ContainSingle().Subject;
        sent.Id.Should().BeNull();
        (sent.Details.FirstName, sent.Details.LastName).Should().Be(("New", "Person"));
        sent.Details.Emails.Should().Equal("a@x.com", "b@x.com");
        sent.Details.Phones.Should().Equal("1", "2");
        viewModel.IsExisting.Should().BeTrue();
        viewModel.ErrorText.Should().BeNull();
    }

    /// <summary>
    /// Verifies missing and malformed addresses are reported without saving.
    /// </summary>
    /// <param name="emails">The typed addresses.</param>
    /// <param name="expected">Part of the error text.</param>
    [Theory]
    [InlineData("  ", "至少")]
    [InlineData("a@x.com;b@x.com", "a@x.com;b@x.com")]
    public async Task Save_BadAddresses_ShowsError(string emails, string expected)
    {
        var viewModel = await CreateAsync();
        await viewModel.NewCommand.ExecuteAsync(null);
        Type(viewModel, emails);

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ErrorText.Should().Contain(expected);
        _dispatcher.Sent<SaveContactCommand>().Should().BeEmpty();
    }

    /// <summary>
    /// Verifies an address of another contact is reported by name.
    /// </summary>
    [Fact]
    public async Task Save_DuplicateAddress_ShowsAddress()
    {
        _dispatcher.On<SaveContactCommand>(_ => throw new DuplicateContactEmailException("zhang@x.com"));
        var viewModel = await CreateAsync();
        await viewModel.NewCommand.ExecuteAsync(null);
        Type(viewModel, "zhang@x.com");

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ErrorText.Should().Contain("zhang@x.com");
    }

    /// <summary>
    /// Verifies fields can be removed, including the last one, and added again.
    /// </summary>
    [Fact]
    public async Task RemoveField_LastOne_LeavesListEmptyUntilAdded()
    {
        var viewModel = await CreateEditingAsync();

        viewModel.Phones.Single().RemoveCommand.Execute(null);
        viewModel.Phones.Should().BeEmpty();
        viewModel.AddPhoneCommand.Execute(null);

        viewModel.Phones.Should().ContainSingle().Which.Value.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the family name is shown first in Chinese only.
    /// </summary>
    [Fact]
    public void NameColumns_Language_OrdersNames()
    {
        var chinese = new ContactsViewModel(_dispatcher, _dialogs, TestLocalizer.Chinese());
        var english = new ContactsViewModel(_dispatcher, _dialogs, TestLocalizer.English());

        (chinese.LastNameColumn, chinese.FirstNameColumn).Should().Be((0, 1));
        (english.FirstNameColumn, english.LastNameColumn).Should().Be((0, 1));
    }

    /// <summary>
    /// Verifies the chosen name order is used for the list, the form and the recipient, and either order can be searched.
    /// </summary>
    /// <param name="order">The stored order.</param>
    /// <param name="title">The expected name.</param>
    /// <param name="familyColumn">The expected column of the family name.</param>
    [Theory]
    [InlineData("familyFirst", "Walker Alice", 0)]
    [InlineData("givenFirst", "Alice Walker", 1)]
    public async Task NameOrder_Chosen_AppliesToListFormAndRecipient(string order, string title, int familyColumn)
    {
        _dispatcher.On<GetAddressBookQuery>(_ => new AddressBook(
            [Contact.Create("Alice", "Walker", ["alice@x.com"])], []));
        var viewModel = new ContactsViewModel(
            _dispatcher,
            _dialogs,
            TestLocalizer.Chinese(),
            settings: new FakeAppSettings { ContactNameOrder = order });
        await viewModel.InitializeAsync();
        string? recipient = null;
        viewModel.CloseRequested += (_, value) => recipient = value;

        viewModel.SearchText = "alice walker";
        viewModel.Contacts.Should().ContainSingle().Which.Title.Should().Be(title);
        viewModel.SearchText = "walker alice";
        viewModel.Contacts.Should().ContainSingle().Which.GroupKey.Should().Be(title[..1]);
        viewModel.SelectedContact = viewModel.Contacts[0];
        viewModel.ShownContact!.Title.Should().Be(title);
        viewModel.WriteCommand.Execute(null);

        recipient.Should().Be($"{title} <alice@x.com>");
        viewModel.LastNameColumn.Should().Be(familyColumn);
    }

    /// <summary>
    /// Verifies suggestions show the chosen order and are found by a name typed in either order.
    /// </summary>
    [Fact]
    public void Find_OtherNameOrder_StillMatches()
    {
        var all = RecipientSuggestions.From(new AddressBook([_contact], []), NameOrder.GivenFirst);

        RecipientSuggestions.Find("张三", all).Should().ContainSingle().Which.Name.Should().Be("三张");
        RecipientSuggestions.Find("三张", all).Should().ContainSingle();
    }

    /// <summary>
    /// Verifies deleting needs a confirmation.
    /// </summary>
    /// <param name="confirmed">Whether the user confirms.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_Contact_AsksFirst(bool confirmed)
    {
        _dispatcher.On<DeleteContactCommand>(_ => Unit.Value);
        _dialogs.ConfirmResult = confirmed;
        var viewModel = await CreateAsync();
        viewModel.SelectedContact = viewModel.Contacts[0];

        await viewModel.DeleteCommand.ExecuteAsync(null);

        _dialogs.Confirmations.Should().ContainSingle().Which.Message.Should().Contain("张三");
        _dispatcher.Sent<DeleteContactCommand>().Should().HaveCount(confirmed ? 1 : 0);
        viewModel.IsViewing.Should().Be(!confirmed);
    }

    /// <summary>
    /// Verifies writing closes the window with the recipient, for contacts and collected addresses.
    /// </summary>
    [Fact]
    public async Task Write_ContactAndCollected_ReturnsRecipient()
    {
        var viewModel = await CreateAsync();
        var recipients = new List<string?>();
        viewModel.CloseRequested += (_, recipient) => recipients.Add(recipient);
        viewModel.WriteCommand.CanExecute(null).Should().BeFalse();
        viewModel.SelectedContact = viewModel.Contacts[0];

        viewModel.WriteCommand.Execute(null);
        viewModel.SelectedCollected.Add(viewModel.Collected[0]);
        viewModel.WriteCollectedCommand.Execute(null);
        await viewModel.CloseCommand.ExecuteAsync(null);

        recipients.Should().Equal("张三 <zhang@x.com>", "Bob <bob@x.com>", null);
    }

    /// <summary>
    /// Verifies a single collected address becomes a contact that is shown right away.
    /// </summary>
    [Fact]
    public async Task AddSelectedCollected_One_SavesAndShowsContact()
    {
        _dispatcher.On<SaveContactCommand>(_ => _contact.Id);
        var viewModel = await CreateAsync();
        viewModel.SelectedPage = 1;
        viewModel.SelectedCollected.Add(viewModel.Collected[0]);
        viewModel.IsOneCollectedShown.Should().BeTrue();

        await viewModel.AddSelectedCollectedCommand.ExecuteAsync(null);

        var details = _dispatcher.Sent<SaveContactCommand>().Should().ContainSingle().Subject.Details;
        (details.FirstName, details.LastName).Should().Be(("Bob", string.Empty));
        details.Emails.Should().Equal("bob@x.com");
        viewModel.SelectedPage.Should().Be(ContactsViewModel.ContactsPage);
        viewModel.IsViewing.Should().BeTrue();
    }

    /// <summary>
    /// Verifies several collected addresses are added at once while the page stays.
    /// </summary>
    [Fact]
    public async Task AddSelectedCollected_Several_SavesEachAndStays()
    {
        _dispatcher.On<GetAddressBookQuery>(_ => new AddressBook([], [_collected, Other()]));
        _dispatcher.On<SaveContactCommand>(_ => Guid.NewGuid());
        var viewModel = await CreateAsync();
        SelectAllCollected(viewModel);
        viewModel.IsManyCollectedShown.Should().BeTrue();
        viewModel.SelectedCollectedText.Should().Contain("2");

        await viewModel.AddSelectedCollectedCommand.ExecuteAsync(null);

        _dispatcher.Sent<SaveContactCommand>().Should().HaveCount(2);
        _dispatcher.Sent<GetAddressBookQuery>().Should().HaveCount(2);
        viewModel.SelectedPage.Should().Be(1);
        viewModel.IsNoCollectedShown.Should().BeTrue();
        viewModel.StatusText.Should().Contain("2");
    }

    /// <summary>
    /// Verifies a failure part of the way is reported after the lists were loaded again.
    /// </summary>
    [Fact]
    public async Task AddSelectedCollected_Failure_ReloadsAndShowsError()
    {
        _dispatcher.On<GetAddressBookQuery>(_ => new AddressBook([], [_collected, Other()]));
        _dispatcher.On<SaveContactCommand>(c => c.Details.Emails[0] == "amy@x.com"
            ? throw new DuplicateContactEmailException("amy@x.com")
            : Guid.NewGuid());
        var viewModel = await CreateAsync();
        SelectAllCollected(viewModel);

        await viewModel.AddSelectedCollectedCommand.ExecuteAsync(null);

        _dispatcher.Sent<GetAddressBookQuery>().Should().HaveCount(2);
        viewModel.ErrorText.Should().Contain("amy@x.com");
    }

    /// <summary>
    /// Verifies one collected address is dismissed without asking.
    /// </summary>
    [Fact]
    public async Task RemoveSelectedCollected_One_DismissesAddress()
    {
        _dispatcher.On<DismissCollectedAddressCommand>(_ => Unit.Value);
        var viewModel = await CreateAsync();
        viewModel.SelectedCollected.Add(viewModel.Collected[0]);
        _dispatcher.On<GetAddressBookQuery>(_ => new AddressBook([_contact], []));

        await viewModel.RemoveSelectedCollectedCommand.ExecuteAsync(null);

        _dialogs.Confirmations.Should().BeEmpty();
        _dispatcher.Sent<DismissCollectedAddressCommand>().Should().ContainSingle().Which.Address.Should().Be("bob@x.com");
        viewModel.Collected.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies removing several collected addresses needs a confirmation.
    /// </summary>
    /// <param name="confirmed">Whether the user confirms.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemoveSelectedCollected_Several_AsksFirst(bool confirmed)
    {
        _dispatcher.On<GetAddressBookQuery>(_ => new AddressBook([], [_collected, Other()]));
        _dispatcher.On<DismissCollectedAddressCommand>(_ => Unit.Value);
        _dialogs.ConfirmResult = confirmed;
        var viewModel = await CreateAsync();
        SelectAllCollected(viewModel);

        await viewModel.RemoveSelectedCollectedCommand.ExecuteAsync(null);

        _dialogs.Confirmations.Should().ContainSingle().Which.Message.Should().Contain("2");
        _dispatcher.Sent<DismissCollectedAddressCommand>().Should().HaveCount(confirmed ? 2 : 0);
    }

    /// <summary>
    /// Verifies opening for a known address shows that contact.
    /// </summary>
    [Fact]
    public async Task Initialize_KnownAddress_ShowsContact()
    {
        var viewModel = await CreateAsync(new ContactsRequest("Someone", "ZHANG@x.com"));

        viewModel.IsViewing.Should().BeTrue();
        viewModel.ShownContact!.Contact.Should().Be(_contact);
    }

    /// <summary>
    /// Verifies opening for an unknown address fills a new form.
    /// </summary>
    [Fact]
    public async Task Initialize_UnknownAddress_PrefillsNewContact()
    {
        var viewModel = await CreateAsync(new ContactsRequest("Eve Smith", "eve@x.com"));

        viewModel.IsFormShown.Should().BeTrue();
        viewModel.IsExisting.Should().BeFalse();
        viewModel.HasUnsavedChanges.Should().BeFalse();
        (viewModel.FirstName, viewModel.LastName).Should().Be(("Eve", "Smith"));
        viewModel.Emails.Select(f => f.Value).Should().Equal("eve@x.com");
        viewModel.Phones.Should().ContainSingle().Which.Value.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the picked file is imported in the format of its extension and the result reported.
    /// </summary>
    [Fact]
    public async Task Import_CsvFile_SendsContentAndReports()
    {
        var path = Path.Combine(Path.GetTempPath(), $"swiftlet-{Guid.NewGuid():N}.CSV");
        await File.WriteAllTextAsync(path, "x", TestContext.Current.CancellationToken);
        _dispatcher.On<ImportContactsCommand>(_ => new ImportContactsResult(1, 2, 3));
        _dialogs.ContactFile = path;
        var viewModel = await CreateAsync();

        try
        {
            await viewModel.ImportCommand.ExecuteAsync(null);
        }
        finally
        {
            File.Delete(path);
        }

        _dispatcher.Sent<ImportContactsCommand>().Should().ContainSingle().Which.Format.Should().Be(ContactFileFormat.Csv);
        viewModel.StatusText.Should().Contain("1").And.Contain("2").And.Contain("3");
    }

    /// <summary>
    /// Verifies a file that is no contact file is reported.
    /// </summary>
    [Fact]
    public async Task Import_BadFile_ShowsFormatError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"swiftlet-{Guid.NewGuid():N}.vcf");
        await File.WriteAllTextAsync(path, "x", TestContext.Current.CancellationToken);
        _dispatcher.On<ImportContactsCommand>(_ => throw new FormatException("bad"));
        _dialogs.ContactFile = path;
        var viewModel = await CreateAsync();

        try
        {
            await viewModel.ImportCommand.ExecuteAsync(null);
        }
        finally
        {
            File.Delete(path);
        }

        viewModel.ErrorText.Should().Contain("vCard");
    }

    /// <summary>
    /// Verifies the export writes the returned bytes to the chosen file.
    /// </summary>
    [Fact]
    public async Task Export_ChosenPath_WritesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"swiftlet-{Guid.NewGuid():N}.vcf");
        _dispatcher.On<ExportContactsQuery>(_ => new byte[] { 1, 2 });
        _dialogs.SavePath = path;
        var viewModel = await CreateAsync();

        try
        {
            await viewModel.ExportVCardCommand.ExecuteAsync(null);

            (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)).Should().Equal(1, 2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies only the last entry is matched and used addresses are not offered again.
    /// </summary>
    [Fact]
    public void Find_LastEntry_SuggestsUnusedMatches()
    {
        var all = RecipientSuggestions.From(new AddressBook([_contact], [_collected]));

        RecipientSuggestions.Find("x@y.com, bo", all).Should().ContainSingle().Which.Address.Should().Be("bob@x.com");
        RecipientSuggestions.Find("acme", all).Should().ContainSingle().Which.Name.Should().Be("张三");
        RecipientSuggestions.Find("张三 <zhang@x.com>, x.com", all).Should().ContainSingle().Which.Name.Should().Be("Bob");
        RecipientSuggestions.Find("bob@x.com, ", all).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies at most the shown number of suggestions is returned.
    /// </summary>
    [Fact]
    public void Find_ManyMatches_ReturnsLimit()
    {
        var all = Enumerable.Range(0, 20).Select(i => new RecipientSuggestion(string.Empty, $"user{i}@x.com"));

        RecipientSuggestions.Find("user", all).Should().HaveCount(RecipientSuggestions.MaxShown);
    }

    /// <summary>
    /// Verifies a chosen suggestion replaces the last entry only.
    /// </summary>
    /// <param name="text">The typed list.</param>
    /// <param name="expected">The list afterwards.</param>
    [Theory]
    [InlineData("bo", "\"Doe, John\" <j@x.com>, ")]
    [InlineData("a@x.com,bo", "a@x.com, \"Doe, John\" <j@x.com>, ")]
    public void Apply_Suggestion_ReplacesLastEntry(string text, string expected)
    {
        RecipientSuggestions.Apply(text, new RecipientSuggestion("Doe, John", "j@x.com")).Should().Be(expected);
    }

    /// <summary>
    /// Verifies the compose window loads its suggestions and works without them when loading fails.
    /// </summary>
    [Fact]
    public async Task ComposeInitialize_LoadsSuggestionsAndSurvivesFailure()
    {
        var account = TestData.Account();
        var request = new ComposeRequest(ComposeMode.New, account.Id, string.Empty, string.Empty, string.Empty, null);
        var viewModel = new ComposeViewModel(_dispatcher, _dialogs, TestLocalizer.Chinese(), [account], request);

        await viewModel.InitializeAsync();
        viewModel.Suggestions.Should().HaveCount(2);

        var failing = new ComposeViewModel(new FakeDispatcher(), _dialogs, TestLocalizer.Chinese(), [account], request);
        await failing.InitializeAsync();
        failing.Suggestions.Should().BeEmpty();
    }

    /// <summary>
    /// A second collected address.
    /// </summary>
    /// <returns>The address.</returns>
    private static CollectedAddress Other() => new("amy@x.com", "Amy", DateTimeOffset.UnixEpoch);

    /// <summary>
    /// Selects every collected address on its page.
    /// </summary>
    /// <param name="viewModel">The view model.</param>
    private static void SelectAllCollected(ContactsViewModel viewModel)
    {
        viewModel.SelectedPage = 1;
        foreach (var item in viewModel.Collected)
        {
            viewModel.SelectedCollected.Add(item);
        }
    }

    /// <summary>
    /// Creates a view model editing the starting contact.
    /// </summary>
    /// <returns>The view model.</returns>
    private async Task<ContactsViewModel> CreateEditingAsync()
    {
        var viewModel = await CreateAsync();
        viewModel.SelectedContact = viewModel.Contacts[0];
        viewModel.EditCommand.Execute(null);
        return viewModel;
    }

    /// <summary>
    /// Replaces the email fields of the form with typed values.
    /// </summary>
    /// <param name="viewModel">The view model.</param>
    /// <param name="values">One value per field.</param>
    private static void Type(ContactsViewModel viewModel, params string[] values)
    {
        while (viewModel.Emails.Count < values.Length)
        {
            viewModel.AddEmailCommand.Execute(null);
        }

        for (var i = 0; i < values.Length; i++)
        {
            viewModel.Emails[i].Value = values[i];
        }
    }

    /// <summary>
    /// Creates an initialized view model.
    /// </summary>
    /// <param name="request">The address it is opened for.</param>
    /// <returns>The view model.</returns>
    private async Task<ContactsViewModel> CreateAsync(ContactsRequest? request = null)
    {
        var viewModel = new ContactsViewModel(_dispatcher, _dialogs, TestLocalizer.Chinese());
        await viewModel.InitializeAsync(request);
        return viewModel;
    }
}
