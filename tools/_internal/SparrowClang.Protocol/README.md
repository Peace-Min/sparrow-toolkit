# SparrowClang.Protocol

`Sparrow.ClangAnalyzer`와 .NET C/C++ 파이프라인 사이의 버전 고정 JSON 계약입니다.

- 분석기는 표준 입력으로 `ClangAnalysisRequest` JSON을 받습니다.
- 분석기는 표준 출력으로 `ClangAnalysisResponse` JSON만 반환합니다.
- 진단 메시지는 응답의 `Diagnostics`에 포함하며 표준 출력에 임의 텍스트를 쓰지 않습니다.
- 프로토콜 버전이 다르면 분석하지 않고 오류 응답을 반환합니다.
