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
    /// Verifies selecting a contact fills the form, and hiding it by searching keeps what is typed.
    /// </summary>
    [Fact]
    public async Task SelectedContact_ThenSearch_KeepsEditedForm()
    {
        var viewModel = await CreateAsync();

        viewModel.SelectedContact = viewModel.Contacts[0];
        viewModel.Emails.Select(f => f.Value).Should().Equal("zhang@x.com");
        viewModel.IsExisting.Should().BeTrue();
        viewModel.FirstName = "typed";
        viewModel.SearchText = "nothing";
        viewModel.SearchText = string.Empty;

        viewModel.FirstName.Should().Be("typed");
        viewModel.SelectedContact.Should().NotBeNull();
    }

    /// <summary>
    /// Verifies saving sends the values of the form.
    /// </summary>
    [Fact]
    public async Task Save_NewContact_SendsDetails()
    {
        _dispatcher.On<SaveContactCommand>(_ => _contact.Id);
        var viewModel = await CreateAsync();
        viewModel.NewCommand.Execute(null);
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
        viewModel.NewCommand.Execute(null);
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
        viewModel.NewCommand.Execute(null);
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
        var viewModel = await CreateAsync();
        viewModel.SelectedContact = viewModel.Contacts[0];

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
        viewModel.IsEditing.Should().Be(!confirmed);
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
        viewModel.WriteCollectedCommand.Execute(viewModel.Collected[0]);
        viewModel.CloseCommand.Execute(null);

        recipients.Should().Equal("张三 <zhang@x.com>", "Bob <bob@x.com>", null);
    }

    /// <summary>
    /// Verifies a collected address becomes a contact with one action.
    /// </summary>
    [Fact]
    public async Task AddCollected_Item_SavesContact()
    {
        _dispatcher.On<SaveContactCommand>(_ => _contact.Id);
        var viewModel = await CreateAsync();
        viewModel.SelectedPage = 1;

        await viewModel.AddCollectedCommand.ExecuteAsync(viewModel.Collected[0]);

        var details = _dispatcher.Sent<SaveContactCommand>().Should().ContainSingle().Subject.Details;
        (details.FirstName, details.LastName).Should().Be(("Bob", string.Empty));
        details.Emails.Should().Equal("bob@x.com");
        viewModel.SelectedPage.Should().Be(ContactsViewModel.ContactsPage);
    }

    /// <summary>
    /// Verifies removing a collected address dismisses it.
    /// </summary>
    [Fact]
    public async Task RemoveCollected_Item_DismissesAddress()
    {
        _dispatcher.On<DismissCollectedAddressCommand>(_ => Unit.Value);
        var viewModel = await CreateAsync();

        await viewModel.RemoveCollectedCommand.ExecuteAsync(viewModel.Collected[0]);

        _dispatcher.Sent<DismissCollectedAddressCommand>().Should().ContainSingle().Which.Address.Should().Be("bob@x.com");
        viewModel.Collected.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies opening for a known address shows that contact.
    /// </summary>
    [Fact]
    public async Task Initialize_KnownAddress_ShowsContact()
    {
        var viewModel = await CreateAsync(new ContactsRequest("Someone", "ZHANG@x.com"));

        viewModel.IsExisting.Should().BeTrue();
        (viewModel.FirstName, viewModel.LastName).Should().Be(("三", "张"));
    }

    /// <summary>
    /// Verifies opening for an unknown address fills a new form.
    /// </summary>
    [Fact]
    public async Task Initialize_UnknownAddress_PrefillsNewContact()
    {
        var viewModel = await CreateAsync(new ContactsRequest("Eve Smith", "eve@x.com"));

        viewModel.IsEditing.Should().BeTrue();
        viewModel.IsExisting.Should().BeFalse();
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
