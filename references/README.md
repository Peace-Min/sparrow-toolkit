# references/ — 참고 자료

**여기 있는 파일은 도구 실행에 전혀 필요하지 않다.** 설계 판단의 근거, 한 번 측정한 기록,
익명화한 실수정 사례를 모아 둔 곳이다. 무엇을 만들지 정할 때 읽고, 만든 뒤에는 갱신한다.

기여자가 먼저 볼 문서는 여기가 아니라 [../docs/architecture.md](../docs/architecture.md) 와
[../docs/extending.md](../docs/extending.md) 다.

## 목록

| 파일 | 성격 | 무엇 |
|---|---|---|
| [track-a-autofix.md](track-a-autofix.md) | **운영 정책 (현행)** | Track A 자동수정의 운영 원칙, Sparrow 체커 ↔ Track A 규칙 대응표, 실행/검증 절차 |
| [track-a-roslyn-policy.md](track-a-roslyn-policy.md) | **설계 기록 (2026-07 시점)** | Track A 를 Roslyn 규칙으로 확장하기로 한 설계안 — 규칙별 변환 계약·안전 조건·skip 조건·커밋명 규약·fixture 필수 케이스. **작성 시점 기준 문서라 "현재 2개 규칙" 같은 서술은 이미 낡았다**(현재 14개 규칙 키). 현행 규칙 목록은 [엔진 README](../tools/_internal/SparrowSyntaxFix/README.md) 를 본다 |
| [RESULTS-6869-analysis.md](RESULTS-6869-analysis.md) | **측정 기록 (2026-07-14, 1회성)** | 자동수정 전/후 Sparrow 재분석 델타를 체커별로 분해한 실측 노트. 결론뿐 아니라 **측정 위생의 교훈**(파일명 매칭이 만든 착시, 전/후를 다른 경로 집합으로 스캔했을 때의 오판)이 핵심 가치다 |
| [real-fix-patterns/](real-fix-patterns/) | **실사례 코퍼스 (계속 쌓는 것)** | 폐쇄망에서 사람이 손으로 고친 커밋에서 **최소 before/after 구조만 익명화해** 축적하는 곳. [README](real-fix-patterns/README.md) 가 절차와 익명화 원칙, [TEMPLATE.md](real-fix-patterns/TEMPLATE.md) 가 체커별 파일 양식 |

> **주의**: `RESULTS-6869-analysis.md` 는 당시 세션의 원문을 중립화(익명화)한 것이라
> 일부 도구 이름이 뭉개져 같은 이름이 두 번 나오는 문장이 있다. 수치와 결론은 유효하지만,
> **도구 이름 대조가 필요하면 그대로 믿지 말고 커밋 이력을 확인할 것.**

## 커밋하지 않는 폴더 (각자 로컬 자산)

`.gitignore` 가 아래 둘을 막는다. 레포는 이것들을 **배포하지 않는다**.

| 폴더 | 무엇 | 왜 커밋 안 하나 |
|---|---|---|
| `references/checkers/` | 체커별 조치 규칙 라이브러리(`<이름>.md`) + 지정 파일(`_assignments.json`) | 폐쇄망/사내 지식이다. Track C 는 이게 없어도 완전히 동작한다(순수 익스포터) |
| `references/sparrow-official-rules/` | 파수(Fasoo) Sparrow 공식 Rule 원문 | 제3자 독점 문서다. 각자 자신의 Sparrow 에서 반입해 로컬로만 쓴다 |

체커 규칙 라이브러리를 만들고 붙이는 방법은
[../docs/usage.md](../docs/usage.md#규칙-라이브러리--체커-지정-자동-매핑-없음) 참조.
