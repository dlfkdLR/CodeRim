# CodeRim 빌드와 릴리스

[English](RELEASING.md) · **한국어**

## 기여자 검증

릴리스 전에 변경 코드에 맞는 검사를 실행합니다. 문서 변경은 `python3 Scripts/validate_docs.py`를 사용하며 서명·게시가 필요하지 않습니다. CI 소스 검사·빌드는 `.github/workflows/ci.yml`·`.github/workflows/windows.yml`에 있습니다.

```sh
swift test
swift build --product CodeRimCLI
python3 Tests/Scripts/companion_cli_tests.py .build/debug/CodeRimCLI
for test_script in Tests/Scripts/*_tests.zsh; do "$test_script"; done
Scripts/build_release.sh
```

`Scripts/build_release.sh`는 도우미·확장·프레임워크·리소스가 있는 ad-hoc Universal 2 앱을 빌드합니다. 적절한 Apple 빌드 도구가 필요하지만 서명 인증서는 필요하지 않습니다. `Scripts/release.sh`는 `CODE_SIGN_IDENTITY`·`CODE_SIGN_TEAM_ID`가 필요한 인증서 경로이며 모든 기여 PR의 전제 조건이 아닙니다. 빌드는 공증·설치·릴리스가 아닙니다.

## 인증서 없는 macOS 안정판 검증

유지관리자는 깨끗한 불변 태그 작업 폴더, 소스 release·update-feed·Homebrew tap 접근, 설정 계정 `HechoLP`의 로그인 Keychain Sparkle Ed25519 개인 키가 필요합니다. 개인 키, 인증서, Apple 자격 증명, 공증 프로필을 커밋하지 않습니다. 메타데이터에는 공개 키만 넣습니다.

```sh
Scripts/release_stable.sh
```

`vVERSION`이 정확히 HEAD인 검토된 커밋에서 실행합니다. 태그, 깨끗한 폴더, 릴리스 노트, 저장소, 빌드·패키지 서명, Universal 2, 메타데이터, entitlement·전송, 아카이브, 바이트 수, URL, 체크섬을 확인합니다. ZIP·DMG, 개별 SHA-256, `SHA256SUMS.txt`, 서명 appcast를 만듭니다. ad-hoc 빌드는 Apple 공증이 없으며 Hardened Runtime·library validation 동작은 실제 빌드·서명 스크립트를 따릅니다.

`BUILD_NUMBER`는 Sparkle 비교 값이며 증가해야 합니다. 과거 `1.4.10`은 `1410`, `2.0.0`은 `20000`, `2.0.1`은 `20001`, `2.1.0`은 `20100`입니다. 새 버전 숫자를 단순 연결해 더 작은 값을 만들지 않습니다.

1. 필수 CI 통과 후 검토된 릴리스 커밋을 병합하고 정확한 커밋에 태그를 붙입니다. 기존 태그를 다시 쓰지 않습니다.
2. 정확한 macOS 파일을 빌드·검증하고 GitHub 릴리스에 ZIP, DMG, 체크섬, `appcast.xml`을 올립니다.
3. 익명 파일 접근을 확인한 뒤 같은 서명 appcast를 `update-feed`에 게시합니다.
4. 정확한 ZIP 게시 후에만 `dlfkdLR/homebrew-tap`의 `Casks/coderim.rb`를 실제 URL·버전·SHA-256으로 갱신합니다. macOS 14, `auto_updates true`, 신뢰 안내를 유지합니다.
5. tap style·audit·새 설치·제거를 검사하고 설치 버전·build·아키텍처·updater·메뉴 막대·Settings·노치·실제 합계를 확인합니다. 로컬 검사, CI, 설치 UI, 게시는 별도 증거입니다.

공개 기본 저장소는 `dlfkdLR/CodeRim`, feed는 `update-feed`입니다. 다른 대상은 `CODERIM_RELEASE_REPOSITORY`·`CODERIM_UPDATE_FEED_BRANCH`를 의도적으로 함께 설정해야 합니다. 이전 CodexMeter feed·저장소 리다이렉트와 공개 보관된 1.0.4 bridge는 호환 경계입니다. 이전 저장소 삭제·재생성, 공개 파일·태그 다시 쓰기를 하지 않습니다.

## 첫 설치와 선택적 Apple 신뢰

설치 안내는 앱별 격리 해제 전에 SHA-256 검증을 요구합니다. Sparkle Ed25519는 이후 업데이트를 인증합니다. 체크섬·ad-hoc 서명은 첫 다운로드를 Apple 신뢰 앱으로 만들지 않습니다.

```sh
xattr -dr com.apple.quarantine /Applications/CodeRim.app
open /Applications/CodeRim.app
```

향후 Developer ID 경로는 실제 인증서·팀·공증 자격 증명으로 Hardened Runtime, Team ID, 공증, stapling, Gatekeeper를 확인합니다. 별도 승인되는 선택적 경로입니다.

```sh
export CODE_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)"
export CODE_SIGN_TEAM_ID="TEAMID"
export NOTARY_PROFILE="coderim-notary"
Scripts/release_public.sh
```

## Windows 릴리스

Windows MSI 버전은 macOS DMG와 독립적으로 올라갈 수 있습니다. CodeRim 2.1.13은 macOS·Windows 버전을 의도적으로 맞췄지만 이후 플랫폼 릴리스는 다시 달라질 수 있습니다. 실제 공개 파일로 플랫폼별 링크를 갱신합니다. GitHub 전체 최신 릴리스는 macOS 버전의 기준이 아닙니다. x64·ARM64 설치 파일을 별도로 패키징·검사합니다. MSI 서명·manifest는 고정 Ed25519 업데이트 신뢰를 쓰며 Authenticode 게시자 서명은 별개입니다. [Windows 패키징·복구](WINDOWS.ko.md#updates)를 참고합니다.

## Microsoft Store (무료 서명)

Store 패키지는 인증서를 사지 않고도 "알 수 없는 게시자" 경고를 없애고, 스마트 앱 컨트롤이 서명 없는 MSI를 막는 PC에도 설치됩니다. 인증을 통과하면 Microsoft가 패키지에 서명합니다.

1. [Partner Center](https://partner.microsoft.com/dashboard/registration)에서 무료 개인 개발자 계정을 만들고 본인 확인을 마칩니다.
2. **앱 및 게임 → 새 제품 → MSIX 또는 PWA 앱**에서 이름 **CodeRim**을 예약합니다.
3. **제품 관리 → 제품 ID**의 *Package/Identity/Name*, *Package/Identity/Publisher*, *Package/Properties/PublisherDisplayName*을 `Windows/Installer/Store/store-identity.json`에 넣습니다.
4. Windows 워크플로가 통과하면 `CodeRim-Windows-Store` 아티팩트를 받아 `CodeRim-Windows-<버전>.msixbundle`을 새 제출에 올립니다. CI는 테스트 서명한 사본을 x64·ARM64에 설치해 실행 별칭을 확인하고 제거합니다. 호스팅 러너는 패키지 앱을 활성화하지 못하므로 패키지 안에서의 실제 실행은 인증 과정에서 처음 검증됩니다.
5. **제출 옵션 → 제한된 기능**에 다음과 같이 설명합니다. *runFullTrust* — 데스크톱 WPF 앱. *unvirtualizedResources* — 모니터링하는 CLI 도구의 설정(예: Claude Code 상태줄)을 읽고 고치며, MSI 설치와 마찬가지로 자체 CLI와 데이터 폴더를 공유합니다.
6. 개인정보 처리방침 URL은 `https://github.com/dlfkdLR/CodeRim/blob/main/PRIVACY.md`, 범주는 개발자 도구, 무료로 지정합니다.

Store 버전은 앱 내 MSI 업데이트를 실행하지 않습니다. 이후 버전은 번들을 새로 제출합니다.

## Rollback

승인된 범위에서 문제 릴리스를 철회하고 이전 서명 appcast를 복원하며 데이터베이스 호환성을 설명합니다. 공개된 이전 버전을 재빌드하거나 태그를 이동하지 않고 새 패치를 만듭니다. 릴리스·push·병합·tap 기록·설치·배포는 각각 사용자 승인 범위 안에서 진행합니다.
