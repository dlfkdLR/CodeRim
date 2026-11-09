# Azure OpenAI

[English](../../providers/azureopenai.md) · **한국어**

계정 할당량이 아닌 엔드포인트 및 배포 상태 확인.

CLI 제공업체 ID: `azureopenai`.

## 연결

Azure API 키, 엔드포인트, API 버전, 배포 이름을 입력합니다. 배포 연결을 검사하며 계정 한도를 조회하지 않습니다.

위 소스로 [공통 연결 단계](../providers.md#제공업체-연결)를 따릅니다.

**Additional provider settings:** `AZURE_OPENAI_API_KEY`, `AZURE_OPENAI_API_VERSION`, `AZURE_OPENAI_DEPLOYMENT_NAME`, `AZURE_OPENAI_ENDPOINT`.

### 모니터링 요금

이 요청은 제공업체가 과금할 수 있습니다. 해당 제공업체 설정에서 **Allow potentially billed monitoring requests**를 켜기 전에는 모니터링을 요청하지 않습니다.

`coderim usage --provider azureopenai`로 [앱 스냅샷을 확인](../cli.md)합니다. [측정값과 문제 해결](../providers.md#측정값과-문제-해결)을 참고하세요.

[고정 소스 안내](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/azure-openai.md). 연결은 [CodeRim 설정](../providers.md#제공업체-연결)을 사용합니다.

[연결 문제 해결](../troubleshooting.md) · [개인정보](../privacy.md) · [문서](../README.md) · [전체 제공업체](../providers.md)
