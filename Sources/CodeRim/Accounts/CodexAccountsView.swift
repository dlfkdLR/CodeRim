import AppKit
import SwiftUI

// An extension of the existing native utility: one account list, explicit actions,
// and a protected restart confirmation. No quota-driven rotation or decorative cards.
struct CodexAccountsView: View {
    @ObservedObject var accounts: CodexAccountStore
    @State private var pendingSwitch: SavedCodexAccount?
    @State private var pendingRemoval: SavedCodexAccount?

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("Codex Accounts").font(.title2.weight(.semibold))
                Spacer()
                if accounts.isBusy {
                    ProgressView().controlSize(.small).accessibilityLabel("Account operation in progress")
                }
            }
            .padding(.bottom, 8)
            Text("Select an account for Codex and its CLI.")
                .foregroundStyle(.secondary)
                .padding(.bottom, 20)

            if accounts.accounts.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    Text("No saved accounts").font(.headline)
                    Text("Save your current login, or sign in to add another account.")
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .frame(maxWidth: .infinity, minHeight: 100, alignment: .leading)
            } else {
                ScrollView {
                    VStack(spacing: 0) {
                        ForEach(accounts.accounts) { account in
                            accountRow(account)
                            if account.id != accounts.accounts.last?.id { Divider() }
                        }
                    }
                }
                .frame(maxHeight: .infinity)
            }

            Divider().padding(.vertical, 16)
            HStack(spacing: 12) {
                Button("Save Current Account") { Task { await accounts.saveCurrent() } }
                    .disabled(accounts.isBusy)
                    .accessibilityIdentifier("accounts.saveCurrent")
                Button("Add Account…", systemImage: "plus") { accounts.addAccount() }
                    .disabled(accounts.isBusy)
                    .accessibilityIdentifier("accounts.add")
                Spacer(minLength: 0)
                if accounts.isSigningIn {
                    Button("Cancel") { accounts.cancelSignIn() }
                } else {
                    Button("Open Codex") { Task { await accounts.openCodex() } }
                        .disabled(accounts.isBusy)
                }
            }
            .controlSize(.regular)

            if let message = accounts.message {
                HStack(alignment: .top, spacing: 8) {
                    if accounts.isError {
                        Image(systemName: "exclamationmark.triangle.fill")
                            .foregroundStyle(.red)
                            .accessibilityLabel("Error")
                    }
                    Text(message)
                        .foregroundStyle(accounts.isError ? Color.primary : Color.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .font(.callout)
                .padding(.top, 12)
                .accessibilityElement(children: .combine)
                .accessibilityIdentifier("accounts.status")
            }

            Text("Saved logins stay in this Mac’s Keychain. Switching restarts Codex; finish your work first.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, 16)
        }
        .padding(24)
        .frame(minWidth: 500, idealWidth: 560, maxWidth: .infinity, minHeight: 300, idealHeight: 400, maxHeight: .infinity, alignment: .topLeading)
        .task { accounts.load() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            accounts.load()
        }
        .modifier(CodexAccountSwitchConfirmation(accounts: accounts, selection: $pendingSwitch))
        .alert("Remove saved account?", isPresented: Binding(
            get: { pendingRemoval != nil }, set: { if !$0 { pendingRemoval = nil } }
        ), presenting: pendingRemoval) { account in
            Button("Cancel", role: .cancel) { pendingRemoval = nil }
            Button("Remove", role: .destructive) { accounts.remove(account.id); pendingRemoval = nil }
        } message: { _ in Text("This removes the saved login from CodeRim, without signing out of Codex.") }
    }

    private func accountRow(_ account: SavedCodexAccount) -> some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(account.email)
                    .fontWeight(.medium)
                    .lineLimit(2)
                    .textSelection(.enabled)
                if accounts.accounts.filter({ $0.email == account.email }).count > 1 {
                    Text("Workspace · \(account.workspaceID.suffix(8))")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 8)
            if account.id == accounts.currentID {
                Label("Current", systemImage: "checkmark")
                    .font(.callout).foregroundStyle(.secondary)
            } else {
                Button("Switch") { pendingSwitch = account }
                    .accessibilityLabel("Switch Codex to \(account.email)")
            }
            Button(role: .destructive) { pendingRemoval = account } label: {
                Image(systemName: "minus.circle").frame(width: 28, height: 28)
            }
            .buttonStyle(.borderless)
            .help("Remove saved account")
            .accessibilityLabel("Remove saved account \(account.email)")
        }
        .disabled(accounts.isBusy)
        .padding(.vertical, 14)
    }
}

@MainActor
final class CodexAccountsWindowController {
    static let shared = CodexAccountsWindowController()
    private var window: NSWindow?

    func show() {
        if window == nil {
            let controller = NSHostingController(rootView: CodexAccountsView(accounts: .shared))
            let window = NSWindow(contentViewController: controller)
            window.title = "Codex Accounts"
            window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
            window.setContentSize(NSSize(width: 560, height: 400))
            window.contentMinSize = NSSize(width: 500, height: 300)
            window.isReleasedWhenClosed = false
            window.center()
            self.window = window
        }
        CodexAccountStore.shared.load()
        NSApplication.shared.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}
