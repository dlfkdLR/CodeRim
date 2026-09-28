using System.IO;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CodeRim.Core.Domain;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task NotchReflowRegression(NotchWindow notch, DashboardStore store, AppSettingsStore settings, string directory)
    {
        var observations = new List<object>();
        var trace = new NotchReflowTrace(notch);
        void Receipt(bool completed) => File.WriteAllText(Path.Combine(directory, "windows-notch-reflow.json"),
            JsonSerializer.Serialize(new { completed, physicalInput = false, observations, events = trace.Events, eventsDropped = trace.DroppedEvents }, JsonOptions));
        string FocusId() => Keyboard.FocusedElement is DependencyObject focused ? AutomationProperties.GetAutomationId(focused) : "";
        Exception? failure = null; var cleanup = new List<Exception>(); var completed = false;
        try
        {
            Receipt(false);
            settings.Save(settings.Current with { Edge = NotchEdge.Right, Offset = 0, Scale = 1,
                ReduceMotion = true, Visibility = NotchVisibility.AlwaysShow, EnabledProviders = ["codex"] });
            await Idle();
            var provider = Descendants<Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == "notch.provider.codex");
            var activated = notch.Activate(); var focusAccepted = provider.Focus(); await Idle();
            observations.Add(new { stage = "provider-setup", activated, focusAccepted, provider.IsKeyboardFocused,
                provider.IsKeyboardFocusWithin, provider.IsLoaded, provider.IsVisible, notch.IsActive, focusId = FocusId(), notch.PopupIsOpen }); Receipt(false);
            Require(provider.IsKeyboardFocused, "Reflow fixture did not acquire actual provider keyboard focus.");
            Require(notch.PopupIsOpen, "Reflow fixture has no open provider card.");
            var gear = Descendants<Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == "notch.settings");
            void HoverGear() => gear.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            HoverGear(); await Idle();
            observations.Add(new { stage = "focused-provider-control-hover", provider.IsKeyboardFocused, notch.PopupIsOpen }); Receipt(false);
            Require(provider.IsKeyboardFocused && notch.PopupIsOpen, "Control hover dismissed a keyboard-focused provider card.");
            var gearFocusAccepted = gear.Focus(); await Idle();
            observations.Add(new { stage = "control-keyboard-focus", gearFocusAccepted, gear.IsKeyboardFocused, notch.PopupIsOpen }); Receipt(false);
            Require(gear.IsKeyboardFocused && !notch.PopupIsOpen, "Actual control keyboard focus did not dismiss its provider card.");
            Keyboard.ClearFocus(); notch.OpenProvider("codex");
            Require(Keyboard.FocusedElement is null && notch.PopupIsOpen, "Pointer-hover control fixture did not establish an unfocused card.");
            HoverGear(); await Idle();
            observations.Add(new { stage = "unfocused-provider-control-hover", focusId = FocusId(), notch.PopupIsOpen }); Receipt(false);
            Require(!notch.PopupIsOpen, "Control hover failed to dismiss an unfocused provider card.");
            provider.Focus(); await Idle();
            Require(provider.IsKeyboardFocused && notch.PopupIsOpen, "Reflow fixture did not restore its actual focused open card.");
            var content = notch.Content;
            notch.QueueDisplayLayout(); notch.QueueDisplayLayout(); notch.QueueDisplayLayout();
            Require(ReferenceEquals(content, notch.Content), "Display reflow ran inline before the DPI/layout change completed.");
            await Idle();
            var replacement = Descendants<Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == "notch.provider.codex");
            observations.Add(new { stage = "provider-reflow", contentReplaced = !ReferenceEquals(content, notch.Content),
                replacement.IsKeyboardFocused, replacement.IsKeyboardFocusWithin, replacement.IsLoaded, replacement.IsVisible,
                notch.IsActive, focusId = FocusId(), notch.PopupIsOpen }); Receipt(false);
            Require(!ReferenceEquals(content, notch.Content) && replacement.IsKeyboardFocused && notch.PopupIsOpen,
                "Display reflow lost the focused provider or its open card.");
            RequirePopupClearOfNotch(notch, "display reflow");
            notch.OpenAccounts(); await Idle();
            var accountFrame = notch.PopupContent;
            var account = Descendants<Button>(accountFrame!).First(); var accountFocusAccepted = account.Focus(); await Idle();
            Require(account.IsKeyboardFocused, "Reflow fixture did not acquire actual account keyboard focus.");
            notch.QueueDisplayLayout(); await Idle();
            observations.Add(new { stage = "account-reflow", accountFocusAccepted, accountFocus = account.IsKeyboardFocused,
                accountFramePreserved = ReferenceEquals(accountFrame, notch.PopupContent), accountMenuPreserved = notch.AccountMenuIsOpen,
                focusId = FocusId() }); Receipt(false);
            Require(notch.AccountMenuIsOpen && ReferenceEquals(accountFrame, notch.PopupContent) && account.IsKeyboardFocused,
                "Display reflow dismissed or replaced an active account menu.");
            Keyboard.ClearFocus();
            foreach (var edge in Enum.GetValues<NotchEdge>())
            {
                settings.Save(settings.Current with { Edge = edge, Scale = 1.25, EnabledProviders = ProviderCatalog.All.Select(x => x.Id).ToArray() });
                Require(store.Synthetic, "Routed wheel fixture must use synthetic providers.");
                await store.RefreshAsync(true); await Idle();
                var scroll = Descendants<ScrollViewer>(notch).Single();
                var vertical = edge is NotchEdge.Left or NotchEdge.Right;
                var first = Descendants<Button>(notch).First(x => AutomationProperties.GetAutomationId(x).StartsWith("notch.provider.", StringComparison.Ordinal));
                var firstId = AutomationProperties.GetAutomationId(first);
                double Offset() => vertical ? scroll.VerticalOffset : scroll.HorizontalOffset;
                void Move(double offset) { if (vertical) scroll.ScrollToVerticalOffset(offset); else scroll.ScrollToHorizontalOffset(offset); }
                // Sensitivity: the former offset-before-Focus order lets WPF's
                // later MakeVisible command move the first provider back into view.
                var scrollExtent = vertical ? scroll.ScrollableHeight : scroll.ScrollableWidth;
                Require(scrollExtent > 240, "Focus/scroll fixture has insufficient scrollable extent on " + edge);
                notch.Activate(); Keyboard.ClearFocus(); scroll.ScrollToHome(); await Idle();
                Move(120); await Idle();
                var reached = Offset();
                Require(Math.Abs(reached - 120) < 1, "Focus/scroll fixture did not reach its target offset on " + edge);
                scroll.ScrollToHome(); await Idle();
                Move(120); first.Focus(); await Idle();
                var oldOrderOffset = Offset();
                observations.Add(new { stage = "focus-order-sensitivity", edge, requested = 120, scrollExtent, reached, oldOrderOffset,
                    first.IsKeyboardFocused, focusId = FocusId() }); Receipt(false);
                Require(first.IsKeyboardFocused && oldOrderOffset < 119, "Old focus/scroll ordering probe was insensitive on " + edge);
                Keyboard.ClearFocus(); scroll.ScrollToHome(); await Idle();
                first.Focus(); await Idle();
                Require(first.IsKeyboardFocused, "Wheel fixture did not acquire actual provider keyboard focus on " + edge);
                var before = Offset();
                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -240) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                scroll.RaiseEvent(wheel); await Idle();
                var after = Offset();
                observations.Add(new { stage = "wheel-result", edge, before, after, wheel.Handled,
                    first.IsKeyboardFocused, first.IsKeyboardFocusWithin, first.IsLoaded, first.IsVisible,
                    focusId = FocusId(), notch.PopupIsOpen, screenPointer = System.Windows.Forms.Cursor.Position }); Receipt(false);
                Require(wheel.Handled && after > before, "Routed wheel did not scroll providers on " + edge);
                Require(first.IsKeyboardFocused && !notch.PopupIsOpen, "Wheel fixture lost focus or failed to dismiss its card on " + edge);
                var hoverTarget = Descendants<Button>(notch).First(x => x != first
                    && AutomationProperties.GetAutomationId(x).StartsWith("notch.provider.", StringComparison.Ordinal));
                var stationaryPoint = System.Windows.Forms.Cursor.Position;
                var dismissedContent = notch.PopupContent;
                hoverTarget.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                hoverTarget.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
                await Idle();
                var observedPoint = System.Windows.Forms.Cursor.Position;
                observations.Add(new { stage = "stationary-provider-hover", edge, stationaryPoint, observedPoint,
                    first.IsKeyboardFocused, notch.PopupIsOpen, contentPreserved = ReferenceEquals(dismissedContent, notch.PopupContent) }); Receipt(false);
                Require(stationaryPoint == observedPoint, "Stationary hover fixture pointer moved on " + edge);
                Require(first.IsKeyboardFocused && !notch.PopupIsOpen && ReferenceEquals(dismissedContent, notch.PopupContent),
                    "Stationary provider enter/move reopened or rebuilt a dismissed card on " + edge);
                var cellEnd = first.TranslatePoint(new Point(first.ActualWidth, first.ActualHeight), scroll);
                Require((vertical ? cellEnd.Y : cellEnd.X) <= 0, "Focused provider was not offscreen before reflow on " + edge);
                notch.QueueDisplayLayout(); await Idle();
                var current = Descendants<ScrollViewer>(notch).Single();
                var focused = Descendants<Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == firstId);
                var retained = vertical ? current.VerticalOffset : current.HorizontalOffset;
                observations.Add(new { stage = "focused-routed-wheel", edge, before, after, retained, cellEnd, wheel.Handled,
                    focused.IsKeyboardFocused, notch.PopupIsOpen, focusId = FocusId() }); Receipt(false);
                Require(Math.Abs(retained - after) < 1, "Display reflow reset provider scroll on " + edge);
                Require(focused.IsKeyboardFocused && !notch.PopupIsOpen, "Display reflow lost focus or reopened a dismissed card on " + edge);
                var removedId = Descendants<Button>(notch).Select(x => AutomationProperties.GetAutomationId(x))
                    .First(x => x.StartsWith("notch.provider.", StringComparison.Ordinal) && x != firstId && x != "notch.provider.claude")["notch.provider.".Length..];
                var removedReading = store.Readings[removedId];
                var beforeReadingReflow = notch.Content;
                Exception? readingFailure = null; var readingCleanup = new List<Exception>();
                try
                {
                    store.Readings.Remove(removedId); notch.RefreshReadings(); await Idle();
                    var refreshed = Descendants<ScrollViewer>(notch).Single();
                    var refreshedFocus = Descendants<Button>(notch).Single(x => AutomationProperties.GetAutomationId(x) == firstId);
                    var refreshedOffset = vertical ? refreshed.VerticalOffset : refreshed.HorizontalOffset;
                    observations.Add(new { stage = "reading-reflow", edge, removedId, after, refreshedOffset,
                        refreshedFocus.IsKeyboardFocused, notch.PopupIsOpen }); Receipt(false);
                    Require(!ReferenceEquals(beforeReadingReflow, notch.Content)
                        && !Descendants<Button>(notch).Any(x => AutomationProperties.GetAutomationId(x) == "notch.provider." + removedId),
                        "Provider-list fixture did not rebuild after removal on " + edge);
                    Require(Math.Abs(refreshedOffset - after) < 1 && refreshedFocus.IsKeyboardFocused && !notch.PopupIsOpen,
                        "Provider-list refresh lost focus/scroll or reopened a dismissed card on " + edge);
                }
                catch (Exception error) when (error is not OutOfMemoryException) { readingFailure = error; }
                finally
                {
                    try { store.Readings[removedId] = removedReading; notch.RefreshReadings(); await Idle(); }
                    catch (Exception error) when (error is not OutOfMemoryException) { readingCleanup.Add(error); }
                }
                if (readingFailure is not null && readingCleanup.Count > 0)
                    throw new AggregateException("Provider-list fixture and restoration failed.", new[] { readingFailure }.Concat(readingCleanup));
                if (readingFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(readingFailure).Throw();
                if (readingCleanup.Count > 0) throw new AggregateException("Provider-list restoration failed.", readingCleanup);
                Keyboard.ClearFocus();
            }
            completed = true;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            try { trace.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            try { Receipt(completed && failure is null && cleanup.Count == 0); }
            catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
        }
        if (cleanup.Count > 0)
            throw new AggregateException("Reflow fixture cleanup or final diagnostics failed.", failure is null ? cleanup : cleanup.Prepend(failure));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // Observer only: record popup and control events in memory, without moving
    // the pointer, dispatching input or changing timing with per-event file I/O.
    private sealed class NotchReflowTrace : IDisposable
    {
        private readonly NotchWindow notch;
        private readonly Popup popup;
        private readonly DependencyPropertyDescriptor content;
        private readonly List<Button> attached = [];
        private readonly List<FrameworkElement> roots = [];
        private int generation;
        internal List<object> Events { get; } = [];
        internal int DroppedEvents { get; private set; }
        internal NotchReflowTrace(NotchWindow notch)
        {
            this.notch = notch;
            popup = typeof(NotchWindow).GetField("popup", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(notch) as Popup
                ?? throw new InvalidOperationException("Reflow trace has no native popup.");
            content = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(NotchWindow))
                ?? throw new InvalidOperationException("Reflow trace has no content descriptor.");
            popup.Opened += Opened; popup.Closed += Closed;
            notch.GotKeyboardFocus += Focused; notch.LostKeyboardFocus += Focused;
            content.AddValueChanged(notch, Changed); Attach();
        }
        private void Record(string stage, string source = "")
        {
            if (Events.Count >= 256) { DroppedEvents++; return; }
            var focusedId = Keyboard.FocusedElement is DependencyObject focused ? AutomationProperties.GetAutomationId(focused) : "";
            Events.Add(new { stage, source, generation, focusedId, notch.PopupIsOpen,
                notch.IsActive, pointer = Mouse.GetPosition(notch), screenPointer = System.Windows.Forms.Cursor.Position });
        }
        private void Opened(object? sender, EventArgs e) => Record("popup-opened");
        private void Closed(object? sender, EventArgs e) => Record("popup-closed");
        private void Focused(object sender, KeyboardFocusChangedEventArgs e) => Record("keyboard-focus", e.OriginalSource is DependencyObject source ? AutomationProperties.GetAutomationId(source) : "");
        private void Entered(object sender, MouseEventArgs e)
        {
            var id = AutomationProperties.GetAutomationId((DependencyObject)sender);
            Record(id.StartsWith("notch.provider.", StringComparison.Ordinal) ? "provider-mouse-enter" : "control-mouse-enter", id);
        }
        private void Changed(object? sender, EventArgs e) { generation++; Record("content-changed"); Attach(); }
        private void Loaded(object sender, RoutedEventArgs e) => Attach();
        private void Attach()
        {
            if (notch.Content is not FrameworkElement root) return;
            if (!roots.Contains(root)) { roots.Add(root); root.Loaded += Loaded; }
            foreach (var button in Descendants<Button>(root).Where(x => AutomationProperties.GetAutomationId(x) is "notch.settings" or "notch.switchAccount"
                || AutomationProperties.GetAutomationId(x).StartsWith("notch.provider.", StringComparison.Ordinal)))
                if (!attached.Contains(button)) { attached.Add(button); button.MouseEnter += Entered; }
        }
        public void Dispose()
        {
            content.RemoveValueChanged(notch, Changed);
            popup.Opened -= Opened; popup.Closed -= Closed;
            notch.GotKeyboardFocus -= Focused; notch.LostKeyboardFocus -= Focused;
            foreach (var button in attached) button.MouseEnter -= Entered;
            foreach (var root in roots) root.Loaded -= Loaded;
        }
    }
}
