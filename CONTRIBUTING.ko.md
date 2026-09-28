# 기여

[English](CONTRIBUTING.md) · **한국어**

CodeRim은 토큰 집계의 정확성과 로컬 개인정보를 유지하는 작고 검토 가능한 변경을 지향합니다.

Pull request를 열기 전에 실행합니다.

```bash
python3 Scripts/prepare_swift_dependencies.py
python3 Tests/Scripts/swift_dependency_patch_tests.py
python3 Scripts/prepare_swift_dependencies.py --run-swift test -Xswiftc -warnings-as-errors
for test_script in Tests/Scripts/*_tests.zsh; do "$test_script"; done
Scripts/build_release.sh
```

준비 명령은 `Package.resolved`의 CodexBar revision을 유지하고,
`Config/DependencyPatches`에 기록된 세 경고 수정을
전용 `.build/coderim-build` 디렉터리의 checkout에 적용합니다.
SwiftPM의 repository 연결을 유지하며 기존 수동 빌드, editable override,
전역 SwiftPM 캐시는 수정하지 않습니다. 기록 파일에는 원본 revision,
패치 해시, 수정된 파일 해시를 저장합니다. 예상과 다른 revision·소스 변경·
수동 override가 있으면 준비를 중단합니다. `--run-swift`는 준비부터 빌드·테스트
종료까지 같은 잠금을 유지하고, 완료 후 고정된 소스와 lockfile을 다시
검증합니다. CI와 릴리스 빌드는 Swift 컴파일 경고가 있으면 실패합니다.
별도 빌드 디렉터리는 `--run-swift` 앞에 `--scratch-path`로 지정합니다.
릴리스 스크립트는 이를 자동으로 처리합니다. Swift 버전에 따른 CLI 테스트
실행 파일 위치는 래퍼의 `--show-bin-path -c debug`로 확인합니다.

실제 Codex 세션 파일을 커밋하지 않습니다. 파서 fixture는 합성 토큰 메타데이터만 사용하며, 프롬프트·응답·소스 코드·터미널 출력·자격 증명을 포함하지 않습니다.

Pull request에는 영향을 받는 집계 의미, 실행한 검사, 성능·개인정보 영향, 되돌리는 방법을 설명합니다.
