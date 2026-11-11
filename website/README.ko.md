# CodeRim website

[English](README.md) · **한국어**

HTML·CSS·JavaScript로 만든 제품 소개·다운로드·제공업체 안내 사이트입니다. 패키지 설치·빌드·API 키는 필요 없습니다. 기본 언어는 한국어이며 `/?lang=en`으로 영어를 엽니다. 사이트 언어·테마만 로컬에 저장합니다. Vercel Web Analytics를 사용하며 앱 계정·설정이나 원격 폰트에는 접근하지 않습니다.

## 미리보기와 검사

저장소 루트에서 실행합니다.

```sh
python3 -m http.server 4173 --bind 127.0.0.1 --directory website
```

[http://127.0.0.1:4173](http://127.0.0.1:4173)을 엽니다. 클립보드는 HTTP에서 확인합니다.

```sh
python3 website/scripts/sync-providers.py --check
node --check website/app.js
node --check website/providers.js
python3 website/scripts/test-language.py
```

`index.html`은 한국어/`data-en` 문구·다운로드·언어별 문서 링크, `styles.css`는 배치, `app.js`는 조작과 애니메이션을 담당합니다. `providers.js`는 영어·한국어 제공업체 표와 공유 이름 switch에서 생성하며, `--check`를 빼면 다시 생성합니다. 카탈로그 수가 바뀌면 생성이 중단되므로 사이트 문구도 검토합니다. `terminal-examples.js`에는 합성 스냅샷으로 실행한 실제 CLI 출력이 있습니다.

Chrome/Chromium 회귀 검사는 임시 프로필에서 한국어 → 영어 → 한국어 문서 경로와 검색·필터·펼침·테마·플랫폼·일시 정지·갤러리 위치·스타일 유지를 검사합니다. 배포나 실제 계정 조회는 하지 않습니다.

## 배포

`website`만 업로드합니다. Root/Output Directory는 `.`, Framework는 Other, Build/Install 명령은 비워 두고 환경 변수는 필요 없습니다. `vercel.json`과 `.vercelignore`가 정적 출력과 유지보수 문서·스크립트·환경 파일 제외를 정의합니다. `.vercel/`는 Git에 넣지 않습니다. [Vercel 정적 설정](https://vercel.com/docs/builds/configure-a-build#skip-build-step).

기록된 배포는 `willbrains-projects/coderim`, [coderim.vercel.app](https://coderim.vercel.app), [coderim.dlfkd.dev](https://coderim.dlfkd.dev)와 Cloudflare DNS 전용 CNAME을 사용합니다. 당시 Git 배포는 연결하지 않았으며 나중에 연결한다면 Root Directory는 `website`입니다. 이는 기록된 설정이며 새 운영 검증 결과가 아닙니다. 배포가 승인되면 `website`에서 실행합니다.

```sh
vercel deploy --prod --scope willbrains-projects
```

페이지는 macOS `v2.1.36`, Windows `v2.1.34`에 고정합니다. 배포 전에 플랫폼별 실제 공개 파일을 확인하고 버전·다운로드/체크섬 URL·릴리스 노트 링크를 함께 바꿉니다. `releases/latest`가 양쪽 플랫폼을 나타낸다고 가정하지 않습니다.

## 이미지와 동작

출처·라이선스·과거 캡처는 [ASSETS](ASSETS.ko.md)를 기준으로 하며 `assets/NOTICE.txt`를 유지합니다. 네이티브 이미지와 CLI 출력은 예시 데이터이며 실제 계정·설치 앱 검증이 아닙니다. [VALIDATION](VALIDATION.md)은 날짜별 로컬 검사이며 배포 근거는 `outputs/website-2026-09-28/deployment-verification.json`에 있습니다.

메인은 네이티브 패널을 합성해 반복하고, 기능 카드 일곱 개는 키보드·터치로 넘기는 수동 갤러리입니다. 작업 링과 터미널 애니메이션은 화면 밖·백그라운드에서 멈추며 동작 줄이기에서는 정적/즉시 전환합니다. 메인에는 재생 버튼이 없고 터미널에는 일시 정지가 있습니다. 기록된 앱 구성요소의 웹 합성이며 원격 제어나 매 프레임 네이티브 동등성을 주장하지 않습니다.
