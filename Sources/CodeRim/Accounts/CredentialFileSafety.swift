import Darwin
import Foundation

/// Whether a credential file or its directory is private to its owner.
///
/// Mode bits alone do not answer that on macOS. A file can read `-rw-------`
/// and still carry an extended ACL granting another principal write access —
/// `stat` never sees it, so a check like `st_mode & 0o077 == 0` calls it
/// private when it is not:
///
///     mode bits: -rw-------      (looks private)
///      0: group:everyone allow write
///
/// So the mode check is necessary and not sufficient, and every place that
/// opens one of these files asks this as well.
enum CredentialFileSafety {
    /// True when an extended ACL grants something to somebody.
    ///
    /// Only *allow* entries count. macOS puts `deny` entries on files
    /// routinely — `group:everyone deny delete` is the common one — and those
    /// are more restrictive than the mode bits, not less. Refusing them would
    /// reject files that are safer than the ones we accept.
    ///
    /// Takes a descriptor, never a path: the callers `openat` first precisely
    /// so that what they validate is what they read, and re-opening by name
    /// here would put the race back.
    static func hasPermissiveACL(fileDescriptor: Int32) -> Bool {
        guard let acl = acl_get_fd_np(fileDescriptor, ACL_TYPE_EXTENDED) else {
            // No ACL at all is the ordinary case; a real failure to read one is
            // reported as an ACL we cannot vouch for, below.
            return false
        }
        defer { acl_free(UnsafeMutableRawPointer(acl)) }

        var entry: acl_entry_t?
        var identifier = ACL_FIRST_ENTRY.rawValue
        while acl_get_entry(acl, Int32(identifier), &entry) == 0 {
            identifier = ACL_NEXT_ENTRY.rawValue
            guard let entry else { continue }
            var tag = acl_tag_t(0)
            // An entry whose type will not read is an entry we cannot clear.
            guard acl_get_tag_type(entry, &tag) == 0 else { return true }
            if tag == ACL_EXTENDED_ALLOW { return true }
        }
        return false
    }
}
