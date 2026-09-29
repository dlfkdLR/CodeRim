# 사용량과 토큰 히스토리

[English](../usage.md) · **한국어**

**Settings → Usage**에서 Codex 또는 연결된 Claude Code를 선택합니다. CodeRim 2.1.13에는 **Overview**, **Usage analytics**, 제공업체 **Limits**가 있습니다. 분석 화면에서 기간별 합계, 토큰 유형·모델 차트, 프로젝트, 세션, 모델·세션 상세를 봅니다.

## 로컬과 계정의 범위

- **Today · This Mac**은 이 Mac의 현재 시간대에서 자정 이후 로컬 기록을 집계합니다. 주간 합계는 설정한 주 시작일, 월간 합계는 로컬 달력을 사용합니다.
- 로컬 분석은 **이 컴퓨터에서 확인 가능한 세션을 계정에 관계없이** 집계합니다. 계정을 전환해도 초기화하거나 다른 계정에 재배정하지 않습니다. Codex와 Claude 히스토리는 별도로 유지합니다.
- 사용자가 **Include ChatGPT history**를 선택하면 Codex **Overview → History**에 별도의 **ChatGPT account** 합계를 표시합니다. **This Week**, **This Month**, **Lifetime**은 현재 계정의 값입니다. 프로필 동기화는 기본으로 꺼져 있고 기존에 저장한 선택은 유지하며, **Stop including ChatGPT history**로 끄면 메모리 스냅샷도 지웁니다. 서버 합계에는 스냅샷 날짜가 있고 로컬 활동보다 늦을 수 있습니다. 응답을 읽지 못하면 이용 불가 상태를 유지합니다.
- 프로필 동기화가 꺼져 있으면 History에 **This Mac**과 **Local History**를 표시합니다. 어느 모드에서도 Usage analytics 차트, Today 상세, 노치, CLI, 데스크톱 위젯은 로컬 범위를 유지합니다. 계정 합계를 로컬 사용량 테이블에 저장하거나 로컬 합계에 더하지 않습니다.
- 다른 제공업체는 각자의 한도, 크레딧, 지출, 상태를 제공합니다. 누락·삭제된 로컬 로그나 원격 컴퓨터에만 있는 기록은 여기에서 복구할 수 없습니다.

## 집계

**Total = Input + Output.** 캐시 입력은 이미 입력에 포함됩니다. 누적 차트는 이를 비캐시 입력, 캐시 입력, 출력으로 나누며 다시 더하지 않습니다. Codex 누적 스냅샷에서는 안전한 증가분만 집계하고 같은 스냅샷이 반복되면 추가하지 않습니다.

Claude 입력에는 비캐시 입력, 캐시 읽기, 캐시 생성이 포함됩니다. 반복 메시지·스트리밍 기록은 중복 합산하지 않고 조정합니다. [Codex 집계](../../Documentation/USAGE.ko.md)와 [Claude 집계](../../Documentation/CLAUDE.ko.md#accounting)를 참고합니다.

## 비용 추정과 첨부 파일

macOS의 API 환산 비용 추정은 가격을 아는 **Codex** 모델에서 지원합니다. Claude 로컬 토큰 히스토리는 지원하지만 Claude 비용 추정과 첨부 파일 개수는 지원하지 않습니다. 추정액은 구독 청구액이 아닙니다. 가격을 모르는 사용량의 토큰은 유지하고, 해당 사용량을 제외한 부분 비용 합계임을 명시합니다.

Codex 이미지 분석은 숫자 개수만 저장합니다. 세션 이미지 수는 히스토리 삭제 기준 시각 이후 보존된 전체 세션의 개수이며, 선택한 차트 기간만의 개수가 아닙니다. 사용량 데이터베이스에 이미지 바이트, 대화 내용, 첨부 파일 경로를 저장하지 않습니다.

**Settings → Providers → Codex 또는 Claude Code Details → Manage Data → Rebuild Statistics / Clear Local History**에서 선택한 서비스의 집계 데이터를 관리합니다. 삭제하면 재가져오기 기준 시각을 기록하며 원본 로그는 삭제하지 않습니다. [Windows 동작](windows.md)은 별도로 안내합니다.

[계정](accounts.md) · [개인정보](privacy.md) · [문제 해결](troubleshooting.md) · [문서](README.md)
