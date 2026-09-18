using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CodeRim.Windows.Tray;

internal sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon notifyIcon;
    private readonly Icon icon;
    private readonly Stream iconStream;

    public TrayIconHost(
        Action toggleWindow,
        Action refresh,
        Action showSettings,
        Action quit)
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/CodeRim.ico"))
            ?? throw new InvalidOperationException("The CodeRim tray icon is missing.");
        iconStream = resource.Stream;
        icon = new Icon(iconStream);
        notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = System.Windows.Forms.Application.ProductName,
            Visible = true,
            ContextMenuStrip = BuildMenu(toggleWindow, refresh, showSettings, quit)
        };
        notifyIcon.ContextMenuStrip.Items.Insert(0, new ToolStripMenuItem("Show notch", null, (_, _) => ShowNotchRequested?.Invoke()));
        notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                toggleWindow();
            }
        };
    }

    public event Action? ShowNotchRequested;
    public void Notify(string title, string message) => notifyIcon.ShowBalloonTip(6000, title, message, ToolTipIcon.Info);

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.ContextMenuStrip?.Dispose();
        notifyIcon.Dispose();
        icon.Dispose();
        iconStream.Dispose();
    }

    private static ContextMenuStrip BuildMenu(
        Action toggleWindow,
        Action refresh,
        Action showSettings,
        Action quit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open CodeRim", null, (_, _) => toggleWindow());
        menu.Items.Add("Refresh", null, (_, _) => refresh());
        menu.Items.Add("Settings", null, (_, _) => showSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());
        return menu;
    }
}
