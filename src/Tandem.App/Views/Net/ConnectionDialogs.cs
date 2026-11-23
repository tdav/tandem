using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Tandem.App.Resources;
using Tandem.App.ViewModels.Net;
using Tandem.App.Views.Dialogs;
using Tandem.Core.Viewing;
using Tandem.Net;
using Tandem.Net.Connections;

namespace Tandem.App.Views.Net;

public sealed class ConnectionListDialog : DialogWindow
{
    public ConnectionListDialog(ConnectionListViewModel model) : base(Loc.Get(model.IsSshOnly
        ? "Connection_SshListTitle" : "Connection_ListTitle"))
    {
        DataContext = model;
        var list = new ListBox
        {
            Name = "Connections",
            MinHeight = 180,
            MaxHeight = 360,
            ItemTemplate = new FuncDataTemplate<ConnectionProfile>((profile, _) =>
            {
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(new TextBlock { Text = profile?.Name });
                row.Children.Add(new TextBlock { Text = profile?.Address, Classes = { "secondary" } });
                return row;
            }),
        };
        list.Bind(ItemsControl.ItemsSourceProperty,
            DialogBinding<ConnectionListViewModel>.Property(nameof(model.Profiles), m => m.Profiles));
        list.Bind(ListBox.SelectedItemProperty,
            DialogBinding<ConnectionListViewModel>.Property(nameof(model.Selected), m => m.Selected, (m, value) => m.Selected = value));
        model.Selected = model.Profiles.FirstOrDefault();
        var connect = MakeButton(Loc.Get("Connection_Connect"), isDefault: true, name: "Connect");
        var add = MakeButton(Loc.Get("Connection_New"), name: "Create");
        var edit = MakeButton(Loc.Get("Connection_Edit"), name: "Edit");
        var delete = MakeButton(Loc.Get("Button_Delete"), name: "Delete");
        var vaultAction = MakeButton("", name: "VaultAction");
        vaultAction.HorizontalAlignment = HorizontalAlignment.Left;
        var vaultStatus = Text(null, secondary: true);
        var close = MakeButton(Loc.Get("Connection_Close"), isCancel: true, name: "Close");
        var error = ErrorText("Error");

        connect.Click += (_, _) => Connect();
        list.DoubleTapped += (_, _) => Connect();
        add.Click += async (_, _) => await model.EditAsync(create: true, this);
        edit.Click += async (_, _) => await model.EditAsync(create: false, this);
        delete.Click += async (_, _) => await model.DeleteAsync(this);
        vaultAction.Click += async (_, _) => await model.OpenVaultAsync(this);
        close.Click += (_, _) => Close();
        model.PropertyChanged += (_, _) => Refresh();
        Refresh();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(add);
        actions.Children.Add(edit);
        actions.Children.Add(delete);
        SetContent(list, [list, actions, vaultStatus, vaultAction, error], connect, close);

        void Connect()
        {
            if (connect.IsEnabled && model.Selected is { } item) Close(item.Id);
        }

        void Refresh()
        {
            add.IsEnabled = model.CanManageProfiles && !model.IsBusy;
            connect.IsEnabled = edit.IsEnabled = delete.IsEnabled = add.IsEnabled && model.Selected is not null;
            var action = model.VaultAction;
            vaultAction.Content = action;
            vaultAction.IsVisible = action is not null;
            vaultAction.IsEnabled = !model.IsBusy;
            vaultStatus.Text = model.VaultStatus;
            vaultStatus.IsVisible = vaultStatus.Text is not null;
            ShowError(error, model.Error);
        }
    }
}

public sealed class ConnectionEditDialog : DialogWindow
{
    public ConnectionEditDialog(ConnectionEditViewModel model) : base(Loc.Get("Connection_EditTitle"))
    {
        DataContext = model;
        var fields = new StackPanel { Spacing = 7 };
        var name = TextBoxFor(nameof(model.Name), m => m.Name, (m, value) => m.Name = value);
        var host = TextBoxFor(nameof(model.Host), m => m.Host, (m, value) => m.Host = value);
        var port = TextBoxFor(nameof(model.Port), m => m.Port, (m, value) => m.Port = value);
        var user = TextBoxFor(nameof(model.User), m => m.User, (m, value) => m.User = value);
        var secret = TextBoxFor(nameof(model.Secret), m => m.Secret, (m, value) => m.Secret = value);
        secret.PasswordChar = '●';
        var keyPath = TextBoxFor(nameof(model.PrivateKeyPath), m => m.PrivateKeyPath, (m, value) => m.PrivateKeyPath = value);

        var protocol = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Name = "Protocol" };
        protocol.ItemsSource = model.AvailableProtocols.Select(Item).ToArray();
        protocol.IsEnabled = model.AvailableProtocols.Count > 1;
        Select(protocol, model.Protocol);
        var authentication = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Name = "Auth" };
        var encoding = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Name = "NameEncoding" };
        encoding.ItemsSource = TextEncodings.All
            .Where(info => info.Id is not ("utf-16le" or "utf-16be"))
            .Select(info => new ComboBoxItem { Content = new TextBlock { Text = info.Id }, Tag = info.Id }).ToArray();
        Select(encoding, model.NameEncoding);
        encoding.SelectionChanged += (_, _) =>
        {
            if (encoding.SelectedItem is ComboBoxItem { Tag: string encodingId }) model.NameEncoding = encodingId;
        };

        var remember = new CheckBox { Content = Loc.Get("Connection_Remember"), Name = "Remember" };
        remember.Bind(CheckBox.IsCheckedProperty,
            DialogBinding<ConnectionEditViewModel>.Property(nameof(model.Remember), m => m.Remember, (m, value) => m.Remember = value));
        var passive = new CheckBox { Content = Loc.Get("Connection_Passive"), Name = "Passive" };
        passive.Bind(CheckBox.IsCheckedProperty,
            DialogBinding<ConnectionEditViewModel>.Property(nameof(model.PassiveMode), m => m.PassiveMode, (m, value) => m.PassiveMode = value));
        var pick = MakeButton(Loc.Get("Connection_Browse"), name: "ChooseKey");
        pick.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Loc.Get("Connection_Key"), AllowMultiple = false,
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) model.PrivateKeyPath = path;
        };
        var keyField = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(pick, Dock.Right);
        pick.Margin = new Thickness(6, 0, 0, 0);
        keyField.Children.Add(pick);
        keyField.Children.Add(keyPath);

        fields.Children.Add(Field("Connection_Name", name));
        fields.Children.Add(Field("Connection_Protocol", protocol));
        fields.Children.Add(Field("Connection_Host", host));
        fields.Children.Add(Field("Connection_Port", port));
        var userField = Field("Connection_User", user);
        fields.Children.Add(userField);
        fields.Children.Add(Field("Connection_Auth", authentication));
        var privateKey = Field("Connection_Key", keyField);
        if (model.HasStoredKey) privateKey.Children.Add(Text(Loc.Get("Vault_StoredKey"), secondary: true));
        fields.Children.Add(privateKey);
        var secretLabel = Text(null);
        var secretField = new StackPanel { Spacing = 4, Children = { secretLabel, secret, remember } };
        fields.Children.Add(secretField);
        var plainWarning = Text(Loc.Get("Connection_PlainFtpWarning"), name: "PlainFtpWarning");
        fields.Children.Add(plainWarning);
        var initialPath = Field("Connection_InitialPath", TextBoxFor(nameof(model.InitialPath), m => m.InitialPath, (m, value) => m.InitialPath = value));
        fields.Children.Add(initialPath);
        fields.Children.Add(passive);
        var encodingField = Field("Connection_FtpEncoding", encoding);
        fields.Children.Add(encodingField);
        var errorText = ErrorText("Error");
        fields.Children.Add(errorText);
        var scrolling = new ScrollViewer { MaxHeight = 540, Content = fields };

        var refreshingAuth = false;
        protocol.SelectionChanged += (_, _) =>
        {
            if (protocol.SelectedItem is ComboBoxItem { Tag: NetProtocol value })
            {
                model.Protocol = value;
                RefreshAuthentication();
            }
        };
        authentication.SelectionChanged += (_, _) =>
        {
            if (!refreshingAuth && authentication.SelectedItem is ComboBoxItem { Tag: NetAuth value })
            {
                model.Auth = value;
                RefreshFields();
            }
        };
        RefreshAuthentication();

        var save = MakeButton(Loc.Get("Button_Save"), isDefault: true, name: "Save");
        save.Click += (_, _) =>
        {
            if (model.Build(out var validation) is { } result) Close(result);
            else ShowError(errorText, validation);
        };
        var cancel = CancelButton();
        cancel.Click += (_, _) => Close();
        Closed += (_, _) => model.Secret = "";
        SetContent(name, [scrolling], save, cancel);

        void RefreshAuthentication()
        {
            refreshingAuth = true;
            authentication.ItemsSource = model.AuthenticationMethods.Select(Item).ToArray();
            Select(authentication, model.Auth);
            refreshingAuth = false;
            RefreshFields();
        }

        void RefreshFields()
        {
            var key = model.Auth == NetAuth.PrivateKey;
            privateKey.IsVisible = key;
            secretField.IsVisible = model.Auth != NetAuth.Anonymous;
            userField.IsVisible = secretField.IsVisible;
            secretLabel.Text = Loc.Get(key ? "Connection_Passphrase" : "Connection_Password");
            plainWarning.IsVisible = model.Protocol == NetProtocol.Ftp;
            initialPath.IsVisible = model.Protocol != NetProtocol.Ssh;
            passive.IsVisible = encodingField.IsVisible = model.IsFtp;
        }
    }

    private static TextBox TextBoxFor(string property, Func<ConnectionEditViewModel, string> get,
        Action<ConnectionEditViewModel, string> set)
    {
        var field = new TextBox { Name = property };
        field.Bind(TextBox.TextProperty, DialogBinding<ConnectionEditViewModel>.Property(property, get, set));
        return field;
    }

    private static StackPanel Field(string label, Control field) =>
        new() { Spacing = 4, Children = { Text(Loc.Get(label)), field } };

    private static ComboBoxItem Item<T>(T value) where T : struct, Enum =>
        new() { Content = new TextBlock { Text = Loc.Enum(value) }, Tag = value };

    private static void Select<T>(ComboBox box, T value) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value));
}

internal sealed class ConnectionVaultDialog : DialogWindow
{
    public ConnectionVaultDialog(ConnectionVaultViewModel model)
        : base(Loc.Get(model.IsCreating ? "Vault_CreateTitle" : "Vault_UnlockTitle"))
    {
        var passwordLabel = Loc.Get(model.IsCreating ? "Vault_NewPassword" : "Vault_MasterPassword");
        var confirmLabel = Loc.Get("Vault_ConfirmPassword");
        var password = PasswordBox("MasterPassword", passwordLabel);
        var confirm = PasswordBox("MasterPasswordConfirmation", confirmLabel);
        var body = new List<Control>
        {
            Text(Loc.Format(model.IsCreating ? "Vault_SetupPath" : "Vault_LockedPath", model.Path), secondary: true),
            Text(passwordLabel),
            password,
        };
        if (model.IsCreating)
        {
            body.Add(Text(confirmLabel));
            body.Add(confirm);
        }
        var error = ErrorText("Error");
        body.Add(error);
        var submit = MakeButton(Loc.Get(model.IsCreating ? "Vault_Create" : "Vault_Unlock"), isDefault: true,
            name: "Confirm");
        var cancel = CancelButton();
        CancellationTokenSource? operation = null;
        model.PropertyChanged += (_, _) => ShowError(error, model.Error);

        submit.Click += async (_, _) =>
        {
            if (operation is not null || !model.Validate(password.Text ?? "", confirm.Text ?? "")) return;
            using var cancellation = new CancellationTokenSource();
            operation = cancellation;
            password.IsEnabled = confirm.IsEnabled = submit.IsEnabled = false;
            var submission = model.SubmitAsync(password.Text ?? "", cancellation.Token);
            ClearPasswords();
            bool unlocked;
            try { unlocked = await submission; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { unlocked = false; }
            finally
            {
                operation = null;
                password.IsEnabled = confirm.IsEnabled = submit.IsEnabled = true;
            }
            if (unlocked || cancellation.IsCancellationRequested) Close(unlocked);
            else password.Focus();
        };
        cancel.Click += (_, _) => Close(false);
        Closing += (_, e) =>
        {
            if (operation is null) return;
            e.Cancel = true;
            operation.Cancel();
        };
        Closed += (_, _) => ClearPasswords();
        SetContent(password, body, submit, cancel);

        void ClearPasswords()
        {
            password.Text = null;
            confirm.Text = null;
        }
    }

    private static TextBox PasswordBox(string name, string label)
    {
        var box = new TextBox { PasswordChar = '●', IsUndoEnabled = false, Name = name };
        AutomationProperties.SetName(box, label);
        box.CopyingToClipboard += (_, e) => e.Handled = true;
        box.CuttingToClipboard += (_, e) => e.Handled = true;
        return box;
    }
}

internal sealed class ConnectionSecretDialog : DialogWindow
{
    public ConnectionSecretDialog(string name, string address, SecretKind kind, bool previousWasWrong)
        : base(Loc.Format("Connection_SecretTitle", name))
    {
        var secret = new TextBox { PasswordChar = '●', Name = "Secret" };
        var remember = new CheckBox { Content = Loc.Get("Connection_Remember"), Name = "Remember" };
        var body = new List<Control> { Text(address, secondary: true) };
        if (previousWasWrong) body.Add(Text(Loc.Get("Connection_SecretRefused")));
        body.Add(Text(Loc.Get(kind == SecretKind.Passphrase ? "Connection_Passphrase" : "Connection_Password")));
        body.Add(secret);
        body.Add(remember);
        var accept = MakeButton(Loc.Get("Connection_Connect"), isDefault: true, name: "Confirm");
        accept.Click += (_, _) => Close(new SecretAnswer(secret.Text ?? "", remember.IsChecked == true));
        var cancel = CancelButton();
        cancel.Click += (_, _) => Close();
        Closed += (_, _) => secret.Text = null;
        SetContent(secret, body, accept, cancel);
    }
}

internal sealed class ConnectionQuestionDialog : DialogWindow
{
    public ConnectionQuestionDialog(string title, string detail, string confirmLabel) : base(title)
    {
        var accept = MakeButton(confirmLabel, name: "Confirm");
        accept.Click += (_, _) => Close(true);
        var cancel = CancelButton(isDefault: true);
        cancel.Click += (_, _) => Close(false);
        SetContent(cancel, [Text(detail)], accept, cancel);
    }
}
