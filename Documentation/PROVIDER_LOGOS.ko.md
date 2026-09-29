# 제공업체 로고 계약

[English](PROVIDER_LOGOS.md) · **한국어**

70개 목록은 기존 기본 로고와 추가 연동의 번들 vendor SVG를 사용합니다. 선택기·Settings·스냅샷·노치는 `NotchProviderCatalog.glyph(for:)`를 사용합니다. archive의 `third` placeholder는 제공업체 ID로 복원합니다. 모르는·없는 로고는 중립 점선 사각형이며 다른 서비스 로고를 대신 쓰지 않습니다.

로고는 고정 CodexBar `51ed16bdd3abe35ec53af99818e1b5f0d2a631d3`의 `branding.iconResourceName`을 따릅니다. OpenAI·Azure, Alibaba 플랜, Kimi·Moonshot 등 같은 브랜드는 의도적으로 공유합니다. vendor SVG는 `Sources/CodeRim/Resources/ProviderLogos`에 있고 `NOTICE`·번들 MIT 라이선스에 출처·상표를 유지합니다. CodeRim Open Rim 앱 로고는 서비스 로고와 독립적입니다.

SVG의 CSS `1em` 크기는 SwiftUI 렌더 전에 NSImage 논리 크기를 명시해야 합니다. `ProviderGlyphView`의 실제 alpha-mask를 검증합니다. `tiffRepresentation`만으로 표시 벡터를 확인할 수 없습니다. 라이트·다크, 모르는 자산, 이전 캐시, 검색·레이아웃, 기본·추가 전체 스냅샷 로고, 패키지 리소스 해시를 확인합니다.

```sh
swift test --filter ProviderGlyph
swift test --filter ProviderLogo
```

설치 성공을 주장하려면 합성 캡처와 실제 패키지 UI를 구분합니다. [2026-09-17 로고 검증 기록](PROVIDER_LOGOS_2026-09-17.md)은 과거 39개 검사·빌드·설치 증거와 제한을 보존합니다. 이번 문서 감사의 새 결과가 아닙니다. [제공업체](PROVIDERS.ko.md)를 참고합니다.
