# MyApp Sparrow 재분석 결과 분석 — 6827(원본) → 6869(3종 CLI 적용 후)

**측정일**: 2026-07-14 · **입력**: `issues_sample_6827.xls`(원본) vs `issues_sample_6869.xls`(Run-SparrowSyntaxFix +
Run-SparrowSyntaxFix + SparrowCommentFix 적용 후) · **대조 도구**: SparrowXlsExport / 로컬 NPOI 프로브.

## 총계
**7170 → 5778 (−1392, −19.4%)**. 그러나 체커별로 극과 극이다.

## 체커별 델타 (원본 → 최종)

| 체커 | before | after | delta | 판정 |
|---|---:|---:|---:|---|
| PRACTICE.OBVIOUS_VARIABLE_TYPE | 1385 | 48 | **−1337** | ✅ 대성공 |
| MISSING_PARENTHESIS_IN_EXPRESSION | 741 | **0** | **−741** | ✅✅ **완전 소거** |
| PRACTICE.LOOP_VARIABLE | 837 | 117 | −720 | ✅ |
| PRACTICE.OBJECT_INSTANTIATION | 1078 | 515 | −563 | ⚠️ 부분 |
| BETWEEN_MEMBER_DEFINITION.MISSING_BLANK_LINE | 616 | 577 | −39 | ❌ 미커버 |
| OBJECT_INITIALIZATION.NOT_USED_INITIALIZER | 64 | 27 | −37 | ✅ |
| OVERLY_BROAD_CATCH | 139 | 114 | −25 | (XLS 분리) |
| COMMENT.MISSING_PERIOD | 855 | 1753 | **+898** | ❌ (아래 주의) |
| COMMENT.MISSING_SPACE_AFTER_DELIMITER | 457 | 1045 | +588 | ❌ |
| COMMENT.LOWERCASE_FIRST_LETTER | 562 | 1149 | +587 | ❌ 미커버 |

## 핵심 결론

### 1. 코드 규칙(var/괄호) = 대성공
- **괄호(MISSING_PARENTHESIS) 741 → 0, 100% 소거.** SparrowSyntaxFix `parens`가 완전히 동작.
- var 계열(OBVIOUS/LOOP/OBJECT_INSTANTIATION) 합계 ~3300 소거. nullcast + SparrowSyntaxFix 유효.

### 2. [코드 규칙] 잔존(OBJECT_INSTANTIATION 515)의 정체 — 2종
소스 샘플(6869)로 확인:
- **(a) 정당한 판단 케이스(변환하면 안 됨, 올바르게 스킵)**:
  - `IDictionary<string,object> expando = new ExpandoObject();` — 선언타입≠생성타입 → var면 타입 변함.
  - `List<(string physical,string output)> x = new List<(string,string)>();` — 튜플 요소명 손실 위험.
- **(b) 깨끗한 케이스인데 SparrowSyntaxFix이 놓침(변환됐어야 함)**:
  - `SampleEntity parentData = new SampleEntity();`
  - `TreeNodeData rootTree = new TreeNodeData(parentData, CategoryType.TARGET);`
  - 모두 `MainTabViewModel.cs` 등 특정 파일 — **SparrowSyntaxFix이 레거시 프로젝트를 부분 로드해 이 파일들을 처리 못 함**(반복 확인된 SparrowSyntaxFix의 한계).
- → **처방: var 변환을 SparrowSyntaxFix에서 SparrowSyntaxFix(Roslyn)로 이관**하면 (b)를 잡고 (a)는 규칙으로 스킵.
  선언타입==생성타입일 때만 var(=`new` 타입과 동일), 인터페이스/기반타입/튜플명 상이는 스킵.

### 3. 주석·레이아웃 = 동일 경로 기준 소거 0 (측정 위생 주의)
- **파일명 매칭 착시**: 파일명만으로 매칭하면 다중프로젝트 동명파일(AssemblyInfo.cs 등)이 뭉쳐 "주석 2배 폭증"처럼 보임 → **오판**.
- **전체 경로 매칭(정확)**: 동일 경로 공통파일 425개의 MISSING_PERIOD = **846 → 846 (변화 0, 증가 파일 0)**.
  즉 **악화된 게 아니라, 하나도 못 지웠다.**
- **원인**: 6869 MISSING_PERIOD 주 패턴이 **`/** @brief ... */` Doxygen 블록주석**인데, SparrowCommentFix의
  space/period 규칙은 **`//` 라인주석 대상**이라 블록주석을 건드리지 않음. + 상당수가 `AssemblyInfo.cs`(생성/반입파일).
- raw 총계가 오른 것은 원본/최종 스캔이 **부분적으로 다른 경로 집합**을 잡았기 때문(전체 907건이 6827에 없던 경로).
  → **스캔 위생 필수: before/after는 반드시 동일 체크아웃/동일 경로로 스캔**해야 델타가 의미를 가짐.

### 4. 아예 미커버(현재 도구로 안 줄어드는 게 정상)
- LOWERCASE_FIRST_LETTER(1149) — 한글/기호 시작이라 ASCII 대문자화 결정론 불가 → 제거된 상태.
- BETWEEN_MEMBER_DEFINITION.MISSING_BLANK_LINE(577), USE_ONE_STATEMENT_PER_LINE(65),
  CONTINUATION_LINE.BAD_INDENTATION(57), BLOCK_OF_ASTERISK(45) — Rule 원문 확보됨, 결정론 구현 가능하나 미구현.
- OVERLY_BROAD_CATCH / EMPTY_CATCH / FORWARD_NULL / RESOURCE_LEAK 등 ~350 — XLS 분리로 넘김(사람/LLM 판단).

## 다음 세션 작업(우선순위)

1. **[높음] var 변환을 SparrowSyntaxFix Roslyn 규칙으로 이관**(`obviousvar`/`objinit`/`loopvar`).
   Rule 원문(로컬 Sparrow 공식 Rule)의 GoodCase 준수. 판단 케이스(선언타입≠생성타입/튜플명/인터페이스) 스킵.
   → SparrowSyntaxFix 의존 제거, OBJECT_INSTANTIATION 잔존 515의 (b) 소거.
2. **[높음] SparrowCommentFix가 `/** */` 블록주석·Doxygen `@brief` 처리하도록 확장** + 생성/반입파일
   (`AssemblyInfo.cs`, `*.Designer.cs`, `*.xaml.cs` 중 생성분) 스캔 제외 여부를 운영자와 확정.
   실물 검출이 블록주석 위주임을 반영(현재 `//` 전용은 미스매치).
3. **[필수] 스캔 위생**: before/after 동일 경로로 재스캔(그래야 델타가 진실). 현재 6827/6869는 경로 집합이 달라
   총계 비교가 오해를 부름.
4. **[중] 미커버 결정론 체커 추가**: BETWEEN_MEMBER 빈줄/USE_ONE_STATEMENT/CONTINUATION_LINE/BLOCK_OF_ASTERISK
   — Rule 원문 확보됨, Roslyn/포맷 규칙으로 구현.
5. **[중] LOWERCASE_FIRST_LETTER 방침 결정**: 한글주석 다수 → 결정론 불가분은 보류(문맥 확보 후 수정)로.

## 참고
- Sparrow 공식 Rule 원문: 각자 Sparrow에서 반입(레포 미포함)
- 현재 도구: `tools/SparrowSyntaxFix`(nullcast+parens, Run-SparrowSyntaxFix.ps1), `tools/SparrowCommentFix`(space+period),
  `references/Run-SparrowSyntaxFix.ps1`(SparrowSyntaxFix).
- 측정 재현: SparrowXlsExport 또는 로컬 프로브로 두 xls 체커별 tally(파일 매칭은 **반드시 전체 경로**로).
