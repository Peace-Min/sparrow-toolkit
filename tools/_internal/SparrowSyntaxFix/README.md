# SparrowSyntaxFix

Deterministic Roslyn source rewriter for Sparrow (스패로우 정적분석) Track A code-rule findings that the
now-deleted `dotnet format` runner did not fully clear on the legacy **MyApp** project (.NET Framework 4.7.2,
non-SDK `.csproj`). It parses C# source text with Roslyn **syntax** APIs only — it never loads an MSBuild
project — and rewrites at the syntax level, preserving all trivia (comments/whitespace/newlines). No
string/regex editing of code, ever.

Current implementation covers the Track A Roslyn expansion in `references/track-a-roslyn-policy.md`:
`nullvar`, `parens`, `objectvar-safe`, `foreachcast`, `obviousvar`, `objectvar-narrowing`, `localconst`,
`objectinitializer`, `arrayvar-safe`, and `arrayvar-narrowing`.
`foreachcast`, `objectinitializer`, `objectvar-narrowing`, `arrayvar-narrowing`, `localconst`, and `nullvar` are review-needed or operator-confirmed rules and must stay isolated in their
own rule run/commit.

## Implemented rules

### Rule 1 ??`nullcast`  (checker `PRACTICE.OBVIOUS_VARIABLE_TYPE.NOT_USED_IMPLICIT_TYPING`)

Sparrow wants `var`, but `var x = null;` is illegal C# so IDE0007 declines it. A cast lets `var` infer the
**identical** static type, so the transform is 100% semantics-preserving.

```
SampleComponent sampleComponent = null;   ->   var sampleComponent = (SampleComponent)null;
List<PropData> lst = null;                ->   var lst = (List<PropData>)null;
A.B.CThing x = null;                      ->   var x = (A.B.CThing)null;
IFoo c = null;                            ->   var c = (IFoo)null;
```

Matches ONLY a plain, single-declarator **local** statement whose sole initializer is the bare `null`
literal. Hard skips (left byte-identical):

- `= new ...` (object creation) ??not handled by current `nullcast`. The next policy splits this into
  `objectvar-safe` and `objectvar-narrowing` (review-needed); see `track-a-roslyn-policy.md`.
- `= default` / `= default(T)` ??out of scope.
- any non-`null` initializer (method call, member access, ternary, ...).
- already `var`; multi-declarator (`Foo a = null, b = null;`); `const`; `using` locals; fields/properties.

### Rule 2 ??`parens`  (checker `MISSING_PARENTHESIS_IN_EXPRESSION`)

Roslyn's IDE0048 treats "relational binds tighter than logical" as commonly understood and skips it; Sparrow
does not. Sparrow requires **every** operand of `&&` / `||` to be parenthesized ??not just the ambiguous ones.
(Confirmed by re-analysis: `(a) || b` is still flagged; only `(a) || (b)` clears it.)

```
if (nIndex > 0 && nIndex <= nCount - 1)          ->  if ((nIndex > 0) && (nIndex <= nCount - 1))
if (sampleComponent != null && dataTypeInfo != null)
                                                 ->  if ((sampleComponent != null) && (dataTypeInfo != null))
var z = a || b;                                  ->  var z = (a) || (b);              // atoms wrapped too
if (x > 0 || flag)                               ->  if ((x > 0) || (flag))           // comparison + atom
finfile.Name.Equals("x") || finfile.Name.Equals("y")
                                                 ->  (finfile.Name.Equals("x")) || (finfile.Name.Equals("y"))
if (a || b && c)                                 ->  if ((a) || ((b) && (c)))
```

**Every** operand is wrapped ??atoms (identifiers, literals, member access `a.b.c`, invocations `f()` /
`x.Equals(y)`, element access, `this`, unary `!x` / `-x`, casts), comparisons, arithmetic/bitwise, and the
**other** logical operator ??**except** (1) anything already parenthesized, and (2) a **same-operator** chain
(`a && b && c`), which is left flat so its leaves become `(a) && (b) && (c)` (not `((a) && (b)) && (c)`). A
partially-parenthesized expression from an earlier pass (`(a) || b`) is completed to `(a) || (b)`.

Both rules are **idempotent**: running twice makes no further change.

## Track A expansion rules

The CLI supports these rules:

| Rule | Transform | Commit policy |
|---|---|---|
| `objectvar-safe` | `Foo x = new Foo()` ??`var x = new Foo()` when declaration type and construction type match | normal |
| `foreachcast` | `foreach (XmlNode n in xs)` ??`foreach (var n in System.Linq.Enumerable.Cast<XmlNode>(xs))`. **Value-type guard:** skipped when the element type is a numeric/implicit-conversion value type — predefined keywords (`int`/`long`/`double`/`decimal`/`bool`/`char`/…), well-known names bare or `System.`-qualified (`Int32`/`Int64`/`Double`/`Boolean`/…), or any nullable form (`T?`, `Nullable<T>`) — because there foreach does an implicit numeric conversion, not a cast, so `Cast<T>` would unbox to the wrong runtime type and throw `InvalidCastException`. (Reference/other named types are unaffected — their conversion IS a cast.) **Residual risk:** enums declared as named types are syntactically indistinguishable from classes and are NOT skipped — human review + build/Sparrow gates are the backstop. | `review-needed` |
| `obviousvar` | `string s = "A"` ??`var s = "A"`; `double d = 20` ??`var d = (double)20` | normal |
| `objectvar-narrowing` | `IList<T> x = new List<T>()` ??`var x = new List<T>()` | `review-needed` |
| `localconst` | `const string s = "A"` ??`var s = "A"` | `review-needed` |
| `nullvar` | `Foo x;` / `Foo x = null;` ??`var x = (Foo)null;` | `review-needed` |
| `objectinitializer` | `Foo x = new Foo(); x.A = 1;` ??`var x = new Foo { A = 1 };` for consecutive assignments only | `review-needed` |
| `arrayvar-safe` | `int[] a = new int[] { 1, 2 };` ??`int[] a = { 1, 2 };` when array types match | normal |
| `arrayvar-narrowing` | `object[] a = new string[] { "A" };` ??`var a = new[] { "A" };` | `review-needed` |
| `forvar` | `for (int i = 0; ...)` ??`for (var i = 0; ...)` for a single-declarator, obvious-init for-loop (multi-declarator / method-call init never touched) | `review-needed` (opt-in, not in default set) |
| `fieldsplit` | `private double a, b, c;` ??one field per line, same indent, initializers/leading comment preserved (fields only) | `review-needed` (opt-in, not in default set) |
| `emptystmt` | `stmt; ;` ??`stmt;` — removes a redundant empty statement (`for(;;)` / labels / loop-body empties kept) | `review-needed` (opt-in, not in default set) |
| `forhoist` | `for (int i = 0, count = queue.Count; ...)` ??`var count = queue.Count;` + `for (var i = 0; ...)` — hoists non-loop declarators out of a multi-declarator for-init so the for stays single-declarator (dependency / name-collision / undeterminable-loop-var cases skipped) | `review-needed` (opt-in, not in default set) |

`review-needed` rules are still CLI-applied, but must be isolated in their own rule run and commit. Suggested
commit names:

```text
sparrow(A)! review-needed: static type narrowing to var
sparrow(A)! review-needed: simplify array declaration with static type narrowing
sparrow(A)! review-needed: demote local const to var
sparrow(A)! review-needed: initialize explicit locals as typed null
```

## One-shot runner policy

Normal operation must use `Run-SparrowSyntaxFix.ps1`, not direct `SparrowSyntaxFix --rules ...` calls.
When `-Rules` is omitted, the runner asks for the solution/folder path, then asks Y/N for
`foreachcast`, `objectinitializer`, `nullvar`, `objectvar-narrowing`, `localconst`, and
`arrayvar-narrowing`, then asks whether to commit. Direct `-Rules` usage is reserved for tests,
automation, and precise re-runs.

Default safe rules are `objectvar-safe`, `obviousvar`, `arrayvar-safe`, and `parens`.

## CLI

```
SparrowSyntaxFix <file-or-dir>... [options]

  --files-from <files.csv>  read target .cs paths from a CSV (파일 경로 column) or a newline list;
                            relative paths resolve against --root
  --root <dir>              base directory for resolving relative paths (default: current dir)
  --rules <list>            comma list of Track A rules or 'all' (default: safe subset)
  --dry-run                 report per-file / per-rule counts without writing

exit codes: 0 = success (whether or not changes were made), 1 = real error, 2 = usage
```

When given a directory it recurses for `*.cs`, **excluding** generated/backup files by default:
`*.Designer.cs`, `*.g.cs`, `*.g.i.cs`, `*.AssemblyInfo.cs`, `AssemblyInfo.cs`, `TemporaryGeneratedFile_*.cs`,
`*.generated.cs`, and any file whose first ~3 lines contain `<auto-generated`.

Console output is greppable ??one line per changed file plus an aligned summary:

```
changed C:\...\Foo.cs  nullcast=2 parens=3
rules:            nullcast,parens
files found:      420
generated skip:   12
non-UTF8 skip:    0
files changed:    137
nullcast edits:   285
parens edits:     741
```

## One-call runner — `Run-SparrowSyntaxFix.ps1` (권장)

솔루션(.sln)/소스 폴더 경로만 주면 동작하는 PowerShell 러너(Track A 2단계). 내부에서 exe를 확보한 뒤 규칙별로
실행하고 규칙별로 커밋한다(검수 가능한 단위). 일반 운영은 이 러너로 하고, 직접 `SparrowSyntaxFix --rules ...`
호출은 테스트/자동화/정밀 재실행에만 쓴다.

```powershell
# 원큐: 그냥 실행하면 솔루션 경로를 묻고, 이어서 커밋 여부(Y/N)를 묻는다.
.\Run-SparrowSyntaxFix.ps1

# 경로를 미리 줘도 됨(커밋 여부는 물음). 솔루션(.sln) 또는 소스 폴더 경로.
.\Run-SparrowSyntaxFix.ps1 -Solution C:\Work\MyApp\MyApp.sln

.\Run-SparrowSyntaxFix.ps1 -Solution ...\MyApp.sln -DryRun                 # 미리보기(변경 안 함)
.\Run-SparrowSyntaxFix.ps1 -Solution ...\MyApp.sln -Commit                 # 규칙별 자동 커밋
.\Run-SparrowSyntaxFix.ps1 -Solution ...\MyApp.sln -Rules nullcast         # 테스트/자동화/정밀 재실행용 예외
.\Run-SparrowSyntaxFix.ps1 -Solution ...\MyApp.sln -FilesFrom files.csv    # (정밀) 지정한 파일만
.\Run-SparrowSyntaxFix.ps1 -Solution ...\MyApp.sln -ExePath C:\tools\SparrowSyntaxFix.exe  # 폐쇄망: 반입 exe 지정
```

exe 확보 순서: `-ExePath` → 스크립트 옆 `publish\SparrowSyntaxFix.exe` → `bin\Release\net8.0\SparrowSyntaxFix.dll`
→ 없으면 `dotnet build`(패키지 복원 가능할 때). **인터넷 없는 폐쇄망 PC는 `-ExePath` 또는 `publish\`로 반입 exe를 주세요**
(반입 번들은 `tools/publish-airgap.ps1`로 생성). `.sln`을 주면 그 폴더 아래 `*.cs`를 재귀 처리한다(생성/백업 제외).
특정 파일만 정밀 처리하려면 `-FilesFrom <파일목록.csv>`(파일명/경로 컬럼 CSV 또는 줄 목록)로 대상 파일 목록을 준다.

## Safety / encoding

- Preserves the file's UTF-8 **BOM** presence and its exact **newlines** (Roslyn keeps every existing newline
  in unchanged trivia; the tool inserts none, so even mixed line endings survive verbatim ??no normalization).
- If a file does **not** round-trip cleanly as UTF-8 (e.g. UTF-16, or invalid bytes), it is **skipped** with a
  warning ??never corrupted.
- **Atomic write**: a temp file in the same directory is written, then moved over the target, so a crash
  cannot truncate source. Only files whose tree text actually changed are written.

## Air-gapped usage

net8 + Roslyn (`Microsoft.CodeAnalysis.CSharp` 4.11.0) only ??no other NuGet dependency, no network at
runtime. Restore/build once on a connected machine, then carry the published output into the closed network.
Typical flow against the legacy solution:

```
# 1) see what would change (safe)
SparrowSyntaxFix C:\src\MyApp --dry-run

# 2) apply only the null-cast rule to a hand-picked file list (CSV or newline list)
SparrowSyntaxFix --files-from files.csv --root C:\src\MyApp --rules nullcast

# 3) apply both across a subtree
SparrowSyntaxFix C:\src\MyApp\Components
```

## Honest boundary

**A Roslyn edit is not a guaranteed Sparrow clearance.** The Roslyn AST boundary is not identical to
Sparrow's. These rewrites are designed to satisfy the two checkers above, but the real gate is
**re-running Sparrow**: the target checker's findings must drop to zero for the edited files, with zero new
findings introduced. Confirm by re-analysis (pipeline gate G2), then build (G1) and human review (G3).

Note: code inside inactive `#if` branches is parsed as disabled-text trivia and is intentionally **not**
edited (conservative ??it cannot be safely rewritten without knowing the build configuration).

## Tests

`FixtureTests/` is a nested test-only harness that compiles the real rewriter sources and asserts the exact
real-world before?뭓fter cases (positives, negatives, the hard `= new` rule, string-literal safety,
idempotency, CRLF preservation). Run the full offline gate:

```
dotnet run --project FixtureTests/FixtureTests.csproj -c Release
# or the on-disk E2E (BOM/CRLF/atomic-write/generated-skip/dry-run/idempotency):
pwsh tests/sparrow-syntaxfix-fixtures.ps1          # from the repo root
pwsh tests/validate.ps1 -IncludeSyntaxFixE2E
```
