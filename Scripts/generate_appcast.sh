#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
project_root=${script_dir:h}
source "${project_root}/Config/Release.env"

release_version=${CODERIM_VERSION:-${MARKETING_VERSION}}
release_tag=${CODERIM_RELEASE_TAG:-"v${release_version}"}
sparkle_account=${CODERIM_SPARKLE_ACCOUNT:-${SPARKLE_ACCOUNT}}
release_repository=${CODERIM_RELEASE_REPOSITORY:-${RELEASE_REPOSITORY}}
artifact_root="${project_root}/Artifacts"
archive_path="${artifact_root}/${PRODUCT_NAME}-${release_version}.zip"
appcast_path="${artifact_root}/appcast.xml"
release_notes_path="${project_root}/Documentation/ReleaseNotes/${release_version}.md"
sparkle_bin_root="${project_root}/.build/artifacts/sparkle/Sparkle/bin"
generate_appcast="${sparkle_bin_root}/generate_appcast"
sign_update="${sparkle_bin_root}/sign_update"

if [[ ! -f "${archive_path}" ]]; then
  print -u2 "Release archive not found: ${archive_path}"
  exit 1
fi
"${script_dir}/verify_release_context.sh"
if [[ ! -x "${generate_appcast}" || ! -x "${sign_update}" ]]; then
  print -u2 "Sparkle signing tools are missing. Run swift package resolve first."
  exit 1
fi

temporary_dir=$(mktemp -d)
trap 'rm -rf "${temporary_dir}"' EXIT
cp "${archive_path}" "${temporary_dir}/"
if [[ -f "${release_notes_path}" ]]; then
  cp "${release_notes_path}" \
    "${temporary_dir}/${PRODUCT_NAME}-${release_version}.md"
fi

download_prefix="https://github.com/${release_repository}/releases/download/${release_tag}/"
release_link="https://github.com/${release_repository}/releases/tag/${release_tag}"
"${generate_appcast}" \
  --account "${sparkle_account}" \
  --download-url-prefix "${download_prefix}" \
  --link "${release_link}" \
  --embed-release-notes \
  --maximum-versions 3 \
  --maximum-deltas 0 \
  -o "${temporary_dir}/appcast.xml" \
  "${temporary_dir}"

generated_appcast="${temporary_dir}/appcast.xml"
xmllint --noout "${generated_appcast}"
"${sign_update}" --account "${sparkle_account}" --verify "${generated_appcast}"

signature=$(xmllint --xpath \
  'string(//*[local-name()="enclosure"]/@*[local-name()="edSignature"])' \
  "${generated_appcast}")
declared_length=$(xmllint --xpath \
  'string(//*[local-name()="enclosure"]/@length)' \
  "${generated_appcast}")
declared_url=$(xmllint --xpath \
  'string(//*[local-name()="enclosure"]/@url)' \
  "${generated_appcast}")
actual_length=$(stat -f '%z' "${archive_path}")
expected_url="${download_prefix}${PRODUCT_NAME}-${release_version}.zip"

if [[ -z "${signature}" || "${declared_length}" != "${actual_length}" \
    || "${declared_url}" != "${expected_url}" ]]; then
  print -u2 "Generated appcast metadata does not match the release archive."
  exit 1
fi
"${sign_update}" --account "${sparkle_account}" --verify \
  "${archive_path}" "${signature}"

install -m 644 "${generated_appcast}" "${appcast_path}"
print "Generated and verified ${appcast_path}"
