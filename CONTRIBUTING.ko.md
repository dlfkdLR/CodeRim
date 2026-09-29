# 기여

[English](CONTRIBUTING.md) · **한국어**

CodeRim은 집계 정확성, 로컬 개인정보, 기본 동작, 기존 [디자인 기준](DESIGN.ko.md)을 유지하는 작고 검토 가능한 변경을 지향합니다.

macOS 코드 변경은 영향받는 파이프라인에 맞는 검사를 실행합니다. 저장소 CI는 다음 폭넓은 검사도 실행합니다.

```sh
python3 Scripts/prepare_swift_dependencies.py
python3 Tests/Scripts/swift_dependency_patch_tests.py
python3 Scripts/prepare_swift_dependencies.py --run-swift test -Xswiftc -warnings-as-errors
python3 Scripts/prepare_swift_dependencies.py --run-swift build --product CodeRimCLI -Xswiftc -warnings-as-errors
product_directory=$(python3 Scripts/prepare_swift_dependencies.py --show-bin-path -c debug)
python3 Tests/Scripts/companion_cli_tests.py "${product_directory}/CodeRimCLI"
for test_script in Tests/Scripts/*_tests.zsh; do "$test_script"; done
Scripts/build_release.sh
```

준비 명령은 `Config/DependencyPatches`의 검토된 CodexBar Swift·C 경고 수정을 전용 `.build/coderim-build` 안에만 적용하고, 고정 revision과 SwiftPM repository 연결을 유지합니다. 래퍼는 준비·빌드·테스트·사후 소스 해시 및 `Package.resolved` 확인까지 같은 잠금을 유지합니다. CI와 릴리스 빌드는 Swift 컴파일 경고가 있으면 실패합니다. 기존 수동 빌드, editable override, 전역 SwiftPM 캐시는 보존합니다. 별도 `--scratch-path`는 `--run-swift` 앞에 지정하고, 도구 버전에 따른 제품 위치는 `--show-bin-path`로 확인합니다.

C 패치는 고정된 QuickJS의 정수 변환을 명시적으로 표현합니다. 회귀 검사는
ARM64·x86_64의 생성 어셈블리를 비교하고 숫자·Unicode·큰 배열 유사 객체
인덱스의 대표 사례를 실행합니다. 이는 기존 동작 보존의 근거이며, 모든
범용 QuickJS API의 정수 안전성을 증명하지는 않습니다.

기록된 패치가 바뀌면 이전 기록 파일이 있는 빌드 디렉터리의 사용을 거부합니다.
`--scratch-path`에 새 빈 디렉터리를 지정하고, Python 회귀 검사의
`CODERIM_DEPENDENCY_TEST_SCRATCH`도 같은 경로로 설정합니다. 기존 checkout과
기록 파일은 유지합니다. 릴리스 빌드는 기본적으로 새 디렉터리를 사용합니다.
`CODERIM_SWIFT_SCRATCH_PATH`를 직접 지정한다면 현재 패치와 일치해야 합니다.

문서만 변경하면 `python3 Scripts/validate_docs.py`를 실행합니다. 영어 기본 경로와 대응 한국어 탐색, 제공업체 ID, 명령어, 코드 블록, 버전·파일, 원본 제한을 맞춥니다. 날짜별 릴리스·감사 기록의 사실은 유지합니다. Windows는 [Windows 문서](Documentation/WINDOWS.ko.md)의 기본 .NET 테스트·아키텍처별 workflow를 따릅니다.

`Scripts/release.sh`는 Apple 서명 ID·팀이 필요한 유지관리자 경로입니다. 빌드·서명·게시·설치는 별개이며 [릴리스 절차](Documentation/RELEASING.ko.md)를 참고합니다. 일반 PR 검사로 릴리스를 게시하지 않습니다.

파서 fixture는 합성 숫자 데이터만 사용합니다. 실제 로그, 프롬프트, 응답, 소스 내용, 터미널 출력, 자격 증명, 개인 경로를 커밋하지 않습니다. 자동 테스트로 활성 계정을 바꾸지 않습니다. 문제·결과 동작, 영향받는 집계·개인정보 계약, 실제 검증을 설명하고 미확인 네이티브·실계정·CI를 구분합니다. 무관한 작업 파일을 보존하고 불필요하게 같은 검사를 반복하지 않습니다.
