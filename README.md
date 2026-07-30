# sparrow-toolkit

Sparrow(파수/Fasoo) 정적분석 산출물(`.xls`)의 지적사항을 **전건 수정(全件)** 하기 위한 도구 모음.
.NET Framework 4.7.2 레거시 코드베이스(MyApp)의 신뢰성시험 대비 — Sparrow 검출을 세 갈래(Track A/B/C)로
결정론적 자동수정 + 체커별 md 분리 익스포트로 처리한다.

> **출처**: 이 레포는 `peace-skillbank` 의 `skills/sparrow-static-analysis/` 를 독립 제품으로 분리한 것.
> Fresh-import 기준 커밋: **peace-skillbank@76c799c** (2026-07-23). 그 이전 상세 이력은 원 레포에 보존.

---

## 세 갈래 (Tracks)

| 트랙 | 대상 | 방식 | 언어 | 도구 |
|------|------|------|------|------|
| **A** 구문 | var 변환·논리식 괄호·foreach Cast·for 호이스팅·필드 분할 등 | Roslyn `CSharpSyntaxTree.ParseText`(구문전용, 프로젝트 로드 없음 = 레거시 안전) 결정론적 재작성 | **C# 전용** | `SparrowSyntaxFix` |
| **B** 주석/레이아웃 | 주석 앞 빈 줄·간격·마침표·대문자화·후행주석 승격 등 | 동일 구문전용 재작성 | **C# 전용** | `SparrowCommentFix` |
| **C** 분리 | 검출 전건을 체커별 md로 | xls를 파싱해 검출 **전건을 체커별 md로 분리** — 각 md는 xls 컬럼(파일·라인·함수·경로·체커 설명·소스 코드)만 렌더링한다 | **언어 무관** | `SparrowXlsExport(.Core)` |

### 언어 지원

- **Track A/B는 C# 전용이다.** Roslyn C# 파서(`CSharpSyntaxTree`)로 코드를 파싱해 재작성하므로 C·C++ 등 다른 언어에는 쓸 수 없다.
- **Track C는 언어에 제약이 없다.** xls의 `언어` 컬럼을 읽지 않고(제외 컬럼), 소스 코드 셀을 **파싱하지 않고 문자열 그대로** 옮기므로, C·C++·C#·Java 등 Sparrow가 검출한 어떤 언어의 결과든 체커별 md로 분리할 수 있다.
- 따라서 **C/C++ 프로젝트는 Track C(체커별 md 분리)만 사용**하고, 자동수정(A/B)은 C# 프로젝트에서만 동작한다.

- **전건 수정 정책(Policy A)**: 심각도/체커 필터로 검출을 걸러내지 않는다. Sparrow가 검출한 전건을 대상으로 한다.
- Track A/B 는 러너(`Run-SparrowSyntaxFix.ps1` / `Run-SparrowCommentFix.ps1`)가 규칙별로 적용·(선택)커밋한다.
- **Track C는 순수 익스포터다.** 선행 문서(체커 가이드·프롬프트·판정 계약)를 일절 요구하지 않는다.
  입력은 Sparrow xls 하나, 출력은 **체커 키 폴더 + 그 안의 항목 md**(`<체커 키>/{ID}_{파일명}_{라인}.md`) 뿐이다.
  인덱스·요약·작업지침 파일은 만들지 않는다. 소스는 건드리지 않는다.
  체커별 가이드가 필요하면 **각자 로컬로 쌓는 자산**이며(`references/checkers/`는 gitignore), 레포는 배포하지 않는다.

## GUI — "Sparrow Helper"

WPF(net8.0-windows) 프런트엔드(`SparrowRunner.Gui`). 핵심 컨셉:

- **상단 대분류 2개로 화면이 완전히 갈린다**(입력도 위험도도 다르므로 섞지 않는다):

  | 대분류 | 하위 탭 | 입력 | 성격 |
  |---|---|---|---|
  | **코드 자동수정 (C#)** | **[코드 규칙]** / **[주석·레이아웃]** | 대상 `.sln/.csproj/폴더` + **로컬 소스 범위 트리** | 소스 파일을 **수정**(파괴적, 커밋은 안 함) · **C# 전용** |
  | **XLS 분리 (모든 언어)** | (없음) | **Sparrow 결과 XLS 하나** + **XLS 경로 범위 트리** | **읽기전용** · **프로젝트 경로 불필요** · 언어 무관 |

  화면 명칭 ↔ 내부 트랙: **[코드 규칙] = Track A · [주석·레이아웃] = Track B · [XLS 분리] = Track C**
  (트랙은 코드·문서·커밋 메시지에만 쓰는 내부 명칭이고 화면에는 노출하지 않는다).

  `--trackc-xls` 로 기동하면 [XLS 분리] 대분류가 자동 선택된다. 실행 버튼은 항상 **지금 선택된 화면**만
  돌린다([코드 자동수정] 화면에서는 선택된 하위 탭, [XLS 분리] 화면에서는 XLS 분리). 로그창은 공유한다.
- **GUI 는 기본적으로 파일만 수정하고 커밋하지 않는다.** 실행이 끝나면
  `N개 파일 수정됨 — 커밋하지 않았습니다. git diff 로 검토 후 커밋하세요.` 안내가 로그와 요약에 뜬다. 변경 검토는
  `git diff`(라인 단위)로 하고 커밋은 사용자가 직접 한다.
- **[규칙별 커밋 생성]** 체크박스(액션바, [코드 자동수정] 화면 전용, 기본 꺼짐)를 켜면 러너가 **규칙 하나마다 커밋**을
  만든다 — 롤백 단위가 규칙이 되어 `git revert <커밋>` 으로 "괄호는 채택, var 는 거부" 같은 선택적 되돌리기가 된다.
  러너의 **규칙별 컴파일 게이트(`-VerifyCmd`)** 도 이 모드에서만 의미가 있다(컴파일이 깨지는 규칙만 자동 revert + 커밋 skip).
- **`-DryRun`·생성 파일 포함(`-IncludeGenerated`)·`-VerifyCmd` 는 CLI 러너 옵션으로만 남아 있다**(자동화/CI 용).
  DryRun 이 주는 정보(변경 예정 파일·건수)는 `-NoCommit` 으로 적용한 뒤 `git diff` 를 보는 쪽이 라인 단위로 더 정확하다.
- **범위 선택 = 팀 분담**(검출 제외가 아니다). 대분류마다 트리의 출처가 다르다:
  - [코드 자동수정]: **로컬 소스 스캔**(폴더/파일 트리). 실제 파일을 고쳐야 하므로 당연히 로컬 소스 기준
    (생성 파일 `.g.cs`/`.designer.cs`/`obj`·`bin` 은 GUI 에서 언제나 제외 — 빌드가 다시 만들어 낸다).
  - [XLS 분리]: **XLS 자신이 적어 둔 검출 경로**로 만든 디렉토리 트리(파일마다 검출 건수, 폴더에 하위 합계).
    로컬 소스를 뒤지지 않으므로 프로젝트 경로가 필요 없고, 선택을 **xls 원본 경로 그대로** 익스포터에 되먹이므로
    매칭이 완전일치다 — **팀원마다 체크아웃 위치가 달라도 어긋날 수 없다.** 아무것도 체크하지 않으면 전건.
- [XLS 분리] 출력 폴더를 지정하면 그 폴더 밑에 **체커 키마다 폴더 하나**가 생기고 그 안에 항목 md만 놓인다
  (GUI에서 비워두면 xls 옆 `<이름>.export`, CLI에서 `--out` 생략 시 `<이름>.items`). 별도 준비 파일은 없다.
- **체커 규칙 = 이름 붙인 라이브러리 + 사용자 지정(자동 매핑 없음)**: 규칙은 `references/checkers/`의
  `<이름>.md`(단, `_`로 시작하는 파일 제외)로 쌓는 **이름 붙인 라이브러리**다(한 규칙을 여러 체커에 재사용 가능).
  체커에 규칙을 **붙이려면 사용자가 직접 지정**해야 한다 — 규칙 파일명이 체커 키와 같아도 **자동으로 지정되지
  않는다.** 지정은 `references/checkers/_assignments.json`(`{ "<체커 키>": "<규칙 이름>" }`)에 저장되며, 다음에
  같은 체커가 나오면 그 지정이 **미리 채워진다(기억)**. [XLS 분리] 화면에는 요약("검출 체커 N종 · 매핑 M · 미매핑 K")과
  **[체커 규칙 관리]** 버튼만 있고, 규칙 CRUD와 체커 지정은 **별도 창(RuleManagerWindow)**에서 한다: 왼쪽
  **규칙 라이브러리**(규칙 만들기/편집/삭제 — 창을 열면 첫 규칙이 자동 선택되고, 파괴적인 [선택 규칙 삭제]는
  목록 아래 우측에 따로 둔다), 오른쪽 **체커 매핑**(각 검출 체커에 규칙 ComboBox로 지정, 미지정은 "— 없음 —").
  실행하면 **지정된 체커만** 그 규칙이 모든 항목 md에 self-contained 부착되고(멱등),
  지정 안 된 체커는 순수 출력이다. 흐름은 **xls 로드 → 규칙 관리 창에서 지정 → 실행(지정만 부착)** 이다.

### 범위 매칭이 어긋나지 않는 이유 (크로스-PC)

공유 xls 하나(예: PC-A의 `D:\Work\MyApp\...` 경로)를 팀이 나눠 고칠 때, 경로 기준이 서로 다르면 범위 필터가
빈 결과를 낼 수 있다. 익스포터의 매칭은 네 단계다:

| 단계 | 언제 | 무엇을 비교 |
|---|---|---|
| **Tier 0** | GUI [XLS 분리] 범위 트리(= `--files-from` 만, `--root` 없음) | **xls 자기 경로 문자열끼리 완전일치**. 언어 무관(.cs/.cpp/.h), 체크아웃 위치와 무관 |
| Tier 1 | 로컬 소스 선택(같은 PC) | 절대경로 완전일치 |
| Tier 2 | 로컬 소스 선택(다른 PC) | 선택 파일의 **root 상대 경로 꼬리**가 xls 경로 끝과 디렉토리 경계에서 일치 |
| Tier 3 | xls `경로` 가 비었을 때 | 파일명이 선택·root 양쪽에서 유일할 때만 |

즉 **크로스-PC 상대경로 매칭(Tier 2)은 CLI 등에서 "로컬 소스 경로"를 직접 줄 때만 쓰인다.** GUI의 XLS 분리
화면은 Tier 0 이므로 애초에 불일치가 생기지 않는다. 전혀 매칭이 안 되면 조용한 빈 결과 대신 **[범위 불일치]**
진단을 로그에 전시한다.

## 빌드 & 실행

```powershell
# GUI 솔루션 빌드(엔진 도구까지 ProjectReference로 함께 빌드됨)
dotnet build SparrowRunner.Gui/SparrowRunner.Gui.sln -c Release

# 엔진 도구만 개별 빌드가 필요하면
dotnet build tools/_internal/SparrowXlsExport/SparrowXlsExport.csproj -c Release
dotnet build tools/_internal/SparrowSyntaxFix/SparrowSyntaxFix.csproj -c Release
dotnet build tools/_internal/SparrowCommentFix/SparrowCommentFix.csproj -c Release

# GUI 실행(발행 후)
./tools/SparrowRunner.Gui/publish/SparrowRunner.Gui.exe
```

### 폐쇄망 반입(오프라인 발행)

```powershell
# self-contained win-x64 로 각 도구를 발행(각 tools/**/publish/ 에 생성; gitignore — 커밋 안 함)
./tools/publish-airgap.ps1
```

## 진단 로그 (문제가 났을 때 무엇을 첨부하나)

화면 로그는 앱을 닫으면 사라지므로, **네 곳**에 사후 판단용 증거가 남는다. 전부 자동이고 기록 실패가 기능을
깨뜨리지 않는다(best-effort).

| 무엇 | 어디 | 들어있는 것 |
|---|---|---|
| **GUI 세션 로그** | `%LOCALAPPDATA%\SparrowRunner\logs\session-<yyyyMMdd-HHmmss>.log` | 시작 헤더(앱 버전·시작 인자·스킬 루트·guides 폴더·OS/.NET·PID) + 화면 로그 전 줄에 `HH:mm:ss.fff` 타임스탬프 + 미처리 예외 + `세션 종료 (정상)` 표식(**이 표식 없이 끊긴 로그 = 비정상 종료**). 최신 20개 보관 |
| **Track C 실행 리포트** | 같은 폴더의 `trackc-<stamp>.json` (+ 사람이 읽는 `.log` 요약) | 입력 xls 경로/크기/**sha256**, 출력·guides 폴더, 소요 ms, 옵션(범위 필터·root·severity/checker/max), 전체 행/매칭/기록 md/체커 폴더 수, **체커별 건수**, **체커별 지정 규칙·부착 건수**, 미매핑 체커, 범위 진단, 경고 목록 |
| **테스트 진단** | `tests/_logs/` (gitignore) | `uia-<stamp>/result.log`(체크별 PASS/FAIL 전문) · `uia-<stamp>/tree-*.txt`(**UIA 트리 덤프** — 요소별 `ControlType/AutomationId/Name/Rect(x,y,w,h)/IsOffscreen/IsEnabled/Value`) · `uia-<stamp>/gui-logs/`(그 실행의 앱 세션 로그+리포트) · 실패 시 `FAILURE-CONTEXT-iter<N>.txt` · `validate-<stamp>.log` |
| **창 스냅샷(PNG)** | `tests/_logs/uia-<stamp>/shots/iter<N>/*.png` (gitignore) | **앱이 스스로 렌더한 실제 창 이미지**. 자동 지점(메인창 로드 / 관리창 오픈 / 실행 완료)마다 1장 + 하네스가 요청한 시점(`req-main`/`req-manager`/`req-assign-saved`/`req-xls-scoped`/`req-fix-section`/`req-exception-tab`) 1장씩. 파일명 = `<순번>-<지점>-<타임스탬프>.png` |

- 리포트는 **절대 Track C 출력 폴더에 쓰지 않는다** — 출력 폴더는 "체커 폴더 + 항목 md만, 부산물 0" 계약을 유지한다.
- CLI 도 `--report <PATH>` 로 같은 json 을 만들 수 있다. 옵션을 주지 않으면 산출물은 **바이트 동일**(순수성 불변).
- 로그 위치를 옮기려면 `SparrowRunner.Gui.exe --log-dir <DIR>`, `./validate.ps1 -LogDir <DIR>`.
- **UI 관련 문제 신고**: `tests/_logs/uia-*/shots/**/*.png`(실제 화면) + `tests/_logs/uia-*/tree-*.txt`(수치)를 함께 첨부한다.
  트리 덤프는 잘림·0크기·화면밖·겹침을 수치로 판정하고(UIA 하네스는 요소가 창 경계 안에 있는지, 규칙 에디터 높이가 임계값
  이상인지, 목록·에디터가 겹치지 않는지를 단정한다), PNG 는 그 수치로는 안 보이는 것(빈 콤보, 잘못된 색/문구, 겹쳐 보이는
  여백)을 **눈으로** 보여 준다.

### 창 스냅샷 = 이 UI 를 눈으로 보는 방법

이 GUI 는 설치되지 않는 커스텀 exe 라서 OS 자동화 허용목록에 올릴 수 없다 — **외부에서 스크린샷을 찍을 수 없다.**
그래서 앱이 **스스로 자기 창을 PNG 로 렌더**한다(`RenderTargetBitmap`, 실제 DPI 배율로 · 불투명 바탕).

```powershell
# 임의 위치에 스냅샷을 남기며 기동(인자를 주지 않으면 스냅샷 기능 전체가 꺼진 기존 동작이다)
./SparrowRunner.Gui.exe --screenshot-dir C:\work\shots

# 임의 시점 캡처: 이 폴더에 capture.request 파일을 만들면 현재 활성 창이 즉시 찍힌다
#   (파일 내용은 결과 파일명의 접미사로 쓰이고, 처리 후 요청 파일은 삭제된다)
"combo-expanded" | Set-Content -Encoding utf8 C:\work\shots\capture.request
```

캡처 결과는 세션 로그에 한 줄씩 남는다(`snapshot: <파일명> (WxHpx)` / 실패 시 `snapshot 실패: <사유>`).
UIA 하네스는 매 실행마다 PNG 6장 이상 · 유효 PNG(시그니처+IHDR, 10KB 초과 = 빈/투명 이미지 아님) ·
**PNG 픽셀 크기 ≈ UIA 창 Rect(±10%)** 를 단정하므로, 잘못된 스케일로 렌더되는 회귀도 걸린다.

## 테스트

```powershell
# 빠른 검사(빌드 없음): 소스 존재 + 모든 테스트 구문검사
./validate.ps1

# 전체 E2E(빌드+실행; .NET SDK 필요). 실 MyApp xls 필요한 테스트는 Downloads 부재 시 자동 skip.
./validate.ps1 -All
```

개별 트랙만: `./validate.ps1 -IncludeSyntaxFixE2E -IncludeCommentE2E -IncludeSparrowE2E`

Track C 파이프라인(익스포터 → 수정+빌드 G1 → G2 게이트)만 따로:

```powershell
./validate.ps1 -IncludeG2GateTests      # Compare-Sparrow G2 게이트 시나리오
./tests/e2e-lab/run-e2e.ps1             # 전체 파이프라인 (골든 fixture xls를 재생성함에 유의)
```

## 레이아웃

```
SKILL.md  HANDOFF.md          # 파이프라인 설명/인계 문서
references/                   # 참고 노트(Track A 정책·실수정 패턴). 도구 실행에 필요한 파일은 없음
tools/                        # Track A/B/C 엔진 + 러너 + 발행 스크립트 + Compare-Sparrow.ps1(G2 게이트)
  _internal/SparrowSyntaxFix, SparrowCommentFix, SparrowXlsExport(.Core)
SparrowRunner.Gui/            # WPF GUI
tests/                        # 회귀 테스트(실 xls 기반 포함) + e2e-lab + g2-gate-tests + validate 대상
docs/usage.md                 # 사용 안내
```

## 라이선스

[LICENSE](LICENSE) 참조.
