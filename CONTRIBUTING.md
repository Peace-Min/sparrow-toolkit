# 기여 가이드

기여를 환영한다. 무엇을 어디에 추가하는지는 [docs/extending.md](docs/extending.md) 에 레시피로 있고,
이 문서는 **환경·빌드·테스트·규약**을 다룬다.

---

## 1. 개발 환경

| 항목 | 요구 |
|---|---|
| OS | **Windows** (GUI 가 WPF `net8.0-windows` 다. GUI 를 건드리지 않는 엔진/CLI 작업은 다른 OS 에서도 가능하지만, 게이트인 `validate.ps1` 은 Windows PowerShell 5.1 기준이다) |
| SDK | **`net8.0` 을 빌드할 수 있는 .NET SDK — 8.0 이상이면 된다.** 아래 참조 |
| 런타임 | 빌드 산출물이 `net8.0` 대상이라 **실행에는 .NET 8 런타임**이 필요하다(`Microsoft.NETCore.App 8.0.x`, GUI 는 추가로 `Microsoft.WindowsDesktop.App 8.0.x`). `dotnet --list-runtimes` 로 확인 |
| Shell | Windows 기본 `powershell.exe` (5.1) 로 검증한다. `pwsh` 7 도 대체로 동작하지만 게이트 기준은 5.1 이다 |
| git | 러너의 작업범위 격리가 `git commit --only --pathspec-from-file --pathspec-file-nul` 에 의존한다. 이 조합은 **git 2.25 이상**이면 쓸 수 있고, **2.45.1 에서 `-Commit`·게이트 테스트 전부 통과를 실측**했다. 그보다 오래된 git 은 확인 필요 |

> **"`.NET 8 SDK` 를 깔아야 한다" 는 요건이 아니다.** 이 레포에는 `global.json` 이 없어서 SDK 버전을 고정하지 않는다.
> **.NET 8 SDK 가 설치되지 않은 PC**(SDK 는 `9.0.302` / `9.0.316` / `10.0.100-preview` 만 존재)에서
> 네 프로젝트 빌드와 `./validate.ps1 -All` 전 테스트가 통과하는 것을 실측했다.
>
> preview SDK 가 선택되면 빌드 로그에 `message NETSDK1057: .NET의 미리 보기 버전을 사용하고 있습니다.` 가
> 프로젝트마다 한 줄씩 뜬다. **경고가 아니라 정보 메시지이고 정상이다** — 빌드는 성공하며 게이트에도 영향이 없다.
> 특정 SDK 로 고정하고 싶으면 각자 로컬에 `global.json` 을 두되 **커밋하지 않는다**(다른 기여자의 환경을 강제하게 된다).

Visual Studio 로 열려면 `SparrowRunner.Gui/SparrowRunner.Gui.sln` 을 연다
(이 폴더에는 의도적으로 `.sln` 만 둔다).

## 2. 빌드

```powershell
# GUI + Track C 코어. 이 솔루션에 담긴 프로젝트는 이 둘뿐이다.
dotnet build SparrowRunner.Gui/SparrowRunner.Gui.sln -c Release

# 엔진 CLI 3종은 솔루션에 없다 — 개별 빌드
dotnet build tools/_internal/SparrowSyntaxFix/SparrowSyntaxFix.csproj -c Release
dotnet build tools/_internal/SparrowCommentFix/SparrowCommentFix.csproj -c Release
dotnet build tools/_internal/SparrowXlsExport/SparrowXlsExport.csproj -c Release
```

> A/B 러너는 엔진 exe/dll 이 없으면 스스로 증분 `dotnet build` 를 한다. 그래서 개발 중에는 개별 빌드를 생략해도 된다.

### 폐쇄망 발행

```powershell
./tools/publish-airgap.ps1                      # self-contained win-x64 (기본, 대상 PC 런타임 불필요)
./tools/publish-airgap.ps1 -FrameworkDependent  # 크기 축소(대상 PC에 .NET 8 런타임 필요)
./tools/publish-airgap.ps1 -DryRun              # 계획만 출력
```

새 엔진을 추가했다면 이 스크립트의 `$projects` 배열에 반드시 등록한다 —
등록하지 않으면 폐쇄망에서 그 트랙만 조용히 죽는다.

## 3. 테스트 (게이트)

```powershell
./validate.ps1              # 빌드 없음: 소스 존재 + 전체 PowerShell 구문검사 (수 초). 커밋 전 최소한 이건 돌린다
./validate.ps1 -All         # 전체 게이트. PR 은 이게 통과해야 한다. ★실제 WPF 창이 뜨고 수 분 걸린다★
./validate.ps1 -IncludeGuiUiaTests    # GUI 하네스만 따로 (실제 창을 띄운다)
```

- **`-All` 은 GUI UIA 하네스를 포함한다.** 즉 `-All` 은 **진짜 WPF 창을 띄웠다 닫으며 수 분이 걸린다.**
  (`-IncludeGuiUiaTests` 는 그 부분만 따로 돌리는 스위치이지, `-All` 에서 빠져 있다는 뜻이 아니다.)
  창이 뜨는 게 곤란한 환경이면 개별 스위치로 나눠 돌린다.
- **`CoreTests` 도 `-All` 에 포함된다**(`tests/coretests-run.ps1` 경유). "Track C 부산물 0" 의 가장 강한 단정이 여기 있다.
- **`-All` 은 `tests/e2e-lab/run-e2e.ps1`(`-IncludeTrackCE2E`)을 포함하지 않는다.** 그 스크립트는 커밋된
  골든 fixture(`tests/e2e-lab/sample-before.xls`, `sample-after.xls`)를 **재생성**해 작업 트리를 더럽힌다.
  일부러 돌렸다면 **fixture 변경을 원복**한 뒤 커밋한다.
- 실 Sparrow xls 가 필요한 테스트는 입력이 없으면 자동 skip 한다(실패가 아니다). → [3.2](#32-실-sparrow-xls-를-쓰는-테스트--자동-탐색과-끄는-법)
- GUI UIA 하네스는 .NET SDK / UI Automation / 데스크톱 세션이 없으면 self-skip 한다.
- 전체 출력이 `tests/_logs/validate-<stamp>.log` 에 남는다. **실패를 신고할 땐 그 파일을 첨부한다.**
  **단, 첨부 전에 반드시 내용을 훑는다** — 실 xls 를 쓰는 테스트가 돌았다면 그 로그와
  `tests/_logs/` 의 상세 진단 파일에 **사내 소스 조각이 들어갈 수 있다.** 자세한 건 [3.2](#32-실-sparrow-xls-를-쓰는-테스트--자동-탐색과-끄는-법).

### 3.1 게이트 결과 읽는 법 · 새 테스트를 추가할 때의 신호 규약

`validate.ps1` 은 마지막에 집계 배너를 찍는다.

```text
---- opt-in 테스트 집계: 실행 N · 스킵 M · 실패 K ----
```

- **실행 N** = 실제로 단정이 돈 테스트 수. **스킵 M** = 전제(.NET SDK / 실 xls / 데스크톱 세션)가 없어 건너뛴 수.
  **실패 K** = 실패한 수. 스킵과 실패는 각각 **이름 + 사유**까지 함께 출력된다.
- **`실행 0`** 이면 `[주의] opt-in 테스트가 전부 스킵됐습니다 — E2E 단정이 0개 실행됐습니다.` 가 뜬다.
  **그 실행은 게이트가 아니다.** "통과" 로 읽지 말고 PR 설명에도 그렇게 쓰지 않는다.
- 실패가 하나라도 있으면 **0 이 아닌 코드로 종료**하고 실패한 테스트 이름을 모아 출력한다.
  하나 실패해도 즉시 멈추지 않고 나머지를 마저 돌린 뒤 마지막에 모아 보고한다.

**새 테스트를 `tests/` 에 추가한다면 아래 신호 규약을 반드시 지킨다.** 부모는 이 세 가지만 구분한다.

| 뜻 | 자식 스크립트가 할 일 |
|---|---|
| **성공** | **마지막에 `exit 0`.** 필수다 |
| **실패** | `throw` **또는** `exit <0 이 아닌 값>`. 부모가 둘 다 잡는다 |
| **스킵** | `$global:SparrowTestSkip` 에 **사유 문자열**을 넣고 `return` |

> **`exit 0` 을 빠뜨리면 거짓 실패가 난다.** 그냥 끝내면 스크립트 안에서 마지막으로 호출한
> 네이티브 명령의 `$LASTEXITCODE` 가 그대로 남는다. 예컨대 "알 수 없는 규칙 → exit 2" 를 단정하고
> 끝나는 테스트는 **통과인데도 2 가 남아** 실패로 집계된다.
>
> 스킵을 exit 코드로 신호하지 않는 이유도 같다 — 스킵은 네이티브 호출 뒤 중간에서도 일어나므로
> exit 코드만으로는 "스킵" 과 "직전 명령의 잔여 코드" 를 구분할 수 없다. 그래서 전역 변수를 쓰고,
> 부모는 **스킵 판정을 exit 코드 판정보다 먼저** 한다.

### 3.2 실 Sparrow xls 를 쓰는 테스트 — 자동 탐색과 끄는 법

실 xls 가 있어야 도는 테스트가 둘 있다(`sparrow-exhaustive-xls-test.ps1`, `sparrow-realxls-scope-loop-tests.ps1`).
xls 경로는 **아래 순서로 해석**된다.

| 순위 | 출처 | 성격 |
|---|---|---|
| 1 | `-XlsPath <경로>` | 명시 지정 |
| 2 | `$env:SPARROW_TEST_XLS` | 명시 지정 |
| 3 | **`%USERPROFILE%\Downloads\issues_*.xls` 중 가장 최신** | **자동 탐색 — 아무 설정 없이도 동작한다** |

- 셋 다 못 찾으면 **skip**(실패가 아니다).
- **자동 탐색을 끄려면**: `-NoAutoDiscover` 스위치, 또는 `$env:SPARROW_TEST_XLS_NO_AUTODISCOVER=1`.
- 로그에는 **파일명·경로를 찍지 않는다.** `-XlsPath 로 명시 지정됨` / `자동 탐색됨: Downloads\issues_*.xls 중 최신`
  같은 **출처 라벨과 크기만** 남는다. `CoreTests` 도 같은 이유로 실 xls 를 절대 인자로 받지 않는다(`--fixtures-only` 고정).

> **⚠ 그래도 로그 공유 전에는 눈으로 확인한다.** 경로는 안 찍히지만, 이 테스트들은 **실 xls 의 내용**으로
> 돌기 때문에 `tests/_logs/` 의 로그·상세 진단 파일(`FAILURE-CONTEXT-*.txt`, 트리 덤프 등)에
> **당신의 사내 소스 조각·식별자가 들어갈 수 있다.** 첨부 전에 반드시 열어 본다.
> 확실히 배제하려면 `-NoAutoDiscover` 로 한 번 더 돌려 그 로그를 첨부한다.

## 4. 인코딩 규약 (이 레포에서 실제로 사고가 났다)

`references/track-a-roslyn-policy.md` 가 **이중 인코딩(mojibake)** 으로 한글 본문의 3분의 1이 깨진 적이 있다.
깨진 바이트는 `?` 로 치환돼 원문을 되돌릴 수 없었고(기계적 복구 불가), 결국 **살아남은 문맥과 실제 코드를
근거로 사람이 다시 쓰는 것 말고는 방법이 없었다.** 그래서 이 규약은 강제다.

- **모든 소스·문서는 UTF-8.**
- **기존 파일을 편집할 때는 그 파일의 BOM 유무와 개행(CRLF/LF)을 그대로 유지한다.**
  이 레포의 문서는 대부분 **UTF-8 BOM 없음 + LF** 다(`references/track-a-roslyn-policy.md` 만 BOM 있음).
  새 문서는 **UTF-8 BOM 없음 + LF** 로 만든다.
- **편집 도구가 개행/BOM 을 바꾸지 않는지 확인한다.** 전체 파일이 통째로 diff 에 뜨면 십중팔구 개행이 바뀐 것이다.
- **개행은 `.gitattributes` 가 고정한다.** 레포 루트의 `.gitattributes` 가 텍스트 파일의 개행을
  체크아웃/커밋 양쪽에서 고정하므로, 각자의 `core.autocrlf` 설정과 무관하게 같은 바이트가 나온다.
  이게 없으면 **`core.autocrlf=true` 인 Windows 클론에서 파일 전체가 diff 로 뜨는** 사고가 난다
  (실제 흔한 현상이고, 진짜 변경 한 줄이 수백 줄 잡음에 묻힌다).
  `.gitattributes` 를 지우거나 새 확장자를 규칙 없이 추가하지 말 것 — 새 텍스트 확장자를 들이면
  거기에도 규칙을 함께 넣는다. 이미 잘못 뜬 diff 는 `git add --renormalize .` 로 정리한다.
- **PowerShell 주의**: `Set-Content`/`Add-Content` 는 Windows PowerShell 5.1 에서 시스템 ANSI 코드페이지를
  기본으로 쓴다. 한글이 들어가는 파일을 쓸 땐 **반드시 `-Encoding utf8`** 을 명시한다.
  단 5.1 의 `-Encoding utf8` 은 **BOM 을 붙인다** — BOM 없는 파일을 써야 하면
  `[System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))` 를 쓴다.
  (엔진들이 `new UTF8Encoding(false)` 로 콘솔 인코딩을 잡는 것도 같은 이유다.)
- **커밋 전 확인**: 한글이 깨진 줄이 없는지 diff 를 눈으로 본다. `??`, `?듭떖`, 낯선 한자(`蹂닿컯`)가
  보이면 mojibake 다. 되돌리고 인코딩을 바로잡은 뒤 다시 편집한다.
- 코드 자체도 같은 원칙이다. `SourceFileIo` 는 대상 소스의 BOM/개행을 보존하고 비-UTF8 파일은 skip 한다 —
  **그 계약을 깨는 변경은 받지 않는다.**

## 5. PR 기대치

- **회귀 테스트를 동반한다.** 규칙을 추가했으면 픽스처(positive/negative/멱등성/안전성)를,
  버그를 고쳤으면 그 버그를 재현하는 케이스를 함께 낸다.
- **`./validate.ps1 -All` 이 통과해야 한다.** 통과 여부를 PR 설명에 적되,
  **집계 배너의 `실행 N · 스킵 M · 실패 K` 를 그대로 옮긴다**([3.1](#31-게이트-결과-읽는-법--새-테스트를-추가할-때의-신호-규약)).
  `실행 0` 은 통과가 아니다 — 그 PC 에 전제(.NET SDK/데스크톱 세션)가 없었다는 뜻이니 그렇게 적는다.
- **GUI 를 바꿨으면 UIA 하네스 단정을 갱신한다** (`tests/gui-uia-tests.ps1`). 탭을 추가/이름변경 했으면
  `$SUB_TABS` 와 하위 탭 개수 단정이 반드시 함께 바뀌어야 한다.
- **문서를 함께 갱신한다.** 규칙을 추가했으면 해당 엔진의 `README.md` 규칙 표를,
  트랙/화면을 바꿨으면 `README.md` 와 `docs/usage.md` 의 구성 표를 갱신한다.
  **틀린 문서는 없는 문서보다 나쁘다.**
- **커밋은 작게, 한 커밋 = 한 가지.** 메시지에 **무엇을 왜** 를 적는다. 예:

  ```text
  feat(A): forhoist 규칙 추가 — 다중 선언자 for 초기화절을 hoist 분해

  for(int i=0, n=q.Count; ...) 는 USE_ONE_DECLARATION_PER_LINE 에 걸린다.
  비루프 선언자를 for 앞으로 빼 for 를 단일 선언자로 유지한다.
  의존/이름충돌/루프변수 판정 불가 케이스는 skip. review-needed(opt-in).
  ```

- 자동수정 규칙 변경은 커밋을 **규칙 단위**로 나눈다(이 레포의 러너가 만드는 커밋과 같은 입자).
- `bin/`·`obj/`·`publish/`·`tests/_logs/` 는 커밋하지 않는다(`.gitignore` 가 막지만 확인할 것).

## 6. 실 데이터 반입 금지 (중요)

이 레포는 **공개**다. 폐쇄망/사내 자료가 들어가서는 안 된다.

**절대 커밋하지 않는 것**

- 실제 Sparrow 결과 `.xls` (`.gitignore`: `issues_*.xls`)
- 사내 경로, 서버명, 공유 폴더 경로
- 실제 파일명·클래스명·메서드명·변수명·문자열 리터럴·UI 문구·도메인 용어
- 체커별 조치 규칙 (`.gitignore`: `references/checkers/`) — 각자 로컬로 쌓는 자산이다
- Sparrow 공식 Rule 원문 (`.gitignore`: `references/sparrow-official-rules/`) — 제3자 독점 문서다

**대신**

- 테스트는 **합성 픽스처**로 만든다. `tests/e2e-lab/gen-xls/`, `tools/_internal/SparrowXlsExport/FixtureGen/`
  가 xls 픽스처를 생성한다.
- 문서·픽스처의 예시 식별자는 **중립 이름** 규약을 따른다: `MyApp`, `Proj`, `ModuleA`, `ModuleB`,
  `Foo`/`Bar`/`Baz`, `SampleComponent`. 실제 제품·과제·사람 이름을 쓰지 않는다.
- 실 수정 사례를 남기고 싶으면 `references/real-fix-patterns/` 에 **익명화한 최소 before/after 구조만**
  넣는다. 절차와 익명화 원칙은 [references/real-fix-patterns/README.md](references/real-fix-patterns/README.md),
  양식은 [TEMPLATE.md](references/real-fix-patterns/TEMPLATE.md).

`.gitignore` 가 위 경로들을 이미 막고 있지만, **최종 책임은 커밋하는 사람에게 있다.**
`git diff --cached` 를 한 번 훑고 커밋한다.

## 7. 건드리면 안 되는 것들

| 대상 | 이유 |
|---|---|
| `SKILL.md` (파일 존재 자체) | GUI 의 `ResolveSkillRoot()`, `validate.ps1` 의 소스 목록, `CoreTests` 가 이 파일을 **레포 루트 마커**로 쓴다. 내용은 고쳐도 되지만 **파일을 지우거나 옮기면 GUI 가 스킬 루트를 못 찾는다** |
| Track C 출력 폴더 계약 | "체커 키 폴더 + 항목 md 만, 부산물 0". 리포트·인덱스·요약을 출력 폴더에 쓰는 변경은 받지 않는다 |
| `SourceFileIo` 의 BOM/개행 보존 · 원자적 쓰기 | 남의 소스를 손상시키지 않겠다는 이 도구의 기본 약속이다 |
| 문자열 리터럴·주석 안전성 | 자동수정이 문자열 안의 `//`·`&&` 를 건드리면 그 순간 이 도구는 못 쓰는 물건이 된다 |
| 전건 정책 | 심각도/체커로 검출을 미리 버리지 않는다. 필터를 추가하려면 먼저 이슈로 논의한다 |
