# Track A Roslyn 보강 정책 — Sparrow 코드 규칙 CLI

> **문서 성격**: 이것은 **작성 시점(2026-07)의 설계 기록**이다. "현재 2개 규칙만 구현되어 있다" 같은
> 현황 서술은 이미 낡았다 — 지금은 규칙 키가 14개다. 이 문서의 가치는 현황이 아니라
> **규칙별 변환 계약·안전 조건·skip 조건·커밋명 규약·fixture 필수 케이스**에 있다.
> 현행 규칙 목록과 계약은 [엔진 README](../tools/_internal/SparrowSyntaxFix/README.md) 를,
> 새 규칙을 추가하는 절차는 [docs/extending.md](../docs/extending.md) 를 본다.

## Track A Roslyn 원샷 CLI 운영 원칙

이 문서의 규칙명은 설계와 테스트를 위한 식별자다. 일반 사용자는 `SparrowSyntaxFix --rules foreachcast`처럼 직접 호출하지 않고, 통합 GUI `tools/Run-SparrowRunnerGui.cmd`에서 체크박스로 선택한다. 테스트/자동화에서만 `tools/_internal/SparrowSyntaxFix/Run-SparrowSyntaxFix.ps1`를 직접 실행한다.

- 기본 안전 규칙은 runner가 자동 실행한다.
- `foreachcast`, `objectinitializer`, `nullvar`, `objectvar-narrowing`, `localconst`, `arrayvar-narrowing`은 runner가 포함 여부를 묻는다.
- `-Rules`는 테스트, 자동화, 특정 규칙 재실행용 예외 경로다.
- 위험 규칙 커밋은 `검토필요`가 드러나도록 분리한다.

이 문서는 `issues_sample_6869.xls` 재분석 결과를 기준으로, Track A의 다음 구현 범위를 확정한 설계안이다.
핵심 결론은 단순하다. **스캔 제외 설정은 후순위이고, 우선 `SparrowSyntaxFix`가 Sparrow의 코드 규칙 검출을 실제로 줄이도록 Roslyn 규칙을 확장한다.**

## 현재 상태 (작성 시점)

작성 시점의 `SparrowSyntaxFix`에는 2개 규칙만 구현되어 있었다.

| 규칙 | 처리 |
|---|---|
| `nullcast` | `Foo x = null;` → `var x = (Foo)null;` |
| `parens` | `&&` / `||` 피연산자 괄호 보강 |

따라서 `Track A 완료`라는 표현은 부정확하다. 더 정확한 상태는 **Track A 일부 완료**다. 괄호와 null initializer
잉여는 처리했지만, 명시 타입 var 계열 대부분은 아직 CLI 규칙으로 구현되지 않았다.

6869 재분석 기준 잔여:

| 체커 | 잔여 |
|---|---:|
| `PRACTICE.OBJECT_INSTANTIATION.NOT_USED_IMPLICIT_TYPING` | 515 |
| `PRACTICE.LOOP_VARIABLE.NOT_USED_IMPLICIT_TYPING` | 117 |
| `PRACTICE.OBVIOUS_VARIABLE_TYPE.NOT_USED_IMPLICIT_TYPING` | 48 |
| **합계** | **680** |

## 구현 원칙

- 판단 없는 문법 변환만 CLI가 처리한다.
- 의미 변경 가능성이 있는 변환도, 신뢰성시험 단계의 코드라는 전제와 빌드 게이트를 최종 안전망으로 삼고
  **별도 규칙/별도 커밋**으로 적용할 수 있다.
- 위험도가 높은 규칙은 커밋명에 `review-needed` 또는 `검토필요`를 강제로 넣는다.
- 테스트 주석을 삽입하지 않는다. 필요한 경우 별도 ledger/report를 만든다.
- 규칙별 실행과 규칙별 커밋을 유지한다. 문제가 생기면 해당 규칙 커밋만 되돌릴 수 있어야 한다.

## 신규 규칙

### 1. `objectvar-safe`

선언 타입과 생성 타입이 동일한 객체 생성만 자동 변환한다.

```csharp
Foo x = new Foo();
Foo y = new Foo(arg1, arg2);
```

```csharp
var x = new Foo();
var y = new Foo(arg1, arg2);
```

기본 안전 조건:

- local declaration만 대상.
- 단일 declarator만 대상.
- 타입이 이미 `var`이면 skip.
- 선언 타입과 `new` 타입의 텍스트가 동일하면 변환.
- `using` local, field, property는 대상이 아님.

기본 커밋명:

```text
sparrow(A): object instantiation var safe
```

### 2. `objectvar-narrowing` — 검토필요

인터페이스/기반타입 선언을 실제 생성 타입으로 좁히는 변환이다.

```csharp
IList<string> x = new List<string>();
Base x = new Derived();
IFoo x = new Foo();
```

```csharp
var x = new List<string>();
var x = new Derived();
var x = new Foo();
```

대부분 빌드와 런타임 동작은 유지되지만, 변수의 정적 타입이 바뀌므로 오버로드 선택, generic inference,
extension method binding이 달라질 수 있다. 따라서 자동화는 허용하되 반드시 별도 규칙/별도 커밋으로 분리한다.

커밋명은 반드시 주의 신호를 포함한다.

```text
sparrow(A)! review-needed: static type narrowing to var
sparrow(A)! 검토필요: 인터페이스 기반 타입 var 변환
```

### 3. `foreachcast`

명시 타입 `foreach`를 `var`로 바꾸되, non-generic enumerable에서 빌드가 깨지는 것을 막기 위해 `Cast<T>`를 사용한다.

```csharp
foreach (XmlNode node in clsNodes)
```

```csharp
foreach (var node in System.Linq.Enumerable.Cast<XmlNode>(clsNodes))
```

이 정책을 기본값으로 둔다. 단순 변환인 `foreach (var node in clsNodes)`는 `XmlNodeList` 같은 non-generic 컬렉션에서 `node`가 `object`로 추론될 수 있어 기본값으로 쓰지 않는다.

skip 조건:

- 이미 `var`인 경우.
- enumerable expression이 이미 `Cast<T>()` 또는 `OfType<T>()` 호출인 경우.
- declaration type이 없거나 파싱이 불완전한 경우.

커밋명:

```text
sparrow(A): foreach implicit type with Cast<T>
```

### 4. `obviousvar`

오른쪽 initializer에서 타입이 명확한 지역 변수를 `var`로 바꾼다.

문자열/문자/bool처럼 literal이 원래 정적 타입을 그대로 보존하는 경우:

```csharp
string s = "A";
bool ok = true;
char c = 'x';
```

```csharp
var s = "A";
var ok = true;
var c = 'x';
```

숫자 literal처럼 `var`가 더 좁은 타입으로 추론될 수 있는 경우는 cast로 원래 정적 타입을 보존한다.

```csharp
double markerH = 20;
long count = 1;
int? pageSize = 0;
```

```csharp
var markerH = (double)20;
var count = (long)1;
var pageSize = (int?)0;
```

공식 rule 예시의 `Convert.ToXxx` 계열은 대상이 아니다.

```csharp
// Convert.ToXxx is intentionally not rewritten in syntax-only mode.
```

```csharp
// Use an explicit cast/literal case instead.
```

커밋명:

```text
sparrow(A): obvious local var conversions
```

### 5. `localconst` — 검토필요

지역 `const`는 전역/클래스 `private const`로 승격하지 않는다. 대신 지역 `var`로 낮춘다.

```csharp
const string name = "Description";
const double limit = 20;
```

```csharp
var name = "Description";
var limit = (double)20;
```

이 변환은 지역 상수성을 제거한다. 신뢰성시험 단계의 코드 규칙 제거 관점에서는 실용적으로 수용하지만, compile-time
constant 요구 위치에 쓰인 경우 깨질 수 있으므로 별도 규칙/별도 커밋으로 둔다.

최소 guardrail:

- local declaration만 대상.
- 단일 declarator만 대상.
- initializer가 literal 또는 단순 cast 보존이 가능한 단일 식일 때만 대상.
- 같은 method/block 안에 `case <identifier>:` 사용이 보이면 skip.

커밋명은 반드시 주의 신호를 포함한다.

```text
sparrow(A)! review-needed: demote local const to var
sparrow(A)! 검토필요: 지역 const var 변환
```

### 6. `nullvar` — 검토필요

기존 `nullcast`를 확장한다. `Foo x = null;`뿐 아니라 initializer 없는 지역 변수도 typed null로 초기화해 `var`를 만든다.

```csharp
Foo x = null;
Foo y;
```

```csharp
var x = (Foo)null;
var y = (Foo)null;
```

신뢰성시험 단계의 코드는 이미 빌드가 통과한다는 전제를 둔다. 따라서 기존 `Foo y;`는 사용 전 모든 컴파일 경로에서
할당되어 있어야 한다. 초기 `(Foo)null`은 Sparrow 코드 규칙 제거를 위한 실용 변환으로 취급한다.

그래도 컴파일러의 definite assignment 안전망을 약하게 만드는 변환이므로 별도 규칙/별도 커밋으로 둔다.

skip 조건:

- 이미 `var`인 선언.
- `const`, `using` local.
- 다중 declarator.
- predefined value type 키워드(`int`, `double`, `bool` 등) no-initializer는 우선 skip.
- field/property는 대상이 아님.

커밋명은 반드시 주의 신호를 포함한다.

```text
sparrow(A)! review-needed: initialize explicit locals as typed null
sparrow(A)! 검토필요: 미초기화 지역 변수 typed-null var 변환
```

## 권장 실행 순서

기본 안전 규칙부터 적용하고, 검토필요 규칙은 뒤에 분리한다.

```powershell
.\Run-SparrowSyntaxFix.ps1
```

일반 운영은 위 runner를 실행한 뒤 솔루션/폴더 경로, 선택 규칙 포함 여부, 커밋 여부를 Y/N으로 입력한다.
직접 `SparrowSyntaxFix --rules ...` 호출은 테스트, 자동화, 특정 규칙 재실행용 예외 경로다.
`Run-SparrowSyntaxFix.ps1 -Commit`은 규칙별 커밋 메시지를 붙이며 `foreachcast`, `objectinitializer`,
`objectvar-narrowing`, `localconst`, `nullvar`, `arrayvar-narrowing`은 커밋명에 `검토필요`를 드러낸다.

## Fixture 필수 케이스

### `objectvar-safe`

- `Foo x = new Foo();` → `var x = new Foo();`
- `Foo x = new Foo(arg);` → `var x = new Foo(arg);`
- `IFoo x = new Foo();`는 `objectvar-safe`에서 skip.

### `objectvar-narrowing`

- `IFoo x = new Foo();` → `var x = new Foo();`
- `IList<string> x = new List<string>();` → `var x = new List<string>();`
- 커밋/출력 라벨이 `review-needed` 계열인지 확인.

### `foreachcast`

- `foreach (XmlNode node in clsNodes)` → `foreach (var node in System.Linq.Enumerable.Cast<XmlNode>(clsNodes))`
- 이미 `foreach (var node in xs)`면 skip.
- 이미 `foreach (var node in System.Linq.Enumerable.Cast<XmlNode>(xs))`면 skip.

### `obviousvar`

- `string s = "A";` → `var s = "A";`
- `bool ok = true;` → `var ok = true;`
- `double d = 20;` → `var d = (double)20;`
- `int? n = 0;` → `var n = (int?)0;`
- `Convert.ToXxx` 계열은 syntax-only symbol identity 보장이 없어 skip.

### `localconst`

- `const string s = "A";` → `var s = "A";`
- `const double d = 20;` → `var d = (double)20;`
- `case s:` 같은 compile-time constant 요구 사용이 있으면 skip.

### `nullvar`

- `Foo x = null;` → `var x = (Foo)null;`
- `Foo x;` → `var x = (Foo)null;`
- `int x;`는 skip.
- `Foo a, b;`는 1차 구현에서 skip.

## 성공 기준

- 규칙별 fixture 통과.
- `SparrowSyntaxFix` 두 번째 실행 시 idempotent.
- 대상 솔루션 빌드 통과.
- Sparrow 재분석에서 해당 Track A 체커 건수가 감소.
- 검토필요 규칙은 별도 커밋으로 분리되어 있어야 한다.
