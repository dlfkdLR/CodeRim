# AWS Bedrock

[English](../../providers/bedrock.md) · **한국어**

AWS 지출과 예산.

CLI 제공업체 ID: `bedrock`.

## 연결

AWS 프로필을 선택하거나 지원 AWS 자격 증명과 지역을 입력합니다. 요청할 Cost Explorer·예산·CloudWatch 데이터에 대한 권한이 필요합니다.

1. **Settings → Providers → Add Provider**에서 **AWS Bedrock**를 추가합니다.
2. **Connection settings**에서 위 소스에 맞는 값을 입력합니다. 브라우저 세션 가져오기가 제공되는 경우 이 제공업체에서 명시적으로 켭니다.
3. **Save and refresh**를 선택합니다. 자격 증명은 CodeRim 전용 Keychain 항목에 저장합니다.

### 추가 설정

앱의 **Additional provider settings**에 다음 선택 필드가 있습니다. 계정과 연결 방식에 필요한 값만 입력합니다.

`AWS_ACCESS_KEY_ID`, `AWS_CLI_PATH`, `AWS_DEFAULT_REGION`, `AWS_PROFILE`, `AWS_REGION`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`, `CODEXBAR_BEDROCK_API_URL`, `CODEXBAR_BEDROCK_AUTH_MODE`, `CODEXBAR_BEDROCK_BUDGET`, `CODEXBAR_BEDROCK_CLOUDWATCH_API_URL`.

### 모니터링 요금

이 요청은 제공업체가 과금할 수 있습니다. 해당 제공업체 설정에서 **Allow potentially billed monitoring requests**를 켜기 전에는 모니터링을 요청하지 않습니다.

## 측정값 확인

노치의 제공업체에 포인터를 올리거나, [CLI를 설치](../cli.md)한 뒤 `coderim usage --provider bedrock`로 최근 앱 스냅샷을 읽습니다. 최신 값을 받으려면 CodeRim을 실행 상태로 둡니다.

측정값이 없으면 세션 만료, 필요한 플랜·권한 누락, 서비스가 유효한 값을 반환하지 않은 경우일 수 있습니다. 다시 연결하고 새로고침합니다. 데이터가 없다는 뜻은 사용량이 0이라는 뜻이 아닙니다.

연결 프로토콜은 CodeRim이 고정한 CodexBar 연동에서 제공합니다. [상류 소스 안내](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/bedrock.md). 연결은 **CodeRim**에서 설정합니다. CodexBar 전용 UI·설정 파일 안내는 그대로 적용되지 않습니다.

[연결 문제 해결](../troubleshooting.md) · [개인정보](../privacy.md) · [문서](../README.md) · [전체 제공업체](../providers.md)
