# Bundled LLVM/Clang policy

이 폴더는 바이너리를 커밋하지 않고 배포에 필요한 라이선스와 고지만 관리합니다.

- `dotnet publish`는 로컬 공식 LLVM 설치의 `clang.exe`와 `clang-format.exe`를 `publish/clang`으로 복사합니다.
- 라이선스 파일은 `publish/licenses`로 복사합니다.
- LLVM 버전을 변경할 때는 공식 배포본인지 확인하고 `THIRD-PARTY-NOTICES.txt`의 버전과 SHA-256을 갱신합니다.
- LLVM/Clang 바이너리 또는 소스 파일을 수정했다면 변경 고지와 추가 라이선스 의무를 별도로 검토합니다.
- 공식 배포 패키지에 추가 `LICENSE`, `NOTICE` 파일이 있으면 모두 `publish/licenses`에 포함해야 합니다.
