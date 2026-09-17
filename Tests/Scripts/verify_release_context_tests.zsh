#!/bin/zsh
set -euo pipefail

test_dir=${0:A:h}
project_root=${test_dir:h:h}
source_script="${project_root}/Scripts/verify_release_context.sh"
fixture_root=$(mktemp -d)
trap 'rm -rf "${fixture_root}"' EXIT

mkdir -p \
  "${fixture_root}/Config" \
  "${fixture_root}/Documentation/ReleaseNotes" \
  "${fixture_root}/Scripts" \
  "${fixture_root}/bin"
cp "${source_script}" "${fixture_root}/Scripts/verify_release_context.sh"
script_under_test="${fixture_root}/Scripts/verify_release_context.sh"
{
  print 'PRODUCT_NAME=CodeRim'
  print 'MARKETING_VERSION=1.2.3'
  print 'BUILD_NUMBER=7'
  print 'BUNDLE_IDENTIFIER=dev.codexmeter.CodexMeter'
  print 'MINIMUM_SYSTEM_VERSION=14.0'
  print 'SPARKLE_ACCOUNT=Test'
  print 'SPARKLE_PUBLIC_KEY=test-key'
  print 'RELEASE_REPOSITORY=Example/CodeRim-Releases'
  print 'UPDATE_FEED_BRANCH=update-feed'
} > "${fixture_root}/Config/Release.env"
print '# CodeRim 1.2.3' \
  > "${fixture_root}/Documentation/ReleaseNotes/1.2.3.md"
{
  print '#!/bin/zsh'
  print 'print -r -- "${FAKE_VISIBILITY:-PUBLIC}"'
} > "${fixture_root}/bin/gh"
chmod 755 "${fixture_root}/bin/gh"

git -C "${fixture_root}" init -q -b main
git -C "${fixture_root}" config user.name 'CodeRim Tests'
git -C "${fixture_root}" config user.email 'tests@coderim.dev'
git -C "${fixture_root}" add Config Documentation Scripts bin
git -C "${fixture_root}" commit -q -m 'test fixture'
git -C "${fixture_root}" tag v1.2.3

common_environment=(
  "PATH=${fixture_root}/bin:${PATH}"
  'CODERIM_REQUIRE_RELEASE_TAG=1'
  'CODERIM_REQUIRE_PUBLIC_REPOSITORY=1'
)

env "${common_environment[@]}" FAKE_VISIBILITY=PUBLIC "${script_under_test}"

if env "${common_environment[@]}" FAKE_VISIBILITY=PRIVATE \
    "${script_under_test}" >/dev/null 2>&1; then
  print -u2 'Expected a private release repository to be rejected.'
  exit 1
fi

print 'dirty' > "${fixture_root}/untracked.txt"
if env "${common_environment[@]}" FAKE_VISIBILITY=PUBLIC \
    "${script_under_test}" >/dev/null 2>&1; then
  print -u2 'Expected a dirty release worktree to be rejected.'
  exit 1
fi
rm -f "${fixture_root}/untracked.txt"

print 'post-tag change' > "${fixture_root}/post-tag.txt"
git -C "${fixture_root}" add post-tag.txt
git -C "${fixture_root}" commit -q -m 'move past tag'
if env "${common_environment[@]}" FAKE_VISIBILITY=PUBLIC \
    "${script_under_test}" >/dev/null 2>&1; then
  print -u2 'Expected a release tag that does not point to HEAD to be rejected.'
  exit 1
fi

print 'Release-context tests passed.'
