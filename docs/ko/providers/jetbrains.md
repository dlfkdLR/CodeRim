# JetBrains AI

[English](../../providers/jetbrains.md) · **한국어**

설치된 JetBrains IDE에서 확인할 수 있는 할당량 정보.

CLI 제공업체 ID: `jetbrains`.

## 연결

지원되는 JetBrains IDE의 AI Assistant에 로그인합니다. CodeRim은 로컬 한도 XML을 읽습니다. IDE 설정 위치가 기본 경로가 아닐 때만 IDE_BASE를 설정합니다.

위 소스로 [공통 연결 단계](../providers.md#제공업체-연결)를 따릅니다.

**Additional provider settings:** `IDE_BASE`.

`coderim usage --provider jetbrains`로 [앱 스냅샷을 확인](../cli.md)합니다. [측정값과 문제 해결](../providers.md#측정값과-문제-해결)을 참고하세요.

IDE가 `Unknown`만 보고하거나 유효한 상한을 주지 않으면 한도 백분율을 표시할 수 없습니다. IDE 계정 설정에서 사용량을 새로고침한 뒤 다시 확인합니다.

[고정 소스 안내](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/jetbrains.md). 연결은 [CodeRim 설정](../providers.md#제공업체-연결)을 사용합니다.

[연결 문제 해결](../troubleshooting.md) · [개인정보](../privacy.md) · [문서](../README.md) · [전체 제공업체](../providers.md)
