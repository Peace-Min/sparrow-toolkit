# Sparrow Helper tools

일반 사용자는 이 폴더에서 아래 파일만 실행하면 됩니다.

```text
Run-SparrowRunnerGui.cmd
```

## 무엇을 써야 하나요?

- `Run-SparrowRunnerGui.cmd`: **[코드 자동수정 (C#)]** ([코드 규칙] · [주석·레이아웃])과 **[XLS 분리 (모든 언어)]** 를 한 화면에서 실행하는 권장 진입점입니다. [XLS 분리]는 Sparrow 결과 XLS 하나만 받아 검출 전건을 체커 키 폴더별 항목 md(`<체커키>\{ID}_{파일명}_{라인}.md`)로 분리합니다. 인덱스/요약 파일은 만들지 않으며, 준비해야 할 선행 파일도 없습니다.
  - 화면 명칭 ↔ 내부 트랙: **[코드 규칙] = Track A · [주석·레이아웃] = Track B · [XLS 분리] = Track C**(트랙은 내부 명칭이라 화면에는 안 나옵니다).
  - **커밋 여부는 화면의 [규칙별 커밋 생성] 체크박스가 정합니다(기본 꺼짐).** 꺼져 있으면 러너에 `-NoCommit` 이 넘어가 파일만 고치고 `N개 파일 수정됨 — 커밋하지 않았습니다. git diff 로 검토 후 커밋하세요.` 안내가 나옵니다. 켜면 `-Commit` 이 넘어가 **규칙 하나마다 커밋 하나**를 만듭니다(롤백 단위 = 규칙).
  - GUI 가 넘기지 않는 **CLI 러너 전용 옵션**: `-DryRun` · `-VerifyCmd`(아래 컴파일 게이트) · `-ExePath` · `-IncludeGenerated`(`Run-SparrowCommentFix.ps1` 에만 있음). 자동화에서 쓸 때는 **`-Rules` 를 반드시 명시**하세요 — 생략하면 러너가 opt-in 규칙마다 Y/N 을 되묻고, 비대화형에서는 그 규칙들이 조용히 전부 꺼집니다.
- `Run-SparrowAll.cmd`: GUI 없이 코드 규칙(Track A)·주석/레이아웃(Track B)만 순차 실행해야 할 때 쓰는 보조 진입점입니다.
- `Compare-Sparrow.ps1`: 수정 전/후 Sparrow xls를 비교하는 G2 회귀 게이트입니다(PASS=0, FAIL=1).
- `SparrowRunner.Gui/`: 통합 GUI 프로젝트입니다.
- `_internal/`: GUI와 러너가 내부적으로 호출하는 엔진 프로젝트입니다. 일반 사용자가 직접 실행할 필요가 없습니다.

## 규칙별 커밋 전 컴파일 게이트 (`-VerifyCmd`, 한 줄)

`-Commit`으로 규칙별 자동 커밋을 돌릴 때, `-VerifyCmd '<빌드 명령>'`을 함께 주면 **각 규칙 edits 후·커밋 전**에 그
명령을 실행한다. 명령이 비정상 종료(exit≠0)하면 그 규칙의 미커밋 `*.cs` edits를 `git checkout -- *.cs`로 되돌리고
커밋을 건너뛴 뒤(`[GATE] rule <r> reverted` 로그) 다음 규칙으로 넘어간다 — **게이트를 통과한 규칙만 커밋**된다.
레거시 비-SDK x64 대상이라 규칙마다 전체 msbuild는 느리므로, 게이트는 선택(opt-in)이며 안 주면 예전과 동일하게
동작하되 `-Commit`일 때 "빌드 게이트 없음 — 커밋 후 반드시 전체 빌드로 확인" 안내가 1줄 출력된다.

```powershell
# 원큐(A→B) 모두에 게이트 적용
.\Run-SparrowAll.ps1 -Solution C:\Work\MyApp\MyApp.sln -Commit -VerifyCmd '"C:\...\msbuild.exe" C:\Work\MyApp\MyApp.sln /t:Build'
# 개별 러너에도 동일한 -VerifyCmd 지원
.\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1 -Solution ...\MyApp.sln -Commit -VerifyCmd '<빌드 명령>'
```

## 내부 구성

- `_internal/SparrowSyntaxFix`: Track A 코드 규칙 자동수정 엔진.
- `_internal/SparrowCommentFix`: Track B 주석/레이아웃 자동수정 엔진.
- `_internal/SparrowXlsExport`: Track C XLS 파서 CLI와 테스트용 도구.
- `_internal/SparrowXlsExport.Core`: Track C 체커별 md 분리 익스포터 공용 라이브러리.

## 폐쇄망 반입(오프라인 배포)

폐쇄망(인터넷/`.NET SDK` 없는 PC)에서 쓰려면 `Run-SparrowRunnerGui.cmd` 하나만 복사해서는 안 됩니다.
GUI/러너는 컴파일된 도구 exe가 있어야 동작합니다. 올바른 최소 반입 단위는 다음과 같습니다.

1. 인터넷 + `.NET SDK`가 있는 PC에서 `tools\publish-airgap.ps1`을 한 번 실행해 도구 4종을 `publish\`로 발행합니다.
2. **레포 폴더 트리 전체**(발행된 `publish\` 산출물 포함)를 폐쇄망 PC로 복사합니다. Track C 익스포터는 선행 문서를 읽지 않으므로 따로 챙길 자료는 없습니다.
3. 폐쇄망 PC에서 `Run-SparrowRunnerGui.cmd`를 실행하면 `SparrowRunner.Gui\publish\SparrowRunner.Gui.exe`를
   자동으로 사용하고, 러너는 `publish\SparrowSyntaxFix.exe` / `publish\SparrowCommentFix.exe`를 자동으로 집어 씁니다
   (`dotnet build`/NuGet 복원 불필요).

기본 발행은 self-contained라 대상 PC에 `.NET` 런타임이 필요 없습니다. 자세한 절차는
[`docs/usage.md`](../docs/usage.md#폐쇄망-반입오프라인-배포)의 "폐쇄망 반입(오프라인 배포)" 절을 참고하세요.

---

## 더 읽을 것

- 구조와 프로세스 경계, 러너 CLI 계약: [`docs/architecture.md`](../docs/architecture.md)
- 규칙 추가 / 새 언어 트랙 추가: [`docs/extending.md`](../docs/extending.md)
- 빌드·테스트·인코딩 규약: [`CONTRIBUTING.md`](../CONTRIBUTING.md)
