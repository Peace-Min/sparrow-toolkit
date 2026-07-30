# Sparrow Static Analysis 사용/확장 가이드

`sparrow-static-analysis`는 Sparrow 정적분석 결과 조치를 반복 가능하게 만들기 위한 폐쇄망용 헬퍼다. 정형화된 코딩/주석 위반 패턴은 Roslyn 기반 CLI로 자동 조치하고, 보안/품질 판단 항목과 예외 케이스는 **체커별 Markdown 파일로 분리**해 LLM 또는 개발자가 체커 단위로 작업한다.

> **Track C는 순수 익스포터다.** 선행 문서(체커 가이드·프롬프트·판정 계약)를 일절 요구하지 않는다. 입력은 Sparrow `.xls` 하나이고, 출력은 체커 키 폴더 + 그 안의 항목 md(`<체커키>/{ID}_{파일명}_{라인}.md`) 뿐이다.

## 빠른 실행

Visual Studio 사용자는 다음 솔루션을 연다.

```text
skills/sparrow-static-analysis/SparrowRunner.Gui/SparrowRunner.Gui.sln
```

명령줄에서 GUI를 바로 실행하려면 다음 파일을 사용한다.

```text
skills/sparrow-static-analysis/tools/Run-SparrowRunnerGui.cmd
```

## GUI 화면 구성 — 대분류 2개

GUI 최상단에서 **무엇을 할지** 먼저 고른다. 두 대분류는 입력도, 위험도도 다르므로 화면이 통째로 갈린다.

```text
┌ Sparrow Helper ────────────────────────────────────────┐
│ [ 코드 자동수정 (C#) ]   [ XLS 분리 (모든 언어) ]        │ ← 대분류 전환
├────────────────────────────────────────────────────────┤
│  (선택된 대분류 전용 화면)                               │
├────────────────────────────────────────────────────────┤
│  실행 / 중지 / 실행 로그 (공유)                          │
└────────────────────────────────────────────────────────┘
```

| 대분류 | 하위 탭 | 입력 | 범위 트리 | 성격 |
| --- | --- | --- | --- | --- |
| **코드 자동수정 (C#)** | **[코드 규칙]** / **[주석·레이아웃]** | 대상 `.sln`/`.csproj`/폴더 | **로컬 소스 스캔** | 소스 파일을 **수정**(파괴적, 커밋은 안 함), **C# 전용** |
| **XLS 분리 (모든 언어)** | (없음) | **Sparrow 결과 XLS 하나** | **XLS 검출 경로** | **읽기전용**, **프로젝트 경로 불필요**, 언어 무관 |

- **화면 명칭 ↔ 내부 트랙**: [코드 규칙] = Track A · [주석·레이아웃] = Track B · [XLS 분리] = Track C. 트랙은 코드/문서/커밋 메시지에만 쓰는 내부 명칭이고 화면에는 노출하지 않는다.
- 실행 버튼은 항상 **지금 선택된 화면**만 돌린다([코드 자동수정]에서는 선택된 하위 탭, [XLS 분리]에서는 XLS 분리). 실행 버튼 라벨도 그에 맞춰 `코드 규칙 수정 실행` / `주석·레이아웃 수정 실행` / `XLS 분리 실행` 로 바뀐다. 하단 로그창은 공유한다.
- **C/C++ 사용자는 [XLS 분리]만 쓰면 된다.** [코드 자동수정]은 Roslyn C# 파서 기반이라 다른 언어에 쓸 수 없고, 그 화면(및 프로젝트 경로 입력)은 [XLS 분리] 대분류에서는 아예 렌더되지 않는다.
- `SparrowRunner.Gui.exe --trackc-xls <경로>` 로 기동하면 [XLS 분리] 대분류가 자동 선택된다.

### GUI 는 커밋하지 않는다 (실행 옵션이 없는 이유)

[코드 자동수정] 화면에는 실행 방식 옵션이 없다. GUI 실행은 **언제나 "파일만 수정, 커밋 없음"** 이다 — 러너에 `-NoCommit` 을 고정으로 넘긴다.

- 실행이 끝나면 로그와 요약바에 `N개 파일 수정됨 — 커밋하지 않았습니다. git diff 로 검토 후 커밋하세요.` 가 뜬다. [대상 폴더 열기] 버튼으로 폴더를 바로 열 수 있다.
- 변경 검토는 `git diff` 로 한다(라인 단위라 DryRun 의 "바뀔 파일·건수" 보고보다 상위 호환이다). 커밋은 사용자가 직접 한다.
- 생성 파일(`.g.cs` / `.designer.cs` / `obj`·`bin` 등)은 GUI 에서 **언제나 제외**한다 — 빌드가 다시 만들어 내므로 고칠 이유가 없다.
- **자동 커밋(`-Commit`) · `-DryRun` · 생성 파일 포함(`-IncludeGenerated`) · 규칙별 컴파일 게이트(`-VerifyCmd`) 는 CLI 러너 옵션으로 그대로 남아 있다.** 자동화/CI 에서 필요하면 `Run-SparrowSyntaxFix.ps1` / `Run-SparrowCommentFix.ps1` 을 직접 호출한다.

### XLS 범위 트리 = 팀 분담 (크로스-PC 불일치 없음)

[XLS 분리] 화면 왼쪽의 **작업 범위(XLS 경로)** 트리는 **로컬 소스를 뒤지지 않는다.** xls 가 스스로 적어 둔 검출 경로(`SparrowExporter.ListPaths`, 어떤 파일도 쓰지 않음)를 디렉토리 트리로 만든 것이다.

- 리프 = 파일(그 파일의 검출 건수), 폴더 = 하위 합계. 폴더를 체크하면 하위 전체가 선택된다.
- **공통 접두는 접는다.** 실 xls 는 모든 경로가 `D:\Work\ModuleB\branches\ModuleB\release\2026-01-01\` 처럼 긴 상위 폴더를 공유한다. 그 "자식이 하나뿐인" 체인은 트리에서 빼고 트리 위에 `공통 경로: …` 한 줄로 보여 주므로, 트리는 **실제 분기 폴더**(ModuleA/Core/ModuleB/src…)부터 시작한다. 접는 것은 **표시뿐**이고 선택·매칭에는 언제나 xls 원본 절대경로 전체를 쓴다. 길어서 잘리는 이름은 말줄임 + 마우스를 올리면 전체 경로가 뜬다(가로 스크롤 없음).
- 요약: `선택 N개 파일 · M건 (전체 …)`. **아무것도 체크하지 않으면 전건**(범위 필터 없음).
- 선택은 **xls 원본 경로 문자열 그대로** 익스포터에 `--files-from` 으로 넘어간다(`--root` 는 넘기지 않는다). 즉 **xls 를 자기 경로로 거르는** 완전일치 매칭이므로, 팀원마다 체크아웃 위치가 달라도 어긋날 수 없고 확장자(언어)와도 무관하다.
- 반대로 **크로스-PC 상대경로 매칭(Tier 2)** 은 **로컬 소스 경로를 직접 줄 때만** 해당된다 — [코드 자동수정]의 범위 트리, 또는 CLI 에서 `--root` + `--files-from` 을 함께 줄 때. 그 경우 전혀 매칭되지 않으면 조용한 빈 결과 대신 **[범위 불일치]** 진단이 로그에 뜬다.

## 폐쇄망 반입(오프라인 배포)

GUI와 러너는 평소 `dotnet run`/`dotnet build`로 동작한다. 이는 대상 PC에 `.NET SDK`와 NuGet 복원(=인터넷)을 요구하므로, 인터넷이 없는 폐쇄망 PC에서는 그대로 실행되지 않는다. 오프라인 반입은 다음 순서로 한다.

1. **인터넷 + `.NET SDK`가 있는 PC**에서 발행 스크립트를 실행한다. 도구 4종(Track A/B/C CLI + WPF GUI)이 각 프로젝트의 `publish\` 폴더로 발행된다.

   ```powershell
   # 기본: self-contained win-x64 (대상 PC에 .NET 런타임 불필요)
   .\skills\sparrow-static-analysis\tools\publish-airgap.ps1

   # 산출물 크기를 줄이려면(대상 PC에 .NET 8 런타임 필요)
   .\skills\sparrow-static-analysis\tools\publish-airgap.ps1 -FrameworkDependent

   # 무엇을 어디로 발행할지 미리보기(빌드 안 함)
   .\skills\sparrow-static-analysis\tools\publish-airgap.ps1 -DryRun
   ```

2. **`skills/sparrow-static-analysis` 폴더 트리 전체**를 폐쇄망 PC로 복사한다. 반드시 함께 넘겨야 하는 것:
   - 방금 생성된 `publish\` 산출물 4곳(`SparrowRunner.Gui\publish\`, `_internal\SparrowSyntaxFix\publish\`, `_internal\SparrowCommentFix\publish\`, `_internal\SparrowXlsExport\publish\`)
   - `tools\`의 러너/진입점(`Run-SparrowRunnerGui.cmd`, `Run-SparrowAll.cmd`, `_internal\...\Run-*.ps1`, `Compare-Sparrow.ps1`)

   > Track C 익스포터는 선행 문서를 읽지 않으므로 별도 반입 자료가 없다. 체커별 가이드를 각자 쌓아두었다면(`references\checkers\`) 그것만 원하는 대로 함께 옮기면 된다.

   > `publish\` 산출물은 머신마다 생성되는 것이라 저장소에 커밋하지 않는다(`.gitignore` 제외 대상). 반입은 파일 복사로 한다.

3. 폐쇄망 PC에서 `tools\Run-SparrowRunnerGui.cmd`를 실행한다. 이 배치는 `SparrowRunner.Gui\publish\SparrowRunner.Gui.exe`가 있으면 그것을 바로 실행하고(없을 때만 `dotnet run`으로 폴백), 러너는 `publish\SparrowSyntaxFix.exe` / `publish\SparrowCommentFix.exe`를 자동으로 집어 쓴다(`dotnet build`/복원 불필요). Windows 기본 `powershell.exe`만 있으면 된다.

### 대상 PC 런타임 요건

| 발행 모드 | 스위치 | 대상 PC .NET 요건 | 산출물 크기 |
| --- | --- | --- | --- |
| self-contained (기본) | (없음) | **불필요**(런타임 동봉) | 큼(도구별 수십~수백 MB) |
| framework-dependent | `-FrameworkDependent` | GUI = **.NET 8 Desktop Runtime**, CLI 3종 = **.NET 8 Runtime** | 작음 |

`win-x64` 자기완결(self-contained) 발행이 폐쇄망 무설치 배포에 가장 안전한 기본값이다. 대상 PC에 이미 .NET 8 런타임이 관리·배포되어 있다면 `-FrameworkDependent`로 용량을 줄일 수 있다.

## 구성

| 구분 | 목적 | 위치 |
| --- | --- | --- |
| 코딩 규칙 자동 조치 | `var`, 괄호, object initializer, 배열 선언 등 사전 정의된 C# 위반 패턴을 Roslyn 기반으로 수정 | `tools/_internal/SparrowSyntaxFix` |
| 주석/레이아웃 자동 조치 | 주석 공백, 마침표, trailing comment, member blank, one statement 등 사전 정의된 주석/레이아웃 패턴을 수정 | `tools/_internal/SparrowCommentFix` |
| 판단 필요 항목 분리 | 예외 처리, null, resource leak, TOCTOU, encapsulation 등 보안/품질 항목을 체커별 md로 분리(파일 위치 + 수정 대상 코드 위치) | `tools/_internal/SparrowXlsExport(.Core)` |
| 전/후 회귀 게이트(G2) | 수정 전/후 Sparrow xls를 비교해 검출 소멸/신규 회귀를 판정 | `tools/Compare-Sparrow.ps1` |

## 디렉터리 구조

```text
skills/sparrow-static-analysis/
  SparrowRunner.Gui/
    SparrowRunner.Gui.sln        # 사용자/Visual Studio 진입점
  tools/
    Run-SparrowRunnerGui.cmd     # GUI 실행
    Run-SparrowAll.ps1           # 코딩/주석 자동 조치 일괄 실행
    Compare-Sparrow.ps1          # 전/후 xls 비교 G2 게이트
    SparrowRunner.Gui/           # WPF GUI 소스
    _internal/
      SparrowSyntaxFix/          # 코딩 규칙 자동 조치 엔진
      SparrowCommentFix/         # 주석/레이아웃 자동 조치 엔진
      SparrowXlsExport/          # Sparrow XLS 파서 CLI
      SparrowXlsExport.Core/     # 체커별 md 분리 익스포터 코어
  tests/
    e2e-lab/                     # 익스포터 -> 수정+빌드(G1) -> G2 파이프라인 E2E
    g2-gate-tests.ps1            # Compare-Sparrow G2 게이트 시나리오
  references/
    (checkers/)                  # (선택) 각자 로컬로 쌓는 체커별 조치 노트 - gitignore, 레포 미포함
    (sparrow-official-rules/)    # (선택) Sparrow 공식 Rule 원문 - 각자 반입, 레포 미포함
    real-fix-patterns/           # 폐쇄망 수정 사례의 익명화 패턴
```

## 확장 기준

- 자동 조치는 반드시 반복 발생하는 정형 패턴에 한정한다.
- Roslyn 구문 트리 또는 trivia 범위 안에서 수정하고, 문자열 리터럴 같은 비대상 영역은 건드리지 않는다.
- 판단이 필요한 보안/품질 항목은 자동수정하지 않고 체커별 md로 분리해 넘긴다.
- Track C는 어떤 항목도 버리지 않는다. 체커 키를 모르는 행도 그냥 하나의 항목 md가 된다(전건 정책 — 심각도/체커 필터 없음).
- 폐쇄망 실제 코드를 학습 자료로 남길 때는 `references/real-fix-patterns/`에 최소 before/after 형태만 익명화해서 기록한다.

## Track C 출력물

익스포터는 지정한 출력 폴더에 **체커 키마다 폴더 하나**를 만들고 그 안에 항목 md만 넣는다.
인덱스·요약·작업지침 같은 부속 파일은 일절 만들지 않는다.

```
<출력 폴더>/
  FORWARD_NULL/
    6893031_Foo.cs_88.md
    6893044_Bar.cs_12.md
  PRACTICE.OBJECT_INITIALIZATION.NOT_USED_INITIALIZER/
    6893062_Baz.cs_144.md
```

| 산출물 | 내용 |
| --- | --- |
| `<체커키>/` | 폴더명 = 체커 키 원문(잘리지 않는다). 폴더 안 md 개수 = 그 체커의 검출 건수 |
| `<체커키>/{ID}_{파일명}_{라인}.md` | 항목 1건 = 파일 1개. **xls 컬럼만 렌더링한다** — 필드표(ID/위험도/체커키/체커명/라인/파일명/함수/경로) + `## 체커 설명` + `## 소스 코드`. 도구는 작업 지시문·앵커 마커를 일절 주입하지 않는다(수정 범위 판단은 사용자·체커별 몫) |

체커별 조치 규칙은 **레포가 배포하지 않는다.** 규칙은 `references/checkers/`에 쌓는 **이름 붙인 라이브러리**다(gitignore 대상). 규칙 하나 = `<이름>.md`(단, `_`로 시작하는 파일 제외; 이름 = 파일명, 내용 = 파일 내용)이고, 한 규칙을 여러 체커에 재사용할 수 있다.

### 규칙 라이브러리 + 체커 지정 (자동 매핑 없음)

**핵심: 이름 기준 자동 매핑이 없다.** 규칙 파일명이 체커 키와 같아도 그것만으로는 부착되지 않는다. 체커에 규칙을 붙이려면 **사용자가 직접 지정**해야 한다. 지정은 `references/checkers/_assignments.json`(`{ "<체커 키>": "<규칙 이름>" }`)에 저장되며, **명시적으로 지정한 것만** 담긴다. 다음에 같은 체커가 나오면 그 지정이 **미리 채워진다(기억)**.

GUI의 [XLS 분리] 화면에는 xls/출력/범위 컨트롤과 함께 요약(**"검출 체커 N종 · 매핑 M · 미매핑 K"**, 지정 기준)과 **[체커 규칙 관리]** 버튼만 있다. 규칙 CRUD와 체커 지정은 **별도 창(체커 규칙 관리)**에서 한다:

- **A. 규칙 라이브러리** (xls 무관): 이름 붙인 규칙 목록 + 에디터. [새 규칙](이름+내용 입력 후 [규칙 저장])으로 만들고, 목록에서 골라 편집한다. 규칙 md는 UTF-8(BOM 없이)로 저장된다. 창을 열면 **첫 규칙이 자동 선택**되어 이름·내용이 채워진 상태로 보이고, 파괴적인 **[선택 규칙 삭제]** 는 [새 규칙] 옆이 아니라 **목록 아래 우측**에 따로 있다(실수 클릭 방지 · 확인 다이얼로그는 그대로).
- **B. 체커 매핑** (현재 xls의 검출 체커): 각 체커 행 = 체커 키 + 건수 + **규칙 선택 ComboBox**(라이브러리 규칙들 + "— 없음 —"). 규칙을 고르면 그 체커의 지정이 바뀌고, **기억된 지정은 미리 선택**되어 나타난다(파일명이 체커 키와 같아도 지정 안 했으면 "— 없음 —"). [지정 저장]이 `_assignments.json`에 기록한다. 미지정 체커가 위로 정렬된다.

**실행하면** `_assignments.json`을 읽어 **지정된 체커만** 그 규칙을 해당 체커의 모든 항목 md에 self-contained 부착하고(멱등), 지정 안 된 체커는 순수 출력이다. 흐름은 **xls 로드 → 규칙 관리 창에서 지정 → 실행(지정만 부착)** 이다. CLI에서는 `--guides <폴더>`를 주면 그 폴더의 `_assignments.json` 지정대로 부착한다(주지 않으면 순수).

## 진단 로그

"언제·어떤 입력에서 뭐가 잘못됐나"를 나중에 판단할 수 있도록 네 종류의 증거(로그 3종 + 창 스냅샷)가 자동으로 남는다.
기록은 전부 best-effort다 — 폴더가 읽기전용이어도 앱/테스트는 그대로 동작한다.

### 1) GUI 세션 로그 — `%LOCALAPPDATA%\SparrowRunner\logs\session-<yyyyMMdd-HHmmss>.log`

화면 로그와 **같은 내용 + 줄마다 `HH:mm:ss.fff`**. 맨 앞에 시작 헤더가 붙는다: 앱 버전, 실행 파일, 시작 인자,
스킬 루트, guides 폴더, 로그 폴더, OS, .NET 런타임, PID/아키텍처, 작업 폴더.
Program Files 같은 쓰기 불가 위치에서 실행해도 되도록 설치 폴더가 아니라 `%LOCALAPPDATA%`에 쓴다.
최신 20개만 보관하며, `--log-dir <DIR>`로 위치를 바꿀 수 있다(테스트가 이 옵션으로 실 폴더 오염을 막는다).

미처리 예외(Dispatcher/AppDomain/Task)도 이 파일에 기록된다(예외를 삼키지는 않는다 — 증거만 남긴다).
정상 종료 시 마지막 줄은 `세션 종료 (정상)`이므로, **이 표식 없이 끊긴 로그는 비정상 종료(크래시/강제 종료)**로 읽으면 된다.

### 2) Track C 실행 리포트 — 같은 폴더의 `trackc-<stamp>.json` + `trackc-<stamp>.log`

Track C를 한 번 돌릴 때마다 **기계 판독 가능한** 실행 증거가 남는다(사람용 요약은 같은 이름의 `.log`).

| 필드 | 뜻 |
| --- | --- |
| `inputXls` / `inputSizeBytes` / `inputSha256` | 어떤 xls였는지 — 해시가 있어 나중에 "같은 입력인가"를 증명할 수 있다 |
| `outDir` / `guidesDir` / `startedUtc` / `elapsedMs` / `toolVersion` | 재현에 필요한 실행 맥락 |
| `options` | 범위 필터(files-from) 사용 여부·경로, `root`, `severity`/`checker`/`status`/`max` |
| `sheet` / `totalRows` / `matchedRows` / `writtenMd` / `checkerFolders` | 어느 시트에서 몇 건을 읽고 몇 건을 썼는지 |
| `checkerCounts` | 체커별 기록 건수 전부 |
| `assignments` | 체커별 `ruleName` / `ruleExists` / `itemsAttached` / `itemCount` — "지정했는데 왜 안 붙었나"(규칙 파일 유실)가 바로 보인다 |
| `unmappedCheckers` | 규칙이 안 붙은 체커 |
| `scope` | `mismatch` + [범위 불일치]/[범위 경고] 원문 |
| `warnings` | 병합 셀, 0건 매칭, `--max` 절단, 매핑 미실행, 지정 유실 등 |

**리포트는 출력 폴더에 쓰지 않는다.** Track C 출력 폴더는 "체커 폴더 + 항목 md만, 부산물 0" 계약을 유지해야 하므로
리포트는 로그 폴더로 간다. CLI는 `--report <PATH>`를 줄 때만 만들고, 주지 않으면 **산출물이 바이트 동일**하다.

```powershell
# 리포트까지 남기며 익스포트(출력 폴더는 그대로 순수)
.\tools\_internal\SparrowXlsExport\bin\Release\net8.0\SparrowXlsExport.exe issues.xls `
    --out C:\work\out --guides C:\...\references\checkers --report C:\work\logs\run1.json
```

### 3) 테스트 진단 — `tests\_logs\` (gitignore)

| 파일 | 내용 |
| --- | --- |
| `uia-<stamp>\result.log` | UIA 하네스의 체크별 PASS/FAIL 전문(기대/실제 수치 포함) |
| `uia-<stamp>\tree-<n>-<단계>-iter<i>.txt` | 단계별(메인창 로드 / 관리창 오픈 / 규칙 저장 / 지정 저장 / 실행 후 / 범위 좁힌 실행 후 / 코드 자동수정 화면) **UIA 트리 덤프**. 한 줄 = 한 요소: `ControlType \| id=… \| name=… \| Rect(x,y,w,h) \| Off=… \| En=… \| Val="…"` |
| `uia-<stamp>\gui-logs\iter<i>\` | 그 실행에서 앱이 스스로 남긴 세션 로그 + Track C 리포트 |
| `uia-<stamp>\shots\iter<i>\*.png` | **창 스냅샷** — 앱이 스스로 렌더한 실제 창 이미지(아래 4절) |
| `uia-<stamp>\FAILURE-CONTEXT-iter<i>.txt` | 실패가 있을 때만 — 실패 목록 + 그 시점 트리 덤프 |
| `validate-<stamp>.log` | `validate.ps1` 전체 출력(콘솔에도 그대로 나간다). 실패 시 마지막에 이 경로를 안내한다 |

`uia-*`는 최신 10개, `validate-*.log`는 최신 10개만 보관한다(스냅샷은 `uia-*` 폴더 하위라 같이 회전한다).
위치는 `-LogRoot`(하네스) / `-LogDir`(validate)로 바꿀 수 있다.

**UI/UX 문제를 신고할 때**: `shots\**\*.png`(실제 화면)와 `tree-*.txt`(수치)를 **함께** 첨부한다. 트리 덤프의
`Rect`/`Off` 수치만으로 잘림(요소가 창 경계를 벗어남)·0크기·화면 밖·겹침을 판정할 수 있고, 하네스는 그 판정을
단정으로 박아 두었다(요소가 창 안에 있는지, `w>0/h>0`·`IsOffscreen=false`인지, 규칙 에디터 높이 ≥ 120px인지,
규칙 목록과 에디터가 겹치지 않는지, 관리창이 최소 900x560인지). 임계값은 `tests\gui-uia-tests.ps1` 상단 상수다.
수치로는 드러나지 않는 것(빈 콤보, 잘못된 문구/색, 어색한 여백)은 PNG를 열어 눈으로 판단한다.

### 4) 창 스냅샷 — `uia-<stamp>\shots\iter<i>\<순번>-<지점>-<타임스탬프>.png`

이 GUI는 설치되지 않는 커스텀 exe라서 **OS 자동화 허용목록에 올릴 수 없다 = 외부에서 스크린샷을 찍을 수 없다.**
그래서 앱이 **스스로 자기 창을 PNG로 렌더**한다(`RenderTargetBitmap` + `PngBitmapEncoder`).

- **실제 DPI 배율로 렌더**한다(`VisualTreeHelper.GetDpi`, 96 고정 금지) — 125%/150% 데스크톱에서도 화면과 같은 크기·선명도.
- 렌더 전에 **불투명 바탕(흰색 + 창 배경)** 을 먼저 깐다 — 투명 PNG는 판독이 불가능하다.
- `.tmp`로 쓰고 rename 하므로, 폴더를 감시하는 쪽이 **반쯤 쓰인 PNG를 보지 않는다.**
- 전부 best-effort다: 창이 아직 레이아웃되지 않았거나 폴더를 쓸 수 없으면 **실패를 로그로만** 남기고 앱은 그대로 동작한다.

캡처 시점은 두 가지다.

| 트리거 | 언제 | 파일명 지점 |
| --- | --- | --- |
| **자동** | 메인 창 로드 완료 / [체커 규칙 관리] 창 오픈 직후 / Track C 실행 완료 후(메인 창) | `main-loaded` · `manager-open` · `after-run` |
| **요청** | 스냅샷 폴더에 `capture.request` 파일이 생긴 순간 — **현재 활성(포커스) 창**을 즉시 캡처하고 요청 파일을 삭제한다 | 요청 파일 내용(비어 있으면 `request`) |
| **요청(하네스가 넣는 것)** | 대분류 두 화면 · 관리창 · 지정 저장 후 · XLS 범위 체크 상태 | `req-main`(XLS 분리) · `req-fix-section`(코드 자동수정) · `req-manager` · `req-assign-saved` · `req-xls-scoped` |

```powershell
# 스냅샷을 남기며 기동. 이 인자를 주지 않으면 스냅샷 기능 전체가 꺼진다(기존 동작 그대로).
.\SparrowRunner.Gui.exe --screenshot-dir C:\work\shots

# 임의 시점 캡처(콤보를 펼친 상태처럼 지나가는 화면을 남길 때)
"combo-expanded" | Set-Content -Encoding utf8 C:\work\shots\capture.request
```

성공·실패는 세션 로그에 한 줄씩 남는다: `snapshot: 03-req-main-20260727-103003-864.png (1360x860px)` /
`snapshot 실패: <사유>`. 하네스(`tests\gui-uia-tests.ps1`)는 트리 덤프와 **같은 단계**에서 요청 캡처를 넣고,
반복당 **PNG 5장 이상**(대분류 두 화면을 각각 담은 `req-main`=XLS 분리 · `req-fix-section`=코드 자동수정) · **유효 PNG**(시그니처 `89 50 4E 47` + IHDR, 10KB 초과 = 빈/투명 이미지 아님) ·
**PNG 픽셀 크기 ≈ UIA가 보고한 창 Rect(±10%)** 를 단정한다(잘못된 스케일 렌더 회귀 방지).

> 한계: 열려 있는 ComboBox 드롭다운·툴팁 같은 **Popup은 별도 창(HWND)** 이라 창 렌더에 포함되지 않는다.
> 펼친 목록 자체를 이미지로 남겨야 한다면 UIA 트리 덤프의 ListItem `Rect`를 함께 본다.

## 기본 검증

```powershell
dotnet build .\skills\sparrow-static-analysis\SparrowRunner.Gui\SparrowRunner.Gui.sln -c Release
dotnet build .\skills\sparrow-static-analysis\tools\_internal\SparrowSyntaxFix\SparrowSyntaxFix.csproj -c Release
dotnet build .\skills\sparrow-static-analysis\tools\_internal\SparrowCommentFix\SparrowCommentFix.csproj -c Release
dotnet build .\skills\sparrow-static-analysis\tools\_internal\SparrowXlsExport\SparrowXlsExport.csproj -c Release
```

PowerShell runner를 수정한 경우 파서 검사를 수행한다.

```powershell
$files = @(
  ".\tools\Run-SparrowAll.ps1",
  ".\tools\Compare-Sparrow.ps1",
  ".\tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1",
  ".\tools\_internal\SparrowCommentFix\Run-SparrowCommentFix.ps1"
)
foreach ($f in $files) {
  $tokens=$null; $errors=$null
  [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $f), [ref]$tokens, [ref]$errors) | Out-Null
  $errors
}
```

(위 구문검사는 `.\validate.ps1`이 이미 포함한다.)

Track C 익스포터/G2 게이트를 바꾼 경우:

```powershell
powershell -ExecutionPolicy Bypass -File .\validate.ps1 -IncludeG2GateTests
powershell -ExecutionPolicy Bypass -File .\tests\e2e-lab\run-e2e.ps1
dotnet run --project .\tools\_internal\SparrowXlsExport.Core\CoreTests\CoreTests.csproj -c Release -- --fixtures-only
```
