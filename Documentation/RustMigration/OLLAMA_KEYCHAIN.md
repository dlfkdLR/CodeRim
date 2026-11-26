# Ollama key persistence on macOS

Eight separate arm64 macOS processes exercised the v2.1.41 Ollama credential methods and the opt-in Rust key policy against one temporary file-backed keychain. The real Security framework created, reopened, updated, locked and deleted items. Each phase also verified a second synthetic item survived.

| Action | Observed result on macOS 26.6.2 |
| --- | --- |
| Store a new key through v2.1.41 | Lookup returned `errSecItemNotFound`, add succeeded, and the Rust candidate read the original value. |
| Reopen and replace | A new process read through both versions. An explicit environment value took precedence without a store read. The candidate updated the existing item, and v2.1.41 read the replacement. |
| Submit whitespace | The candidate kept the original value and made zero mutation calls. |
| Abandon an edit | The real SwiftUI SecureField received synthetic character input. Closing its temporary AppKit window without submitting made zero mutation calls. |
| Attempt a replacement while locked | Security returned `-25293`. The product reported save failure. After an explicit unlock the old value was readable. |
| Remove and reopen | Deletion succeeded. Both versions subsequently observed `errSecItemNotFound`. The second explicit removal was idempotent. |

The comparison compiles both credential implementations into one fixture executable to keep its file-keychain ACL identity consistent between launches. It verifies that the production `NotchKeychain.swift` source is byte-identical in the two revisions. Four calls from that source are bound to adapters that constrain their target. Add supplies `kSecUseKeychain`. Read, update and delete supply a one-element `kSecMatchSearchList`. The adapters accept only the two fixture services and the expected account, item class and attributes. The generated SwiftUI test field uses the shipped secure input and submit handler. The app's full Settings window and signed-distribution ACL upgrades are outside this check.

The private keychain lives inside a new owner-only temporary directory. The replay checks the file path returned by Security, disables interaction for its process, blocks networking and denies direct filesystem access to the user's Keychains directory. It removes the temporary directory after closing the processes. The code does not call a default-keychain or search-list setter. Apple's file-based implementation excludes private keychain creation from search-list updates and treats explicit keychain/search-list attributes as macOS file-keychain operations. [Apple’s private-keychain selection](https://github.com/apple-oss-distributions/Security/blob/main/OSX/libsecurity_keychain/lib/StorageManager.cpp), [Apple’s item query routing](https://github.com/apple-oss-distributions/Security/blob/main/OSX/libsecurity_keychain/lib/SecItem.cpp).

## Reproduce

Build the candidate's native accounting archive on macOS and pass it to the fixture. The CLI writes only phase names, OS status codes, revision identifiers and store-source hashes to the requested JSON file.

```sh
cargo build --manifest-path Rust/Cargo.toml --locked --release -p coderim-accounting-bridge
python3 Scripts/rust_migration/ollama-keychain/check.py \
  --reference v2.1.41 --candidate HEAD \
  --archive Rust/target/release/libcoderim_accounting_bridge.a \
  --output /tmp/coderim-ollama-persistence.json
```

The file-based keychain management APIs emit macOS SDK deprecation warnings. This test uses them to address an isolated file explicitly. The shipping application's keychain implementation is unchanged.
