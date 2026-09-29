# 문제 해결

[English](../troubleshooting.md) · **한국어**

## 제공업체가 보이지 않음

**Settings → Providers → Add Provider**를 엽니다. 목록은 70개이며 노치·기본 CLI 출력에는 선택한 제공업체만 표시합니다. `coderim providers`로 모든 ID를 확인합니다. Z.ai는 GLM, Factory는 Droid, Gemini CLI는 Gemini입니다. [이름 매핑과 설정](providers.md)을 참고합니다.

## 제공업체 측정값이 없음

[연결 안내](providers.md), 원래 소스 로그인, 플랜, 권한, 엔드포인트, 지역을 확인하고 새로고침합니다. 제공업체를 추가해도 인증되지는 않습니다. 데이터 누락은 사용량 0이 아닙니다. 과금 가능한 AWS Bedrock·Azure OpenAI·일부 Doubao 경로는 모니터링 토글을 명시적으로 켜야 합니다. StepFun은 브라우저 가져오기 토글 없이 사용자 이름·비밀번호 또는 Oasis-Token을 사용합니다.

## Claude 한도가 오래됨

Claude 연동을 켜고 로그인된 계정을 추가한 뒤 Claude Code 응답을 완료하면 상태 줄 도우미가 5시간·주간 한도를 전송합니다. 만료된 값은 현재 값이 아닌 마지막 측정값입니다. [Claude 설정](providers/claude.md)을 참고합니다.

## 로컬 히스토리가 없거나 적음

로컬 Codex·Claude Code 세션을 실행한 뒤 **Settings → Usage** 도구 모음의 새로고침 아이콘 또는 **Command-R**을 사용합니다. 삭제된 로그·다른 컴퓨터 기록은 복구할 수 없습니다. **Settings → Providers → Codex 또는 Claude Code → Manage Data**의 **Rebuild Statistics**는 삭제 기준 시각을 유지하며 관측 가능한 로그를 다시 처리합니다. **Clear Local History**는 집계 행만 지우고 새 기준 시각을 기록하며 원본 로그를 유지합니다.

계정 히스토리와 로컬 히스토리는 범위가 다릅니다. CodeRim 2.1.13은 Overview에 날짜가 있는 ChatGPT 계정 합계를 표시할 수 있으며 로컬 차트·Today는 This Mac을 유지합니다. 새 기본 설정은 프로필 히스토리를 켜지만 기존에 저장한 값은 존중하며 별도 Settings 스위치는 노출하지 않습니다. [범위](usage.md)를 참고합니다.

## macOS가 앱을 차단함

[체크섬 검증과 첫 실행 안내](installation.md#direct-download-and-macos-first-launch-help)를 따릅니다. 공개 macOS 앱은 ad-hoc 서명이며 Apple 공증을 받지 않았습니다.

## CLI·위젯이 없거나 오래됨

**Settings → Diagnostics → Install CLI**로 CLI를 설치하고 필요하면 `~/.local/bin`을 PATH에 추가한 뒤 CodeRim을 실행 상태로 둡니다. 위젯은 WidgetKit 일정도 따릅니다. [CLI](cli.md)·[위젯](widgets.md)을 확인합니다.

## 앱 이름·아이콘이 CodexMeter로 남아 있음

이전 Sparkle 업데이트는 `CodexMeter.app` 파일명을 유지할 수 있었습니다. 이전 기능은 Applications에 수동 설치된 기존 번들을 바꾸고 한 번 재시작할 수 있습니다. Homebrew는 설치 기록에 따른 업그레이드 경로를 사용합니다. 설정, 계정, 알림 권한, 호환 저장소 ID는 유지하며 실행 시 기존 CLI 링크를 복구합니다.

설치 후 실행 중인 앱을 종료하고 `/Applications/CodeRim.app`을 다시 엽니다. Settings를 닫는 것만으로 메뉴 막대 앱이 종료되지는 않습니다. 사용자 지정 이름·다른 폴더·기존 대상 앱·쓰기 불가 폴더는 자동 이름 변경을 막을 수 있습니다. 교체 전에 버전을 확인합니다. macOS 알림 아이콘 캐시는 다음 로그인까지 남을 수 있습니다. 권한·시스템 전체 캐시를 초기화하지 마세요. [이전 상세](../../Documentation/REBRANDING.ko.md)를 확인합니다.

<a id="homebrew-cannot-find-the-coderim-cask"></a>
## Homebrew가 CodeRim cask를 찾지 못함

`dlfkdlr/tap/coderim` cask를 찾지 못하면 tap이 등록되지 않았을 수 있습니다. 새 설치는 [설치 안내](installation.md)를 따릅니다. 기존 설치를 복구하려면 CodeRim·CodexMeter를 종료하고 기존 대상 앱을 확인한 뒤 실행합니다.

```sh
brew update &&
brew tap dlfkdLR/tap &&
HOMEBREW_NO_INSTALL_CLEANUP=1 brew reinstall --cask --force dlfkdLR/tap/coderim &&
xattr -dr com.apple.quarantine /Applications/CodeRim.app &&
open /Applications/CodeRim.app
```

Homebrew가 아카이브 SHA-256을 검증한 뒤 이 앱에만 격리 해제 명령을 실행합니다. `--force`는 기존 대상 앱을 교체합니다. 사용자 지정 `--appdir`은 실행 경로도 맞춰야 합니다. 설정·계정·히스토리는 유지하므로 `--zap`을 추가하지 않습니다. 전체 cask 경로로 설치할 때 tap 신뢰 검사를 끌 필요는 없습니다. [Homebrew tap 신뢰](https://docs.brew.sh/Tap-Trust)를 참고합니다.

<a id="homebrew-upgrade-cannot-find-codexmeterapp"></a>
## Homebrew 업그레이드가 CodexMeter.app을 찾지 못함

`It seems the App source '/Applications/CodexMeter.app' is not there`는 기존 설치 기록이 없는 앱을 가리킨다는 뜻입니다. ZIP을 다운로드했다는 사실만으로 업그레이드 성공을 뜻하지는 않습니다. 설치 기록·Application Support 폴더를 지우지 않고 위 재설치 복구를 사용한 뒤 실행한 앱의 버전·위치를 확인합니다.

### xcrun 아키텍처 오류

`libxcrun.dylib`의 `have 'arm64,arm64e', need 'x86_64'`는 별도의 셸·도구 아키텍처 불일치입니다. 다음을 확인합니다.

```sh
uname -m
sysctl -in sysctl.proc_translated 2>/dev/null
brew --prefix
xcode-select -p
```

Apple silicon에서 변환 프로세스 값 `1`은 Rosetta를 뜻합니다. 기본 터미널과 설치된 기본 Homebrew(보통 `/opt/homebrew/bin/brew`)를 사용합니다. Intel·기본 Homebrew는 설치 기록이 별개이므로 경로를 무작정 바꾸거나 삭제하지 않습니다. 실제 Intel Mac은 아키텍처에 맞는 개발 도구가 필요합니다. [Universal DMG](installation.md)는 컴파일이 필요 없습니다.

## 업데이트·로그인 항목·데이터베이스

Launch at Login의 macOS 권한은 **Settings → General → Open Login Items Settings**에서 관리합니다. 패키징된 앱이 필요하며 `swift run`은 같지 않습니다. 로그는 **Settings → Diagnostics → Open Log Folder**에서 봅니다. 디버그 로그는 자격 증명·대화 내용을 제외합니다. 데이터 작업·**Open Data Folder**는 선택한 제공업체 설정에 있습니다.

Sparkle 설치 재시작 오류, 데이터베이스 스키마·크기 제한, 복구는 [기술 문제 해결](../../Documentation/TROUBLESHOOTING.ko.md)을 참고합니다. Windows Setup·업데이트는 [별도 안내](windows.md)가 있습니다.

[문서](README.md)
