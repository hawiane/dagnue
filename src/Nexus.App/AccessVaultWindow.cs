using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexus.Core.Models;
using Windows.Graphics;
using Windows.UI;

namespace Nexus.App;

public sealed class AccessVaultWindow : Window
{
    private readonly ListView _profiles = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBox _name = Field("Capability name");
    private readonly ComboBox _kind = EnumBox<CapabilityKind>();
    private readonly ComboBox _permission = EnumBox<CapabilityPermission>();
    private readonly ComboBox _provider = EnumBox<DatabaseProvider>();
    private readonly TextBox _endpoint = Field("Database server, Oracle data source, ODBC DSN, or service endpoint");
    private readonly TextBox _resource = Field("Database/service/file path");
    private readonly TextBox _username = Field("Username or account label");
    private readonly PasswordBox _secret = new() { PlaceholderText = "Protected secret (leave blank to keep current)", MinHeight = 40 };
    private readonly TextBox _scopes = Field("Allowed schemas, folders, mailboxes, or operations");
    private readonly TextBox _tags = Field("HRIS, PAYROLL, BIOMETRIC, SALN, FILES, EMAIL, SYSTEM ADMIN");
    private readonly TextBox _dictionary = Field("What this access contains and when DAGNUE should use it");
    private readonly ToggleSwitch _enabled = new() { Header = "Enabled", IsOn = true };
    private readonly ToggleSwitch _remote = new() { Header = "Allow online tasks" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListView _audit = new();
    private readonly Grid _databaseFields = new();
    private CapabilityProfile? _selected;
    private bool _loaded;

    public AccessVaultWindow()
    {
        Title = "DAGNUE ACCESS // CAPABILITY VAULT";
        SystemBackdrop = new MicaBackdrop();
        Content = BuildLayout();
        AppWindow.Resize(new SizeInt32(1180, 790));
        _profiles.SelectionChanged += (_, _) => LoadSelected();
        _kind.SelectionChanged += (_, _) => UpdateFieldVisibility();
        Activated += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await RefreshAsync();
        };
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Background = Brush(245, 2, 8, 13), Padding = new Thickness(26), RowSpacing = 15 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new StackPanel { Spacing = 3 };
        heading.Children.Add(Text("ACCESS // CAPABILITY VAULT", 23, Cyan(), 65));
        heading.Children.Add(Text("ENCRYPTED LOCAL SECRETS  //  TASK-SCOPED ACCESS  //  AUDITED MASTER TOOLS", 9, Muted(), 70));
        root.Children.Add(heading);

        var body = new Grid { ColumnSpacing = 14 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(BuildProfileList());
        var editor = BuildEditor(); Grid.SetColumn(editor, 1); body.Children.Add(editor);
        Grid.SetRow(body, 1); root.Children.Add(body);
        return root;
    }

    private FrameworkElement BuildProfileList()
    {
        var panel = Panel();
        var stack = new Grid { RowSpacing = 10 };
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.Children.Add(Text("REGISTERED ACCESS", 11, Cyan(), 115));
        _profiles.ItemTemplate = ProfileTemplate();
        Grid.SetRow(_profiles, 1); stack.Children.Add(_profiles);
        var buttons = new Grid { ColumnSpacing = 8 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var add = ActionButton("＋ NEW"); add.Click += (_, _) => NewProfile();
        var refresh = ActionButton("REFRESH"); refresh.Click += async (_, _) => await RefreshAsync();
        buttons.Children.Add(add); Grid.SetColumn(refresh, 1); buttons.Children.Add(refresh);
        Grid.SetRow(buttons, 2); stack.Children.Add(buttons);
        panel.Child = stack;
        return panel;
    }

    private FrameworkElement BuildEditor()
    {
        var panel = Panel();
        var grid = new Grid { RowSpacing = 12 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(Text("CAPABILITY CONTRACT", 11, Cyan(), 115));
        var scroll = new ScrollViewer();
        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(Label("NAME")); form.Children.Add(_name);
        form.Children.Add(TwoColumns(Labeled("TYPE", _kind), Labeled("PERMISSION", _permission)));
        form.Children.Add(_databaseFields);
        BuildDatabaseFields();
        form.Children.Add(Label("ALLOWED SCOPE")); form.Children.Add(_scopes);
        form.Children.Add(Label("CAPABILITY TAGS // ROUTING INDEX")); form.Children.Add(_tags);
        form.Children.Add(Label("SECOND-BRAIN DICTIONARY // PURPOSE")); form.Children.Add(_dictionary);
        form.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Children = { _enabled, _remote } });
        form.Children.Add(new Border
        {
            Padding = new Thickness(13), Background = Brush(180, 5, 26, 34), BorderBrush = Brush(95, 0, 229, 255),
            BorderThickness = new Thickness(1), Child = Text(
                "SECRETS ARE STORED IN WINDOWS CREDENTIAL MANAGER. THEY ARE NEVER SAVED IN NEXUS.DB, SENT TO THE ONLINE SERVER, OR SHOWN TO THE AI.",
                9, Muted(), 45, true)
        });
        form.Children.Add(Label("STATUS")); _status.Foreground = Muted(); form.Children.Add(_status);
        form.Children.Add(Label("RECENT CAPABILITY AUDIT"));
        _audit.Height = 145; form.Children.Add(_audit);
        scroll.Content = form; Grid.SetRow(scroll, 1); grid.Children.Add(scroll);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, HorizontalAlignment = HorizontalAlignment.Right };
        var test = ActionButton("TEST ACCESS"); test.Click += async (_, _) => await TestAsync();
        var save = ActionButton("SAVE TO VAULT"); save.Click += async (_, _) => await SaveAsync();
        var delete = ActionButton("DELETE"); delete.Click += async (_, _) => await DeleteAsync();
        actions.Children.Add(test); actions.Children.Add(save); actions.Children.Add(delete);
        Grid.SetRow(actions, 2); grid.Children.Add(actions);
        panel.Child = grid;
        return panel;
    }

    private void BuildDatabaseFields()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Label("DATABASE PROVIDER")); stack.Children.Add(_provider);
        stack.Children.Add(Label("ENDPOINT / DATA SOURCE")); stack.Children.Add(_endpoint);
        stack.Children.Add(Label("RESOURCE / DATABASE")); stack.Children.Add(_resource);
        stack.Children.Add(Label("USERNAME")); stack.Children.Add(_username);
        stack.Children.Add(Label("PASSWORD / TOKEN")); stack.Children.Add(_secret);
        _databaseFields.Children.Add(stack);
    }

    private async Task RefreshAsync()
    {
        var selectedId = _selected?.Id;
        var profiles = await App.Services.AccessVault.GetProfilesAsync();
        _profiles.ItemsSource = profiles;
        _profiles.SelectedItem = profiles.FirstOrDefault(profile => profile.Id == selectedId) ?? profiles.FirstOrDefault();
        var audit = await App.Services.Repository.GetRecentCapabilityAuditAsync(50);
        _audit.ItemsSource = audit.Select(entry =>
            $"{entry.Timestamp.ToLocalTime():MM-dd HH:mm:ss}  {entry.Tool}  {entry.Status.ToUpperInvariant()}  {entry.Detail}").ToArray();
        if (profiles.Count == 0) NewProfile();
    }

    private void NewProfile()
    {
        _profiles.SelectedItem = null; _selected = null;
        _name.Text = string.Empty; _kind.SelectedItem = CapabilityKind.Database.ToString();
        _permission.SelectedItem = CapabilityPermission.AutomaticRead.ToString();
        _provider.SelectedItem = DatabaseProvider.Oracle.ToString();
        _endpoint.Text = _resource.Text = _username.Text = _secret.Password = _scopes.Text = _tags.Text = _dictionary.Text = string.Empty;
        _enabled.IsOn = true; _remote.IsOn = false;
        _status.Text = "NEW PROFILE // enter access details, test, then save";
        UpdateFieldVisibility();
    }

    private void LoadSelected()
    {
        if (_profiles.SelectedItem is not CapabilityProfile profile) return;
        _selected = profile;
        _name.Text = profile.Name; _kind.SelectedItem = profile.Kind.ToString();
        _permission.SelectedItem = profile.Permission.ToString();
        _provider.SelectedItem = profile.DatabaseProvider?.ToString() ?? DatabaseProvider.Oracle.ToString();
        _endpoint.Text = profile.Endpoint ?? string.Empty; _resource.Text = profile.Resource ?? string.Empty;
        _username.Text = profile.Username ?? string.Empty; _secret.Password = string.Empty;
        _scopes.Text = profile.AllowedScopes ?? string.Empty; _enabled.IsOn = profile.IsEnabled;
        _tags.Text = string.Join(", ", DagnueCapabilityDictionary.GetProfileTags(profile));
        _dictionary.Text = DagnueCapabilityDictionary.DescribeProfile(profile);
        _remote.IsOn = profile.AllowRemoteTasks;
        _status.Text = profile.LastTestStatus ?? "NOT TESTED";
        UpdateFieldVisibility();
    }

    private CapabilityProfile BuildProfile()
    {
        Enum.TryParse<CapabilityKind>(_kind.SelectedItem?.ToString(), out var kind);
        Enum.TryParse<CapabilityPermission>(_permission.SelectedItem?.ToString(), out var permission);
        Enum.TryParse<DatabaseProvider>(_provider.SelectedItem?.ToString(), out var provider);
        var id = _selected?.Id ?? Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        return new CapabilityProfile(id, _name.Text.Trim(), kind, _enabled.IsOn, _remote.IsOn, permission,
            Null(_endpoint.Text), Null(_resource.Text), Null(_username.Text), kind == CapabilityKind.Database ? provider : null,
            Null(_scopes.Text), _selected?.SecretTarget ?? $"DAGNUE/capability/{id}", _selected?.CreatedAt ?? now, now,
            _selected?.LastTestedAt, _selected?.LastTestStatus,
            DagnueCapabilityDictionary.NormalizeTags((_tags.Text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)),
            Null(_dictionary.Text), _selected?.Aliases);
    }

    private async Task SaveAsync()
    {
        try
        {
            var profile = BuildProfile();
            await App.Services.AccessVault.SaveAsync(profile, Null(_secret.Password));
            _selected = profile; _secret.Password = string.Empty;
            _status.Text = "SAVED // protected access profile is ready";
            await RefreshAsync();
        }
        catch (Exception ex) { _status.Text = "SAVE FAILED // " + ex.Message; }
    }

    private async Task TestAsync()
    {
        try
        {
            _status.Text = "TESTING SECURE LINK...";
            var profile = BuildProfile();
            var result = profile.Kind == CapabilityKind.DesktopCapture
                ? "Desktop captured to " + await App.Services.DesktopCapture.CaptureAsync(
                    Path.Combine(App.Services.DataDirectory, "capability-tests"))
                : await App.Services.AccessVault.TestAsync(profile, Null(_secret.Password));
            profile = profile with { LastTestedAt = DateTimeOffset.UtcNow, LastTestStatus = result, UpdatedAt = DateTimeOffset.UtcNow };
            _status.Text = "VERIFIED // " + result;
            if (_selected is not null)
            {
                await App.Services.AccessVault.SaveAsync(profile, Null(_secret.Password));
                _selected = profile; _secret.Password = string.Empty;
                await RefreshAsync();
            }
        }
        catch (Exception ex) { _status.Text = "TEST FAILED // " + ex.Message; }
    }

    private async Task DeleteAsync()
    {
        if (_selected is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = (Content as FrameworkElement)?.XamlRoot,
            Title = "Delete protected capability?",
            Content = $"This removes {_selected.Name} and its stored secret from Windows Credential Manager.",
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await App.Services.AccessVault.DeleteAsync(_selected);
        _selected = null; await RefreshAsync();
    }

    private void UpdateFieldVisibility() => _databaseFields.Visibility =
        _kind.SelectedItem?.ToString() == CapabilityKind.Database.ToString() ? Visibility.Visible : Visibility.Visible;

    private static DataTemplate ProfileTemplate() => (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Border Margin="0,0,0,7" Padding="11" Background="#B8071921" BorderBrush="#5500E5FF" BorderThickness="1">
            <StackPanel Spacing="3">
              <TextBlock Text="{Binding Name}" Foreground="#E9F9FC" FontSize="12" TextWrapping="Wrap"/>
              <TextBlock Text="{Binding Kind}" Foreground="#7600E5FF" FontSize="9"/>
            </StackPanel>
          </Border>
        </DataTemplate>
        """);

    private static Border Panel() => new()
    {
        Padding = new Thickness(16), Background = Brush(225, 3, 14, 20),
        BorderBrush = Brush(100, 0, 229, 255), BorderThickness = new Thickness(1)
    };
    private static TextBox Field(string placeholder) => new() { PlaceholderText = placeholder, MinHeight = 40 };
    private static ComboBox EnumBox<T>() where T : struct, Enum => new()
    {
        ItemsSource = Enum.GetNames<T>(), SelectedIndex = 0, MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private static TextBlock Label(string value) => Text(value, 9, Muted(), 100);
    private static FrameworkElement Labeled(string label, FrameworkElement control)
    {
        var stack = new StackPanel { Spacing = 5 }; stack.Children.Add(Label(label)); stack.Children.Add(control); return stack;
    }
    private static Grid TwoColumns(FrameworkElement first, FrameworkElement second)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(first); Grid.SetColumn(second, 1); grid.Children.Add(second); return grid;
    }
    private static Button ActionButton(string text) => new() { Content = text, MinHeight = 40 };
    private static TextBlock Text(string value, double size, Brush foreground, int spacing = 0, bool wrap = false) => new()
    {
        Text = value, FontSize = size, Foreground = foreground, CharacterSpacing = spacing,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
    };
    private static SolidColorBrush Cyan() => Brush(255, 0, 229, 255);
    private static SolidColorBrush Muted() => Brush(255, 117, 146, 158);
    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));
    private static string? Null(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
