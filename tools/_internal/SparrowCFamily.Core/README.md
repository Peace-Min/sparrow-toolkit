# SparrowCFamily.Core

Sparrow Helper의 C 및 C++ 자동수정 엔진이 공유하는 기반 라이브러리입니다.

- 대상 확장자: `.c`, `.h`, `.cpp`, `.hpp`
- 파일 인코딩, 줄바꿈, 토큰 및 텍스트 변경 처리를 공유합니다.
- 파일별 `CFamilySourceDocument`가 원문, 인코딩/BOM, 선호 줄바꿈과 토큰을 공유합니다.
- 규칙은 `EditCollector`에 변경 위치를 수집하며, 겹침 검사 후 원본을 한 번 순회해 적용합니다.
- UTF-8/UTF-16/UTF-32 BOM과 CP949를 보존하고 혼합 줄바꿈을 불필요하게 정규화하지 않습니다.
- 문자열, C++ raw string, 문자 리터럴, 주석과 전처리기 영역을 토큰화 단계에서 보호합니다.
- 파일 단위 제한 병렬 처리, 프로세스 내 증분 캐시와 파일별 처리 지표를 제공합니다.
- GUI와 C# Roslyn 엔진에서 독립된 .NET 클래스 라이브러리입니다.
- 향후 Clang AST 분석기는 이 엔진에 분석 결과를 제공하는 별도 구성요소로 연결합니다.

엔진 구분은 다음과 같습니다.

- C/C++ 코드: `SparrowCFamilySyntaxFix`
- C/C++ 주석: `SparrowCFamilyCommentFix`
- C/C++ 통합 실행: `SparrowCFamilyPipeline`
- C# 코드: `SparrowSyntaxFix`
- C# 주석: `SparrowCommentFix`
