import AppKit
import CodexBarCore
import Foundation

/// A sign-in CodeRim carries out itself, adapted from CodexBar's login flows (MIT): it needs
/// no command-line tool, and what it returns is kept in this app's own Keychain item for the
/// provider, where the upstream reader picks it up.
enum InAppSignIn: Equatable {
    /// GitHub's device flow: a short code approved on github.com yields a Copilot token.
    case githubDevice
    /// Google account consent for Antigravity through a local loopback redirect.
    case antigravityGoogle
}

@MainActor
enum InAppSignInRunner {
    enum Outcome: Equatable {
        case signedIn
        case failed(String)
    }

    /// Runs the sign-in. `update` receives what the user has to do right now, for the waiting UI.
    static func run(_ kind: InAppSignIn, update: @escaping @MainActor (String) -> Void) async -> Outcome {
        switch kind {
        case .githubDevice:
            let flow = CopilotDeviceFlow()
            return await gitHubDevice(
                requestCode: {
                    let code = try await flow.requestDeviceCode()
                    return (code.userCode, code.deviceCode, URL(string: code.verificationURLToOpen), code.interval)
                },
                poll: { device, interval in try await flow.pollForToken(deviceCode: device, interval: interval) },
                save: { token in try store(for: .copilot) { $0.provider.apiKey = token } },
                open: { NSWorkspace.shared.open($0) },
                update: update)
        case .antigravityGoogle:
            return await antigravityGoogle(client: { AntigravityOAuthConfig.resolvedClient() }, update: update)
        }
    }

    // MARK: GitHub device flow

    static func gitHubDevice(
        requestCode: () async throws -> (user: String, device: String, verify: URL?, interval: Int),
        poll: (String, Int) async throws -> String,
        save: (String) throws -> Void,
        open: (URL) -> Void,
        update: @escaping @MainActor (String) -> Void) async -> Outcome {
        do {
            let code = try await requestCode()
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(code.user, forType: .string)
            update("Enter the code \(code.user) on GitHub (it is already copied), approve CodeRim, and it connects on its own.")
            if let url = code.verify { open(url) }
            let token = try await poll(code.device, code.interval)
            try save(token)
            return .signedIn
        } catch is CancellationError {
            return .failed("Sign-in was cancelled.")
        } catch {
            return .failed("GitHub sign-in did not finish: \(error.localizedDescription) Choose Try again for a new code.")
        }
    }

    // MARK: Antigravity Google sign-in

    static func antigravityGoogle(client resolveClient: () -> AntigravityOAuthClient?,
                                  open: (URL) -> Bool = { NSWorkspace.shared.open($0) },
                                  timeout: Duration = .seconds(300),
                                  update: @escaping @MainActor (String) -> Void) async -> Outcome {
        guard let client = resolveClient() else {
            return .failed("Install the Antigravity app first — CodeRim signs in with the same Google sign-in it uses. Then choose Try again.")
        }
        let state = UUID().uuidString.replacingOccurrences(of: "-", with: "")
        let server = OAuthLoopbackServer(state: state)
        do {
            let callbackURL = try await server.start()
            var components = URLComponents(url: AntigravityOAuthConfig.authURL, resolvingAgainstBaseURL: false)!
            components.queryItems = [
                URLQueryItem(name: "client_id", value: client.clientID),
                URLQueryItem(name: "redirect_uri", value: callbackURL.absoluteString),
                URLQueryItem(name: "response_type", value: "code"),
                URLQueryItem(name: "scope", value: AntigravityOAuthConfig.scopes.joined(separator: " ")),
                URLQueryItem(name: "access_type", value: "offline"),
                URLQueryItem(name: "prompt", value: "select_account consent"),
                URLQueryItem(name: "state", value: state),
            ]
            guard let authURL = components.url, open(authURL) else {
                server.stop(); return .failed("The Google sign-in page could not be opened.")
            }
            update("Choose your Google account in the browser and allow access. CodeRim connects as soon as you finish.")
            let callback = try await withThrowingTaskGroup(of: OAuthCallback.self) { group in
                group.addTask { try await server.waitForCallback() }
                // Ends the wait on timeout and on Cancel alike: the callback wait is a plain
                // continuation that ignores cancellation, and the group cannot return while it hangs.
                group.addTask {
                    try? await Task.sleep(for: timeout)
                    server.cancelCallbackWait(with: CancellationError())
                    throw CancellationError()
                }
                defer { group.cancelAll() }
                return try await group.next()!
            }
            server.stop()
            if let error = callback.error, !error.isEmpty {
                return .failed(error == "access_denied" ? "Google sign-in was cancelled." : "Google sign-in failed: \(error)")
            }
            guard callback.state == state, let code = callback.code, !code.isEmpty else {
                return .failed("Google did not return a sign-in code. Try again.")
            }
            let tokens = try await exchange(code: code, redirect: callbackURL, client: client)
            let credentials = AntigravityOAuthCredentials(
                accessToken: tokens.accessToken, refreshToken: tokens.refreshToken,
                expiryDate: Date().addingTimeInterval(TimeInterval(tokens.expiresIn)), idToken: tokens.idToken,
                email: nil, projectID: nil, clientID: client.clientID, clientSecret: client.clientSecret)
            let value = try AntigravityOAuthCredentialsStore.tokenAccountValue(for: credentials)
            try store(for: .antigravity) {
                $0.environment[AntigravityOAuthCredentialsStore.environmentCredentialsKey] = value
            }
            return .signedIn
        } catch is CancellationError {
            server.stop(); return .failed("Google sign-in timed out or was cancelled.")
        } catch {
            server.stop(); return .failed("Google sign-in failed: \(error.localizedDescription)")
        }
    }

    private struct Tokens: Decodable {
        let accessToken: String
        let refreshToken: String?
        let expiresIn: Int
        let idToken: String?
        enum CodingKeys: String, CodingKey {
            case accessToken = "access_token", refreshToken = "refresh_token", expiresIn = "expires_in", idToken = "id_token"
        }
    }

    private static func exchange(code: String, redirect: URL, client: AntigravityOAuthClient) async throws -> Tokens {
        var request = URLRequest(url: AntigravityOAuthConfig.tokenURL)
        request.httpMethod = "POST"
        request.timeoutInterval = 30
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        var allowed = CharacterSet.urlQueryAllowed; allowed.remove(charactersIn: "+&=")
        let form = ["code": code, "client_id": client.clientID, "client_secret": client.clientSecret,
                    "redirect_uri": redirect.absoluteString, "grant_type": "authorization_code"]
        request.httpBody = form.map { "\($0.key)=\($0.value.addingPercentEncoding(withAllowedCharacters: allowed) ?? $0.value)" }
            .joined(separator: "&").data(using: .utf8)
        let (data, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw NSError(domain: "CodeRim.GoogleSignIn", code: (response as? HTTPURLResponse)?.statusCode ?? -1,
                          userInfo: [NSLocalizedDescriptionKey: "Google refused the sign-in code."])
        }
        return try JSONDecoder().decode(Tokens.self, from: data)
    }

    /// Writes into this app's Keychain item for the provider, turning nothing else on.
    static func store(for id: CodexBarCore.UsageProvider, _ change: (inout ExtendedProviderConfiguration) -> Void) throws {
        let descriptor = ProviderDescriptorRegistry.descriptor(for: id)
        // A refused or unreadable item must not be replaced by a blank one: that would erase the
        // keys the user saved. A missing item already loads as a fresh configuration.
        var configuration = try ExtendedProviderConfigurationStore.load(descriptor, interactive: true)
        change(&configuration)
        try ExtendedProviderConfigurationStore.save(configuration)
    }
}
