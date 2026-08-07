# 사용 가이드 (운영자용)

`sparrow-toolkit`은 Sparrow 정적분석 결과 조치를 반복 가능하게 만들기 위한 폐쇄망용 헬퍼다. 정형화된 코딩/주석 위반 패턴은 Roslyn 기반 CLI로 자동 조치하고, 보안/품질 판단 항목과 예외 케이스는 **체커별 Markdown 파일로 분리**해 LLM 또는 개발자가 체커 단위로 작업한다.

> 이 문서는 **도구를 쓰는 사람**을 위한 것이다. 도구를 **고치거나 확장하려면**
> [architecture.md](architecture.md) → [extending.md](extending.md) → [../CONTRIBUTING.md](../CONTRIBUTING.md) 순으로 읽는다.

> **[XLS 분리]는 순수 익스포터다.** 선행 문서(체커 가이드·프롬프트·판정 계약)를 일절 요구하지 않는다. 입력은 Sparrow `.xls` 하나이고, 출력은 체커 키 폴더 + 그 안의 항목 md(`<체커키>/{ID}_{파일명}_{라인}.md`) 뿐이다.

## 빠른 실행

Visual Studio 사용자는 다음 솔루션을 연다(레포 루트 기준 상대 경로).

```text
SparrowRunner.Gui/SparrowRunner.Gui.sln
```

명령줄에서 GUI를 바로 실행하려면 다음 파일을 사용한다. **받은 인자를 GUI에 그대로 전달**하므로
`--xls` 같은 옵션도 여기에 붙이면 된다.

```text
tools\Run-SparrowRunnerGui.cmd
tools\Run-SparrowRunnerGui.cmd --xls C:\work\issues.xls
```

exe를 직접 부르고 싶다면 경로는 다음과 같다.

| 상황 | GUI 실행 파일 |
| --- | --- |
| `dotnet build -c Release` 후(개발 PC) | `tools\SparrowRunner.Gui\bin\Release\net8.0-windows\SparrowRunner.Gui.exe` |
| `publish-airgap.ps1` 발행 후(폐쇄망 반입본) | `tools\SparrowRunner.Gui\publish\SparrowRunner.Gui.exe` |

GUI가 받는 CLI 옵션: `--xls <경로>` · `--xls-out <경로>` · `--guides-dir <DIR>` · `--log-dir <DIR>` ·
`--screenshot-dir <DIR>` · `--xls-autorun` · `--open-rule-manager`.

### 진입점이 알려 주는 실행 바이너리와 낡은 발행본 경고

`Run-SparrowRunnerGui.cmd` 는 **발행본(`publish\`)이 있으면 그것을 먼저 실행한다.** 그래서 어느 바이너리가
도는지 콘솔 첫 줄에서 밝히고, 그 발행본이 오래됐으면 경고한다.

| 출력 | 언제 | 뜻 |
| --- | --- | --- |
| `[INFO] Launching published bundle: <경로>` | `tools\SparrowRunner.Gui\publish\SparrowRunner.Gui.exe` 가 있을 때(**항상**) | 그 발행본이 실행된다. 소스를 고쳐도 이 exe 가 그대로면 화면에 반영되지 않는다 |
| `[INFO] No published GUI exe found; running via "dotnet run" ...` | 발행본이 없을 때 | 소스에서 빌드해 실행한다(인터넷 + `.NET SDK` 필요) |
| `[WARN] The published bundle is OLDER than your local Release build:` (+ 경로·시각 4줄) | 발행본과 `tools\SparrowRunner.Gui\bin\Release\net8.0-windows\SparrowRunner.Gui.exe` 가 **둘 다 있고** 빌드 쪽이 더 새로울 때 | 발행본이 이기므로 **방금 빌드한 변경은 실행되지 않는다.** 두 파일의 타임스탬프와 경로가 함께 찍힌다 |

**"고친 게 반영이 안 된다" 싶으면 이 줄부터 본다.** 11일 된 발행본이 조용히 우선 실행돼 옛 UI 가 뜨고
"트리가 안 나온다"는 신고로 이어진 사고가 실제로 있었다 — 그 진단 시간을 없애려고 넣은 출력이다.
조치는 둘 중 하나다.

- `tools\publish-airgap.ps1` 을 다시 실행해 발행본을 갱신한다(폐쇄망 반입본을 유지해야 할 때).
- `tools\SparrowRunner.Gui\publish\` 를 지운다 — 그러면 `.cmd` 가 `dotnet run` 폴백으로 내려가 항상 현재 소스를 돈다.

> `[WARN]` 분기는 **발행본과 로컬 Release 빌드가 둘 다 있는 개발 PC 에서만** 돈다. 폐쇄망 반입본에는
> `bin\Release\` 가 없으므로 비교 자체가 일어나지 않는다.
>
> 신구 판정은 PowerShell 로 `LastWriteTime` **순서**를 비교한다. 배치의 `%%~tF` 는 로캘 형식 문자열이라
> 문자열 비교로는 "다르다"까지만 알 수 있고 신구를 가릴 수 없어, 갓 발행한 직후에도 거짓 경고가 떴다(실측).
>
> 어느 exe 가 실제로 돌았는지는 **세션 로그 헤더의 `실행 파일` 줄**로도 사후 확인할 수 있다([진단 로그](#진단-로그)).

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
| **코드 자동수정 (C#)** | **[코드 규칙]** / **[주석·레이아웃]** | **대상 폴더** 하나 | **로컬 소스 스캔** | 소스 파일을 **수정**(파괴적, 커밋은 [규칙별 커밋 생성] 체크 시에만), **C# 전용** |
| **XLS 분리 (모든 언어)** | (없음) | **Sparrow 결과 XLS 하나** | **XLS 검출 경로** | **읽기전용**, **프로젝트 경로 불필요**, 언어 무관 |

- 실행 버튼은 항상 **지금 선택된 화면**만 돌린다([코드 자동수정]에서는 선택된 하위 탭, [XLS 분리]에서는 XLS 분리). 실행 버튼 라벨도 그에 맞춰 `코드 규칙 수정 실행` / `주석·레이아웃 수정 실행` / `XLS 분리 실행` 로 바뀐다. 하단 로그창은 공유한다.
- **C/C++ 사용자는 [XLS 분리]만 쓰면 된다.** [코드 자동수정]은 Roslyn C# 파서 기반이라 다른 언어에 쓸 수 없고, 그 화면(및 프로젝트 경로 입력)은 [XLS 분리] 대분류에서는 아예 렌더되지 않는다.
- `SparrowRunner.Gui.exe --xls <경로>` 로 기동하면 [XLS 분리] 대분류가 자동 선택된다.

### 대상 고르기

[코드 자동수정] 화면 맨 위의 입력 라벨은 **`대상 폴더`** 이고, 그 옆에 버튼이 **둘뿐**이다.

| 버튼 | 하는 일 |
| --- | --- |
| **[폴더 선택]** | 폴더 선택 대화상자를 연다. 입력칸이 이미 폴더면 거기서, **실재하는** `.sln`/`.csproj` 경로가 들어 있으면 **그 파일의 부모 폴더에서** 탐색이 시작된다 |
| **[새로고침]** | 대상은 그대로 두고 범위 트리를 다시 스캔한다(밖에서 파일이 추가·삭제된 뒤 목록을 맞출 때). 경로가 잘못됐으면 그때 메시지가 뜨고, git 여부도 여기서 다시 판정된다 |

- 예전에는 **[파일 선택]** 버튼이 하나 더 있었지만 제거했다. `.sln`/`.csproj` 중 무엇을 골라도 결국
  **그 파일의 부모 폴더**가 소스 루트가 되어 [폴더 선택]과 결과가 완전히 같았고, 선택지가 둘로 보이는 바람에
  "sln 을 골라야 하나?"라는 오해만 만들었다 — 그 오해가 "sln 을 골랐는데 트리가 안 나온다"는 신고로 이어졌다.
  (**그 신고의 실제 원인은 대상 선택이 아니라 낡은 발행본이었다** →
  [진입점이 알려 주는 실행 바이너리와 낡은 발행본 경고](#진입점이-알려-주는-실행-바이너리와-낡은-발행본-경고).)
- **경로를 직접 입력·붙여넣는 길은 그대로다.** 입력칸에 `.sln`/`.csproj` 경로를 붙여넣어도 받아들여
  부모 폴더로 환원해 쓴다 — 러너의 `-Solution` 과 **같은 규칙**이다(파일이면 부모 폴더, 폴더면 그대로).

### 로컬 소스 범위 트리 = 폴더 구조 (`.sln` 을 파싱하지 않는다)

[코드 자동수정] 화면 왼쪽의 **작업 범위(로컬 소스)** 트리는 대상의 **폴더 구조 그대로**다.

- 대상이 `.sln`/`.csproj` 면 **그 파일의 부모 폴더**가 루트, 폴더면 그 폴더가 루트다. 이건 러너가 쓰는 규칙(`Split-Path -Parent` 후 그 아래 `*.cs` 재귀)과 **똑같다** — 그래서 트리에 보이는 것과 러너가 실제로 고치는 것이 일치한다.
- 예전에는 트리가 `.sln` 을 파싱해 **sln 이 선언한 프로젝트 폴더 아래만** 담았다. 그래서 sln 에 없는 프로젝트, 루트 레벨 `.cs`, 느슨한 폴더의 `.cs` 가 **트리에 아예 안 보였고**, GUI 는 체크된 파일만 넘기므로 그 파일들은 영원히 안 고쳐졌다.
- 반대로 지금은 **대상 경로를 넓게 잡으면 그만큼 넓게 스캔한다.** 어디까지가 대상인지는 사용자가 정한다 — 대상 경로를 좁히거나, 트리에서 필요 없는 노드의 체크를 푼다.
- 제외는 그대로다: `bin` / `obj` / `.git` / `.vs` / `packages` 디렉터리와 생성 파일(`.g.cs` · `.Designer.cs` · 자동생성 헤더가 붙은 파일 등).

### git 이 없는 폴더를 대상으로 잡으면

자동수정은 소스 파일을 **실제로 고친다.** 대상 루트(와 그 상위)에 `.git` 이 없으면 **되돌릴 수단이 없으므로** GUI 가 다음처럼 동작한다.

> **판정 규칙(`FindGitRepositoryRoot`)**: 대상 루트에서 **상위로 올라가며** `.git` 을 찾는다(= git 자신의 판정).
> 하위 폴더를 골라도 상위가 저장소면 git 이다. `.git` 이 **디렉토리든 파일이든 인정**하므로
> **worktree·submodule 도 저장소로 본다.** 최대 **64단계**까지 올라가고, 거기까지 없으면 git 아님으로 본다.
> 판정 시점은 **대상 경로가 바뀌어 범위를 다시 스캔할 때마다 + 실행 직전**이다(경로가 그 사이 바뀌었을 수 있다).

- **[규칙별 커밋 생성] 체크박스가 비활성 + 해제**된다(툴팁에 이유가 뜬다). 커밋할 수 없는 상태에서 `-Commit` 을 넘겨 봐야 파일만 고쳐지고 커밋은 실패하기 때문이다.
- 대상 경로 아래에 **안내 배너**가 뜬다: 이 폴더는 git 저장소가 아니라 되돌릴 수단이 없다는 것 + 조치 방법.
- **[git 저장소 만들기]** 버튼을 누르면(확인 대화상자 후) 대상 루트에서 `git init` → `git add -A` → `git commit -m "baseline"` 을 실행해 **기준 커밋**을 만든다. 성공하면 상태를 다시 판정해 커밋 체크박스가 다시 켜진다. 실패하면 사유가 실행 로그에 남는다(git 미설치·PATH·`user.name`/`user.email` 미설정 등).
- **실행 버튼은 막지 않는다.** SVN 등 다른 버전관리를 쓰는 곳도 있어서, git 이 없다고 도구 자체를 못 쓰게 하지는 않는다 — 경고만 한다.

CLI 러너(`Run-SparrowSyntaxFix.ps1` / `Run-SparrowCommentFix.ps1`)도 같은 판정을 한다 — 다만 판정 수단은
`git rev-parse --is-inside-work-tree` 다. `-Commit` 인데 대상 루트가 git 저장소가 아니면 **규칙을 돌리기 전에**
사유를 구분해 알리고(`대상 루트가 git 저장소가 아닙니다(상위 폴더에도 .git 없음)` / `git 이 설치되어 있지 않거나
PATH 에 없습니다`) 커밋 단계를 통째로 건너뛴다. **파일 수정은 그대로 진행**하고, 그 사유는 러너 로그에도
`[GIT] commit skipped: …` 로 남는다. 예전처럼 git 사용법 도움말이 콘솔에 쏟아지거나
"git 락 5회 재시도 후에도 실패"라는 **오진**이 나오지 않는다.

- git 저장소이되 **작업트리가 dirty** 한 경우는 다르다 — 그건 **경고만** 하고 그대로 진행한다
  ([자동수정 러너 로그는 대상 소스 루트에 쌓인다](#자동수정-러너-로그는-대상-소스-루트에-쌓인다) 참조:
  러너가 로그를 먼저 써서 그 경고를 스스로 유발하기도 한다).

### 커밋 동작 — [규칙별 커밋 생성] 체크박스 (기본 꺼짐)

GUI 는 러너에 `-Commit` 과 `-NoCommit` 중 **반드시 하나**를 넘기고, 어느 쪽인지는 실행 줄의 **[규칙별 커밋 생성]** 체크박스가 정한다. 이 체크박스는 [코드 규칙]/[주석·레이아웃] 화면 전용이다([XLS 분리]는 소스를 건드리지 않으므로 숨겨진다).

| 체크박스 | 러너에 넘어가는 것 | 결과 |
| --- | --- | --- |
| **꺼짐 (기본값)** | `-NoCommit` | 파일만 수정하고 커밋하지 않는다. 실행이 끝나면 로그와 요약바에 `N개 파일 수정됨 — 커밋하지 않았습니다. git diff 로 검토 후 커밋하세요.` 가 뜬다 |
| **켜짐** | `-Commit` | 러너가 **규칙 하나마다 커밋 하나**를 만든다. 롤백 단위가 규칙이 되어 "괄호는 채택, var 는 거부" 같은 선택적 되돌리기(`git revert <커밋>`)가 된다. `review-needed` 규칙은 커밋 메시지에 `검토필요` 가 드러난다 |

- **기본값은 꺼짐이다.** 아무것도 건드리지 않고 실행하면 커밋은 일어나지 않는다.
- 변경 검토는 `git diff` 로 한다(라인 단위라 DryRun 의 "바뀔 파일·건수" 보고보다 상위 호환이다). [대상 폴더 열기] 버튼으로 폴더를 바로 열 수 있다.
- **규칙별 컴파일 게이트(`-VerifyCmd`)는 커밋 모드에서만 의미가 있다** — 게이트의 revert 기준선이 "직전 규칙 커밋" 이기 때문이다. GUI 는 `-VerifyCmd` 를 넘기지 않으므로, 게이트를 쓰려면 CLI 러너를 직접 호출한다.
- 생성 파일(`.g.cs` / `.designer.cs` / `obj`·`bin` 등)은 GUI 에서 **언제나 제외**한다 — 빌드가 다시 만들어 내므로 고칠 이유가 없다.
- **GUI 가 넘기지 않는 CLI 전용 옵션**: `-DryRun` · `-VerifyCmd` · `-ExePath` · `-IncludeGenerated`(이건 `Run-SparrowCommentFix.ps1` 에만 있다). 자동화/CI 에서 필요하면 `Run-SparrowSyntaxFix.ps1` / `Run-SparrowCommentFix.ps1` 을 직접 호출한다 — 그때는 **`-Rules` 를 반드시 명시**한다(아래 [CLI 자동화 주의](#cli-자동화-주의사항) 참조).

### XLS 범위 트리 = 팀 분담 (크로스-PC 불일치 없음)

[XLS 분리] 화면 왼쪽의 **작업 범위(XLS 경로)** 트리는 **로컬 소스를 뒤지지 않는다.** xls 가 스스로 적어 둔 검출 경로(`SparrowExporter.ListPaths`, 어떤 파일도 쓰지 않음)를 디렉토리 트리로 만든 것이다.

- 리프 = 파일(그 파일의 검출 건수), 폴더 = 하위 합계. 폴더를 체크하면 하위 전체가 선택된다.
- **공통 접두는 접는다.** 실 xls 는 모든 경로가 `D:\Work\ModuleB\branches\ModuleB\release\2026-01-01\` 처럼 긴 상위 폴더를 공유한다. 그 "자식이 하나뿐인" 체인은 트리에서 빼고 트리 위에 `공통 경로: …` 한 줄로 보여 주므로, 트리는 **실제 분기 폴더**(ModuleA/Core/ModuleB/src…)부터 시작한다. 접는 것은 **표시뿐**이고 선택·매칭에는 언제나 xls 원본 절대경로 전체를 쓴다. 길어서 잘리는 이름은 말줄임 + 마우스를 올리면 전체 경로가 뜬다(가로 스크롤 없음).
- 요약: `선택 N개 파일 · M건 (전체 …)`. **아무것도 체크하지 않으면 전건**(범위 필터 없음).
- 선택은 **xls 원본 경로 문자열 그대로** 익스포터에 `--files-from` 으로 넘어간다(`--root` 는 넘기지 않는다). 즉 **xls 를 자기 경로로 거르는** 완전일치 매칭이므로, 팀원마다 체크아웃 위치가 달라도 어긋날 수 없고 확장자(언어)와도 무관하다.
- 반대로 **크로스-PC 상대경로 매칭(Tier 2)** 은 **로컬 소스 경로를 직접 줄 때만** 해당된다 — [코드 자동수정]의 범위 트리, 또는 CLI 에서 `--root` + `--files-from` 을 함께 줄 때. 그 경우 전혀 매칭되지 않으면 조용한 빈 결과 대신 **[범위 불일치]** 진단이 로그에 뜬다.

## 폐쇄망 반입(오프라인 배포)

GUI와 러너는 평소 `dotnet run`/`dotnet build`로 동작한다. 이는 대상 PC에 `.NET SDK`와 NuGet 복원(=인터넷)을 요구하므로, 인터넷이 없는 폐쇄망 PC에서는 그대로 실행되지 않는다. 오프라인 반입은 다음 순서로 한다.

1. **인터넷 + `.NET SDK`가 있는 PC**에서 발행 스크립트를 실행한다. 도구 4종([코드 규칙]·[주석·레이아웃]·[XLS 분리] CLI + WPF GUI)이 각 프로젝트의 `publish\` 폴더로 발행된다.

   ```powershell
   # 기본: self-contained win-x64 (대상 PC에 .NET 런타임 불필요)
   .\tools\publish-airgap.ps1

   # 산출물 크기를 줄이려면(대상 PC에 .NET 8 런타임 필요)
   .\tools\publish-airgap.ps1 -FrameworkDependent

   # 무엇을 어디로 발행할지 미리보기(빌드 안 함)
   .\tools\publish-airgap.ps1 -DryRun
   ```

2. **레포 폴더 트리 전체**를 폐쇄망 PC로 복사한다. 반드시 함께 넘겨야 하는 것(경로는 전부 **레포 루트 기준**):
   - 방금 생성된 `publish\` 산출물 4곳 — `tools\SparrowRunner.Gui\publish\`, `tools\_internal\SparrowSyntaxFix\publish\`, `tools\_internal\SparrowCommentFix\publish\`, `tools\_internal\SparrowXlsExport\publish\`
     (레포 루트의 `SparrowRunner.Gui\` 폴더에는 `.sln` 하나만 있다. GUI **소스와 발행본은 `tools\SparrowRunner.Gui\` 쪽**이다 — 둘을 헷갈리지 말 것)
   - `tools\`의 러너/진입점(`Run-SparrowRunnerGui.cmd`, `Run-SparrowAll.cmd`, `_internal\...\Run-*.ps1`, `Compare-Sparrow.ps1`)

   > **왜 `publish\` 만 떼어 가면 안 되고 트리 통째여야 하나.** GUI 는 기동하자마자 `ResolveSkillRoot()` 로
   > 자기 exe 위치에서 위로 올라가며 **`SKILL.md` + `tools\Run-SparrowRunnerGui.cmd` +
   > `tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1` 세 파일을 동시에** 갖춘 폴더를 찾는다.
   > 그 폴더가 러너·`references\checkers\` 경로의 기준이 된다. 셋 중 하나라도 없으면 **예외를 던지고 창이 아예 안 뜬다.**
   >
   > **그리고 그 실패는 세션 로그를 한 줄도 남기지 않는다** — `ResolveSkillRoot()` 가 `SessionLog.Create` 보다
   > 먼저 실행되기 때문이다. 증상은 "더블클릭했는데 아무 일도 안 일어남" 이다. 그때 무엇을 수집할지는
   > [README 진단 로그](../README.md#진단-로그-문제가-났을-때-무엇을-첨부하나) 참조.

   > [XLS 분리] 익스포터는 선행 문서를 읽지 않으므로 별도 반입 자료가 없다. 체커별 가이드를 각자 쌓아두었다면(`references\checkers\`) 그것만 원하는 대로 함께 옮기면 된다.

   > `publish\` 산출물은 머신마다 생성되는 것이라 저장소에 커밋하지 않는다(`.gitignore` 제외 대상). 반입은 파일 복사로 한다.

3. 폐쇄망 PC에서 `tools\Run-SparrowRunnerGui.cmd`를 실행한다. 이 배치는 자기 폴더 기준으로 `SparrowRunner.Gui\publish\SparrowRunner.Gui.exe`(= 레포 루트 기준 `tools\SparrowRunner.Gui\publish\...`)가 있으면 그것을 바로 실행하고(없을 때만 `dotnet run`으로 폴백), 러너는 각 엔진 폴더의 `publish\SparrowSyntaxFix.exe` / `publish\SparrowCommentFix.exe`를 자동으로 집어 쓴다(`dotnet build`/복원 불필요). Windows 기본 `powershell.exe`만 있으면 된다.

   > 실행하면 첫 줄에 `[INFO] Launching published bundle: <경로>` 가 찍히므로 **반입한 발행본이 정말 실행됐는지**
   > 그 자리에서 확인할 수 있다. 반입본을 갱신했는데 화면이 그대로면 이 경로부터 본다 →
   > [진입점이 알려 주는 실행 바이너리와 낡은 발행본 경고](#진입점이-알려-주는-실행-바이너리와-낡은-발행본-경고).

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
sparrow-toolkit/
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
- [XLS 분리]는 어떤 항목도 버리지 않는다. 체커 키를 모르는 행도 그냥 하나의 항목 md가 된다(전건 정책 — 심각도/체커 필터 없음).
- 폐쇄망 실제 코드를 학습 자료로 남길 때는 `references/real-fix-patterns/`에 최소 before/after 형태만 익명화해서 기록한다.

## [XLS 분리] 출력물

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

- **A. 규칙 라이브러리** (xls 무관): 이름 붙인 규칙 목록 + 에디터. **[새 규칙]** 버튼으로 만들고(이름·내용을 입력한 뒤 **[규칙 저장]**), 목록에서 골라 편집한다. 규칙 md는 UTF-8(BOM 없이)로 저장된다. 창을 열면 **첫 규칙이 자동 선택**되어 이름·내용이 채워진 상태로 보이고, 파괴적인 **[선택 규칙 삭제]** 는 [새 규칙] 옆이 아니라 **목록 아래 우측**에 따로 있다(실수 클릭 방지 · 확인 다이얼로그는 그대로).
- **B. 체커 매핑** (현재 xls의 검출 체커): 각 체커 행 = 체커 키 + 건수 + **규칙 선택 ComboBox**(라이브러리 규칙들 + "— 없음 —"). 규칙을 고르면 그 체커의 지정이 바뀌고, **기억된 지정은 미리 선택**되어 나타난다(파일명이 체커 키와 같아도 지정 안 했으면 "— 없음 —"). [지정 저장]이 `_assignments.json`에 기록한다. 미지정 체커가 위로 정렬된다.

**실행하면** `_assignments.json`을 읽어 **지정된 체커만** 그 규칙을 해당 체커의 모든 항목 md에 self-contained 부착하고(멱등), 지정 안 된 체커는 순수 출력이다. 흐름은 **xls 로드 → 규칙 관리 창에서 지정 → 실행(지정만 부착)** 이다. CLI에서는 `--guides <폴더>`를 주면 그 폴더의 `_assignments.json` 지정대로 부착한다(주지 않으면 순수).

## 진단 로그

"언제·어떤 입력에서 뭐가 잘못됐나"를 나중에 판단할 수 있도록 다섯 종류의 증거(로그 4종 + 창 스냅샷)가 남는다.
기록은 전부 best-effort다 — 폴더가 읽기전용이어도 앱/테스트는 그대로 동작한다.
**1~3 은 자동이고, 5(창 스냅샷)는 `--screenshot-dir` 를 줄 때만 활성이다.**

### 1) GUI 세션 로그 — `%LOCALAPPDATA%\SparrowRunner\logs\session-<yyyyMMdd-HHmmss>.log`

화면 로그와 **같은 내용 + 줄마다 `HH:mm:ss.fff`**. 맨 앞에 시작 헤더가 붙는다: 앱 버전, 실행 파일, 시작 인자,
스킬 루트, guides 폴더, 로그 폴더, OS, .NET 런타임, PID/아키텍처, 작업 폴더.
Program Files 같은 쓰기 불가 위치에서 실행해도 되도록 설치 폴더가 아니라 `%LOCALAPPDATA%`에 쓴다.
최신 20개만 보관하며, `--log-dir <DIR>`로 위치를 바꿀 수 있다(테스트가 이 옵션으로 실 폴더 오염을 막는다).

미처리 예외(Dispatcher/AppDomain/Task)도 이 파일에 기록된다(예외를 삼키지는 않는다 — 증거만 남긴다).
정상 종료 시 마지막 줄은 `세션 종료 (정상)`이므로, **이 표식 없이 끊긴 로그는 비정상 종료(크래시/강제 종료)**로 읽으면 된다.

> **⚠ 이 로그가 아예 안 생기는 경우가 하나 있다 — 시작 실패.**
> GUI 는 생성자에서 `ResolveSkillRoot()`(스킬 루트 탐색)를 **`SessionLog.Create` 보다 먼저** 부르고,
> `App.xaml.cs` 에는 미처리 예외 핸들러가 없다. 그래서 **폐쇄망에서 가장 흔한 실패인 "레포 루트를 못 찾음"** 은
> 로그 파일이 열리기도 전에 일어나 **세션 로그를 0줄** 남긴다. 증상: 창이 안 뜨고 조용히 끝난다.
>
> 그때 수집할 것:
> 1. **레포 트리에 이 셋이 다 있는지** — `SKILL.md` · `tools\Run-SparrowRunnerGui.cmd` ·
>    `tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1`. **세 파일을 동시에** 갖춘 폴더가 스킬 루트다.
>    `publish\` 폴더만 떼어 반입했다면 반드시 여기서 실패한다.
> 2. **exe 의 실제 경로**(어느 폴더에서 기동했는지).
> 3. **`cmd` 창에서 `tools\Run-SparrowRunnerGui.cmd` 를 직접 실행한 출력** — 예외 메시지가 콘솔에는 보인다.
> 4. Windows **이벤트 뷰어 → Windows 로그 → 응용 프로그램**의 .NET Runtime 오류 항목.

### 자동수정 러너 로그는 대상 소스 루트에 쌓인다

[코드 규칙]·[주석·레이아웃]을 GUI 로 돌리면 러너가 `Run-SparrowSyntaxFix.<stamp>.log` / `Run-SparrowCommentFix.<stamp>.log` 를
**당신의 소스 루트**에 쓴다. GUI 가 러너의 `-LogDir` 로 **대상 경로에서 환원한 소스 루트**를 넘기기 때문이다
(`%LOCALAPPDATA%` 가 아니다). 로그에는 규칙별 stdout 전문·exit 코드·커밋/게이트 판정이 들어간다.

부작용 둘을 알고 있어야 한다.

1. **실행할 때마다 대상 레포에 로그 파일이 하나씩 쌓인다.** 그 레포의 `.gitignore` 에 `*.log` 가 없으면
   추적되지 않은 파일로 계속 늘어난다. 주기적으로 지우거나 대상 레포의 `.gitignore` 에 규칙을 넣는다.
2. **러너가 자기 경고를 스스로 유발한다.** 러너는 로그를 **먼저 쓴 뒤** `git status --porcelain` 으로
   작업트리를 검사하므로, 깨끗한 레포에서 실행해도 `작업트리에 미커밋 변경이 있습니다` 경고가 뜬다.
   **안내일 뿐 실행을 막지 않는다** — 그 개수에 방금 생긴 로그 파일이 포함돼 있다고 보면 된다.

대상 레포를 건드리기 싫으면 GUI 대신 CLI 러너를 직접 호출하고 `-LogDir` 를 다른 폴더로 준다.
**그 폴더는 미리 만들어 둬야 한다** — 러너는 `-LogDir` 를 생성하지 않고 바로 그 안에 쓰므로,
없으면 첫 줄을 쓰다 `[FATAL] Run-SparrowSyntaxFix 중단: ...` 으로 죽는다.

```powershell
New-Item -ItemType Directory -Force C:\work\sparrow-logs | Out-Null
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1 `
    -Solution C:\work\MyApp -Rules parens,obviousvar -LogDir C:\work\sparrow-logs -NoCommit
```

### 2) [XLS 분리] 실행 리포트 — 같은 폴더의 `xlssplit-<stamp>.json` + `xlssplit-<stamp>.log`

[XLS 분리]를 한 번 돌릴 때마다 **기계 판독 가능한** 실행 증거가 남는다(사람용 요약은 같은 이름의 `.log`).

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

**리포트는 출력 폴더에 쓰지 않는다.** [XLS 분리] 출력 폴더는 "체커 폴더 + 항목 md만, 부산물 0" 계약을 유지해야 하므로
리포트는 로그 폴더로 간다. CLI는 `--report <PATH>`를 줄 때만 만들고, 주지 않으면 **산출물이 바이트 동일**하다.

```powershell
# 리포트까지 남기며 익스포트(출력 폴더는 그대로 순수)
# 개발 PC(dotnet build 후):
.\tools\_internal\SparrowXlsExport\bin\Release\net8.0\SparrowXlsExport.exe issues.xls `
    --out C:\work\out --guides ...\references\checkers --report C:\work\logs\run1.json
```

> **폐쇄망 PC 에는 `bin\Release\net8.0\` 이 없다.** `bin\` 은 빌드 산출물이라 반입 대상이 아니고
> `.gitignore` 도 막는다. 반입본에서 쓸 경로는 **`publish\`** 다.
>
> ```powershell
> .\tools\_internal\SparrowXlsExport\publish\SparrowXlsExport.exe issues.xls `
>     --out C:\work\out --report C:\work\logs\run1.json
> ```

### 3) 테스트 진단 — `tests\_logs\` (gitignore)

| 파일 | 내용 |
| --- | --- |
| `uia-<stamp>\result.log` | UIA 하네스의 체크별 PASS/FAIL 전문(기대/실제 수치 포함) |
| `uia-<stamp>\tree-<n>-<단계>-iter<i>.txt` | 단계별(메인창 로드 / 관리창 오픈 / 규칙 저장 / 지정 저장 / 실행 후 / 범위 좁힌 실행 후 / 코드 자동수정 화면) **UIA 트리 덤프**. 한 줄 = 한 요소: `ControlType \| id=… \| name=… \| Rect(x,y,w,h) \| Off=… \| En=… \| Val="…"` |
| `uia-<stamp>\gui-logs\iter<i>\` | 그 실행에서 앱이 스스로 남긴 세션 로그 + [XLS 분리] 리포트 |
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

### 4) 창 스냅샷

파일 위치: `uia-<stamp>\shots\iter<i>\<순번>-<지점>-<타임스탬프>.png`

이 GUI는 설치되지 않는 커스텀 exe라서 **OS 자동화 허용목록에 올릴 수 없다 = 외부에서 스크린샷을 찍을 수 없다.**
그래서 앱이 **스스로 자기 창을 PNG로 렌더**한다(`RenderTargetBitmap` + `PngBitmapEncoder`).

- **실제 DPI 배율로 렌더**한다(`VisualTreeHelper.GetDpi`, 96 고정 금지) — 125%/150% 데스크톱에서도 화면과 같은 크기·선명도.
- 렌더 전에 **불투명 바탕(흰색 + 창 배경)** 을 먼저 깐다 — 투명 PNG는 판독이 불가능하다.
- `.tmp`로 쓰고 rename 하므로, 폴더를 감시하는 쪽이 **반쯤 쓰인 PNG를 보지 않는다.**
- 전부 best-effort다: 창이 아직 레이아웃되지 않았거나 폴더를 쓸 수 없으면 **실패를 로그로만** 남기고 앱은 그대로 동작한다.

캡처 시점은 두 가지다.

| 트리거 | 언제 | 파일명 지점 |
| --- | --- | --- |
| **자동** | 메인 창 로드 완료 / [체커 규칙 관리] 창 오픈 직후 / [XLS 분리] 실행 완료 후(메인 창) | `main-loaded` · `manager-open` · `after-run` |
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
# 이 솔루션에는 GUI + SparrowXlsExport.Core 두 프로젝트만 들어 있다.
dotnet build .\SparrowRunner.Gui\SparrowRunner.Gui.sln -c Release
# 엔진 CLI 3종은 솔루션 밖이라 개별 빌드한다.
dotnet build .\tools\_internal\SparrowSyntaxFix\SparrowSyntaxFix.csproj -c Release
dotnet build .\tools\_internal\SparrowCommentFix\SparrowCommentFix.csproj -c Release
dotnet build .\tools\_internal\SparrowXlsExport\SparrowXlsExport.csproj -c Release
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

(위 구문검사는 `validate.ps1`이 이미 포함한다.)

[XLS 분리] 익스포터/G2 게이트를 바꾼 경우:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\validate.ps1 -IncludeG2GateTests
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\e2e-lab\run-e2e.ps1
dotnet run --project .\tools\_internal\SparrowXlsExport.Core\CoreTests\CoreTests.csproj -c Release -- --fixtures-only
```

> **실행 정책**: Windows 기본값은 `Restricted` 라 `.\validate.ps1` 를 그냥 부르면 막힌다.
> 이 문서와 `README.md` 는 **`powershell -NoProfile -ExecutionPolicy Bypass -File <스크립트>` 형태를 표준으로 쓴다.**
> 정책을 이미 완화해 둔 PC 라면 `.\validate.ps1` 처럼 짧게 써도 결과는 같다.

## 파괴적 기능을 안전하게 시험하기 (샌드박스)

[코드 자동수정]은 **소스 파일을 실제로 덮어쓴다.** 처음 쓰거나 새 규칙/새 러너를 시험할 때는
**절대 실제 작업 레포에 바로 대지 말 것.** 이 레포에 시험용 합성 프로젝트가 이미 들어 있다.

```text
tests\e2e-lab\SampleApp\        # 합성 C# 5파일 + SampleApp.csproj (실 식별자 없음, 결함을 일부러 심어 둔 미니 프로젝트)
```

**절차** — 네 가지를 지키면 되돌릴 수 없는 사고가 나지 않는다.

1. **복사본에서 한다.** 원본을 고치면 레포 픽스처가 더러워진다.

   ```powershell
   Copy-Item -Recurse .\tests\e2e-lab\SampleApp C:\work\sandbox\SampleApp
   ```

2. **`-LogDir` 폴더를 먼저 만든다.** 러너는 이 폴더를 만들지 않는다 — 없으면 `[FATAL]` 로 죽는다.

   ```powershell
   New-Item -ItemType Directory -Force C:\work\sandbox\logs | Out-Null
   ```

3. **`-DryRun` 으로 먼저 본다.** 파일을 쓰지 않고 규칙별 건수만 보고한다.
4. **`-Rules` 를 반드시 명시한다.** 이유는 바로 아래.

```powershell
# 1차: 아무것도 안 쓰고 무엇이 바뀔지만 본다
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1 `
    -Solution C:\work\sandbox\SampleApp -Rules parens,obviousvar `
    -LogDir C:\work\sandbox\logs -DryRun

# 2차: 실제로 고치되 커밋은 안 한다. 그 뒤 git diff 로 눈으로 확인
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1 `
    -Solution C:\work\sandbox\SampleApp -Rules parens,obviousvar `
    -LogDir C:\work\sandbox\logs -NoCommit
```

GUI 로 시험한다면 대상 경로에 **샌드박스 복사본**을 넣고, [규칙별 커밋 생성] 은 **꺼 둔 채로** 돌린 뒤 `git diff` 를 본다.

### CLI 자동화 주의사항

`-DryRun` 은 "자동화/CI 용" 옵션이지만 **러너 자체는 대화형이다.**
`-Rules` 를 생략하면 러너가 opt-in 규칙 **10개마다 `Read-Host` Y/N 프롬프트**를 띄운다.
비대화형 stdin(파이프라인·스케줄러·CI 러너)에서는 그 응답이 **전부 빈 문자열**이 되어
**opt-in 규칙이 조용히 전부 꺼진 채** "변경 없음" 을 보고한다 — 실패처럼 안 보이는 실패다.

- **CI/자동화에서는 예외 없이 `-Rules <a,b,c>` 를 명시한다.** 그러면 프롬프트 자체가 뜨지 않는다.
- 커밋 여부도 마찬가지다. `-Commit` / `-NoCommit` / `-DryRun` 중 하나도 안 주면 러너가 커밋 여부를 되묻는다
  (비대화형이면 커밋하지 않고 진행). **의도를 항상 스위치로 명시한다.**
- GUI 는 이 함정에 걸리지 않는다 — 체크박스에서 규칙을 모아 `-Rules` 를 항상 채워 넘기고,
  `-Commit`/`-NoCommit` 중 하나를 반드시 붙인다.
