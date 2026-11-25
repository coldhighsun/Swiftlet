using AwesomeAssertions;
using Mediator;
using Swiftlet.Application.Labels;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Domain.Labels;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="LabelManagerViewModel"/>.
/// </summary>
public class LabelManagerViewModelTests
{
    /// <summary>
    /// A view model wired to a fake dispatcher backed by an in-memory label list.
    /// </summary>
    private sealed class Fixture
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Fixture"/> class.
        /// </summary>
        public Fixture()
        {
            Dispatcher.On<GetLabelsQuery>(_ => (IReadOnlyList<Label>)Stored.OrderBy(l => l.Name, StringComparer.Ordinal).ToList());
            Dispatcher.On<CreateLabelCommand>(c => CreateStored(c));
            Dispatcher.On<UpdateLabelCommand>(_ => Unit.Value);
            Dispatcher.On<DeleteLabelCommand>(c =>
            {
                Stored.RemoveAll(l => l.Id == c.LabelId);
                return Unit.Value;
            });
            ViewModel = new LabelManagerViewModel(Dispatcher, TestLocalizer.Chinese());
        }

        /// <summary>
        /// Gets the dispatcher.
        /// </summary>
        public FakeDispatcher Dispatcher { get; } = new();

        /// <summary>
        /// Gets the labels the fake database holds.
        /// </summary>
        public List<Label> Stored { get; } = [];

        /// <summary>
        /// Gets the view model under test.
        /// </summary>
        public LabelManagerViewModel ViewModel { get; }

        /// <summary>
        /// Stores the label a create command describes.
        /// </summary>
        /// <param name="command">The command.</param>
        /// <returns>The new label identifier.</returns>
        public Guid CreateStored(CreateLabelCommand command)
        {
            var label = Label.Create(command.Name, command.Color);
            Stored.Add(label);
            return label.Id;
        }
    }

    /// <summary>
    /// Verifies initialization lists the stored labels.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_StoredLabels_ListsThem()
    {
        var fixture = new Fixture();
        fixture.Stored.Add(Label.Create("Work", "#E53935"));

        await fixture.ViewModel.InitializeAsync();

        fixture.ViewModel.Labels.Should().ContainSingle().Which.Name.Should().Be("Work");
        fixture.ViewModel.ColorOptions.Should().HaveCountGreaterThanOrEqualTo(8).And.OnlyContain(c => c.Length == 7 && c[0] == '#');
    }

    /// <summary>
    /// Verifies adding a label sends the create command, reloads the list and clears the input.
    /// </summary>
    [Fact]
    public async Task Add_ValidName_SendsCreateAndReloads()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.NewName = "Travel";
        fixture.ViewModel.NewColor = fixture.ViewModel.ColorOptions[1];

        await fixture.ViewModel.AddCommand.ExecuteAsync(null);

        var created = fixture.Dispatcher.Sent<CreateLabelCommand>().Should().ContainSingle().Which;
        created.Name.Should().Be("Travel");
        created.Color.Should().Be(fixture.ViewModel.ColorOptions[1]);
        fixture.ViewModel.Labels.Select(l => l.Name).Should().Equal("Travel");
        fixture.ViewModel.NewName.Should().BeEmpty();
        fixture.ViewModel.ErrorText.Should().BeNull();
    }

    /// <summary>
    /// Verifies the add command is disabled while the name is blank.
    /// </summary>
    [Fact]
    public void Add_BlankName_CannotExecute()
    {
        var fixture = new Fixture();

        fixture.ViewModel.AddCommand.CanExecute(null).Should().BeFalse();

        fixture.ViewModel.NewName = "Work";
        fixture.ViewModel.AddCommand.CanExecute(null).Should().BeTrue();
    }

    /// <summary>
    /// Verifies a duplicate name shows the localized message and not the exception text.
    /// </summary>
    [Fact]
    public async Task Add_DuplicateName_ShowsLocalizedError()
    {
        var fixture = new Fixture();
        fixture.Dispatcher.On<CreateLabelCommand>(_ => throw new InvalidOperationException("A label named 'Work' already exists."));
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.NewName = "Work";

        await fixture.ViewModel.AddCommand.ExecuteAsync(null);

        fixture.ViewModel.ErrorText.Should().Be(TestLocalizer.Chinese()["Labels_ErrorDuplicate"]);
        fixture.ViewModel.ErrorText.Should().NotContain("already exists");
        fixture.ViewModel.NewName.Should().Be("Work");
    }

    /// <summary>
    /// Verifies an invalid name (too long) shows the localized validation message.
    /// </summary>
    [Fact]
    public async Task Add_InvalidName_ShowsLocalizedValidationError()
    {
        var fixture = new Fixture();
        fixture.Dispatcher.On<CreateLabelCommand>(_ => throw new ArgumentException("Label name must be at most 50 characters."));
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.NewName = new string('x', 80);

        await fixture.ViewModel.AddCommand.ExecuteAsync(null);

        fixture.ViewModel.ErrorText.Should().Be(TestLocalizer.Chinese()["Labels_ErrorInvalid"]);
    }

    /// <summary>
    /// Verifies a later successful operation clears the previous error.
    /// </summary>
    [Fact]
    public async Task Add_AfterError_ClearsErrorOnSuccess()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.NewName = "Work";
        fixture.Dispatcher.On<CreateLabelCommand>(_ => throw new InvalidOperationException("dup"));
        await fixture.ViewModel.AddCommand.ExecuteAsync(null);
        fixture.ViewModel.ErrorText.Should().NotBeNull();
        fixture.Dispatcher.On<CreateLabelCommand>(c => fixture.CreateStored(c));

        await fixture.ViewModel.AddCommand.ExecuteAsync(null);

        fixture.ViewModel.ErrorText.Should().BeNull();
    }

    /// <summary>
    /// Verifies saving a row sends the edited name and colour.
    /// </summary>
    [Fact]
    public async Task Save_EditedLabel_SendsUpdateCommand()
    {
        var fixture = new Fixture();
        var work = Label.Create("Work", "#E53935");
        fixture.Stored.Add(work);
        await fixture.ViewModel.InitializeAsync();
        var item = fixture.ViewModel.Labels.Single();
        item.Name = "Office";
        item.Color = fixture.ViewModel.ColorOptions[2];

        await fixture.ViewModel.SaveCommand.ExecuteAsync(item);

        var sent = fixture.Dispatcher.Sent<UpdateLabelCommand>().Should().ContainSingle().Which;
        sent.LabelId.Should().Be(work.Id);
        sent.Name.Should().Be("Office");
        sent.Color.Should().Be(fixture.ViewModel.ColorOptions[2]);
    }

    /// <summary>
    /// Verifies a duplicate name on save shows the localized error.
    /// </summary>
    [Fact]
    public async Task Save_DuplicateName_ShowsLocalizedError()
    {
        var fixture = new Fixture();
        fixture.Stored.Add(Label.Create("Work", "#E53935"));
        fixture.Dispatcher.On<UpdateLabelCommand>(_ => throw new InvalidOperationException("A label named 'Home' already exists."));
        await fixture.ViewModel.InitializeAsync();

        await fixture.ViewModel.SaveCommand.ExecuteAsync(fixture.ViewModel.Labels[0]);

        fixture.ViewModel.ErrorText.Should().Be(TestLocalizer.Chinese()["Labels_ErrorDuplicate"]);
    }

    /// <summary>
    /// Verifies deleting a label sends the delete command and removes the row.
    /// </summary>
    [Fact]
    public async Task Delete_SelectedLabel_SendsDeleteAndRemovesFromList()
    {
        var fixture = new Fixture();
        var work = Label.Create("Work", "#E53935");
        fixture.Stored.AddRange([work, Label.Create("Home", "#1E88E5")]);
        await fixture.ViewModel.InitializeAsync();
        var item = fixture.ViewModel.Labels.Single(l => l.Name == "Work");

        await fixture.ViewModel.DeleteCommand.ExecuteAsync(item);

        fixture.Dispatcher.Sent<DeleteLabelCommand>().Should().ContainSingle().Which.LabelId.Should().Be(work.Id);
        fixture.ViewModel.Labels.Select(l => l.Name).Should().Equal("Home");
    }

    /// <summary>
    /// Verifies a failing delete is reported instead of thrown.
    /// </summary>
    [Fact]
    public async Task Delete_Fails_ShowsLocalizedError()
    {
        var fixture = new Fixture();
        fixture.Stored.Add(Label.Create("Work", "#E53935"));
        fixture.Dispatcher.On<DeleteLabelCommand>(_ => throw new IOException("disk"));
        await fixture.ViewModel.InitializeAsync();

        await fixture.ViewModel.DeleteCommand.ExecuteAsync(fixture.ViewModel.Labels[0]);

        fixture.ViewModel.ErrorText.Should().StartWith(TestLocalizer.Chinese()["Labels_ErrorFailed"]);
        fixture.ViewModel.Labels.Should().ContainSingle();
    }
}
