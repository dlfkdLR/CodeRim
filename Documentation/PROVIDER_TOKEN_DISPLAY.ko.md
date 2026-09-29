# 제공업체 토큰 표시

[English](PROVIDER_TOKEN_DISPLAY.md) · **한국어**

노치는 로컬 Codex·Claude 기록을 **Today · This Mac**으로 표시합니다. 모든 로컬 계정을 포함하며 계정 한도·계정 전체 API 합계가 아닙니다. 로컬 스냅샷 구독이 한도 조회·오래된 한도 캐시·한도 오류와 독립적으로 갱신합니다. 알려진 0, loading, unavailable, partial, stale은 구분하며 일별 로컬 값은 한도 archive에 저장하지 않습니다.

추가 어댑터는 제공된 costUsage와 OpenAI Admin·Mistral 히스토리 값을 보존합니다. 원래 기간을 today로 바꾸지 않으며 라벨이 없으면 보고된 N일을 씁니다. 범위를 모르면 Period unavailable입니다. 잘못되거나 모르는 토큰 수는 생략하며 명시적 0은 표시합니다.

고정 CodexBarCore revision `51ed16bdd3abe35ec53af99818e1b5f0d2a631d3`에 따른 다른 매핑입니다.

- LLM Proxy: 요청·토큰·제공업체별 상세는 텍스트이며 가짜 0% 막대를 만들지 않습니다.
- LongCat: 실제 토큰 한도의 분자·분모와 백분율을 같이 유지합니다.
- Bedrock: 원래 보고 기간(예: Claude 14d)을 유지합니다.
- GLM: TOKENS_LIMIT.currentValue는 사용 토큰 한도입니다. 크레딧 플랜·MCP 개수는 토큰이 아닙니다.
- Groq 콘솔·DeepSeek·Claude Admin: 일반 상세 행이 실제 토큰 수를 보존합니다.
- Alibaba Token Plan: 상세는 크레딧이며 이름만으로 토큰이라고 하지 않습니다.
- 기본 Cursor·Copilot·Command Code·OpenCode Go·Grok·Ollama·Antigravity는 허용량·요청·크레딧·비용·모델 상태를 제공합니다. 토큰 합계를 추론하지 않으며 새 과금 요청·로그인·자격 증명 소스를 추가하지 않습니다.

회귀 검증은 loading과 0, 한도 실패 중 로컬 갱신, archive 복원, 제공업체 분리, 전체 추가 어댑터, GLM 단위, OpenAI·Mistral 산식, 과거 기간 라벨, 실제 SwiftUI 카드 렌더를 다룹니다. 검증 범위이며 이번 문서 작업의 새 테스트 통과 주장이 아닙니다.
