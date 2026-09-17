import Foundation

struct ProfileUsageSnapshot: Equatable, Sendable {
    let today: Int64
    let week: Int64
    let month: Int64
    let lifetime: Int64
    let statsAsOf: Date
    let generatedAt: Date
}

enum ProfileUsageStatus: Equatable, Sendable {
    case disabled
    case idle
    case refreshing
    case ready
    case credentialsUnavailable
    case unavailable

    var message: String {
        switch self {
        case .disabled:
            "Account totals are off"
        case .idle:
            "Account totals are ready to sync"
        case .refreshing:
            "Updating account totals…"
        case .ready:
            "Account totals updated"
        case .credentialsUnavailable:
            "Codex sign-in is unavailable"
        case .unavailable:
            "Account totals are unavailable"
        }
    }
}

enum ProfileUsageError: Error, Equatable, Sendable {
    case credentialsUnavailable
    case unsafeCredentialFile
    case invalidCredentials
    case invalidRequest
    case redirectRejected
    case responseTooLarge
    case transportFailure
    case invalidHTTPResponse
    case invalidResponse
}

struct ProfileCredential: Equatable, Sendable {
    let accessToken: String
    let accountID: String
}

struct ProfileHTTPResponse: @unchecked Sendable {
    let data: Data
    let response: HTTPURLResponse
}

typealias ProfileCredentialLoading = @Sendable () throws -> ProfileCredential
typealias ProfileRequestLoading = @Sendable (URLRequest) async throws -> ProfileHTTPResponse
typealias ProfileUsageFetching = @Sendable (
    _ now: Date,
    _ calendar: Calendar,
    _ weekStart: WeekStart
) async throws -> ProfileUsageSnapshot
