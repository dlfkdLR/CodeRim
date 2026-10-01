using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CodeRim.Windows.Services;
using QRCoder;
using Image = System.Windows.Controls.Image;
using TextBox = System.Windows.Controls.TextBox;

namespace CodeRim.Windows.Views;

internal sealed class MobileSettingsSection : StackPanel
{
    internal MobileSettingsSection(MobileConnectionStore connection)
    {
        var state = Ui.Text(connection.Status, 13, "#A6A6AA");
        var name = new TextBox { Width = 200, Text = "Windows PC", MaxLength = 24 };
        var relay = new TextBox { Width = 260, Text = connection.RelayAddress };
        relay.LostFocus += (_, _) => { if (relay.Text.Trim() != connection.RelayAddress) connection.RelayAddress = relay.Text; };
        var relayRow = SettingsUi.Row("Relay server", relay);
        var inputs = SettingsUi.Section("iPhone · Dynamic Island", SettingsUi.Row("Device name", name), relayRow,
            SettingsUi.Action("Connect iPhone", async () => { connection.RelayAddress = relay.Text; await connection.StartPairingAsync(name.Text).ConfigureAwait(true); }));
        var code = new Image { Width = 184, Height = 184, Margin = new Thickness(14) };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(code, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
        System.Windows.Automation.AutomationProperties.SetName(code, "Pairing QR code");
        var countdown = Ui.Text("", 12, "#A6A6AA");
        var scan = new StackPanel { Margin = new Thickness(0, 14, 14, 14), VerticalAlignment = VerticalAlignment.Center };
        scan.Children.Add(Ui.Text("Scan with the CodeRim iPhone app", 14, weight: FontWeights.SemiBold));
        scan.Children.Add(Ui.Text("The code works once and expires in five minutes.", 12, "#A6A6AA"));
        scan.Children.Add(countdown);
        var offer = new DockPanel(); offer.Children.Add(code); offer.Children.Add(scan);
        var pairing = SettingsUi.Section("iPhone · Dynamic Island", offer, SettingsUi.Action("Cancel", connection.CancelPairing));
        var synchronizing = false;
        var share = Ui.Toggle("Share task titles", connection.ShareTitles, async value => {
            if (!synchronizing) await connection.SetShareTitlesAsync(value).ConfigureAwait(true);
        });
        share.Margin = new Thickness(14, 6, 14, 6);
        var connected = SettingsUi.Section("iPhone · Dynamic Island", SettingsUi.Row("Status", state),
            share,
            SettingsUi.Action("Disconnect this PC", async () => await connection.DisconnectAsync().ConfigureAwait(true)));
        Children.Add(inputs); Children.Add(pairing); Children.Add(connected);
        var message = Ui.Text(connection.Status, 12, "#A6A6AA"); message.Margin = new Thickness(32, 6, 32, 0); Children.Add(message);
        var notice = Ui.Text("", 12, "#FF9F0A"); notice.Margin = new Thickness(32, 6, 32, 0); Children.Add(notice);
        Children.Add(SettingsUi.Note("Open the CodeRim iPhone app and scan the QR code. Internet access is enough; the devices do not need the same Wi-Fi. The Island appears on its own while a task is running."));
        string? shown = null;
        var clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => countdown.Text = connection.PairingExpiresAt is { } expiry && expiry > DateTimeOffset.UtcNow
            ? "Expires in " + (expiry - DateTimeOffset.UtcNow).ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture) : "";
        void Refresh()
        {
            synchronizing = true; share.IsChecked = connection.ShareTitles; synchronizing = false;
            var offering = connection.PairingLink is not null;
            inputs.Visibility = connection.Connected || offering ? Visibility.Collapsed : Visibility.Visible;
            pairing.Visibility = offering ? Visibility.Visible : Visibility.Collapsed;
            connected.Visibility = connection.Connected ? Visibility.Visible : Visibility.Collapsed;
            relayRow.Visibility = string.IsNullOrEmpty(MobileConnectionStore.DefaultRelay) || connection.RelayAddress != MobileConnectionStore.DefaultRelay ? Visibility.Visible : Visibility.Collapsed;
            if (connection.PairingLink != shown) { shown = connection.PairingLink; code.Source = shown is null ? null : QRCode(shown); }
            if (offering) clock.Start(); else clock.Stop();
            IsEnabled = !connection.Busy; state.Text = connection.Status; message.Text = connection.Status;
            notice.Text = connection.ServerNotice ?? ""; notice.Visibility = connection.ServerNotice is null ? Visibility.Collapsed : Visibility.Visible;
        }
        System.ComponentModel.PropertyChangedEventHandler handler = (_, _) => Refresh();
        Loaded += (_, _) => { connection.PropertyChanged += handler; Refresh(); };
        Unloaded += (_, _) => { connection.PropertyChanged -= handler; clock.Stop(); };
        Refresh();
    }
    private static BitmapImage QRCode(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = new MemoryStream(png); image.EndInit(); image.Freeze();
        return image;
    }
}
