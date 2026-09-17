#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
project_root=${script_dir:h}
source "${project_root}/Config/Release.env"

release_version=${CODERIM_VERSION:-${MARKETING_VERSION}}
release_build=${CODERIM_BUILD_NUMBER:-${BUILD_NUMBER}}
release_tag=${CODERIM_RELEASE_TAG:-"v${release_version}"}
release_repository=${CODERIM_RELEASE_REPOSITORY:-${RELEASE_REPOSITORY}}
update_feed_branch=${CODERIM_UPDATE_FEED_BRANCH:-${UPDATE_FEED_BRANCH}}
require_release_tag=${CODERIM_REQUIRE_RELEASE_TAG:-0}
require_public_repository=${CODERIM_REQUIRE_PUBLIC_REPOSITORY:-0}
release_notes_path="${project_root}/Documentation/ReleaseNotes/${release_version}.md"

fail() {
  print -u2 "Release context is invalid: $1"
  exit 1
}

if ! print -r -- "${release_version}" \
    | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$'; then
  fail "unsupported version '${release_version}'"
fi
if ! print -r -- "${release_build}" | grep -Eq '^[1-9][0-9]*$'; then
  fail "build number must be a positive integer"
fi
if [[ "${release_tag}" != "v${release_version}" ]]; then
  fail "tag '${release_tag}' does not match version '${release_version}'"
fi
if ! print -r -- "${release_repository}" \
    | grep -Eq '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$'; then
  fail "release repository must use owner/name form"
fi
if ! git check-ref-format --branch "${update_feed_branch}" >/dev/null 2>&1; then
  fail "update feed branch is not a valid Git branch name"
fi
if [[ ! -f "${release_notes_path}" ]]; then
  fail "release notes are missing: ${release_notes_path}"
fi
if [[ "${require_release_tag}" == "1" ]]; then
  if ! git -C "${project_root}" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    fail "public release source is not a Git worktree"
  fi
  if [[ -n "$(git -C "${project_root}" status --porcelain --untracked-files=normal)" ]]; then
    fail "public releases require a clean worktree"
  fi
  head_commit=$(git -C "${project_root}" rev-parse HEAD)
  if ! tag_commit=$(git -C "${project_root}" rev-parse "${release_tag}^{commit}" 2>/dev/null); then
    fail "tag '${release_tag}' does not exist"
  fi
  if [[ "${tag_commit}" != "${head_commit}" ]]; then
    fail "tag '${release_tag}' does not point to HEAD"
  fi
fi

if [[ "${require_public_repository}" == "1" ]]; then
  if ! command -v gh >/dev/null 2>&1; then
    fail "GitHub CLI is required to verify release visibility"
  fi
  if ! visibility=$(gh repo view "${release_repository}" --json visibility --jq '.visibility' 2>/dev/null); then
    fail "release repository '${release_repository}' is not accessible"
  fi
  if [[ "${visibility}" != "PUBLIC" ]]; then
    fail "release repository '${release_repository}' is not public"
  fi
fi

print "Verified release context ${release_tag} in ${release_repository}"
