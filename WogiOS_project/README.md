# WogiOS (시제품)

APK 의 WogShim / WogPatcher 를 그대로 쓰고, 안드로이드 전용 부분(WogAndroid)만 iOS 용으로 새로 쓴 프로젝트입니다.

## 빌드
1. 이 폴더 전체를 GitHub 저장소에 올립니다 (.github, libs, WogiOS 폴더 포함).
2. Actions 탭 → "Build unsigned IPA" → Run workflow.
3. 끝나면 Artifacts 의 WogiOS-unsigned 를 받아 Sideloadly/AltStore 로 설치합니다.

## 알려진 한계 (v0.1)
- 컨트롤러/화면 패드 없음 (터치만)
- 폴더 가져오기 없음: 원본 assets 가 필요하면 파일 앱 → 이 앱 폴더 → assets/ 에 직접 복사
- 아직 한 번도 컴파일/실행해 보지 않음: 첫 빌드는 에러 로그를 보고 고쳐야 할 가능성이 큼
- 재서명 시 Sideloadly 의 JIT 관련 옵션은 필요 없음 (인터프리터 모드 사용)
