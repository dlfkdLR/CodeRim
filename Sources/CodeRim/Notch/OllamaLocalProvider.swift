import Foundation

/// What `ollama serve` has loaded right now, read from `/api/ps` on loopback.
///
/// Not a usage ring — a local model server has no quota — so the cell shows
/// the count of loaded models and the tooltip lists them by name and memory.
/// No daemon, no cell.
///
/// Ported from the MIT-licensed Codenotch (`OllamaLocalProvider` /
/// `OllamaLocalUsage`), trimmed to the model list.
@MainActor
final class OllamaLocalProvider: NotchProvider {
    let id = "ollama-local"
    let displayName = "Ollama"
    let glyph: ProviderGlyph = .ollamaLocal
    var isVisibleWhenAbsent: Bool { false }

    private let endpoint: URL
    private let session: URLSession

    init(endpoint: URL = URL(string: "http://127.0.0.1:11434")!) {
        self.endpoint = endpoint
        let config = URLSessionConfiguration.ephemeral
        config.httpShouldSetCookies = false
        config.urlCredentialStorage = nil
        self.session = URLSession(configuration: config)
    }

    var signInRoute: SignInRoute {
        .openApp(bundleID: "com.electron.ollama", name: "Ollama")
    }

    func fetchSnapshot() async throws -> ProviderSnapshot {
        var request = URLRequest(url: endpoint.appendingPathComponent("api/ps"))
        request.timeoutInterval = 3
        request.cachePolicy = .reloadIgnoringLocalCacheData
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await BoundedHTTP.data(for: request, on: session)
        } catch {
            // The daemon is not running (or not on this port) — hide the cell.
            throw NotchProviderError.needsAuth
        }
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw NotchProviderError.badResponse(
                status: (response as? HTTPURLResponse)?.statusCode ?? 0
            )
        }

        let models = OllamaLocalUsage.models(in: data)
        // Daemon up but idle — no cell, rather than a dimmed "0".
        guard !models.isEmpty else {
            throw NotchProviderError.nothingMetered("Ollama is running with no models loaded")
        }
        let windows = [LimitWindow(id: "loaded", label: "Loaded models", used: models.count)]
            + models.map { model in
                LimitWindow(id: "model.\(model.name)", group: "Loaded models",
                            label: model.name, used: model.megabytes)
            }
        return ProviderSnapshot(
            id: id, displayName: displayName, glyph: glyph,
            fidelity: .official, status: .ok,
            windows: windows, headlineID: "loaded"
        )
    }
}

enum OllamaLocalUsage {
    struct Model: Equatable {
        let name: String
        /// Resident size in MB (RAM or VRAM), rounded — the tooltip's trailing
        /// figure.
        let megabytes: Int
    }

    static func models(in data: Data) -> [Model] {
        struct Response: Decodable {
            struct Entry: Decodable {
                let name: String?
                let model: String?
                let size: Int64?
                let size_vram: Int64?
            }
            let models: [Entry]?
        }
        guard let decoded = try? JSONDecoder().decode(Response.self, from: data) else { return [] }
        var seen = Set<String>()
        return (decoded.models ?? []).compactMap { entry in
            guard let name = (entry.name ?? entry.model)?
                .trimmingCharacters(in: .whitespacesAndNewlines), !name.isEmpty,
                  seen.insert(name).inserted
            else { return nil }
            let bytes = max(entry.size_vram ?? 0, entry.size ?? 0)
            return Model(name: name, megabytes: Int((Double(bytes) / 1_048_576).rounded()))
        }
        .sorted { $0.name < $1.name }
    }
}
