# 아키텍처 (기여자 필독)

이 문서는 **"왜 이렇게 나뉘어 있나"** 와 **"어디가 확장의 이음매인가"** 를 설명한다.
실제로 무언가를 추가하려면 이 문서를 읽고 [extending.md](extending.md) 로 간다.

---

## 1. 레이어와 프로세스 경계

세 층이다. **층 사이가 프로세스 경계이거나 라이브러리 경계**이고, 그게 이 레포의 확장성 전부다.

```text
        ┌──────────────────────────────────────────────────────────┐
   L1   │  SparrowRunner.Gui   (WPF · net8.0-windows)              │
        │  대분류/하위 탭 · 범위 트리 · 규칙 체크박스 · 공유 로그창   │
        └───────┬───────────────────────────────────┬──────────────┘
                │ ① 프로세스 경계 (Track A/B)        │ ② 라이브러리 경계 (Track C)
                │   powershell.exe <러너>.ps1        │   ProjectReference 로 직접 호출
                ▼                                   ▼
        ┌──────────────────────────────┐   ┌────────────────────────────────┐
   L2   │ 러너 (.ps1)                  │   │ SparrowXlsExport.Core          │
        │  Run-SparrowSyntaxFix.ps1    │   │  SparrowExporter.Run()         │
        │  Run-SparrowCommentFix.ps1   │   │  CheckerRuleMapper.Apply()     │
        └───────┬──────────────────────┘   └───────────┬────────────────────┘
                │ 프로세스 실행                          │ 같은 코어를 CLI 도 쓴다
                ▼                                       ▼
        ┌──────────────────────────────┐   ┌────────────────────────────────┐
   L3   │ 엔진 CLI (net8 콘솔)          │   │ SparrowXlsExport (CLI)         │
        │  SparrowSyntaxFix.exe        │   └────────────────────────────────┘
        │  SparrowCommentFix.exe       │
        └──────────────────────────────┘
```

**왜 이렇게 나눴나**

| 이유 | 효과 |
|---|---|
| **엔진 교체 자유** | GUI 는 러너를 `powershell.exe` 로 실행하고 stdout 을 로그창에 흘릴 뿐이다. 엔진이 C#이든 C++이든 Python이든 GUI 코드는 모른다. |
| **언어 확장** | 새 언어 트랙은 **새 엔진 + 새 러너**만 만들면 된다. GUI 는 탭 하나와 러너 경로 한 줄만 늘어난다. |
| **폐쇄망 배포 자유도** | 엔진이 별도 exe 라서 `publish-airgap.ps1` 로 각각 self-contained 발행해 파일 복사만으로 반입된다. 러너는 `-ExePath` / `publish\` 로 반입 exe 를 집어 쓰므로 대상 PC 에 SDK·NuGet 이 필요 없다. |
| **파괴적 작업 격리** | 소스를 실제로 고치는 건 엔진 프로세스다. 크래시가 GUI 를 죽이지 않고, 러너가 규칙 단위로 커밋/revert 를 관리할 수 있다. |

Track C 만 예외적으로 **라이브러리 경계**다(GUI 안에서 `SparrowXlsExport.Core` 를 직접 호출).
읽기 전용이고, 진행 상황을 로그창에 실시간으로 흘리며, 산출물 통계를 그대로 리포트로 만들어야 해서
프로세스를 나눌 이득이 없었다. 같은 코어를 `SparrowXlsExport` CLI 도 쓰므로 코드 중복은 없다.

> **새 언어 트랙은 둘 중 아무 쪽이나 골라도 된다.** 프로세스 경계(A/B 방식)가 기본 권장이다 —
> 엔진 기술 선택이 완전히 자유롭고, 폐쇄망 발행 경로가 이미 있다.

---

## 2. 러너 CLI 계약 — **확장의 이음매**

GUI 는 Track A/B 실행 시 러너 `.ps1` 에 **아래 인자만** 넘긴다
(`tools/SparrowRunner.Gui/MainWindow.xaml.cs` 의 `BuildJobs()`).
**이 계약을 지키는 `.ps1` 이면 GUI 에 그대로 붙는다.**

| 인자 | 값 | 의미 |
|---|---|---|
| `-Solution <경로>` | `.sln` / `.csproj` / 폴더 | 대상. 러너는 **`.sln`/`.csproj` 파일이면 `Split-Path -Parent` 로 그 폴더로 환원**하고, 폴더면 그대로 소스 루트로 쓴다 |
| `-Rules <a,b,c>` | 콤마 구분 규칙 키 | GUI 체크박스에서 켠 규칙들. 러너가 규칙마다 엔진을 한 번씩 돌린다 |
| `-LogDir <경로>` | 폴더 | 러너 실행 로그(`Run-<이름>.<stamp>.log`)를 쓸 곳. GUI 는 대상 루트를 준다 |
| `-FilesFrom <파일>` | CSV 또는 줄 목록 | 범위 트리에서 고른 파일 목록(manifest). **빈 목록이거나 소스 루트 밖 파일뿐이면 전체로 확대하지 않고 실패해야 한다** |
| `-Commit` \| `-NoCommit` | (스위치) | GUI 의 [규칙별 커밋 생성] 체크 상태에 따라 **둘 중 하나가 반드시** 온다 |

GUI 는 그 외 인자를 넘기지 않는다. `-DryRun` · `-IncludeGenerated` · `-VerifyCmd` · `-ExePath` 는
**CLI 러너 전용 옵션**으로만 존재한다(자동화/CI/폐쇄망).

### 러너가 하는 일 (계약 상세)

기존 러너 두 개(`tools/_internal/SparrowSyntaxFix/Run-SparrowSyntaxFix.ps1`,
`tools/_internal/SparrowCommentFix/Run-SparrowCommentFix.ps1`)가 이 순서를 구현한다.
**새 러너는 이 둘을 그대로 베끼면 된다.**

1. **소스 루트 확정** — `.sln`/`.csproj` 이면 부모 폴더, 폴더면 그대로.
2. **엔진 바이너리 확보** — 우선순위:
   `-ExePath` → 스크립트 옆 `publish\<엔진>.exe` → (csproj + SDK 가 있으면) **항상 증분 `dotnet build`**
   → 그래도 없으면 기존 `bin\Release\net8.0\<엔진>.dll`.
   *증분 빌드를 먼저 하는 이유*: 오래된 `bin` dll 을 그대로 쓰면 소스를 고쳐도 옛 규칙이 돌아
   "안 고쳐졌다"처럼 보이는 사고가 실제로 있었다. 폐쇄망은 `-ExePath`/`publish\` 로 우회한다.
3. **대상 파일 확정** — `-FilesFrom` 이 있으면 그 목록(소스 루트 아래 것만), 없으면 소스 루트 재귀 글롭.
   생성/백업 파일(`*.g.cs`, `*.Designer.cs`, `AssemblyInfo.cs`, `obj\`/`bin\` 등)은 기본 제외.
4. **규칙별 실행** — 고정 순서로 규칙 하나씩 엔진을 호출한다(로그에 규칙별 변경 건수가 남는다).
5. **(`-Commit` 일 때) 규칙별 커밋 + 컴파일 게이트** — 아래 3.3 참조.
6. **로그 파일 기록** — `-LogDir` 아래 타임스탬프 `.log`. 전체 stdout 이 남는다.

### 계약을 지키는 확인 방법

```powershell
# GUI 가 넘기는 것과 똑같은 형태로 러너를 직접 호출해 본다
.\tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1 `
    -Solution C:\work\Proj -Rules parens,obviousvar -LogDir C:\work\logs `
    -FilesFrom C:\work\files.csv -NoCommit
```

---

## 3. Track A/B 내부 — 구문 전용 재작성

### 3.1 핵심 성질: 프로젝트를 로드하지도 컴파일하지도 않는다

`RewriteEngine.Rewrite()` 는 `CSharpSyntaxTree.ParseText(source, LanguageVersion.Latest)` 로
**텍스트를 파싱만** 하고 `CSharpSyntaxRewriter` 를 돌린 뒤 `ToFullString()` 을 되돌려 준다.
MSBuild 프로젝트를 열지 않고, 컴파일도 하지 않고, 심볼 테이블도 만들지 않는다.

**얻는 것**

- 대상이 **레거시 비-SDK `.csproj`(.NET Framework 4.7.2)** 여도, 심지어 **빌드가 깨져 있어도** 동작한다.
  프로젝트를 부분 로드하다 실패해 일부 파일을 통째로 놓치는 사고(과거 실제 발생)가 원천적으로 없다.
- 대상 PC 의 SDK 버전·참조 어셈블리 상태와 무관하다.
- Roslyn 이 `ToFullString()` 으로 원문을 바이트 단위로 복원하므로, **매치가 없으면 파일은 아예 쓰이지 않는다**
  (mtime 도 안 바뀐다). 주석/공백/개행 trivia 가 전부 보존된다.

**대가**

- **의미 분석(semantic model)이 필요한 규칙은 못 한다.** 심볼이 실제로 무엇인지 모른다.
- 그래서 애매한 변환은 **보수적으로 갈라 둔다**:
  - `objectvar-safe`(선언 타입 == 생성 타입, 안전) vs `objectvar-narrowing`(상위 타입 → 실제 타입, `review-needed`)
  - `arrayvar-safe` vs `arrayvar-narrowing`
  - 판정 불가능한 것은 **건드리지 않는다**. 예: `Convert.ToXxx` 계열은 심볼 동일성을 보장할 수 없어 skip.
- **잔여 위험은 문서에 명시한다.** 예: `foreachcast` 는 숫자/`Nullable` 값 타입을 skip 하지만,
  named type 으로 선언된 enum 은 구문상 클래스와 구별할 수 없어 skip 되지 않는다 → 사람 리뷰 + 빌드/Sparrow 게이트가 최후 방어선.
- **비활성 `#if` 분기**는 disabled-text trivia 로 파싱되어 의도적으로 **수정하지 않는다**(빌드 구성을 모르면 안전하게 못 고친다).

### 3.2 파일 IO 안전성

`SourceFileIo` 가 담당한다.

- 원본의 **UTF-8 BOM 유무**와 **개행 스타일**을 그대로 유지한다(혼합 개행도 그대로 살아남는다 — 정규화하지 않는다).
- UTF-8 로 깨끗하게 round-trip 하지 않는 파일(UTF-16, 잘못된 바이트)은 **경고 후 skip**한다. 절대 손상시키지 않는다.
- **원자적 쓰기**: 같은 폴더에 임시 파일을 쓰고 target 위로 move 한다 — 크래시가 소스를 잘라먹을 수 없다.
- 한 파일에서 rewriter 가 예외를 던져도 **그 파일만 건너뛰고**(변경 없음) 전체 실행은 계속된다.

### 3.3 규칙별 커밋 + 컴파일 게이트 (러너)

`-Commit` 이면 러너가 **규칙 하나마다 커밋 하나**를 만든다. 롤백 단위가 규칙이 되어
"괄호는 채택, var 는 거부" 같은 선택적 되돌리기(`git revert <커밋>`)가 가능해진다.
`review-needed` 규칙은 커밋 메시지에 `검토필요` 가 드러난다.

- **작업 범위 격리**: `-FilesFrom` 이 있으면 커밋은 `git commit --only --pathspec-from-file=… --pathspec-file-nul`
  로 **선택 파일만** 담는다. 다른 개발자가 이미 stage 해 둔 범위 밖 변경이 Sparrow 자동 커밋에 섞이지 않는다.
- **`-VerifyCmd` 게이트**: 각 규칙 edits 후·커밋 전에 그 명령(보통 msbuild)을 실행한다.
  비정상 종료면 그 규칙의 미커밋 edits 를 되돌리고 커밋을 건너뛴 뒤(`[GATE] rule <r> reverted`) 다음 규칙으로 간다 —
  **게이트를 통과한 규칙만 커밋된다.**
- **사용자 변경 보존**: revert 는 `git checkout` 만으로 하지 않고, 규칙 실행 직전에 떠 둔 **선택 파일 백업**을 복원한다.
  덕분에 사용자의 기존 unstaged 변경이 날아가지 않는다.
- 게이트를 주지 않으면(`-Commit` 만) "빌드 게이트 없음 — 커밋 후 반드시 전체 빌드로 확인" 안내가 한 줄 뜬다.

### 3.4 파일 목록(manifest) 포맷

GUI 의 범위 트리는 선택 파일을 임시 CSV manifest 로 떨어뜨리고(`ScopeManifestWriter`),
러너/엔진이 `-FilesFrom` / `--files-from` 으로 읽는다. 파서는 관대하다:

- CSV 면 `경로` → `파일명` → `path` → `filepath` → `file` → `fullpath` 순으로 컬럼을 찾고, 없으면 첫 컬럼을 쓴다.
- 콤마가 없고 알려진 헤더도 아니면 **줄 단위 경로 목록**으로 읽는다.
- 상대 경로는 `--root` 기준으로 해석한다.

---

## 4. Track C 내부 — xls → 체커별 md

### 4.1 순수성 계약 (절대 어기지 말 것)

입력은 **Sparrow xls 하나**. 출력은 지정한 폴더 아래 **체커 키 폴더 + 그 안의 항목 md** 뿐이다.

```text
<출력 폴더>/
  FORWARD_NULL/
    6893031_Foo.cs_88.md
    6893044_Bar.cs_12.md
  PRACTICE.OBJECT_INITIALIZATION.NOT_USED_INITIALIZER/
    6893062_Baz.cs_144.md
```

- 폴더명 = **체커 키 원문**(잘리지 않는다). 폴더 안 md 개수 = 그 체커의 검출 건수.
- 항목 md 는 **xls 컬럼만 렌더링한다** — 필드표(ID/위험도/체커키/체커명/라인/파일명/함수/경로)
  \+ `## 체커 설명` + `## 소스 코드`. 도구는 작업 지시문·앵커 마커를 **주입하지 않는다**.
- **인덱스·요약·README·작업지침 같은 부산물을 0 개 만든다.** 실행 리포트(json)조차 출력 폴더가 아니라
  로그 폴더로 간다. CLI 는 `--report <PATH>` 를 줄 때만 리포트를 만들고, 주지 않으면 **산출물이 바이트 동일**하다.
- **선행 문서를 요구하지 않는다.** 체커 가이드도, 프롬프트도, 판정 계약도 필요 없다.
- 소스 트리를 건드리지 않는다(읽기 전용).

### 4.2 언어 무관성이 공짜인 이유

익스포터는 xls 의 `언어` 컬럼을 **읽지 않고**, 소스 코드 셀을 **파싱하지 않고 문자열 그대로** 옮긴다.
경로 매칭도 문자열 비교다. 그래서 C·C++·C#·Java 어떤 언어의 검출이든 **오늘 그대로 동작한다.**

> 새 언어 트랙을 추가하는 기여자는 **Track C 를 손댈 필요가 전혀 없다.**

### 4.3 Track C 범위 매칭 (Tier 0~3)

공유 xls 하나(예: PC-A 의 `D:\Work\Proj\...` 경로)를 팀이 나눠 고칠 때, 경로 기준이 서로 다르면
범위 필터가 조용히 빈 결과를 낼 수 있다. 그래서 매칭이 네 단계다.

| 단계 | 언제 | 무엇을 비교 |
|---|---|---|
| **Tier 0** | GUI [XLS 분리] 범위 트리(= `--files-from` 만, `--root` 없음) | **xls 자기 경로 문자열끼리 완전일치**. 언어 무관(`.cs`/`.cpp`/`.h`), 체크아웃 위치와 무관 |
| Tier 1 | 로컬 소스 선택(같은 PC) | 절대경로 완전일치. xls `경로` 가 디렉터리면 `경로 + 파일명` 으로 비교 |
| Tier 2 | 로컬 소스 선택(다른 PC) | 선택 파일의 **root 상대 경로 꼬리**가 xls 경로 끝과 **디렉터리 경계에서** 일치 |
| Tier 3 | xls `경로` 가 비었을 때 | 파일명이 선택·root 양쪽에서 **유일할 때만**. 동명 파일이 둘 이상이면 추측하지 않고 제외 |

즉 **크로스-PC 상대경로 매칭(Tier 2)은 "로컬 소스 경로"를 직접 줄 때만** 쓰인다
([코드 자동수정] 범위 트리, 또는 CLI 에서 `--root` + `--files-from`).
GUI 의 [XLS 분리] 화면은 Tier 0 이라 애초에 불일치가 생기지 않는다.
전혀 매칭되지 않으면 조용한 빈 결과 대신 **[범위 불일치]** 진단을 로그에 띄운다.

범위 필터는 `severity`/`checker`/`max` 필터보다 **먼저** 적용된다.

### 4.4 체커 규칙 = 이름 붙인 라이브러리 + 명시적 지정

체커별 조치 규칙은 **레포가 배포하지 않는다**(폐쇄망 자산이다).
`references/checkers/<이름>.md`(단, `_` 로 시작하는 파일 제외)가 **이름 붙인 규칙**이고, 한 규칙을 여러 체커에 재사용할 수 있다.

- **이름 기준 자동 매핑이 없다.** 규칙 파일명이 체커 키와 같아도 그것만으로는 붙지 않는다.
- 지정은 `references/checkers/_assignments.json`(`{ "<체커 키>": "<규칙 이름>" }`)에 저장되고 **기억된다**.
- 실행하면 **지정된 체커만** 그 규칙이 항목 md 에 self-contained 로 부착된다(멱등). 나머지는 순수 출력.
- GUI 흐름: **xls 로드 → [체커 규칙 관리] 창에서 지정 → 실행**. CLI 는 `--guides <폴더>`.

---

## 5. 디렉토리 지도

```text
sparrow-toolkit/
  README.md                      # 정문
  SKILL.md                       # (선택) Claude Code 스킬 매니페스트 겸 ★레포 루트 마커★
  CONTRIBUTING.md                # 빌드/테스트/인코딩/PR 규약
  LICENSE                        # MIT
  validate.ps1                   # 단일 검증 진입점

  docs/
    architecture.md              # 이 문서
    extending.md                 # 규칙 추가 / 새 언어 트랙 추가 레시피
    usage.md                     # 운영자 사용 안내

  SparrowRunner.Gui/
    SparrowRunner.Gui.sln        # Visual Studio 진입점(이 폴더엔 sln 만 둔다)
                                 #   담긴 프로젝트: SparrowRunner.Gui + SparrowXlsExport.Core (2개뿐)

  tools/
    Run-SparrowRunnerGui.cmd     # GUI 실행(발행본 있으면 그걸, 없으면 dotnet run)
    Run-SparrowAll.cmd/.ps1      # GUI 없이 Track A→B 순차 실행
    Compare-Sparrow.ps1          # 전/후 xls 비교 G2 회귀 게이트
    publish-airgap.ps1           # 폐쇄망 반입 발행(도구 4종)
    README.md                    # tools/ 진입점 안내
    SparrowRunner.Gui/           # WPF GUI 소스 (MainWindow.xaml(.cs), RuleManagerWindow, Scope*, SessionLog, WindowSnapshot…)
    _internal/
      SparrowSyntaxFix/          # Track A 엔진: Program.cs, RewriteEngine.cs, *Rewriter.cs(규칙 하나 = 파일 하나),
                                 #   FileDiscovery.cs, SourceFileIo.cs, VarRewriteHelpers.cs, Run-*.ps1, FixtureTests/
      SparrowCommentFix/         # Track B 엔진: Program.cs(규칙 전부), Run-*.ps1
      SparrowXlsExport/          # Track C CLI (+ FixtureGen/)
      SparrowXlsExport.Core/     # Track C 코어: SparrowExporter, CheckerRuleMapper/Store, TrackCRunReport (+ CoreTests/)

  tests/                         # 회귀 테스트(아래 6절)
  references/                    # 참고 자료(설계 정책·측정 기록·실사례 패턴). 실행에 필요한 파일은 없음
```

**gitignore 되는 것**: `bin/`·`obj/`·`publish/`, `tests/_logs/`, `issues_*.xls`,
`references/checkers/`(로컬 규칙 자산), `references/sparrow-official-rules/`(제3자 독점 문서), `*.log`, `*.dll`.

---

## 6. 테스트 아키텍처

### 6.1 단일 진입점 `validate.ps1`

```powershell
./validate.ps1              # 빌드 없음: 소스 존재 확인 + 모든 PowerShell 테스트/러너 구문검사 (수 초)
./validate.ps1 -All         # 전체 opt-in E2E (빌드+실행, .NET SDK 필요)
```

- 개별 스위치로 트랙별 실행 가능(`-IncludeSyntaxFixE2E`, `-IncludeCommentE2E`, `-IncludeSparrowE2E`,
  `-IncludeSparrowMappingTests`, `-IncludeG2GateTests`, `-IncludeGuiUiaTests` …).
- 전체 출력을 `tests/_logs/validate-<stamp>.log` 에 Tee 한다. 실패 시 그 경로를 안내하므로 신고 시 그 파일 하나면 된다.
- **`-All` 은 `-IncludeTrackCE2E` 를 포함하지 않는다** — `tests/e2e-lab/run-e2e.ps1` 은 커밋된 골든 fixture
  (`sample-before.xls`/`sample-after.xls`)를 재생성해 작업 트리를 더럽힌다. 돌렸으면 fixture 변경을 원복할 것.
- 실 Sparrow xls 가 필요한 테스트는 입력이 없으면 **자동 skip**(실패가 아니다).

### 6.2 픽스처 기반 회귀

| 층 | 무엇 | 어디 |
|---|---|---|
| 엔진 단위(인프로세스) | 실제 rewriter 소스를 컴파일해 before/after 를 단정 | `tools/_internal/SparrowSyntaxFix/FixtureTests/`, `tools/_internal/SparrowXlsExport.Core/CoreTests/` |
| 엔진 E2E(디스크) | 실제 파일에 대고 BOM/CRLF 보존·원자적 쓰기·생성파일 skip·`--dry-run`·멱등성 | `tests/sparrow-syntaxfix-fixtures.ps1`, `tests/sparrow-commentfix-fixtures.ps1`, `tests/sparrow-xlsexport-fixtures.ps1` |
| 교차 규칙 | 규칙을 섞어 돌렸을 때의 멱등성·컴파일 | `tests/sparrow-loop-tests.ps1` |
| 실 xls 기반 | 실제 검출 패턴 회귀(입력 없으면 skip) | `tests/sparrow-realxls-*.ps1`, `tests/sparrow-exhaustive-xls-test.ps1` |
| 게이트 | 전/후 xls 비교(G2) | `tests/g2-gate-tests.ps1` (`tools/Compare-Sparrow.ps1`) |
| 파이프라인 | 익스포터 → 수정+빌드(G1) → G2 | `tests/e2e-lab/run-e2e.ps1` |

모든 픽스처는 **합성 데이터**다. 실 Sparrow xls·사내 경로·실 식별자는 커밋하지 않는다([CONTRIBUTING.md](../CONTRIBUTING.md)).

### 6.3 GUI UIA 하네스 — 창을 실제로 띄워 단정한다

`tests/gui-uia-tests.ps1` 은 **진짜 WPF 창을 띄우고** `System.Windows.Automation`(UI Automation, OS API)으로
클릭·선택·입력하며 계약을 단정한다(기본 2회 반복). SDK/UIA/데스크톱 세션이 없으면 self-skip 한다.

단정하는 것(발췌):

- 대분류 전환이 실제로 화면을 바꾸는가(반대편 전용 컨트롤이 UIA 트리에서 사라지는가).
- **하위 탭 개수·라벨·순서** (`$SUB_TABS = @('코드 규칙', '주석·레이아웃')`) — **새 탭을 추가하면 여기를 갱신해야 한다.**
- 화면 텍스트에 내부 명칭(`Track A`/`Track B`)이 하나도 없는가.
- 자동 매핑 없음: 체커 키와 같은 이름의 규칙 파일이 있어도 지정 전에는 붙지 않는가.
- 범위 트리가 xls 자기 경로로 만들어지고, 폴더 하나만 체크하면 정확히 그 폴더만 나오는가.
- GUI 가 러너에 `-Commit`/`-NoCommit` 을 넘기고 `-DryRun`/`-IncludeGenerated` 는 넘기지 않는가.
- 레이아웃 수치: 모든 핵심 요소가 창 사각형 **안**에 있는가(잘림 없음), `w>0/h>0`, `IsOffscreen=false`,
  규칙 에디터 높이 ≥ 임계값, 목록/에디터 사각형이 겹치지 않는가.

### 6.4 창 스냅샷 — 이 UI 를 눈으로 보는 방법

이 GUI 는 설치되지 않는 커스텀 exe 라서 OS 자동화 허용목록에 올릴 수 없다 —
**외부 도구로 스크린샷을 찍을 수 없다.** 그래서 앱이 **스스로 자기 창을 PNG 로 렌더**한다
(`RenderTargetBitmap`, 실제 DPI 배율, 불투명 바탕, `.tmp` 쓰고 rename).

- `SparrowRunner.Gui.exe --screenshot-dir <DIR>` 를 줄 때만 활성(안 주면 기능 전체가 꺼진다).
- 자동 지점: 메인창 로드 / 규칙 관리창 오픈 / 실행 완료.
- 임의 시점: 그 폴더에 `capture.request` 파일을 만들면 **현재 활성 창**을 즉시 찍고 요청 파일을 지운다.
- 하네스는 반복당 유효 PNG 개수(시그니처+IHDR, 10KB 초과) 와 **PNG 픽셀 크기 ≈ UIA 창 Rect(±10%)** 를 단정한다
  → 잘못된 DPI 스케일로 렌더되는 회귀도 걸린다.
- 한계: 펼친 ComboBox 드롭다운·툴팁은 **별도 HWND(Popup)** 라 창 렌더에 포함되지 않는다. 그건 UIA 트리 덤프의 `Rect` 로 본다.

**UI 문제를 판단할 땐 PNG 를 연다.** 트리 덤프는 수치의 눈, PNG 는 문자 그대로의 눈이다.

---

## 7. 알려진 제약 / 다음 사람이 알아야 할 리스크

| 영역 | 내용 |
|---|---|
| `.sln` 파싱 | GUI 의 프로젝트 트리는 `.sln` 안의 일반 C# 프로젝트 라인을 기준으로 만든다. **Solution Folder / Shared Project / 특수 SDK 포맷은 미지원** — 필요하면 `SourceScopeDiscovery` 를 확장한다. |
| 프로젝트 중복 | 같은 물리 폴더를 여러 `.csproj` 가 참조하면 트리에 파일이 중복 표시될 수 있다. 선택 manifest 는 중복을 제거하므로 실행은 중복되지 않는다. |
| git 버전 | 러너의 작업범위 격리는 `git commit --only --pathspec-from-file` 에 의존한다(Windows Git 2.48.1 확인). 오래된 git 에서는 지원 여부 확인 필요. |
| Track C basename 매칭 | Tier 3 는 **의도적으로 보수적**이다. xls `경로` 가 비어 있고 동명 파일이 여럿이면 추측하지 않고 제외한다. |
| 스캔 접근 실패 | 권한 등으로 못 읽은 디렉터리는 아직 UI 경고로 표시하지 않는다. 필요하면 `SourceScopeDiscovery` 에 skipped count/message 를 추가한다. |
| Roslyn ≠ Sparrow | **Roslyn 편집이 Sparrow 검출 소멸을 보장하지 않는다.** AST 경계가 서로 다르다. 진짜 게이트는 **Sparrow 재분석**(G2)이다. 자동수정 → 빌드(G1) → 재분석(G2) → 사람 리뷰(G3) 순으로 확인한다. |
| 측정 위생 | 전/후 xls 델타는 **반드시 동일 체크아웃·동일 경로 집합**으로 스캔해야 의미가 있다. 파일명만으로 매칭하면 다중 프로젝트 동명 파일이 뭉쳐 오판을 부른다(실제 사고 사례: [references/RESULTS-6869-analysis.md](../references/RESULTS-6869-analysis.md)). |
