# 기여 가이드

기여를 환영한다. 무엇을 어디에 추가하는지는 [docs/extending.md](docs/extending.md) 에 레시피로 있고,
이 문서는 **환경·빌드·테스트·규약**을 다룬다.

---

## 1. 개발 환경

| 항목 | 요구 |
|---|---|
| OS | **Windows** (GUI 가 WPF `net8.0-windows` 다. GUI 를 건드리지 않는 엔진/CLI 작업은 다른 OS 에서도 가능하지만, 게이트인 `validate.ps1` 은 Windows PowerShell 5.1 기준이다) |
| SDK | **.NET 8 SDK** |
| Shell | Windows 기본 `powershell.exe` (5.1) 로 검증한다. `pwsh` 7 도 대체로 동작하지만 게이트 기준은 5.1 이다 |
| git | 러너의 작업범위 격리가 `git commit --only --pathspec-from-file` 에 의존한다(2.48.1 확인). 오래된 git 은 확인 필요 |

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
./validate.ps1 -All         # 전체 게이트. PR 은 이게 통과해야 한다
./validate.ps1 -IncludeGuiUiaTests    # GUI 만 (실제 창을 띄운다)
```

- 전체 출력이 `tests/_logs/validate-<stamp>.log` 에 남는다. 실패를 신고할 땐 그 파일을 첨부한다.
- **`-All` 은 `tests/e2e-lab/run-e2e.ps1`(`-IncludeTrackCE2E`)을 포함하지 않는다.** 그 스크립트는 커밋된
  골든 fixture(`tests/e2e-lab/sample-before.xls`, `sample-after.xls`)를 **재생성**해 작업 트리를 더럽힌다.
  일부러 돌렸다면 **fixture 변경을 원복**한 뒤 커밋한다.
- 실 Sparrow xls 가 필요한 테스트는 입력이 없으면 자동 skip 한다(실패가 아니다).
- GUI UIA 하네스는 .NET SDK / UI Automation / 데스크톱 세션이 없으면 self-skip 한다.

## 4. 인코딩 규약 (이 레포에서 실제로 사고가 났다)

`references/track-a-roslyn-policy.md` 가 **이중 인코딩(mojibake)** 으로 한글 본문의 3분의 1이 깨진 적이 있다.
깨진 바이트는 `?` 로 치환돼 원문을 되돌릴 수 없었고(기계적 복구 불가), 결국 **살아남은 문맥과 실제 코드를
근거로 사람이 다시 쓰는 것 말고는 방법이 없었다.** 그래서 이 규약은 강제다.

- **모든 소스·문서는 UTF-8.**
- **기존 파일을 편집할 때는 그 파일의 BOM 유무와 개행(CRLF/LF)을 그대로 유지한다.**
  이 레포의 문서는 대부분 **UTF-8 BOM 없음 + LF** 다(`references/track-a-roslyn-policy.md` 만 BOM 있음).
  새 문서는 **UTF-8 BOM 없음 + LF** 로 만든다.
- **편집 도구가 개행/BOM 을 바꾸지 않는지 확인한다.** 전체 파일이 통째로 diff 에 뜨면 십중팔구 개행이 바뀐 것이다.
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
- **`./validate.ps1 -All` 이 통과해야 한다.** 통과 여부를 PR 설명에 적는다.
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
