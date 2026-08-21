# SparrowCFamilyPipeline

> Clang AST 연동 구조와 배포·검증 방법은 `docs/clang-ast-integration.md`를 참고하세요.

C/C++ 코드 규칙 엔진과 주석 규칙 엔진을 하나의 파일 처리 파이프라인으로 조정합니다.

- 파일을 한 번 읽고 모든 선택 규칙을 메모리에서 실행한 뒤 한 번만 저장합니다.
- 파일 단위 제한 병렬 처리와 프로세스 내 증분 캐시를 제공합니다.
- 엔진별 규칙 설정은 `SparrowCFamilySyntaxFix`와 `SparrowCFamilyCommentFix`에 그대로 분리되어 있습니다.
- 파일별 변경 여부, 캐시 건너뜀 여부와 토큰화 횟수를 결과 모델로 반환합니다.
