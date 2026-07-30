# Sparrow Static Analysis Handoff

## 작업 목적

이 스킬은 폐쇄망 환경에서 Sparrow 정적분석 결과를 줄이기 위한 헬퍼다.

이번 세션의 핵심 목적은 다음과 같았다.

- Track A/B CLI가 기존 Sparrow 코드 규칙 검출 항목을 더 많이 자동 보정하도록 보완한다.
- 사용자는 명령줄 인자 조합보다 GUI에서 파일/폴더/프로젝트 범위를 직접 선택해 실행하기를 원한다.
- 협업 환경에서는 전체 솔루션을 무조건 수정하지 않고, 솔루션/프로젝트/폴더/파일 단위로 작업 범위를 제한해야 한다.
- Track C는 Track A/B 이후 남은 검출 항목을 LLM 또는 사람이 고칠 수 있도록 XLS를 체커별 Markdown 항목 파일로 분리한다.

정리하면, 이 스킬은 일반 코드 포맷터가 아니라 Sparrow 검출 감소를 위한 폐쇄망용 원샷 도구다. 자동화 가능한 것은 Track A/B에서 제한적으로 고치고, 판단이 필요한 것은 Track C가 체커별 md로 분리해 넘긴다.

> **Track C 단순화(현행)**: Track C는 순수 xls→md 분리 익스포터다. 선행 문서(체커 가이드·프롬프트·판정 계약)를
> 일절 요구하지 않으며, 출력은 **체커 키 폴더 + 그 안의 항목 md**(`<체커키>/{ID}_{파일명}_{라인}.md`) 뿐이다 —
> `items/`도, `index.csv`도, `checkers.md`도, 요약/작업지침 md도 만들지 않는다. 그 위에 얹혀 있던 LLM 판정 패키징
> 레이어와 그 부속 문서 폴더는 전부 제거되었다. 아래 과거 세션 기록 중 그 레이어(또는 옛 index.csv 산출)를
> 전제한 서술은 더 이상 유효하지 않다.

## 현재 사용자 요구

사용자는 `tools/Run-SparrowRunnerGui.cmd`로 실행되는 GUI를 주 작업 표면으로 사용한다.

사용 흐름:

1. 대상 `.sln`, `.csproj`, 폴더 또는 단일 `.cs` 파일을 선택한다.
2. 좌측 작업 범위 트리에서 이번 작업 대상 프로젝트/폴더/파일을 선택한다.
3. Track A/B/C를 한 화면에서 선택한다.
4. 실행하면 Track A/B 자동 수정과 Track C md 분리가 같은 선택 파일 범위를 공유한다.

중요한 UX 결정:

- 사용자는 `-Rules foreachcast` 같은 CLI 직접 인자를 선호하지 않는다.
- GUI나 PowerShell runner가 필요한 선택지를 직접 묻거나 체크박스로 제공해야 한다.
- Track A/B/C는 통합 GUI에서 다뤄야 한다.
- GUI 좌측은 단순 폴더 루트가 아니라 Visual Studio Solution Explorer처럼 프로젝트 구조를 보여줘야 한다.

## 이번 세션 주요 변경

### GUI 범위 선택

변경 파일:

- `tools/SparrowRunner.Gui/MainWindow.xaml`
- `tools/SparrowRunner.Gui/MainWindow.xaml.cs`
- `tools/SparrowRunner.Gui/SourceScopeNode.cs`
- `tools/SparrowRunner.Gui/SourceScopeDiscovery.cs`
- `tools/SparrowRunner.Gui/ScopeManifestWriter.cs`

구현 내용:

- 좌측에 Solution Explorer 형태의 3상태 체크 트리를 추가했다.
- 선택 파일 목록을 임시 CSV manifest로 만들고 Track A/B/C가 같은 manifest를 사용한다.
- `bin`, `obj`, `.git`, `.vs`, `packages`, generated 파일은 기본 제외한다.
- `IncludeGeneratedCheck`를 켜면 생성 파일도 범위 탐색에 포함한다. **(이후 세션에서 폐기 — GUI 옵션 3종을 없애고 생성 파일은 언제나 제외한다. 포함이 필요하면 CLI 러너의 `-IncludeGenerated` 를 쓴다.)**
- 사용자가 일부 파일만 선택한 뒤 새로고침하거나 generated 옵션을 바꿔도 기존 선택을 최대한 보존한다.
- `.sln` 파일을 선택하면 `.sln` 내부 `Project(...) = ..., "*.csproj"` 항목을 파싱해 프로젝트 노드를 만든다.
- 실제 `C:\Users\user\source\repos\myapp-wpf\MyApp.sln` 기준으로 프로젝트 9개가 좌측 루트 아래 표시되는 것을 확인했다.

검증한 실제 솔루션 프로젝트:

- `MyApp`
- `ResourceLib`
- `MyApp.Model`
- `MyApp.ViewModel`
- `MyApp.Message`
- `CommonLib`
- `SampleVerification`
- `SampleMessageVerifierHost`
- `SampleUIMapVerifierHost`

### Track A/B 범위 제한

변경 파일:

- `tools/_internal/SparrowSyntaxFix/Run-SparrowSyntaxFix.ps1`
- `tools/_internal/SparrowCommentFix/Run-SparrowCommentFix.ps1`

구현 내용:

- GUI가 넘긴 `-FilesFrom` CSV를 Track A/B runner에 전달한다.
- Track A runner에서 `-FilesFrom`이 있을 때 전체 root를 위치 인자로 함께 넘겨 전체 루트와 합집합 처리되던 문제를 수정했다.
- Git 처리도 선택 파일 범위로 제한했다.
- 선택 파일이 있을 때 commit은 `git commit --only --pathspec-from-file=... --pathspec-file-nul`을 사용한다.
- 다른 개발자가 이미 stage한 선택 범위 밖 변경이 Sparrow 자동 커밋에 섞이지 않도록 했다.
- `-VerifyCmd` 실패 시 `git checkout -- *.cs`로 되돌리지 않고, 규칙 실행 직전 선택 파일 백업을 복원한다. 이로써 사용자의 기존 unstaged 변경을 보존한다.
- `-FilesFrom`이 비어 있거나 root 밖 파일뿐이면 전체 `.cs`로 확대하지 않고 실패시킨다.
- 파일 목록 CSV는 `경로` 컬럼을 우선 사용하고, `파일명/path/filepath/file/fullpath` 및 newline list도 지원한다.
  (익스포터가 index.csv를 만들던 시절의 호환 규칙이며, 지금도 임의 CSV/목록을 그대로 받는다.)

검증한 실제 임시 Git 시나리오:

- Track A: 선택된 `a.cs`만 커밋되고 기존 staged `b.cs`는 staged 상태로 보존됨.
- Track A: `VerifyCmd` 실패 시 규칙 적용 전 dirty `a.cs` 내용이 복원되고 커밋은 생성되지 않음.
- Track B: 선택된 `a.cs`만 커밋되고 기존 staged `b.cs`는 staged 상태로 보존됨.

### Track C XLS 범위 필터

변경 파일:

- `tools/_internal/SparrowXlsExport.Core/SparrowExporter.cs`
- `tools/_internal/SparrowXlsExport/Program.cs`
- `tools/_internal/SparrowXlsExport.Core/CoreTests/Program.cs`

구현 내용:

- `ExportOptions`에 `RootPath`, `FilesFrom`을 추가했다.
- CLI에 `--root`, `--files-from`을 추가했다.
- Track C exporter가 XLS 행을 severity/checker/max보다 먼저 선택 파일 범위로 필터링한다.
- XLS `경로`가 절대 파일이면 정확 비교한다.
- XLS `경로`가 디렉터리이면 `경로 + 파일명`으로 비교한다.
- 상대 경로는 source root 기준으로 해석한다.
- `경로`가 비어 있고 `파일명`만 있으면 전체 source root에서 basename이 유일할 때만 매칭한다.
- 같은 파일명이 source root에 둘 이상 있으면 추측하지 않고 제외한다.

추가 테스트:

- 절대경로 매칭
- 디렉터리 + 파일명 매칭
- root 상대경로 매칭
- basename 유일 매칭
- source root 내 중복 basename 제외
- 범위 밖 파일 제외
- `Max`가 scope filter 이후 적용되는지 확인
- 빈 files-from은 0건 처리

## Track별 현재 역할

| Track | 역할 | 자동 수정 여부 |
| --- | --- | --- |
| Track A | C# 코드 규칙 자동 수정 | Roslyn 기반으로 제한적 자동 수정 |
| Track B | 주석/레이아웃 자동 수정 | Roslyn/trivia 기반으로 제한적 자동 수정 |
| Track C | Sparrow XLS를 체커별 폴더의 항목 md로 분리(`<체커키>/{ID}_{파일명}_{라인}.md`) | 대상 코드 직접 수정 없음 |

Track A/B는 검증 가능한 정형 패턴만 자동 수정한다. Track C는 판단이 필요한 보안/품질/잔여 코드 규칙을 사람/LLM이 볼 수 있게 위치와 대상 코드로 분리해 두는 단계다.

## 검증 완료 명령

이번 세션에서 통과 확인한 명령:

```powershell
dotnet build skills/sparrow-static-analysis/tools/SparrowRunner.Gui/SparrowRunner.Gui.csproj -c Release --nologo
dotnet build skills/sparrow-static-analysis/tools/_internal/SparrowXlsExport/SparrowXlsExport.csproj -c Release --nologo

$files=@(
  'skills/sparrow-static-analysis/tools/_internal/SparrowSyntaxFix/Run-SparrowSyntaxFix.ps1',
  'skills/sparrow-static-analysis/tools/_internal/SparrowCommentFix/Run-SparrowCommentFix.ps1'
)
foreach($f in $files){
  $tokens=$null; $errors=$null
  [System.Management.Automation.Language.Parser]::ParseFile($f,[ref]$tokens,[ref]$errors)>$null
  if($errors.Count){ $errors; exit 1 }
}

dotnet run --project tools/_internal/SparrowXlsExport.Core/CoreTests/CoreTests.csproj -c Release -- --fixtures-only
powershell -NoProfile -ExecutionPolicy Bypass -File ./validate.ps1 -IncludeG2GateTests
powershell -NoProfile -ExecutionPolicy Bypass -File ./tests/e2e-lab/run-e2e.ps1
git diff --check
```

결과:

- GUI build: PASS
- SparrowXlsExport build: PASS
- Track A/B PowerShell parser: PASS
- CoreTests fixtures-only: PASS, 70 checks / 0 fails
- G2 게이트 시나리오: PASS
- Track C E2E: PASS
- `git diff --check`: PASS
- 실제 `MyApp.sln` scope probe: 프로젝트 9개, 462 files discovered, 9 excluded, 453 selected

주의: `run-e2e.ps1`은 `sample-before.xls`, `sample-after.xls`를 재생성하므로 테스트 후 해당 fixture 변경은 원복해야 한다.

## 다음 작업자가 알아야 할 리스크

- WPF GUI의 실제 화면 캡처 검증은 제한적으로만 수행했다. 빌드와 scope probe는 통과했지만, UI 스크롤/잘림/고DPI는 추가 수동 확인이 필요하다.
- `.sln` 프로젝트 파싱은 일반 C# 프로젝트 라인을 기준으로 한다. Solution Folder, Shared Project, SDK 특수 포맷은 필요 시 확장해야 한다.
- 같은 물리 폴더를 여러 `.csproj`가 참조하면 프로젝트 노드 간 파일 중복이 생길 수 있다. 선택 manifest는 중복 제거하므로 실행 자체는 중복되지 않는다.
- Track A/B runner의 Git 격리는 Windows Git 2.48.1에서 확인했다. 오래된 Git에서 `git commit --only --pathspec-from-file` 지원 여부를 확인해야 한다.
- Track C의 basename-only 매칭은 intentionally conservative다. 경로가 비어 있고 동명 파일이 있으면 제외된다.
- 접근 실패 디렉터리는 아직 UI에 별도 warning으로 표시하지 않는다. 필요하면 `SourceScopeDiscovery`에 skipped directory count/message를 추가한다.

## 이어서 작업할 때 우선순위

1. GUI 실제 실행 화면에서 `.sln` 선택 시 프로젝트 9개가 바로 펼쳐져 보이는지 확인한다.
2. `myapp-wpf` 같은 대형 repo에서 파일/프로젝트 단위 선택 후 Track A/B DryRun이 선택 파일만 대상으로 잡는지 확인한다.
3. Track C XLS에 선택 파일 범위가 제대로 반영되는지 실제 XLS로 확인한다.
4. 필요한 경우 좌측 트리에 검색/필터, 선택된 파일 수 per-project 표시를 추가한다.
5. GUI 문구가 깨져 보이는 파일이 있으면 UTF-8/CRLF 정책을 정리한다.

## 커밋 대상 주의

현재 작업은 `skills/sparrow-static-analysis` 내부 변경만 커밋해야 한다.

다른 작업자가 만든 것으로 보이는 다음 변경은 이 세션 목적과 무관하므로 건드리지 않는다.

- repo 루트 `README.md`
- `skills/sparrow-code-review/`
