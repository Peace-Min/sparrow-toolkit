# Sparrow.ClangAnalyzer

Clang의 JSON AST를 별도 프로세스에서 분석해 안전한 소스 범위와 논리식 편집 지시를 반환합니다.

분석 순서:

1. 프로젝트와 상위 폴더에서 `compile_commands.json`을 찾습니다.
2. 파일에 해당하는 컴파일 명령을 정리해 Clang에 전달합니다.
3. 데이터베이스가 없으면 C는 GNU11, C++은 GNU++17 기본 옵션으로 분석합니다.
4. Clang 파싱이나 인코딩 처리가 실패하면 실패 응답을 반환하고 GUI 파이프라인은 토큰 엔진을 사용합니다.

분석기는 소스 파일을 직접 수정하지 않으며 JSON 프로토콜만 사용합니다. 현재 구현은 Clang JSON AST를 사용하므로 LibTooling ABI에 직접 결합되지 않습니다.
