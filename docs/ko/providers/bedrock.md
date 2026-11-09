# AWS Bedrock

[English](../../providers/bedrock.md) · **한국어**

AWS 지출과 예산.

CLI 제공업체 ID: `bedrock`.

## 연결

AWS 프로필을 선택하거나 지원 AWS 자격 증명과 지역을 입력합니다. 요청할 Cost Explorer·예산·CloudWatch 데이터에 대한 권한이 필요합니다.

위 소스로 [공통 연결 단계](../providers.md#제공업체-연결)를 따릅니다.

**Additional provider settings:** `AWS_ACCESS_KEY_ID`, `AWS_CLI_PATH`, `AWS_DEFAULT_REGION`, `AWS_PROFILE`, `AWS_REGION`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`, `CODEXBAR_BEDROCK_API_URL`, `CODEXBAR_BEDROCK_AUTH_MODE`, `CODEXBAR_BEDROCK_BUDGET`, `CODEXBAR_BEDROCK_CLOUDWATCH_API_URL`.

### 모니터링 요금

이 요청은 제공업체가 과금할 수 있습니다. 해당 제공업체 설정에서 **Allow potentially billed monitoring requests**를 켜기 전에는 모니터링을 요청하지 않습니다.

`coderim usage --provider bedrock`로 [앱 스냅샷을 확인](../cli.md)합니다. [측정값과 문제 해결](../providers.md#측정값과-문제-해결)을 참고하세요.

[고정 소스 안내](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/bedrock.md). 연결은 [CodeRim 설정](../providers.md#제공업체-연결)을 사용합니다.

[연결 문제 해결](../troubleshooting.md) · [개인정보](../privacy.md) · [문서](../README.md) · [전체 제공업체](../providers.md)
