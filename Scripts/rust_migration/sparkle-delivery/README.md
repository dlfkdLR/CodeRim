# Replaying the macOS stable update

Run this on macOS with Xcode command-line tools and Python 3.11 or newer. From this checkout, fetch the public release into a temporary directory. The appcast revision below served CodeRim 2.1.41 (20141).

```sh
fixture_dir="$(mktemp -d)"
mkdir "$fixture_dir/cli-src"
feed_revision=4ed84d2b7d98eedd5056c0a4881d2f14f48e7a49
sparkle_revision=ac2def288cbff5cfc7df3ffef6abdf45b72bcb0a
curl -fsSL "https://raw.githubusercontent.com/dlfkdLR/CodeRim/$feed_revision/appcast.xml" -o "$fixture_dir/appcast.xml"
for name in CodeRim-2.1.41.zip SHA256SUMS.txt; do
  curl -fsSL "https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.41/$name" -o "$fixture_dir/$name"
done
curl -fsSL https://github.com/sparkle-project/Sparkle/releases/download/2.9.6/Sparkle-2.9.6.tar.xz -o "$fixture_dir/Sparkle-2.9.6.tar.xz"
for name in main.m SPUCommandLineDriver.h SPUCommandLineDriver.m SPUCommandLineUserDriver.h SPUCommandLineUserDriver.m; do
  curl -fsSL "https://raw.githubusercontent.com/sparkle-project/Sparkle/$sparkle_revision/sparkle-cli/$name" -o "$fixture_dir/cli-src/$name"
done
python3 Scripts/rust_migration/sparkle-delivery/check.py \
  --feed "$fixture_dir/appcast.xml" --archive "$fixture_dir/CodeRim-2.1.41.zip" \
  --checksums "$fixture_dir/SHA256SUMS.txt" --sparkle-archive "$fixture_dir/Sparkle-2.9.6.tar.xz" \
  --cli-source "$fixture_dir/cli-src"
```

The check reads the repository's release configuration and authenticates the signed appcast body and enclosure with CryptoKit Ed25519. It compares the URL, length, versions, published SHA-256, and the archived app's signed-update settings. It verifies the extracted app's nested code signatures and Apple Developer ID requirement. Flipping one bit in either signed object must fail.

The runner verifies the upstream Sparkle 2.9.6 distribution and the five source files before compiling the official `sparkle-cli`. An information-only probe runs against a disposable app with a unique bundle identifier, a temporary preferences directory, and a loopback server. Its packet filter restricts outbound connections to localhost. The fixture signs its own archive and appcasts using an ephemeral seed passed explicitly to `sign_update`. It exercises a newer and an equal version, a modified or unsigned feed, and a signature from a different QA key. Two additional probes replay the exact public feed with its distribution public key. The output records process results and every HTTP path requested. The installed version, downloads, and application-restart behavior require a separate installer test.

The release workflow and production feed receive no writes. The compiled probe, temporary app, archive, and QA keys live in a private temporary directory that the runner removes when it exits. The public downloads remain in `$fixture_dir` for replay.
