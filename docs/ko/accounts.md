# 저장한 계정

[English](../accounts.md) · **한국어**

macOS에서 노치의 계정 버튼으로 Codex Accounts 또는 Claude Accounts를 엽니다. 계정을 추가·저장·전환·제거할 수 있습니다. 전환은 수동이며, 한도가 부족해져도 CodeRim이 계정을 자동으로 바꾸지 않습니다.

## Codex

저장한 로그인은 이 Mac의 Keychain에 보관합니다. 전환 시 확인을 요청하고 Codex를 재시작할 수 있습니다. 안내에 따라 활성 Codex 클라이언트를 닫아 안전하게 자격 증명을 교체합니다. 공용 로그인을 사용하므로 새 `codex` CLI 세션에도 선택한 계정이 적용됩니다. 기존 CLI 세션은 전환 후 재시작합니다. CodeRim은 성공을 표시하기 전에 공식 CLI의 로컬 계정 상태를 확인하며, 확인하지 못하거나 선택한 계정과 다르면 검증되지 않은 전환으로 표시합니다. 만료·철회된 로그인은 공식 로그인을 다시 해야 할 수 있습니다.

## Claude Code

공식 Claude CLI에서 로그인한 뒤 CodeRim에 계정을 추가합니다. **Add Account**는 격리된 공식 CLI 브라우저 로그인 흐름을 엽니다. 전환 전에 Claude Code 세션을 닫고 변경을 확인합니다. 공용 CLI 자격 증명과 프로필을 함께 갱신하며 `claude auth status`의 이메일·조직이 선택한 계정과 일치하는지 확인합니다. 선택한 계정을 쓰려면 새 CLI 세션을 시작합니다. 사용자 지정 설정 홈, API 키, 관리형 인증은 Claude Code 자체에서 관리해야 합니다.

## 히스토리와 자격 증명

로컬 토큰 히스토리는 계정에 관계없이 이 Mac의 기록으로 유지합니다. 사용량 저장소와 진단 정보에 자격 증명을 넣지 않습니다. 저장한 계정을 제거해도 원본 세션 로그는 삭제하지 않습니다. ad-hoc 서명된 앱을 업데이트하면 Keychain 권한 요청이 다시 나타날 수 있습니다.

[지원 구성과 전환 상세](../../Documentation/ACCOUNTS.ko.md) · [Claude 설정](providers/claude.md) · [개인정보](privacy.md) · [문서](README.md)

Windows는 별도의 보호된 계정 저장소와 전환 구현을 사용합니다. [Windows 설정](windows.md)을 참고합니다.
