using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task UiErrorRecoveryRegression(NotchWindow notch, AppSettingsStore settings, CredentialVault vault, string directory)
    {
        Require(Path.GetFileName(CompanionFile.DataDirectory).StartsWith("CodeRim-Smoke-", StringComparison.Ordinal), "Error recovery QA requires isolated settings");
        Require(SynchronizationContext.Current is DispatcherSynchronizationContext, "Relay timer QA requires the WPF dispatcher context");
        var failures = new List<Exception>();
        void Unhandled(object sender, DispatcherUnhandledExceptionEventArgs args) { failures.Add(args.Exception); args.Handled = true; }
        var dispatcher = Dispatcher.CurrentDispatcher; dispatcher.UnhandledException += Unhandled;
        var before = settings.Current;
        var temporary = Path.Combine(CompanionFile.DataDirectory, "settings.json.new"); var blocked = false;
        var checks = new List<string>(); Exception? failure = null; var cleanup = new List<Exception>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        static FieldInfo Field(Type type, string name) => type.GetField(name, flags) ?? throw new InvalidOperationException("Missing fixture field: " + name);
        using var handler = new RecoveryRelayHandler();
        using var mobile = new MobileConnectionStore(vault, _ => new MobileSnapshot(1, 0, [], []), enabled: false);
        var saved = new MobileCredential("https://relay.example.com/", new string('a', 32), DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
        try
        {
            vault.Save("iphone-relay", JsonSerializer.Serialize(saved));
            Field(typeof(MobileConnectionStore), "credential").SetValue(mobile, saved);
            Field(typeof(MobileConnectionStore), "client").SetValue(mobile, new MobileRelayClient(saved.Endpoint, handler));
            var tick = typeof(MobileConnectionStore).GetMethod("Tick", flags) ?? throw new InvalidOperationException("Missing sharing timer handler");
            var changes = 0; mobile.PropertyChanged += (_, _) => changes++;
            tick.Invoke(mobile, [null, EventArgs.Empty]);
            await MotionUntil(() => changes > 0, "Null relay reply did not complete the timer handler"); await Idle();
            Require(failures.Count == 0 && mobile.Connected && mobile.Status == "Offline · retrying"
                && Field(typeof(MobileConnectionStore), "sending").GetValue(mobile) is false
                && Field(typeof(MobileConnectionStore), "lastSent").GetValue(mobile) is null
                && vault.Load("iphone-relay") == JsonSerializer.Serialize(saved), "Null relay reply escaped the dispatcher or discarded retry state");
            var previousChanges = changes;
            tick.Invoke(mobile, [null, EventArgs.Empty]);
            await MotionUntil(() => changes > previousChanges, "Valid relay reply did not complete the next timer handler"); await Idle();
            Require(failures.Count == 0 && handler.Calls == 2 && mobile.Connected && mobile.Status == "Sharing with iPhone"
                && Field(typeof(MobileConnectionStore), "sending").GetValue(mobile) is false
                && Field(typeof(MobileConnectionStore), "lastSent").GetValue(mobile) is MobileSnapshot,
                "The sharing timer did not recover after the null reply");
            checks.Add("Actual async-void timer retains credentials and clears sending after JSON null, then publishes the next valid reply without an unhandled dispatcher exception");

            settings.Save(before with { Edge = NotchEdge.Right, Offset = 80, Visibility = NotchVisibility.AlwaysShow, ReduceMotion = true, EnabledProviders = ["codex"] }); await Idle();
            var persisted = File.ReadAllText(Path.Combine(CompanionFile.DataDirectory, "settings.json"));
            var handle = new WindowInteropHelper(notch).Handle;
            Require(WindowBounds(handle, out var initial), "Notch fixture has no HWND bounds");
            Require(!Path.Exists(temporary), "Settings failure fixture path is occupied");
            Directory.CreateDirectory(temporary); blocked = true;
            var pointer = System.Windows.Forms.Control.MousePosition;
            Field(typeof(NotchWindow), "dragging").SetValue(notch, true);
            Field(typeof(NotchWindow), "dragOffset").SetValue(notch, settings.Current.Offset);
            Field(typeof(NotchWindow), "dragStart").SetValue(notch, new Point(pointer.X, pointer.Y + 48));
            notch.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
            Require(WindowBounds(handle, out var moved) && !moved.Equals(initial), "Drag failure fixture did not temporarily move the notch");
            notch.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            await Idle(); CheckSavedNotch("Alt-drag release");
            Require(Field(typeof(NotchWindow), "dragging").GetValue(notch) is false && !notch.IsMouseCaptured, "Failed drag retained mouse capture or dragging state");
            foreach (var label in new[] { "Recentre", "Hide notch" })
            {
                var menu = ((FrameworkElement)notch.Content).ContextMenu;
                menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, label)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Idle(); CheckSavedNotch(label);
            }
            checks.Add("Routed drag release and Recentre/Hide menu actions survive a blocked staged settings path, retaining current/persisted offset and visibility and restoring HWND position");
            Directory.Delete(temporary); blocked = false;
            ((FrameworkElement)notch.Content).ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Recentre")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Idle();
            Require(settings.Current.Offset == 0 && new AppSettingsStore().Current.Offset == 0, "Notch settings could not save after the persistence failure cleared");
            checks.Add("Recentre saves successfully after the blocked path is removed");
            File.WriteAllText(Path.Combine(directory, "windows-ui-error-recovery.json"), JsonSerializer.Serialize(new { completed = true, physicalInput = false,
                balloonDeliveryVerified = false, checks }, JsonOptions));

            void CheckSavedNotch(string action)
            {
                Require(failures.Count == 0 && settings.Current.Offset == 80 && settings.Current.Visibility == NotchVisibility.AlwaysShow && notch.IsVisible
                    && WindowBounds(handle, out var restored) && restored.Equals(initial)
                    && File.ReadAllText(Path.Combine(CompanionFile.DataDirectory, "settings.json")) == persisted,
                    action + " escaped the dispatcher, moved the saved notch or changed persisted settings");
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            void Restore(Action action) { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); } }
            Restore(() => { if (blocked) Directory.Delete(temporary); });
            Restore(() => settings.Save(before));
            Restore(() => vault.Delete("iphone-relay"));
            dispatcher.UnhandledException -= Unhandled;
        }
        if (cleanup.Count > 0) throw new AggregateException("UI error recovery fixture cleanup failed", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class RecoveryRelayHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(1, cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(++Calls == 1 ? "null" : "{\"ok\":true,\"interval\":300}", System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
