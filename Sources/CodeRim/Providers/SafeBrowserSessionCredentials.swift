import Foundation
import CryptoKit

/// Typed values never merge tokens from different origins, profiles, or conflicting JSON fields.
struct SafeBrowserSessionCredential: Sendable, Equatable {
    let profileID: String
    let profileURL: URL
    let origin: String
    let token: String?
    let refreshToken: String?
    let groupID: String?
    var scope: String {
        SHA256.hash(data: Data([profileURL.path, origin, token ?? "", refreshToken ?? "", groupID ?? ""].joined(separator: "\0").utf8))
            .map { String(format: "%02x", $0) }.joined()
    }
}

enum SafeBrowserSessionCredentials {
    static let deepSeekKeys = ["userToken"]
    static let factoryKeys = ["workos:access-token", "workos:refresh-token"]
    static let tokenFields = ["access_token", "accessToken", "id_token", "idToken", "token", "authToken", "authorization", "bearer"]
    static let groupFields = ["group_id", "groupId", "groupID", "GroupID", "gid"]
    static let miniMaxKeys = tokenFields + ["user_detail", "persist:root", "group_id", "groupId", "groupID"]

    static func origins(_ provider: String, cn: Bool = false) -> [String] {
        switch provider {
        case "deepseek": ["https://platform.deepseek.com"]
        case "factory": ["https://app.factory.ai", "https://auth.factory.ai"]
        case "minimax": ["https://platform.", "https://www.", "https://"].map { $0 + (cn ? "minimaxi.com" : "minimax.io") }
        default: []
        }
    }
    static func token(_ raw: String) throws -> String {
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard (20...32768).contains(text.utf8.count), text.utf8.allSatisfy({ (33...126).contains($0) && ![34, 39, 92].contains($0) }) else { throw LocalStorageReadError.invalid }
        return text
    }
    static func merge(_ a: String?, _ b: String?) throws -> String? {
        if let a, let b, a != b { throw LocalStorageReadError.invalid }; return b ?? a
    }
    static func unquote(_ raw: String) throws -> String {
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.hasPrefix("\"") {
            guard case let .string(value) = try StorageJSON.parse(text) else { throw LocalStorageReadError.invalid }; return value
        }
        return text
    }
    static func deepSeekToken(_ raw: String) throws -> String {
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard text.utf8.count <= 65536 else { throw LocalStorageReadError.limit }
        if text.hasPrefix("{") || text.hasPrefix("[") || text.hasPrefix("\"") {
            let value = try StorageJSON.parse(text)
            if case let .string(value) = value { return try token(value) }
            guard case let .object(fields) = value else { throw LocalStorageReadError.invalid }
            var result: String?
            for key in ["value", "token", "access_token", "accessToken", "userToken"] {
                guard let field = fields[key] else { continue }
                guard case let .string(text) = field else { throw LocalStorageReadError.invalid }
                result = try merge(result, token(text))
            }
            guard let result else { throw LocalStorageReadError.missing }; return result
        }
        return try token(text.hasPrefix("'") && text.hasSuffix("'") ? String(text.dropFirst().dropLast()) : text)
    }
    static func identifier(_ text: String) throws -> String {
        guard (1...256).contains(text.utf8.count), text.utf8.allSatisfy({ (48...57).contains($0) || (65...90).contains($0) || (97...122).contains($0) || $0 == 45 || $0 == 95 }) else { throw LocalStorageReadError.invalid }; return text
    }
    static func group(_ node: StorageJSON) throws -> String {
        switch node {
        case .string(let text): return try identifier(text)
        case .number(let text): guard let value = UInt64(text) else { throw LocalStorageReadError.invalid }; return String(value)
        default: throw LocalStorageReadError.invalid
        }
    }
    static func jwtClaims(_ token: String) throws -> [String: StorageJSON]? {
        let parts = token.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { return nil }
        let payload = String(parts[1])
        guard !payload.isEmpty, payload.utf8.allSatisfy({ (48...57).contains($0) || (65...90).contains($0) || (97...122).contains($0) || $0 == 45 || $0 == 95 }) else { throw LocalStorageReadError.invalid }
        var encoded = payload.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        encoded += String(repeating: "=", count: (4 - encoded.utf8.count % 4) % 4)
        guard let bytes = Data(base64Encoded: encoded), let json = String(data: bytes, encoding: .utf8),
              case let .object(claims) = try StorageJSON.parse(json) else { throw LocalStorageReadError.invalid }
        return claims
    }
    static func miniMaxFields(_ values: [String: String]) throws -> (token: String?, group: String?) {
        var access: String?, groupID: String?
        func visit(_ node: StorageJSON, depth: Int) throws {
            guard depth <= 4, case let .object(fields) = node else { throw LocalStorageReadError.invalid }
            for (key, value) in fields {
                if tokenFields.contains(key) {
                    guard case let .string(raw) = value else { throw LocalStorageReadError.invalid }
                    access = try merge(access, token(raw))
                } else if groupFields.contains(key) { groupID = try merge(groupID, group(value)) }
                else if ["user_detail", "user", "auth"].contains(key) {
                    if case let .string(raw) = value { try visit(StorageJSON.parse(raw), depth: depth + 1) }
                    else { try visit(value, depth: depth + 1) }
                }
            }
        }
        for (key, value) in values {
            if tokenFields.contains(key) { access = try merge(access, token(unquote(value))) }
            else if groupFields.contains(key) { groupID = try merge(groupID, identifier(unquote(value))) }
            else if ["user_detail", "persist:root"].contains(key) { try visit(StorageJSON.parse(value), depth: 0) }
        }
        if let access, let claims = try jwtClaims(access) {
            for key in groupFields { if let field = claims[key] { groupID = try merge(groupID, group(field)) } }
        }
        return (access, groupID)
    }
    static func credential(provider: String, profileID: String, profileURL: URL, origin: String,
                           values: [String: String], cn: Bool = false) throws -> SafeBrowserSessionCredential? {
        guard origins(provider, cn: cn).contains(origin) else { throw LocalStorageReadError.unsupported }
        var access: String?, refresh: String?, groupID: String?
        switch provider {
        case "deepseek": if let value = values["userToken"] { access = try deepSeekToken(value) }
        case "factory":
            if let value = values["workos:access-token"] { access = try token(unquote(value)) }
            if let value = values["workos:refresh-token"] { refresh = try token(unquote(value)) }
            if let access, let claims = try jwtClaims(access), let value = claims["org_id"] { groupID = try group(value) }
        case "minimax": (access, groupID) = try miniMaxFields(values)
        default: throw LocalStorageReadError.unsupported
        }
        guard access != nil || refresh != nil || groupID != nil else { return nil }
        return SafeBrowserSessionCredential(profileID: profileID, profileURL: profileURL, origin: origin,
            token: access, refreshToken: refresh, groupID: groupID)
    }
}

/// Exact keys and numeric source text are retained; Foundation keyed containers would hide duplicate keys.
indirect enum StorageJSON: Sendable, Equatable {
    case object([String: StorageJSON]), array([StorageJSON]), string(String), number(String), bool(Bool), null
    static func parse(_ text: String) throws -> StorageJSON {
        guard text.utf8.count <= 65536 else { throw LocalStorageReadError.limit }
        let parser = Parser(Array(text.utf8)); let value = try parser.value(0); parser.space()
        guard parser.at == parser.bytes.count else { throw LocalStorageReadError.invalid }; return value
    }
    private final class Parser {
        let bytes: [UInt8]; var at = 0, nodes = 0
        init(_ bytes: [UInt8]) { self.bytes = bytes }
        func space() { while at < bytes.count && [9, 10, 13, 32].contains(bytes[at]) { at += 1 } }
        func consume(_ byte: UInt8) -> Bool { space(); if at < bytes.count && bytes[at] == byte { at += 1; return true }; return false }
        func string() throws -> String {
            space(); let start = at
            guard at < bytes.count, bytes[at] == 34 else { throw LocalStorageReadError.invalid }; at += 1
            while at < bytes.count {
                let c = bytes[at]; at += 1
                if c == 34 {
                    guard let value = try JSONSerialization.jsonObject(with: Data(bytes[start..<at]), options: .fragmentsAllowed) as? String else { throw LocalStorageReadError.invalid }; return value
                }
                if c == 92 { guard at < bytes.count else { throw LocalStorageReadError.invalid }; at += 1 }
                else if c < 32 { throw LocalStorageReadError.invalid }
            }
            throw LocalStorageReadError.invalid
        }
        func value(_ depth: Int) throws -> StorageJSON {
            try Task.checkCancellation(); nodes += 1
            guard depth <= 8, nodes <= 2048 else { throw LocalStorageReadError.limit }; space()
            guard at < bytes.count else { throw LocalStorageReadError.invalid }
            if bytes[at] == 34 { return .string(try string()) }
            if consume(123) {
                var values: [String: StorageJSON] = [:]
                if consume(125) { return .object(values) }
                repeat {
                    let key = try string(); guard consume(58), values[key] == nil else { throw LocalStorageReadError.invalid }
                    values[key] = try value(depth + 1)
                    if consume(125) { return .object(values) }
                } while consume(44)
                throw LocalStorageReadError.invalid
            }
            if consume(91) {
                var values: [StorageJSON] = []
                if consume(93) { return .array(values) }
                repeat {
                    guard values.count < 256 else { throw LocalStorageReadError.limit }; values.append(try value(depth + 1))
                    if consume(93) { return .array(values) }
                } while consume(44)
                throw LocalStorageReadError.invalid
            }
            let start = at
            while at < bytes.count && ![9, 10, 13, 32, 44, 93, 125].contains(bytes[at]) { at += 1 }
            guard at > start, let literal = String(bytes: bytes[start..<at], encoding: .utf8) else { throw LocalStorageReadError.invalid }
            switch literal {
            case "true": return .bool(true)
            case "false": return .bool(false)
            case "null": return .null
            default:
                _ = try JSONSerialization.jsonObject(with: Data(bytes[start..<at]), options: .fragmentsAllowed)
                guard literal.first == "-" || literal.first?.isNumber == true else { throw LocalStorageReadError.invalid }
                return .number(literal)
            }
        }
    }
}
