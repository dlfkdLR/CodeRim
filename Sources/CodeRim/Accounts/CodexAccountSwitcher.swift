import AppKit
import SwiftUI

// Extend the native popover: current account first, saved logins one menu away.
// Account selection still requires the existing protected restart confirmation.
// Keep usage, quota, credential storage, and the visual system unchanged.
struct CodexAccountSwitcher: View {
    @ObservedObject var accounts: CodexAccountStore
    var showManagement: () -> Void = { CodexAccountsWindowController.shared.show() }
    /// Settings must open without an unsolicited Keychain read. Its button
    /// opens the existing account window when the user requests saved logins.
    var opensManagementDirectly = false
    @State private var pendingSwitch: SavedCodexAccount?

    private var currentAccount: SavedCodexAccount? {
        accounts.accounts.first { $0.id == accounts.currentID }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                if accounts.isBusy {
                    ProgressView().controlSize(.small)
                        .accessibilityLabel("Account operation in progress")
                } else {
                    Image(systemName: "person.crop.circle")
                        .foregroundStyle(.secondary)
                        .accessibilityHidden(true)
                }
                Text(accounts.currentAccountDisplayName ?? "Codex account")
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .help(accounts.currentAccountDisplayName ?? "No signed-in account found")
                if opensManagementDirectly {
                    Button("Switch", action: showManagement)
                        .buttonStyle(.borderless)
                        .frame(minHeight: 28)
                        .accessibilityIdentifier("menu.accountSwitcher")
                        .accessibilityLabel("Switch Codex account")
                        .help("Switch, add, or manage Codex accounts")
                } else {
                    accountMenu
                }
            }
            .font(.subheadline)
            .padding(.horizontal, 6)
            .frame(minHeight: 36)

            if accounts.isError, let message = accounts.message {
                Label(message, systemImage: "exclamationmark.triangle")
                    .font(.caption)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("menu.accountError")
                    .padding(.horizontal, 6)
                    .padding(.bottom, 4)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 4)
        .task { refreshAccount() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            refreshAccount()
        }
        .modifier(CodexAccountSwitchConfirmation(accounts: accounts, selection: $pendingSwitch))
    }

    private func refreshAccount() {
        if opensManagementDirectly {
            accounts.refreshCurrentPlanType()
        } else {
            accounts.load()
        }
    }

    private var accountMenu: some View {
        Menu {
                if accounts.accounts.isEmpty {
                    Text("No saved accounts")
                } else {
                    ForEach(accounts.accounts) { account in
                        Button {
                            pendingSwitch = account
                        } label: {
                            if account.id == accounts.currentID {
                                Label(account.menuTitle(in: accounts.accounts), systemImage: "checkmark")
                            } else {
                                Text(account.menuTitle(in: accounts.accounts))
                            }
                        }
                        .disabled(accounts.isBusy || account.id == accounts.currentID)
                        .accessibilityLabel(account.id == accounts.currentID
                            ? "Current account, \(account.menuTitle(in: accounts.accounts))"
                            : "Switch Codex to \(account.menuTitle(in: accounts.accounts))")
                    }
                }
                Divider()
                if currentAccount == nil {
                    Button("Save Current Account") { Task { await accounts.saveCurrent() } }
                        .disabled(accounts.isBusy)
                }
                if accounts.isSigningIn {
                    Button("Cancel Sign-In") { accounts.cancelSignIn() }
                } else {
                    Button("Add Account…", systemImage: "plus") {
                        showManagement()
                        accounts.addAccount()
                    }
                    .disabled(accounts.isBusy)
                }
                Button("Manage Accounts…", systemImage: "person.crop.circle") { showManagement() }
        } label: {
            Text("Switch")
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .frame(minHeight: 28)
        .accessibilityIdentifier("menu.accountSwitcher")
        .accessibilityLabel("Switch Codex account")
        .accessibilityValue(currentAccount?.menuTitle(in: accounts.accounts) ?? "No saved login selected")
        .accessibilityHint("Choose a saved account to switch, or add and manage accounts.")
        .help("Switch, add, or manage Codex accounts")
    }
}

extension SavedCodexAccount {
    /// Disambiguate multiple workspaces without exposing credentials in menu labels.
    func menuTitle(in accounts: [SavedCodexAccount]) -> String {
        guard accounts.filter({ $0.email == email }).count > 1 else { return email }
        return "\(email) · \(workspaceID.suffix(8))"
    }
}

struct CodexAccountSwitchConfirmation: ViewModifier {
    @ObservedObject var accounts: CodexAccountStore
    @Binding var selection: SavedCodexAccount?

    func body(content: Content) -> some View {
        content.alert("Switch Codex account?", isPresented: Binding(
            get: { selection != nil }, set: { if !$0 { selection = nil } }
        ), presenting: selection) { account in
            Button("Cancel", role: .cancel) { selection = nil }
            Button("Quit Codex & Switch") {
                selection = nil
                Task { await accounts.switchAccount(to: account.id) }
            }
            .disabled(accounts.isBusy)
        } message: { account in
            Text("Codex will quit and reopen as \(account.menuTitle(in: accounts.accounts)). Finish any running tasks and save your work first. Other Codex processes must also be closed.")
        }
    }
}
