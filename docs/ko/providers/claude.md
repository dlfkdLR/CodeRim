# Claude Code

[English](../../providers/claude.md) · **한국어**

로컬 토큰 사용 기록과 상태 표시줄을 통한 사용 한도.

CLI 제공업체 ID: `claude`.

## 연결

1. `claude`를 실행하고 공식 Claude CLI에 로그인합니다.
2. **Settings → Providers → Claude Code Details**에서 연동을 켜고 로그인된 계정을 추가합니다.
3. Claude Code 응답을 완료하면 상태 줄 연동이 5시간·주간 한도를 보냅니다.

## 측정값 확인

노치의 제공업체에 포인터를 올리거나, [CLI를 설치](../cli.md)한 뒤 `coderim usage --provider claude`로 최근 앱 스냅샷을 읽습니다. 최신 값을 받으려면 CodeRim을 실행 상태로 둡니다.

한도는 Claude Code status-line 갱신 시 바뀝니다. 마지막 값만 있으면 새 응답을 완료해야 할 수 있습니다. [Claude 연동](../../../Documentation/CLAUDE.ko.md)·[저장한 계정](../accounts.md)을 참고합니다.

[연결 문제 해결](../troubleshooting.md) · [개인정보](../privacy.md) · [문서](../README.md) · [전체 제공업체](../providers.md)
