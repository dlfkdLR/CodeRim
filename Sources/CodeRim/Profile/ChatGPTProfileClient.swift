import Foundation

struct ChatGPTProfileClient: Sendable {
    static let endpoint = URL(string: "https://chatgpt.com/backend-api/wham/profiles/me")!
    static let maximumResponseSize = 1_024 * 1_024

    private let credentialLoader: ProfileCredentialLoading
    private let requestLoader: ProfileRequestLoading

    init(
        credentialLoader: @escaping ProfileCredentialLoading = {
            try CodexAuthCredentialLoader().load()
        },
        requestLoader: @escaping ProfileRequestLoading = { request in
            try await BoundedProfileRequestLoader(maximumSize: Self.maximumResponseSize)
                .load(request)
        }
    ) {
        self.credentialLoader = credentialLoader
        self.requestLoader = requestLoader
    }

    func fetch(
        now: Date = Date(),
        calendar: Calendar = .current,
        weekStart: WeekStart = .monday
    ) async throws -> ProfileUsageSnapshot {
        let credential = try credentialLoader()
        let request = try makeRequest(credential: credential)

        let loaded: ProfileHTTPResponse
        do {
            loaded = try await requestLoader(request)
        } catch let error as ProfileUsageError {
            throw error
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw ProfileUsageError.transportFailure
        }

        guard loaded.data.count <= Self.maximumResponseSize else {
            throw ProfileUsageError.responseTooLarge
        }
        guard loaded.response.url == Self.endpoint else {
            throw ProfileUsageError.invalidHTTPResponse
        }
        if loaded.response.statusCode == 401 || loaded.response.statusCode == 403 {
            throw ProfileUsageError.invalidCredentials
        }
        guard loaded.response.statusCode == 200 else {
            throw ProfileUsageError.invalidHTTPResponse
        }

        return try ProfileResponseDecoder.decode(
            loaded.data,
            now: now,
            calendar: calendar,
            weekStart: weekStart
        )
    }

    func makeRequest(credential: ProfileCredential) throws -> URLRequest {
        guard CodexAuthCredentialLoader.isValidHeaderValue(
            credential.accessToken,
            maximumUTF8Length: CodexAuthCredentialLoader.maximumAccessTokenLength
        ), CodexAuthCredentialLoader.isValidHeaderValue(
            credential.accountID,
            maximumUTF8Length: CodexAuthCredentialLoader.maximumAccountIDLength
        ) else {
            throw ProfileUsageError.invalidCredentials
        }

        var request = URLRequest(
            url: Self.endpoint,
            cachePolicy: .reloadIgnoringLocalAndRemoteCacheData,
            timeoutInterval: 20
        )
        request.httpMethod = "GET"
        request.httpShouldHandleCookies = false
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("no-store", forHTTPHeaderField: "Cache-Control")
        request.setValue("no-cache", forHTTPHeaderField: "Pragma")
        request.setValue("Bearer \(credential.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue(credential.accountID, forHTTPHeaderField: "ChatGPT-Account-ID")
        request.setValue("CodeRim/1", forHTTPHeaderField: "User-Agent")
        return request
    }
}

private enum ProfileResponseDecoder {
    static func decode(
        _ data: Data,
        now: Date,
        calendar baseCalendar: Calendar,
        weekStart: WeekStart
    ) throws -> ProfileUsageSnapshot {
        let response: ProfileResponseDTO
        do {
            response = try JSONDecoder().decode(ProfileResponseDTO.self, from: data)
        } catch {
            throw ProfileUsageError.invalidResponse
        }

        guard response.stats.lifetimeTokens >= 0 else {
            throw ProfileUsageError.invalidResponse
        }
        guard let generatedAt = parseGeneratedAt(response.metadata.generatedAt),
              let statsAsOfDay = ProfileDay(response.metadata.statsAsOf)
        else {
            throw ProfileUsageError.invalidResponse
        }

        var calendar = Calendar(identifier: .gregorian)
        calendar.locale = baseCalendar.locale
        calendar.timeZone = baseCalendar.timeZone
        calendar.firstWeekday = weekStart.rawValue
        guard let statsAsOf = statsAsOfDay.date(in: calendar) else {
            throw ProfileUsageError.invalidResponse
        }

        let currentDay = calendar.startOfDay(for: now)
        let currentWeek = calendar.dateInterval(of: .weekOfYear, for: currentDay)
        let currentMonth = calendar.dateInterval(of: .month, for: currentDay)
        guard let currentWeek, let currentMonth else {
            throw ProfileUsageError.invalidResponse
        }

        var seenDays = Set<ProfileDay>()
        var today: Int64 = 0
        var week: Int64 = 0
        var month: Int64 = 0

        for bucket in response.stats.dailyUsageBuckets {
            guard bucket.tokens >= 0,
                  let day = ProfileDay(bucket.startDate),
                  day <= statsAsOfDay,
                  seenDays.insert(day).inserted,
                  let date = day.date(in: calendar)
            else {
                throw ProfileUsageError.invalidResponse
            }

            if day == statsAsOfDay {
                today = bucket.tokens
            }
            if currentWeek.contains(date) {
                week = try checkedAdd(week, bucket.tokens)
            }
            if currentMonth.contains(date) {
                month = try checkedAdd(month, bucket.tokens)
            }
        }

        return ProfileUsageSnapshot(
            today: today,
            week: week,
            month: month,
            lifetime: response.stats.lifetimeTokens,
            statsAsOf: statsAsOf,
            generatedAt: generatedAt
        )
    }

    private static func checkedAdd(_ lhs: Int64, _ rhs: Int64) throws -> Int64 {
        let result = lhs.addingReportingOverflow(rhs)
        guard !result.overflow else { throw ProfileUsageError.invalidResponse }
        return result.partialValue
    }

    private static func parseGeneratedAt(_ value: String) -> Date? {
        guard value == value.trimmingCharacters(in: .whitespacesAndNewlines) else { return nil }
        if let date = try? Date.ISO8601FormatStyle(includingFractionalSeconds: true).parse(value) {
            return date
        }
        return try? Date.ISO8601FormatStyle().parse(value)
    }
}

private struct ProfileDay: Hashable, Comparable, Sendable {
    let year: Int
    let month: Int
    let day: Int

    init?(_ value: String) {
        let bytes = Array(value.utf8)
        guard bytes.count == 10,
              bytes[4] == 45,
              bytes[7] == 45,
              bytes.enumerated().allSatisfy({ index, byte in
                  index == 4 || index == 7 || (48 ... 57).contains(byte)
              })
        else { return nil }

        year = Int(value.prefix(4)) ?? 0
        month = Int(value.dropFirst(5).prefix(2)) ?? 0
        day = Int(value.suffix(2)) ?? 0

        var validationCalendar = Calendar(identifier: .gregorian)
        validationCalendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let components = DateComponents(year: year, month: month, day: day)
        guard let date = validationCalendar.date(from: components) else { return nil }
        let roundTrip = validationCalendar.dateComponents([.year, .month, .day], from: date)
        guard roundTrip.year == year, roundTrip.month == month, roundTrip.day == day else {
            return nil
        }
    }

    func date(in calendar: Calendar) -> Date? {
        let components = DateComponents(
            calendar: calendar,
            timeZone: calendar.timeZone,
            year: year,
            month: month,
            day: day
        )
        guard let date = calendar.date(from: components) else { return nil }
        let roundTrip = calendar.dateComponents([.year, .month, .day], from: date)
        guard roundTrip.year == year, roundTrip.month == month, roundTrip.day == day else {
            return nil
        }
        return date
    }

    static func < (lhs: ProfileDay, rhs: ProfileDay) -> Bool {
        (lhs.year, lhs.month, lhs.day) < (rhs.year, rhs.month, rhs.day)
    }
}

private struct ProfileResponseDTO: Decodable {
    let stats: ProfileStatsDTO
    let metadata: ProfileMetadataDTO
}

private struct ProfileStatsDTO: Decodable {
    let lifetimeTokens: Int64
    let dailyUsageBuckets: [ProfileDailyBucketDTO]

    private enum CodingKeys: String, CodingKey {
        case lifetimeTokens = "lifetime_tokens"
        case dailyUsageBuckets = "daily_usage_buckets"
    }
}

private struct ProfileDailyBucketDTO: Decodable {
    let startDate: String
    let tokens: Int64

    private enum CodingKeys: String, CodingKey {
        case startDate = "start_date"
        case tokens
    }
}

private struct ProfileMetadataDTO: Decodable {
    let generatedAt: String
    let statsAsOf: String

    private enum CodingKeys: String, CodingKey {
        case generatedAt = "generated_at"
        case statsAsOf = "stats_as_of"
        case statsError = "stats_error"
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        generatedAt = try container.decode(String.self, forKey: .generatedAt)
        statsAsOf = try container.decode(String.self, forKey: .statsAsOf)
        guard container.contains(.statsError), try container.decodeNil(forKey: .statsError) else {
            throw DecodingError.dataCorruptedError(
                forKey: .statsError,
                in: container,
                debugDescription: "Profile statistics are unavailable"
            )
        }
    }
}

private final class BoundedProfileRequestLoader: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let maximumSize: Int
    private let lock = NSLock()
    private var continuation: CheckedContinuation<ProfileHTTPResponse, any Error>?
    private var response: HTTPURLResponse?
    private var receivedData = Data()
    private var session: URLSession?
    private var task: URLSessionDataTask?
    private var completed = false
    private var cancelled = false

    init(maximumSize: Int) {
        self.maximumSize = maximumSize
    }

    func load(_ request: URLRequest) async throws -> ProfileHTTPResponse {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                let configuration = URLSessionConfiguration.ephemeral
                configuration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
                configuration.urlCache = nil
                configuration.httpCookieStorage = nil
                configuration.httpShouldSetCookies = false
                configuration.urlCredentialStorage = nil
                configuration.timeoutIntervalForRequest = 20
                configuration.timeoutIntervalForResource = 30

                let session = URLSession(
                    configuration: configuration,
                    delegate: self,
                    delegateQueue: nil
                )
                let task = session.dataTask(with: request)

                lock.lock()
                if cancelled {
                    completed = true
                    lock.unlock()
                    session.invalidateAndCancel()
                    continuation.resume(throwing: CancellationError())
                    return
                }
                self.continuation = continuation
                self.session = session
                self.task = task
                lock.unlock()
                task.resume()
            }
        } onCancel: {
            self.cancel()
        }
    }

    func urlSession(
        _: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection _: HTTPURLResponse,
        newRequest _: URLRequest,
        completionHandler: @escaping (URLRequest?) -> Void
    ) {
        completionHandler(nil)
        finish(.failure(ProfileUsageError.redirectRejected))
        task.cancel()
    }

    func urlSession(
        _: URLSession,
        dataTask: URLSessionDataTask,
        didReceive response: URLResponse,
        completionHandler: @escaping (URLSession.ResponseDisposition) -> Void
    ) {
        guard let httpResponse = response as? HTTPURLResponse else {
            completionHandler(.cancel)
            finish(.failure(ProfileUsageError.invalidHTTPResponse))
            dataTask.cancel()
            return
        }
        if response.expectedContentLength > Int64(maximumSize) {
            completionHandler(.cancel)
            finish(.failure(ProfileUsageError.responseTooLarge))
            dataTask.cancel()
            return
        }

        lock.lock()
        self.response = httpResponse
        lock.unlock()
        completionHandler(.allow)
    }

    func urlSession(_: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        lock.lock()
        let wouldExceedLimit = data.count > maximumSize - receivedData.count
        if !wouldExceedLimit {
            receivedData.append(data)
        }
        lock.unlock()

        if wouldExceedLimit {
            finish(.failure(ProfileUsageError.responseTooLarge))
            dataTask.cancel()
        }
    }

    func urlSession(
        _: URLSession,
        task _: URLSessionTask,
        didCompleteWithError error: (any Error)?
    ) {
        if error != nil {
            finish(.failure(ProfileUsageError.transportFailure))
            return
        }

        lock.lock()
        let response = self.response
        let data = receivedData
        lock.unlock()
        guard let response else {
            finish(.failure(ProfileUsageError.invalidHTTPResponse))
            return
        }
        finish(.success(ProfileHTTPResponse(data: data, response: response)))
    }

    private func cancel() {
        lock.lock()
        cancelled = true
        let task = self.task
        lock.unlock()
        finish(.failure(CancellationError()))
        task?.cancel()
    }

    private func finish(_ result: Result<ProfileHTTPResponse, any Error>) {
        lock.lock()
        guard !completed else {
            lock.unlock()
            return
        }
        completed = true
        let continuation = self.continuation
        self.continuation = nil
        let session = self.session
        self.session = nil
        self.task = nil
        lock.unlock()

        session?.invalidateAndCancel()
        continuation?.resume(with: result)
    }
}
