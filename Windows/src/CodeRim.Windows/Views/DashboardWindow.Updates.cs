using System.Threading;
using System.Windows;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;

namespace CodeRim.Windows.Views;

internal sealed partial class DashboardWindow
{
    private CancellationTokenSource? updateOperation;
    private bool updateWindowClosed;
    private long updateViewRevision;
    private (string Operation, string Launcher)? pendingRestart;
    private bool updateHandedOff;
    private void CancelUpdateOperation()
    {
        updateOperation?.Cancel();
        if (!updateHandedOff && pendingRestart is { } restart)
            try { UpdateBootstrap.WithdrawRestart(restart.Operation, restart.Launcher); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    private bool IsCurrentUpdate(CancellationTokenSource operation, long revision) => !updateWindowClosed && page == "about"
        && updateViewRevision == revision && ReferenceEquals(updateOperation, operation) && !operation.IsCancellationRequested;
    private void AddUpdateSection()
    {
        var status = Ui.Text("", 12, "#A6A6AA");
        var revision = updateViewRevision;
        var cancel = Ui.Button("Cancel", CancelUpdateOperation); cancel.Visibility = Visibility.Collapsed;
        var check = Ui.AsyncButton("Check for updates", async () =>
        {
            if (updateOperation is not null) { status.Text = "Another update operation is finishing."; return; }
            using var cancellation = new CancellationTokenSource(); updateOperation = cancellation;
            cancel.Visibility = Visibility.Visible;
            status.Text = "Checking…";
            try
            {
                var update = await ReleaseUpdates.CheckAsync(UpdateNotifications.Architecture, cancellation.Token).ConfigureAwait(true);
                if (!IsCurrentUpdate(cancellation, revision)) return;
                status.Text = update.IsNewer ? "CodeRim " + update.Version + " is available." : "You are using the latest Windows release.";
                if (!update.IsNewer) return;
                if (!UpdateCoordinator.SigningConfigured || update.Package is null)
                {
                    status.Text += " Use the manual release download for this installation.";
                    if (MessageBox.Show(this, "Download CodeRim " + update.Version + " for Windows?", "CodeRim update", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                        OpenUrl(update.Download.AbsoluteUri);
                    return;
                }
                if (MessageBox.Show(this, "Download and verify CodeRim " + update.Version + "? You can review the result before restarting.", "CodeRim update", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
                status.Text = "Downloading and verifying the signed update…";
                var result = await UpdateCoordinator.PrepareAsync(update.Package, cancellation.Token).ConfigureAwait(true);
                if (result.Status == UpdateExecutionStatus.Prepared && result.OperationId is { } prepared) pendingRestart = (prepared, prepared);
                if (!IsCurrentUpdate(cancellation, revision)) return;
                status.Text = result.Message;
                if (result.Status == UpdateExecutionStatus.Prepared && result.OperationId is { } id
                    && MessageBox.Show(this, "The signed update is ready. Restart CodeRim and install it now?", "CodeRim update", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    pendingRestart = (id, id);
                    await UpdateBootstrap.ConfirmRestartAsync(id, cancellation.Token).ConfigureAwait(true);
                    if (!IsCurrentUpdate(cancellation, revision)) return;
                    updateHandedOff = true;
                    ((App)System.Windows.Application.Current).ShutdownApplication();
                }
                else if (result.Status is UpdateExecutionStatus.UnmanagedInstallation or UpdateExecutionStatus.SigningNotConfigured)
                {
                    if (MessageBox.Show(this, result.Message + "\n\nOpen the manual ZIP download?", "CodeRim update", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                        OpenUrl(update.Download.AbsoluteUri);
                }
            }
            catch (OperationCanceledException) { status.Text = "Update check cancelled."; }
            catch (Exception error) when (error is not OutOfMemoryException) { status.Text = "Could not complete the Windows update. Try again or open the releases page."; }
            finally { if (!updateHandedOff) { CancelUpdateOperation(); pendingRestart = null; } if (ReferenceEquals(updateOperation, cancellation)) updateOperation = null; cancel.Visibility = Visibility.Collapsed; }
        });
        check.HorizontalAlignment = HorizontalAlignment.Left; check.Margin = new Thickness(14, 9, 14, 9);
        cancel.HorizontalAlignment = HorizontalAlignment.Left; cancel.Margin = new Thickness(14, 0, 14, 9);
        body.Children.Add(SettingsUi.Section("Updates", check, cancel));
        if (UpdateCoordinator.SigningConfigured)
        {
            var recovery = Ui.AsyncButton("Recover interrupted update", async () =>
            {
                if (updateOperation is not null) { status.Text = "Another update operation is finishing."; return; }
                using var cancellation = new CancellationTokenSource(); updateOperation = cancellation;
                cancel.Visibility = Visibility.Visible;
                try
                {
                    var id = UpdateRecovery.LatestPendingOperation();
                    if (id is null) { status.Text = "No interrupted update was found."; return; }
                    var launcher = Guid.NewGuid().ToString("N"); status.Text = "Verifying preserved update files…";
                    var result = await UpdateRecovery.PrepareAsync(id, launcher, typeof(App).Assembly, cancellation.Token).ConfigureAwait(true);
                    if (result.Status == UpdateExecutionStatus.Prepared) pendingRestart = (id, launcher);
                    if (!IsCurrentUpdate(cancellation, revision)) return;
                    status.Text = result.Message;
                    if (result.Status == UpdateExecutionStatus.Prepared && MessageBox.Show(this, "Quit CodeRim and recover the verified binary update? Reopen CodeRim after recovery.", "CodeRim update recovery", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    {
                        pendingRestart = (id, launcher);
                        await UpdateBootstrap.ConfirmRestartAsync(id, launcher, cancellation.Token).ConfigureAwait(true);
                        if (!IsCurrentUpdate(cancellation, revision)) return;
                        updateHandedOff = true;
                        ((App)System.Windows.Application.Current).ShutdownApplication();
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException) { status.Text = "Preserved update files could not be recovered automatically. Open the releases page for a manual repair."; }
                finally { if (!updateHandedOff) { CancelUpdateOperation(); pendingRestart = null; } if (ReferenceEquals(updateOperation, cancellation)) updateOperation = null; cancel.Visibility = Visibility.Collapsed; }
            });
            recovery.HorizontalAlignment = HorizontalAlignment.Left; recovery.Margin = new Thickness(14, 9, 14, 9);
            body.Children.Add(SettingsUi.Section("Recovery", recovery));
        }
        status.Margin = new Thickness(32, 6, 32, 0); body.Children.Add(status);
        body.Children.Add(SettingsUi.Note(UpdateCoordinator.SigningConfigured
            ? "Checks GitHub releases. Signed managed installations can restart to update. Local history and credentials are preserved."
            : "Checks GitHub releases. Token usage data is never sent. Installation is manual; signed updates are not configured."));
    }
}
