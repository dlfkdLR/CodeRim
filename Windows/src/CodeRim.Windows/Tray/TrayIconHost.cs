using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodeRim.Windows.Tray;

internal sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon notifyIcon;
    private readonly Font menuFont = new("Segoe UI", 10);
    private readonly Func<bool> notchVisible;
    private readonly Func<bool> canCheckUpdates;
    private readonly ToolStripMenuItem notchItem;
    private readonly ToolStripMenuItem updateItem;
    private Icon? icon;
    private bool disposed;
    internal ContextMenuStrip Menu { get; }

    public TrayIconHost(Action showUsage, Action toggleNotch, Action showSettings, Action checkUpdates,
        Action quit, Func<bool> notchVisible, Func<bool> canCheckUpdates)
    {
        this.notchVisible = notchVisible; this.canCheckUpdates = canCheckUpdates;
        Menu = new TrayContextMenu { Font = menuFont, ShowImageMargin = false, ShowCheckMargin = true,
            Padding = new Padding(5), AccessibleName = "CodeRim", Renderer = new TrayMenuRenderer() };
        Menu.Items.Add(Item("Token Usage…", showUsage, Keys.Control | Keys.U));
        Menu.Items.Add(new ToolStripSeparator());
        notchItem = Item("Show Notch", toggleNotch); Menu.Items.Add(notchItem);
        Menu.Items.Add(Item("Settings…", showSettings, Keys.Control | Keys.Oemcomma, "Ctrl+,"));
        updateItem = Item("Check for Updates…", checkUpdates); Menu.Items.Add(updateItem);
        Menu.Items.Add(new ToolStripSeparator());
        Menu.Items.Add(Item("Quit CodeRim", quit, Keys.Control | Keys.Q));
        Menu.Opening += (_, _) => RefreshState();
        // The left-click path uses the same popup as right click. Foreground activation
        // lets the framework dismiss it on an outside click, even without a dashboard.
        Menu.Opened += (_, _) => SetForegroundWindow(Menu.Handle);
        notifyIcon = new NotifyIcon { Text = "CodeRim", ContextMenuStrip = Menu };
        notifyIcon.MouseClick += (_, args) => { if (args.Button == MouseButtons.Left) ShowMenu(Cursor.Position); };
        RefreshAppearance(); RefreshState(); notifyIcon.Visible = true;
    }

    private ToolStripMenuItem Item(string title, Action action, Keys shortcut = Keys.None, string? display = null)
    {
        var item = new ToolStripMenuItem(title) { AccessibleName = title, ShortcutKeys = shortcut, Padding = new Padding(0, 3, 0, 3) };
        if (display is not null) item.ShortcutKeyDisplayString = display;
        item.Click += (_, _) => { action(); if (!disposed) RefreshState(); };
        return item;
    }
    internal void ShowMenu(Point location)
    {
        if (disposed) return;
        if (Menu.Visible) { Menu.Close(); return; }
        Menu.Show(location);
    }
    internal void RefreshState()
    {
        if (disposed) return;
        notchItem.Checked = notchVisible(); updateItem.Enabled = canCheckUpdates();
    }
    internal void RefreshAppearance()
    {
        if (disposed) return;
        var previous = icon;
        icon = TrayMark.Create(SystemInformation.SmallIconSize.Width, TrayMark.Foreground());
        notifyIcon.Icon = icon; previous?.Dispose(); Menu.Invalidate();
    }
    public void Notify(string title, string message) => notifyIcon.ShowBalloonTip(6000, title, message, ToolTipIcon.Info);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; notifyIcon.Visible = false; notifyIcon.Dispose(); Menu.Dispose(); icon?.Dispose(); menuFont.Dispose();
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
