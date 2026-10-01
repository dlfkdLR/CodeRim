# CodeRim website

[English](README.md) · **한국어**

CodeRim의 제품 소개, 다운로드, 제공자 연결과 첫 사용을 안내하는 한국어·영어 정적 사이트입니다. HTML, CSS, JavaScript와 로컬 이미지로 구성하며 패키지 설치나 빌드, API 키가 필요하지 않습니다. 앱의 원본 코드와 설정에는 접근하지 않습니다.

## 로컬 실행

저장소 루트에서 실행합니다.

```sh
python3 -m http.server 4173 --bind 127.0.0.1 --directory website
```

브라우저에서 [http://127.0.0.1:4173](http://127.0.0.1:4173)을 엽니다. 영어를 직접 열려면 `/?lang=en`을 사용하세요. 문구·스타일 수정 후 페이지를 새로고침합니다. `file://`로도 기본 콘텐츠를 볼 수 있지만 클립보드 기능은 로컬 HTTP 서버에서 확인하세요.

## Vercel 배포

2026-09-28 사용자 요청으로 [https://coderim.vercel.app](https://coderim.vercel.app)에 운영 배포했습니다. Vercel 프로젝트는 `willbrains-projects/coderim`이며, `website` 폴더만 CLI로 업로드합니다. 앱 저장소의 원본 코드나 검증 산출물은 배포하지 않습니다.

현재 CLI 배포 설정:

| 설정 | 값 |
| --- | --- |
| 업로드 폴더 | `website` |
| 프로젝트 Root Directory | `.` (업로드 폴더 기준) |
| Framework Preset | `Other` |
| Build Command | 비워 두기 |
| Output Directory | `.` |
| Install Command | 별도 설치 없음 |
| 환경 변수 | 필요 없음 |

`website/vercel.json`은 정적 파일을 그대로 출력하도록 설정합니다. [Vercel 공식 정적 사이트 빌드 안내](https://vercel.com/docs/builds/configure-a-build#skip-build-step)를 따릅니다. `.vercelignore`는 유지보수 문서와 로컬 스크립트, 환경 파일을 업로드에서 제외합니다. `.vercel/` 연결 정보는 Git에 포함하지 않습니다.

다음 배포는 `website` 디렉터리에서 실행합니다.

```sh
vercel deploy --prod --scope willbrains-projects
```

Git 기반 자동 배포는 연결하지 않았습니다. 나중에 이 저장소를 Vercel에 연결한다면 Git 기준 Root Directory를 `website`로 지정하세요.

운영 주소는 [https://codrim.dlfkd.dev](https://codrim.dlfkd.dev)입니다. 호스팅은 Vercel, DNS 관리는 Cloudflare를 사용합니다. `codrim` CNAME을 Vercel이 안내한 주소에 DNS 전용으로 연결했습니다. 저장소의 웹사이트 링크는 GitHub About에 둡니다.

초기 운영 배포는 `READY` 상태와 인증 없는 HTTPS 200 응답을 확인했습니다. 공개된 HTML·CSS·JavaScript·이미지 등 82개 파일이 로컬 최종본과 SHA-256 기준으로 모두 일치하며, 유지보수 문서와 로컬 스크립트 경로는 404입니다. 배포 ID와 상세 확인 결과는 `outputs/website-2026-09-28/deployment-verification.json`에 기록합니다. [VALIDATION.md](VALIDATION.md)는 배포 전 로컬 검증 기록입니다.

## 콘텐츠 유지보수

- `index.html`: 한국어 기본 문구와 각 요소의 `data-en` 영어 문구, 다운로드·문서 링크.
- `styles.css`: 기존 CodeRim의 Quiet Instrument 원칙을 따르는 중립색, 명확한 정보 계층과 화면 크기별 레이아웃.
- `app.js`: 한국어·English 선택 메뉴, 테마·모바일 메뉴, 실제 앱 UI 합성 및 자동 반복 애니메이션, 제공자 검색·필터, 운영체제 탭과 명령 복사. 언어를 바꿔도 현재 보는 영역의 위치를 유지합니다. 언어와 테마만 이 사이트의 localStorage에 저장합니다.
- `terminal-examples.js`: 예시 snapshot으로 실제 CLI를 실행한 명령별 출력. `app.js`에서 타이핑·출력 애니메이션을 재생하며, 한도 → 토큰 → JSON을 자동 반복하고 일시 정지를 지원합니다. 화면 밖이나 백그라운드에서는 정지하고, 동작 줄이기 설정에서는 정적으로 표시합니다.
- `providers.js`: 현재 제공자 문서와 공유 카탈로그에서 생성한 70개 제공자. 아래 명령으로 갱신합니다.

```sh
python3 website/scripts/sync-providers.py
node --check website/app.js
node --check website/providers.js
```

카탈로그 수가 바뀌면 생성 스크립트는 중단하며, 사이트의 제공자 수 문구도 함께 검토해야 합니다. 원래 앱에 포함된 SVG와 네이티브 `GlyphOutline` 경로에서 제공자 마크를 가져옵니다. 상세 출처와 라이선스는 [ASSETS.md](ASSETS.ko.md) 및 `assets/NOTICE.txt`에 있습니다.

웹사이트는 macOS DMG를 `v2.1.15`, Windows x64·ARM64 MSI를 `v2.1.15`에 고정합니다. 플랫폼별 릴리스 버전은 독립적으로 관리합니다. `releases/latest`에 의존하지 말고 사이트 배포 전에 플랫폼별 실제 공개 파일을 확인합니다. 새 릴리스 때 HTML의 다운로드, 버전, 검증 파일과 릴리스 노트 링크를 함께 바꾸세요.

메인과 기능 소개에는 실제 CodeRim SwiftUI로 렌더한 이미지를 사용합니다. 에디터 배경은 새로 만든 HTML/CSS이며, 미리보기의 사용량·차트·CLI 값은 명시된 예시입니다. 이미지는 확대 팝업이 없는 일반 콘텐츠입니다. 기능 소개는 사용 한도·토큰 분석·노치 설정·계정 전환·CLI·macOS 위젯·프로젝트 작업 상태를 이미지와 함께 한국어와 영어로 설명합니다. 화면 전체 너비의 가로 갤러리에서 데스크톱은 화면 크기에 맞춰 두세 장, 모바일·태블릿은 한 장씩 크게 표시하며, 좌우 화살표·방향키·Home/End·터치 스크롤로 이동합니다. 카드 전체를 감싸는 테두리를 두지 않고 양 끝에는 약한 블러와 페이드를 적용합니다. 끝에서는 해당 화살표가 비활성화됩니다. 동작 줄이기 설정에서는 즉시 이동합니다. 계정·위젯은 실제 네이티브 UI의 합성 데이터 렌더 자료를 그대로 사용하고, CLI 이미지는 실제 명령 출력으로 만든 SVG입니다. 메인 장면은 작은 손잡이 → 노치 펼침 → Codex 상세 → Claude 상세 → 프로젝트 작업 목록 → 접힘을 자동 반복합니다. 실제 앱의 NotchMotion 스프링 값, 셀별 지연과 SideNotchShape 윤곽을 웹 애니메이션에 사용하며, 각 내용은 약 2.3초 뒤 다음 화면으로 넘어가며 선택 버튼은 없습니다. 작업 목록은 실제 SessionList 렌더 이미지에 앱과 같은 1.4초 회전 링을 합성합니다. 메인 장면에는 재생·일시 정지 버튼이 없습니다. 화면 밖·백그라운드에서는 링도 멈추며, 동작 줄이기 설정을 따릅니다. 제공자 수는 카탈로그 수이며 모든 계정의 실제 연결 성공을 의미하지 않습니다. Windows는 미리보기로 표시하고, 로컬 토큰 기록과 계정 한도, macOS 공증 여부를 구분해 안내합니다. 사이트는 외부 분석 스크립트, 원격 폰트, 앱 계정 조회를 사용하지 않습니다.
