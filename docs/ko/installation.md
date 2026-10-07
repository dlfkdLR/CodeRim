# CodeRim 설치

[English](../installation.md) · **한국어**

**macOS 14 이상 · Apple silicon 및 Intel.**

[![macOS 다운로드](../../Assets/README/download-macos.svg)](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.20/CodeRim-2.1.20.dmg)

[전체 릴리스](https://github.com/dlfkdLR/CodeRim/releases/latest) · [변경 이력](../../CHANGELOG.md)

Homebrew로 설치할 수도 있습니다.

```sh
brew tap dlfkdLR/tap &&
brew install --cask dlfkdLR/tap/coderim
```

`brew tap`을 명시하면 CodeRim 저장소를 등록한 적 없는 Mac에서도 설치할 수 있습니다. 여전히 cask를 찾지 못하면 [복구 안내](troubleshooting.md#homebrew-cannot-find-the-coderim-cask)를 따릅니다.

앱은 **ad-hoc 서명되어 있으며 Apple 공증을 받지 않았습니다**. Homebrew는 다운로드 체크섬을 검증합니다. 자동 업데이트는 Sparkle 서명을 사용합니다.

<a id="direct-download-and-macos-first-launch-help"></a>
## 직접 다운로드와 macOS 첫 실행

DMG와 [SHA256SUMS.txt](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.20/SHA256SUMS.txt)를 같은 폴더에 저장하고 검증합니다.

```sh
cd ~/Downloads
grep ' CodeRim-2.1.20.dmg$' SHA256SUMS.txt | shasum -a 256 -c -
```

체크섬 결과가 `OK`이면 DMG를 열고 CodeRim을 Applications에 넣습니다. 검증한 앱을 macOS가 차단하면 **CodeRim에만** 격리 속성을 제거한 뒤 실행합니다.

```sh
xattr -dr com.apple.quarantine /Applications/CodeRim.app
open /Applications/CodeRim.app
```

검증한 Homebrew 설치 후에도 같은 방법을 사용할 수 있습니다.

## CodexMeter에서 이동

기존 Homebrew 설치는 다음과 같이 업데이트합니다.

```sh
brew update &&
brew tap dlfkdLR/tap &&
brew upgrade --cask --greedy dlfkdLR/tap/coderim
```

`It seems the App source '/Applications/CodexMeter.app' is not there` 오류가 나거나 최신 버전이라고 나오는데 앱 이름이 그대로이면, 실행 중인 앱을 종료하고 설치를 복구합니다.

```sh
brew update &&
brew tap dlfkdLR/tap &&
HOMEBREW_NO_INSTALL_CLEANUP=1 brew reinstall --cask --force dlfkdLR/tap/coderim
```

기존 앱이 없어도 최신 릴리스를 `CodeRim.app`으로 다시 설치하고 Homebrew 설치 기록을 갱신합니다. `--force`는 Homebrew의 앱 경로에 있는 기존 `CodeRim.app`도 교체하므로 실행 전에 해당 복사본을 확인합니다. 사용자 지정 `--appdir`을 사용하면 위 실행 명령의 `/Applications`를 그 경로로 바꿉니다.

설정, 사용량 히스토리, 저장한 계정을 유지합니다. `--zap`을 추가하거나 CodexMeter Application Support 폴더를 삭제하지 마세요. 설치 후 `CodeRim.app`을 다시 실행하며 필요하면 위의 검증 후 첫 실행 안내를 따릅니다. [이름 변경 상세](../../Documentation/REBRANDING.ko.md) · [Homebrew 문제 해결](troubleshooting.md#homebrew-upgrade-cannot-find-codexmeterapp).

[다음: 시작하기](getting-started.md) · [문서](README.md)
## Windows 11

현재 설치 파일은 [2.1.20 x64](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.20/CodeRim-Windows-2.1.20-x64-Setup.msi)와 [2.1.20 ARM64](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.20/CodeRim-Windows-2.1.20-arm64-Setup.msi)입니다. [Windows 설치와 업데이트](windows.md)를 참고합니다.
