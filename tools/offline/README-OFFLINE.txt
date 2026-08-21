Sparrow Helper Portable (Windows x64, offline)
================================================

실행 방법
---------
1. ZIP 전체를 쓰기 가능한 사용자 폴더에 압축 해제합니다.
   예: C:\Users\<사용자>\Desktop\SparrowHelper-Portable-win-x64
2. ZIP 안에서 직접 실행하거나 EXE 하나만 따로 옮기지 마세요.
3. Start-SparrowHelper.cmd를 더블클릭합니다.

이 번들은 self-contained .NET 8/WPF 런타임, C# 변환기, C/C++ Clang AST
분석기, LLVM/Clang 20.1.0을 포함하므로 대상 PC에 .NET, LLVM, NuGet 또는
Visual Studio를 설치할 필요가 없습니다.

검증 방법
---------
Verify-SparrowHelper.cmd를 더블클릭하면 필수 파일, SHA-256 manifest,
Clang 버전 및 승인된 Clang 실행파일 해시를 확인합니다.

사용자 데이터
-----------
- 체커 규칙/매핑: %LOCALAPPDATA%\SparrowRunner\checkers
- GUI 실행 로그: %LOCALAPPDATA%\SparrowRunner\logs
- C# 규칙 실행 로그: 사용자가 선택한 대상 프로젝트 폴더

배포 제한
---------
이 패키지는 공개 배포용으로 승인된 산출물이 아니라, 권한이 있는 사용자/조직의
내부 오프라인 사용을 위해 생성한 로컬 패키지입니다. NPOI 2.8.0 조건을 별도로
확인하지 않은 상태로 외부에 재배포하지 마세요. 세부 내용은
licenses\SPARROW-THIRD-PARTY-NOTICES.txt 및 licenses\nuget 폴더를 확인하세요.

라이선스
--------
- Sparrow 작성 코드: 루트 LICENSE (MIT)
- LLVM/Clang: tools\SparrowRunner.Gui\publish\licenses
- NuGet 패키지: licenses\nuget 및 SPARROW-THIRD-PARTY-NOTICES.txt
