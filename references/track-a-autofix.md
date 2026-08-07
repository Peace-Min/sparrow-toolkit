# Track A 결정론적 자동수정

Track A는 Sparrow 코드 규칙 계열을 LLM 없이 자동수정하는 경로다. 현재 운영 기준은 **Roslyn 기반 `SparrowSyntaxFix` 단독 사용**이다.

## 운영 원칙

- 사용자가 세부 `-Rules` 값을 외워 직접 호출하는 방식을 기본 운영으로 안내하지 않는다.
- 일반 실행은 통합 GUI `tools/Run-SparrowRunnerGui.cmd`를 사용한다.
- 직접 runner 실행이 필요한 테스트/자동화에서는 `tools/_internal/SparrowSyntaxFix/Run-SparrowSyntaxFix.ps1`를 사용한다.
- GUI/CLI는 솔루션 또는 소스 폴더 경로, 선택 규칙, 커밋 여부만 받는다.
- `-Rules` 직접 지정은 테스트, 자동화, 특정 규칙 재실행용 예외 경로다.
- 검토필요 규칙은 커밋 메시지에 `! 검토필요`가 남도록 유지한다.

## 대상 체커

현행 규칙 키는 **14개**다. 아래 표가 전부이며, 하나라도 빠지면 이 문서가 낡은 것이다
(원천은 `tools/_internal/SparrowSyntaxFix/Program.cs` 의 `RuleOrder` 배열).

| Sparrow 체커 | Track A 규칙 | 대표 변경 |
|---|---|---|
| `PRACTICE.OBJECT_INSTANTIATION.NOT_USED_IMPLICIT_TYPING` | `objectvar-safe`, `objectvar-narrowing` | `Foo x = new Foo();` -> `var x = new Foo();` / (narrowing) 상위 타입·인터페이스 선언을 실제 생성 타입 `var` 로 축소 — **검토필요** |
| `PRACTICE.OBVIOUS_VARIABLE_TYPE.NOT_USED_IMPLICIT_TYPING` | `obviousvar`, `nullvar`, `localconst` | `string s = "A";` -> `var s = "A";` |
| `PRACTICE.LOOP_VARIABLE.NOT_USED_IMPLICIT_TYPING` | `forvar`, `foreachcast` | `foreach (XmlNode n in xs)` -> `foreach (var n in Enumerable.Cast<XmlNode>(xs))` |
| `PRACTICE.OBJECT_INITIALIZATION.NOT_USED_INITIALIZER` | `objectinitializer` | 생성 직후 연속 대입을 object initializer로 통합 |
| `PRACTICE.ARRAY_DECLARATION.COMPLICATED_SYNTAX` | `arrayvar-safe`, `arrayvar-narrowing` | `int[] a = new int[] { 1 };` -> `int[] a = { 1 };` 또는 `var a = new[] { 1 };` |
| `MISSING_PARENTHESIS_IN_EXPRESSION` | `parens` | `a || b && c` -> `(a) || ((b) && (c))` |
| 한 줄 다중 선언/구문 계열 | `fieldsplit`, `emptystmt`, `forhoist` | 다중 선언 분리, 빈 문장 제거, for 초기화절 hoist |

## 실행

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1
```

또는 GUI:

```cmd
tools\Run-SparrowRunnerGui.cmd
```

일반 사용자는 위처럼 실행한 뒤 화면/프롬프트에서 솔루션 또는 폴더 경로와 커밋 여부를 선택한다.

> **자동화/CI 에서는 `-Rules` 를 반드시 명시한다.** 생략하면 러너가 opt-in 규칙마다 Y/N 을 되묻고,
> 비대화형 stdin 에서는 그 규칙들이 조용히 전부 꺼진다. `-LogDir` 를 줄 때는 **폴더를 미리 만들어 둔다**
> (러너가 만들지 않는다). 자세한 건 [../docs/usage.md](../docs/usage.md#cli-자동화-주의사항).

## 검증

자동수정 후 반드시 다음을 확인한다.

1. 대상 솔루션 빌드 통과.
2. Sparrow 재분석에서 대상 체커 건수 감소.
3. `! 검토필요` 커밋은 사람이 diff를 확인.

Roslyn 구문 경계와 Sparrow 검출 경계가 완전히 같지는 않다. 자동수정이 끝났다는 사실만으로 Sparrow 검출 소멸을 보장하지 않으므로 재분석 확인이 필수다.
