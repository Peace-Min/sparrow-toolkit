# sparrow-toolkit

**English summary** — sparrow-toolkit turns a Sparrow (Fasoo) static-analysis `.xls` report into finished work. It does three things: **code rules** and **comments/layout** are deterministic Roslyn *syntax-only* rewriters that auto-fix C# coding-rule and comment/layout findings without ever loading or compiling a project, and **XLS split** is a language-agnostic exporter that splits every remaining finding out of the XLS into one Markdown file per finding, grouped in a folder per checker, for a human or an LLM to work through. A WPF GUI drives all three, and the engines sit behind a process boundary (the GUI shells out to PowerShell runners) so a new language can be added without touching the GUI's internals. Everything runs offline. **Adding C/C++ support? Start at [docs/extending.md](docs/extending.md).**

---

Sparrow(파수/Fasoo) 정적분석 산출물(`.xls`)의 지적사항을 **전건(全件) 처리**하기 위한 도구 모음.
정형 패턴은 결정론적으로 자동수정하고, 판단이 필요한 나머지는 체커별 Markdown으로 분리해 사람/LLM에게 넘긴다.

> **기여자에게**: 이 레포는 외부 기여를 받는다. 먼저 읽을 문서는 세 개다.
> [docs/architecture.md](docs/architecture.md)(구조·프로세스 경계) →
> [docs/extending.md](docs/extending.md)(**규칙 추가 / 새 언어 갈래 추가 레시피**) →
> [CONTRIBUTING.md](CONTRIBUTING.md)(빌드·테스트·인코딩 규약).
>
> **"C/C++ 전용 탭을 만들고 싶다"** → [docs/extending.md 레시피 2](docs/extending.md#레시피-2-새-언어-갈래-추가-cc-예시)로 바로 가면 된다.
> 건드릴 접점이 전부 목록으로 있고, **GUI ↔ 엔진 사이의 러너 CLI 계약만 지키면 어떤 언어·기술로 엔진을 만들어도 붙는다.**

## 무엇을 해결하나

Sparrow 스캔 한 번이면 수천 건이 나온다. 그 중 상당수는 `var` 변환·괄호 보강·주석 마침표 같은
**기계적으로 판정 가능한 형식 위반**이고, 나머지는 null 역참조·리소스 누수처럼 **사람이 봐야 하는 항목**이다.
이 레포는 그 둘을 갈라 각각 다른 방식으로 처리한다.

- **전건 수정 정책(Policy A)**: 심각도/체커 필터로 검출을 미리 걸러내지 않는다. Sparrow가 검출한 전건이 작업 대상이다.
- 자동수정은 **정형 패턴에 한정**한다. 애매하면 고치지 않고 넘긴다 — 잘못 고치는 것보다 안 고치는 게 낫다.
- 판단이 필요한 항목은 **버리지 않고** 체커별 md로 분리해 다음 단계(사람/LLM)로 넘긴다.

## 세 갈래

| 갈래(화면 명칭) | 하는 일 | 방식 | 언어 | 엔진 |
|---|---|---|---|---|
| **[코드 규칙]** | `var` 변환·논리식 괄호·`foreach` Cast·for 호이스팅·필드 분할 등 | Roslyn **구문 전용** 재작성(프로젝트 로드/컴파일 없음) | **C# 전용** | `SparrowSyntaxFix` |
| **[주석·레이아웃]** | 주석 앞 공백·마침표·대문자화·후행주석 승격·멤버 빈 줄 등 | 동일한 구문 전용 재작성(주석 trivia 한정) | **C# 전용** | `SparrowCommentFix` |
| **[XLS 분리]** | 검출 전건을 **체커별 폴더 + 항목 md**로 분리 | xls 셀을 **파싱하지 않고 문자열 그대로** 옮김 | **언어 무관** | `SparrowXlsExport(.Core)` |

화면·코드·문서·커밋 메시지가 모두 같은 이름(하는 일)을 쓴다 — 별도의 내부 분류 명칭은 없다.

### 언어 지원 — 지금 어디까지 되나

- **[코드 규칙]·[주석·레이아웃]은 C# 전용이다.** Roslyn C# 파서(`CSharpSyntaxTree.ParseText`)로 파싱해 재작성하므로 C·C++ 등에는 쓸 수 없다.
- **[XLS 분리]는 이미 언어 무관이다.** xls의 `언어` 컬럼을 읽지 않고 소스 코드 셀을 그대로 옮기므로,
  C·C++·C#·Java 등 Sparrow가 검출한 **어떤 언어의 결과든** 지금 그대로 체커별 md로 분리된다.
- 따라서 **오늘 C/C++ 프로젝트는 [XLS 분리]만 쓰면 되고**, C/C++ 자동수정을 원하면
  [새 언어 갈래를 추가](docs/extending.md#레시피-2-새-언어-갈래-추가-cc-예시)하면 된다.
  **그때도 [XLS 분리]는 손댈 필요가 없다.**

## 빠른 시작

필요한 것: **Windows** + **`net8.0` 을 빌드할 수 있는 .NET SDK**(8.0 이상이면 된다 — `global.json` 이 없어 버전을 고정하지 않는다)
\+ 실행용 **.NET 8 런타임**. 폐쇄망 대상 PC는 SDK 없이 발행본만으로 돈다.
자세한 요건과 실측(“.NET 8 SDK 없는 PC 에서 전 테스트 통과”)은 [CONTRIBUTING.md 1. 개발 환경](CONTRIBUTING.md#1-개발-환경).

```powershell
# 1) GUI + [XLS 분리] 코어 빌드 (이 솔루션에 담긴 프로젝트는 이 둘뿐이다)
dotnet build SparrowRunner.Gui/SparrowRunner.Gui.sln -c Release

# 2) 엔진 CLI 3종은 솔루션에 없다 — 따로 빌드한다
#    (자동수정 러너는 exe 가 없으면 알아서 dotnet build 하므로 생략해도 동작한다)
dotnet build tools/_internal/SparrowSyntaxFix/SparrowSyntaxFix.csproj -c Release
dotnet build tools/_internal/SparrowCommentFix/SparrowCommentFix.csproj -c Release
dotnet build tools/_internal/SparrowXlsExport/SparrowXlsExport.csproj -c Release

# 3) 실행
tools\Run-SparrowRunnerGui.cmd
```

`dotnet build` 로 만든 GUI 실행 파일 자체는 여기 있다 — **문서에 나오는 CLI 플래그
(`--xls` · `--log-dir` · `--screenshot-dir` · `--guides-dir`)를 쓰려면 이 경로가 필요하다.**

```text
tools\SparrowRunner.Gui\bin\Release\net8.0-windows\SparrowRunner.Gui.exe
```

진입점 `tools\Run-SparrowRunnerGui.cmd` 는 받은 인자를 그대로 GUI 에 전달하므로
`tools\Run-SparrowRunnerGui.cmd --xls C:\work\issues.xls` 처럼 써도 된다.
폐쇄망 반입본에서는 `tools\SparrowRunner.Gui\publish\SparrowRunner.Gui.exe` 가 그 자리를 대신한다.

### 폐쇄망 반입(오프라인 발행)

```powershell
# 인터넷 + SDK 가 있는 PC에서 1회. 도구 4종을 각 프로젝트의 publish\ 로 self-contained 발행한다.
./tools/publish-airgap.ps1              # 기본: self-contained win-x64 (대상 PC 런타임 불필요)
./tools/publish-airgap.ps1 -FrameworkDependent   # 크기 축소(대상 PC에 .NET 8 런타임 필요)
./tools/publish-airgap.ps1 -DryRun               # 계획만 출력
```

발행 산출물(`publish/`)은 머신마다 생기는 것이라 **커밋하지 않는다**(`.gitignore` 대상). 반입은 폴더 복사로 한다.
자세한 절차·런타임 요건은 [docs/usage.md](docs/usage.md#폐쇄망-반입오프라인-배포).

## 화면 구성 — 대분류 2개

GUI(`Sparrow Helper`) 최상단에서 **무엇을 할지** 먼저 고른다. 두 대분류는 입력도 위험도도 다르므로 화면이 통째로 갈린다.

```text
┌ Sparrow Helper ────────────────────────────────────────┐
│ [ 코드 자동수정 (C#) ]   [ XLS 분리 (모든 언어) ]        │ ← 대분류 전환
├────────────────────────────────────────────────────────┤
│  (선택된 대분류 전용 화면 + 왼쪽 작업 범위 트리)          │
├────────────────────────────────────────────────────────┤
│  실행 / 중지 / 실행 로그 (공유)                          │
└────────────────────────────────────────────────────────┘
```

| 대분류 | 하위 탭 | 입력 | 범위 트리 | 성격 |
|---|---|---|---|---|
| **코드 자동수정 (C#)** | **[코드 규칙]** / **[주석·레이아웃]** | 대상 `.sln`/`.csproj`/폴더 | **로컬 소스 스캔** | 소스 파일을 **수정**(파괴적) · **C# 전용** |
| **XLS 분리 (모든 언어)** | (없음) | **Sparrow 결과 XLS 하나** | **XLS 자신의 검출 경로** | **읽기전용** · 프로젝트 경로 불필요 · 언어 무관 |

- 실행 버튼은 항상 **지금 선택된 화면**만 돌린다. 로그창은 공유한다.
  `SparrowRunner.Gui.exe --xls <경로>` 로 기동하면 [XLS 분리]가 자동 선택된다.
- **GUI 는 기본적으로 파일만 수정하고 커밋하지 않는다.** 실행이 끝나면
  `N개 파일 수정됨 — 커밋하지 않았습니다. git diff 로 검토 후 커밋하세요.` 가 로그와 요약바에 뜬다.
- **[규칙별 커밋 생성]** 체크박스([코드 자동수정] 화면 전용, 기본 꺼짐)를 켜면 러너가 **규칙 하나마다 커밋**을 만든다 —
  롤백 단위가 규칙이 되어 "괄호는 채택, var 는 거부" 같은 선택적 되돌리기(`git revert`)가 된다.
  러너의 **규칙별 컴파일 게이트(`-VerifyCmd`)** 도 이 모드에서만 의미가 있다.
- **`-DryRun` · `-VerifyCmd` · `-ExePath` · 생성 파일 포함(`-IncludeGenerated`, [주석·레이아웃] 러너에만 있다) 은
  CLI 러너 전용 옵션이다**(자동화/CI 용). GUI 는 이것들을 넘기지 않는다.
  **CLI 로 자동화할 때는 `-Rules` 를 반드시 명시한다** — 생략하면 러너가 대화형 프롬프트를 띄우고,
  비대화형에서는 opt-in 규칙이 조용히 전부 꺼진다([docs/usage.md](docs/usage.md#cli-자동화-주의사항)).
- **범위 선택 = 팀 분담**이지 검출 제외가 아니다. [XLS 분리]의 범위 트리는 **xls가 스스로 적어 둔 경로**로 만들고
  선택을 그 문자열 그대로 되먹이므로, 팀원마다 체크아웃 위치가 달라도 어긋나지 않는다(Tier 0 완전일치).
  자세한 4단계 매칭(Tier 0~3)은 [docs/architecture.md](docs/architecture.md#43-xls-분리-범위-매칭-tier-03).

자세한 조작법은 [docs/usage.md](docs/usage.md).

## 아키텍처 한눈에

핵심 성질 하나: **GUI 와 엔진은 프로세스 경계로 분리돼 있다.** GUI 는 [코드 규칙]·[주석·레이아웃] 에서 러너 `.ps1` 을
`powershell.exe` 로 shell-out 할 뿐이다 — **그 러너 CLI 계약만 지키면 엔진은 어떤 언어·기술로 만들어도 붙는다.**
이것이 새 언어 갈래(C/C++ 등)를 붙일 수 있는 이유다.

```text
        ┌──────────────────────────────────────────────────────────┐
        │  SparrowRunner.Gui   (WPF · net8.0-windows)              │
        │  대분류/하위 탭 · 범위 트리 · 규칙 체크박스 · 공유 로그창   │
        └───────┬───────────────────────────────────┬──────────────┘
                │ ① 프로세스 경계 (자동수정)         │ ② 같은 프로세스 (XLS 분리)
                │   powershell.exe <러너>.ps1        │   ProjectReference 직접 호출
                │   -Solution -Rules -LogDir         │
                │   -FilesFrom  -Commit|-NoCommit    │
                ▼                                   ▼
   ┌──────────────────────────────┐   ┌────────────────────────────────┐
   │ 러너 (.ps1)                  │   │ SparrowXlsExport.Core          │
   │  Run-SparrowSyntaxFix.ps1    │   │  SparrowExporter.Run()         │
   │  Run-SparrowCommentFix.ps1   │   │  CheckerRuleMapper.Apply()     │
   │  exe 확보 → 규칙별 실행       │   └───────────┬────────────────────┘
   │  → (선택) 규칙별 커밋+게이트  │               │ 같은 코어를 CLI 도 쓴다
   └───────┬──────────────────────┘               ▼
           │ 프로세스 실행                ┌────────────────────────────┐
           ▼                              │ SparrowXlsExport (CLI)     │
   ┌──────────────────────────────┐       └────────────────────────────┘
   │ 엔진 CLI (net8 콘솔)          │
   │  SparrowSyntaxFix.exe        │   ← 새 언어 갈래는 여기에 형제를 하나 더 만든다
   │  SparrowCommentFix.exe       │      (Roslyn 이 아니어도 된다)
   └──────────────────────────────┘
```

- **[코드 규칙]·[주석·레이아웃]**: 구문 전용 파싱이라 **프로젝트를 로드하지도 컴파일하지도 않는다.**
  덕분에 빌드가 깨진 레거시 비-SDK `.csproj`(.NET Framework 4.7.2)에서도 동작한다.
  대가는 의미 분석이 필요한 규칙을 못 한다는 것 — 그래서 애매한 변환은 `-safe` / `-narrowing` 으로 갈라 둔다.
- **[XLS 분리]**: **순수 익스포터**다. 선행 문서(체커 가이드·프롬프트·판정 계약)를 일절 요구하지 않는다.
  입력은 xls 하나, 출력은 **체커 키 폴더 + 그 안의 항목 md**(`<체커키>/{ID}_{파일명}_{라인}.md`) 뿐이고
  인덱스·요약·작업지침은 만들지 않는다(**부산물 0 계약**). 소스는 건드리지 않는다.

전체 설명은 [docs/architecture.md](docs/architecture.md).

## 확장하기

> 이 레포에서 가장 중요한 문서는 [**docs/extending.md**](docs/extending.md) 다.

| 하고 싶은 것 | 어디를 보나 | 요약 |
|---|---|---|
| **C# 갈래에 규칙 하나 추가** | [레시피 1](docs/extending.md#레시피-1-기존-c-갈래에-규칙-추가) | Rewriter 파일 1개 + enum 1줄 + `--rules` switch 1줄 + GUI 체크박스 + 픽스처 |
| **C/C++ 등 새 언어 갈래 추가** | [레시피 2](docs/extending.md#레시피-2-새-언어-갈래-추가-cc-예시) | 자체 엔진 CLI + 러너 `.ps1`(계약 준수 + **`.cs` 하드코딩 4곳 교체**) + GUI 배선 11접점. **[XLS 분리] 는 손대지 않는다** |
| **체커별 조치 규칙 붙이기** | [docs/usage.md](docs/usage.md#규칙-라이브러리--체커-지정-자동-매핑-없음) | 규칙 라이브러리 + 명시적 지정(자동 매핑 없음) |

## 테스트

Windows 기본 실행 정책은 `Restricted` 라 `.ps1` 을 그냥 실행하면 막힌다. **이 레포의 모든 스크립트는 아래 형태로 부른다.**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\validate.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\validate.ps1 -All
```

(실행 정책을 이미 완화해 둔 PC 라면 `.\validate.ps1 -All` 로 줄여 써도 같다. 문서에서 짧은 형태가 보이면 이 전제다.)

| 명령 | 무엇 | 걸리는 시간 |
|---|---|---|
| `validate.ps1` | 빌드 없음: 소스 존재 + 전체 PowerShell 구문검사 | 수 초 |
| `validate.ps1 -All` | **전체 게이트.** 빌드+E2E+`CoreTests`+**GUI UIA 하네스** | **수 분. 실제 WPF 창이 떴다 닫힌다** |
| `validate.ps1 -IncludeSyntaxFixE2E -IncludeCommentE2E -IncludeSparrowE2E` | 개별 갈래만 | |
| `validate.ps1 -IncludeGuiUiaTests` | **GUI 하네스만 따로** (`-All` 에도 이미 포함돼 있다) | |

- 실 xls 가 필요한 테스트는 입력이 없으면 자동 skip 한다(실패가 아니다).
  **xls 는 `%USERPROFILE%\Downloads\issues_*.xls` 에서 자동 탐색되며, 끄는 법이 있다** →
  [CONTRIBUTING.md 3.2](CONTRIBUTING.md#32-실-sparrow-xls-를-쓰는-테스트--자동-탐색과-끄는-법).
- 마지막 배너 `실행 N · 스킵 M · 실패 K` 를 확인한다. **`실행 0` 은 통과가 아니라 "단정이 하나도 안 돌았다"** 는 뜻이다.
  실패가 있으면 0 이 아닌 코드로 종료하고 실패한 테스트 이름을 모아 출력한다.
- 새 테스트를 추가한다면 신호 규약(성공 `exit 0` / 실패 `throw`·`exit≠0` / 스킵 `$global:SparrowTestSkip`)을 지킨다 →
  [CONTRIBUTING.md 3.1](CONTRIBUTING.md#31-게이트-결과-읽는-법--새-테스트를-추가할-때의-신호-규약).
- `-All` 은 `-IncludeXlsSplitE2E` 를 **포함하지 않는다** — `tests/e2e-lab/run-e2e.ps1` 은 커밋된 골든 fixture xls 를
  재생성해 작업 트리를 더럽히므로 필요할 때만 명시 실행한다.

### 파괴적 기능을 안전하게 시험하려면

[코드 규칙]·[주석·레이아웃]은 **소스를 실제로 고친다.** 남의 레포에 처음부터 대고 시험하지 말 것 —
레포 안에 그 용도의 합성 C# 프로젝트가 이미 있다(`tests/e2e-lab/SampleApp/`).
절차는 [docs/usage.md](docs/usage.md#파괴적-기능을-안전하게-시험하기-샌드박스) 한 곳에 모아 두었다.

## 진단 로그 (문제가 났을 때 무엇을 첨부하나)

화면 로그는 앱을 닫으면 사라지므로 **다섯 곳**에 사후 판단용 증거가 남는다. 기록은 전부 best-effort 다
(못 써도 앱은 그대로 동작한다). **자동으로 남는 것과 인자를 줘야 남는 것이 섞여 있으니 아래 "활성 조건" 열을 볼 것.**

| 무엇 | 어디 | 활성 조건 | 들어있는 것 |
|---|---|---|---|
| **GUI 세션 로그** | `%LOCALAPPDATA%\SparrowRunner\logs\session-<stamp>.log` | 자동 | 시작 헤더(앱 버전·인자·OS/.NET·PID) + 화면 로그 전 줄 + 미처리 예외 + `세션 종료 (정상)` 표식. 최신 20개 |
| **[XLS 분리] 실행 리포트** | 같은 폴더의 `xlssplit-<stamp>.json` (+ 사람용 `.log`) | 자동([XLS 분리] 실행마다) | 입력 xls 경로/크기/**sha256**, 소요, 옵션, 전체/매칭/기록 수, 체커별 건수, 지정 규칙·부착 건수, 범위 진단, 경고 |
| **[코드 규칙]·[주석·레이아웃] 러너 로그** | **대상 소스 루트**의 `Run-SparrowSyntaxFix.<stamp>.log` / `Run-SparrowCommentFix.<stamp>.log` | 자동 | 규칙별 stdout 전문 + exit 코드 + 커밋/게이트 판정. **아래 주의 참조** |
| **테스트 진단** | `tests/_logs/` (gitignore) | 테스트 실행 시 | `uia-<stamp>/result.log` · `tree-*.txt`(UIA 트리 덤프) · `gui-logs/` · 실패 시 `FAILURE-CONTEXT-*.txt` · `validate-<stamp>.log` |
| **창 스냅샷(PNG)** | `tests/_logs/uia-<stamp>/shots/iter<N>/*.png` (gitignore) | **`--screenshot-dir <DIR>` 를 줄 때만** (UIA 하네스는 항상 준다) | **앱이 스스로 렌더한 실제 창 이미지** |

- 리포트는 **절대 [XLS 분리] 출력 폴더에 쓰지 않는다** — 출력 폴더의 "부산물 0" 계약을 유지한다.
- **UI 관련 문제 신고**: `shots/**/*.png`(실제 화면) + `tree-*.txt`(수치)를 함께 첨부한다.
- 로그 위치 변경: `SparrowRunner.Gui.exe --log-dir <DIR>`, `validate.ps1 -LogDir <DIR>`.
- **로그 첨부 전 내용 확인**: 실 xls 를 쓰는 테스트가 돌았다면 진단 파일에 사내 소스 조각이 섞일 수 있다 →
  [CONTRIBUTING.md 3.2](CONTRIBUTING.md#32-실-sparrow-xls-를-쓰는-테스트--자동-탐색과-끄는-법).

> **⚠ [코드 규칙]·[주석·레이아웃] 러너 로그는 당신의 소스 루트에 쌓인다.** GUI 가 러너의 `-LogDir` 로 **대상 소스 루트**를 넘기기 때문이다.
> 부작용 둘:
> 1. 실행할 때마다 대상 레포에 `Run-SparrowSyntaxFix.<stamp>.log` 가 하나씩 늘어난다 —
>    그 레포의 `.gitignore` 에 `*.log` 가 없으면 **추적되지 않은 파일로 계속 쌓인다.**
> 2. 러너는 그 로그를 **먼저 쓴 뒤** `git status --porcelain` 으로 작업트리를 검사하므로,
>    깨끗한 레포에서도 **"작업트리에 미커밋 변경이 있습니다" 경고를 러너가 스스로 유발**한다.
>    이 경고는 안내일 뿐 실행을 막지 않는다.
>
> 대상 레포를 더럽히기 싫으면 GUI 대신 CLI 러너를 직접 부르고 `-LogDir` 를 다른 폴더로 준다.
> 그때 **그 폴더는 미리 만들어 둬야 한다** — 러너는 `-LogDir` 를 만들지 않고 바로 그 안에 쓰므로,
> 없으면 첫 줄을 쓰다 `[FATAL]` 로 죽는다.

**시작 실패는 로그가 한 줄도 안 남는다 (폐쇄망에서 가장 흔한 실패).**
GUI 는 생성자에서 `ResolveSkillRoot()` 를 **`SessionLog.Create` 보다 먼저** 부르고, `App.xaml.cs` 에는
예외 핸들러가 없다. 즉 **스킬 루트를 못 찾아 던지는 순간에는 세션 로그 파일이 아직 열리기 전**이라
`%LOCALAPPDATA%\SparrowRunner\logs\` 에 아무것도 안 생긴다 — 창이 안 뜨고 조용히 끝난 것처럼 보인다.
그때 수집할 것:

1. **레포 트리에 이 세 파일이 모두 있는지** — `SKILL.md`, `tools\Run-SparrowRunnerGui.cmd`,
   `tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1`. `ResolveSkillRoot()` 는 exe 위치에서
   위로 올라가며 **셋을 동시에** 갖춘 폴더를 찾는다. 하나라도 없으면 실패한다.
2. **exe 의 실제 경로** — 반입할 때 `publish\` 폴더만 따로 떼어냈다면 위 세 파일이 상위에 없어 반드시 실패한다.
3. **콘솔에서 직접 기동한 출력** — `tools\Run-SparrowRunnerGui.cmd` 를 `cmd` 창에서 실행하면
   예외 메시지가 콘솔에 보인다(로그 파일에는 없다).
4. Windows **이벤트 뷰어 → Windows 로그 → 응용 프로그램**의 .NET Runtime 오류 항목.

이 GUI 는 설치되지 않는 커스텀 exe 라 OS 자동화 허용목록에 올릴 수 없다 = **외부에서 스크린샷을 찍을 수 없다.**
그래서 앱이 **스스로 자기 창을 PNG 로 렌더**한다 — **`--screenshot-dir <DIR>` 를 줄 때만 활성이고, 안 주면 기능 전체가 꺼진다.**
자세한 내용은 [docs/usage.md](docs/usage.md#4-창-스냅샷).

## 문서 지도

| 문서 | 독자 | 내용 |
|---|---|---|
| `README.md` (이 파일) | 처음 오는 사람 | 정체성·세 갈래·빠른 시작·아키텍처 요약 |
| [docs/architecture.md](docs/architecture.md) | **기여자 필독** | 레이어/프로세스 경계, **러너 CLI 계약**, 갈래별 내부, 디렉토리 지도, 테스트 구조 |
| [docs/extending.md](docs/extending.md) | **기여자 필독** | 규칙 추가 / **새 언어 갈래 추가** 레시피 + 체크리스트 |
| [CONTRIBUTING.md](CONTRIBUTING.md) | 기여자 | 개발 환경·빌드·테스트·**인코딩 규약**·PR 기대치·실 데이터 반입 금지 |
| [docs/usage.md](docs/usage.md) | 운영자 | GUI 조작, 범위 트리, 규칙 라이브러리, 폐쇄망 반입, 진단 로그 상세 |
| [tools/README.md](tools/README.md) | 운영자 | `tools/` 진입점과 CLI 러너 옵션 |
| [tools/_internal/SparrowSyntaxFix/README.md](tools/_internal/SparrowSyntaxFix/README.md) | 기여자 | [코드 규칙] 엔진 규칙별 계약·CLI·안전성 보장 |
| [tools/_internal/SparrowCommentFix/README.md](tools/_internal/SparrowCommentFix/README.md) | 기여자 | [주석·레이아웃] 엔진 규칙별 계약·CLI·비활성 규칙 사유 |
| [references/README.md](references/README.md) | 참고 | 설계 정책·측정 기록·실사례 패턴(참고 자료, 실행에 필요 없음) |
| [SKILL.md](SKILL.md) | (선택) | Claude Code 스킬 매니페스트. **레포 루트 마커도 겸한다** — 지우지 말 것 |

### Claude Code 스킬로도 쓸 수 있다 (선택)

이 레포는 원래 `peace-skillbank` 의 스킬 하나로 시작했고, 그 흔적으로 루트에 [SKILL.md](SKILL.md) 가 있다.
레포 폴더를 Claude Code 등 에이전트의 스킬 디렉토리에 두면 그 매니페스트가 "언제 이 도구를 쓰는가"를 알려준다.
**선택 사항이다** — GUI/CLI 사용에는 전혀 필요 없다. 다만 `SKILL.md` 는 GUI(`ResolveSkillRoot`)와
`validate.ps1`, `CoreTests` 가 **레포 루트를 찾는 마커 파일**로도 쓰므로 파일 자체는 지우면 안 된다.

## 출처

`peace-skillbank` 의 `skills/sparrow-static-analysis/` 를 독립 제품으로 분리한 것이다.
Fresh-import 기준 커밋: **peace-skillbank@76c799c** (2026-07-23). 그 이전 상세 이력은 원 레포에 있다.

## 라이선스

MIT — [LICENSE](LICENSE) 참조.
