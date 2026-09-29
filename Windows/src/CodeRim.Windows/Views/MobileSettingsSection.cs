using System.Windows;
using System.Windows.Controls;
using CodeRim.Windows.Services;
using TextBox = System.Windows.Controls.TextBox;

namespace CodeRim.Windows.Views;

internal sealed class MobileSettingsSection : StackPanel
{
    internal MobileSettingsSection(MobileConnectionStore connection)
    {
        var state = Ui.Text(connection.Status, 13, "#A6A6AA");
        var endpoint = new TextBox { Width = 260 };
        var name = new TextBox { Width = 200, Text = "Windows PC", MaxLength = 24 };
        var code = new TextBox { Width = 160, MaxLength = 10 };
        var inputs = SettingsUi.Section("iPhone · Dynamic Island", SettingsUi.Row("Device name", name),
            SettingsUi.Row("HTTPS relay server", endpoint), SettingsUi.Row("Connection code", code),
            SettingsUi.Action("Connect iPhone", async () => { await connection.PairAsync(endpoint.Text, code.Text, name.Text).ConfigureAwait(true); if (connection.Connected) code.Clear(); }));
        var synchronizing = false;
        var share = Ui.Toggle("Share task titles", connection.ShareTitles, async value => {
            if (!synchronizing) await connection.SetShareTitlesAsync(value).ConfigureAwait(true);
        });
        share.Margin = new Thickness(14, 6, 14, 6);
        var connected = SettingsUi.Section("iPhone · Dynamic Island", SettingsUi.Row("Status", state),
            share,
            SettingsUi.Action("Disconnect this PC", async () => await connection.DisconnectAsync().ConfigureAwait(true)));
        Children.Add(inputs); Children.Add(connected);
        var message = Ui.Text(connection.Status, 12, "#A6A6AA"); message.Margin = new Thickness(32, 6, 32, 0); Children.Add(message);
        Children.Add(SettingsUi.Note("Create a connection code in iPhone Settings. Internet access is enough; the devices do not need the same Wi-Fi."));
        void Refresh() { synchronizing = true; share.IsChecked = connection.ShareTitles; synchronizing = false; inputs.Visibility = connection.Connected ? Visibility.Collapsed : Visibility.Visible; connected.Visibility = connection.Connected ? Visibility.Visible : Visibility.Collapsed; IsEnabled = !connection.Busy; state.Text = connection.Status; message.Text = connection.Status; }
        System.ComponentModel.PropertyChangedEventHandler handler = (_, _) => Refresh();
        Loaded += (_, _) => { connection.PropertyChanged += handler; Refresh(); };
        Unloaded += (_, _) => connection.PropertyChanged -= handler;
        Refresh();
    }
}
