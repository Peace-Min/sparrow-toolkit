# SparrowCommentFix

Deterministic net8 console tool that fixes narrow **comment and layout** style issues flagged by the Sparrow
(파수) static analyzer. It is the **주석·레이아웃** deterministic fixer of the Sparrow pipeline: the mechanical,
judgement-free comment clean-ups that a weak local/air-gapped LLM should never touch.

It uses **Roslyn** (`Microsoft.CodeAnalysis.CSharp`) to parse each `.cs` file with
`CSharpSyntaxTree.ParseText`, edits **only comment trivia**, and atomically rewrites the file. It **never
loads a project**, so legacy non-SDK `.csproj` / .NET Framework 4.7.2 targets are irrelevant — it operates
on `.cs` text directly.

## Correctness guarantee (non-negotiable)

It is **not regex-based.** Because every edit is confined to spans Roslyn identifies as comment trivia, a
`//` or `/*` that appears **inside a string or char literal is never touched** — e.g. `"http://example.com"`
and `"a//b"` are left byte-identical. This guarantee is covered by the SAFETY fixtures.

## CLI

```
SparrowCommentFix <file.cs> [<file2.cs> ...] [--files-from <files.csv>] [--root <dir>] --rules <flatten,trailing,blockpromote,space,period,capitalize,memberblank,onestatement,onedeclaration,continuation,linqalign|all> [--dry-run]
```

- Positional args are `.cs` file paths.
- `--files-from <csv>` reads a file list CSV (`파일명`/`경로`/`path`… column) or newline list and takes the **distinct `파일명`** values (CSV
  quoting handled). Paths are resolved against `--root` (default: current directory).
- Positional files + `--files-from` files are **unioned, de-duplicated** by full path, order-stable.
- `--rules` is **required**. Comma-separated keys, or `all` for all active 주석·레이아웃 rules.
- `--dry-run` computes and reports would-change counts but **writes nothing**.
- A missing input file is a `WARN:` to stderr and is skipped; if **no** valid files remain, exit code `2`.

Exit codes: `0` success, `1` runtime error, `2` usage/validation error.

## Rules

The active rule set covers deterministic 주석·레이아웃 fixes. Per the **code-fix-only** decision,
a rule with no safe deterministic contract is left unhandled rather than shipped as a wrong edit.

| key | Sparrow checker | what it does | key guards |
|---|---|---|---|
| `flatten` | `FORMATTING.COMMENT.BLOCK_OF_ASTERISK`, `FORMATTING.COMMENT.LOWERCASE_FIRST_LETTER`, `FORMATTING.COMMENT.MISSING_PERIOD` | `/** @brief x */` -> `// X.` line comments | skips empty blocks and comments containing preprocessor-like `#` text |
| `trailing` | inline/trailing comment rule | `code; //ABC` -> `// ABC.` above `code;` | only real line-comment trivia after code on the same line |
| `blockpromote` | `MISSING_BLANK_LINE_BEFORE_COMMENT` (inline `/* */`) | `if (/* x */ (cond))` -> `// x.` promoted above the statement (blank line before), inline block removed, residual whitespace collapsed | **opt-in** (not in the runner default set); single-line inline `/* */` only; skips multi-line blocks, `/**` doc blocks, own-line comments, empty text, undeterminable enclosing statement |
| `space` | `FORMATTING.COMMENT.MISSING_SPACE_AFTER_DELIMITER` | `//x`→`// x`, `///x`→`/// x`, `/*x*/`→`/* x*/` | untouched if next char is whitespace, another `/`, or (block) `*`; `//`/`////` untouched |
| `period` | `FORMATTING.COMMENT.MISSING_PERIOD` | append `.` to comment content | only when the last content char is a **letter** (ASCII / Hangul / CJK) or **digit**; skips dividers (`// ----`, `/****/`), commented-out code ending in `;`/`]`, and content already ending in punctuation |
| `capitalize` | `FORMATTING.COMMENT.LOWERCASE_FIRST_LETTER` | strip leading punctuation (`<`, `.`, `[`, `(`, ...), then uppercase an ASCII `a-z` first letter (`// <variableSource>`→`// VariableSource>`, `//badcase`→`//Badcase`) | **skips** `///` doc + `/**` Doxygen; never strips/uppercases Korean/CJK (no case); aborts if only symbols/whitespace precede the first letter (`// ==== divider`); inline `/* */` edited in place, **never** converted to `//` |
| `memberblank` | `FORMATTING.BETWEEN_MEMBER_DEFINITION.MISSING_BLANK_LINE` | inserts one blank line between adjacent method/property declarations | only pure whitespace between members; skips comments/directives/attributes |
| `onestatement` | `USE_ONE_STATEMENT_PER_LINE` | splits adjacent statements and single-line if blocks | same statement list only; skips comments/directives |
| `onedeclaration` | `USE_ONE_DECLARATION_PER_LINE` | splits local/field multi-declarator declarations | plain declarations only; skips comments/directives/attributes/using locals |
| `continuation` | `FORMATTING.CONTINUATION_LINE.BAD_INDENTATION` | fixes leading whitespace on continuation argument/logical lines | leading whitespace only |
| `linqalign` | `FORMATTING.LINQ.QUERY_CLAUSE_ALIGNMENT` | aligns query clauses to the first `from` column | leading whitespace only |

Every rule is **idempotent** — running it twice
changes nothing.

## Rules that are NOT active (2)

Passing legacy keys — `--rules blankline`, `--rules asterisk` — (or any unknown
key) exits `2` with a message naming the valid keys and the reason. The clean rule registry means each can be
**re-added later as a small diff** (one rule key + one rewrite method) once a correct, real-data-backed
contract is defined.

Legacy alias keys remain inactive; use the active rule names in the table above.

| key | Sparrow checker | status | reason |
|---|---|---|---|
| `blankline` | `MISSING_BLANK_LINE_BEFORE_COMMENT` | **removed** | the real rule targets **trailing/inline** comments (`code; //c`) and wants them on their own line — the opposite target of the old "insert a blank line before a line-leading comment" logic; re-targeting is a risky structural rewrite for only ~10 real hits |
| `asterisk` | `FORMATTING.COMMENT.BLOCK_OF_ASTERISK` | **deferred** | removing Doxygen `/** * */` blocks is a style judgment, not a safe mechanical edit |

> `capitalize` was previously removed but is **now active** (re-added with a safe, real-data-backed contract — see the Rules table above).

## Generated-file noise is excluded by default

Real-data analysis found **~79%** of all comment hits are in **auto-generated / backup** files
(`obj\...\*.g.cs`, `*.g.i.cs`, `*.Designer.cs`, `AssemblyInfo.cs`, `*복사본*`). The operator **excludes these
at Sparrow-scan time** where possible. SparrowCommentFix also skips generated/backup paths and
`<auto-generated>` headers by default; use `--include-generated` directly, or `-IncludeGenerated` through
the runner, only when those files must be rewritten.

## One-call runner (`Run-SparrowCommentFix.ps1`)

### One-shot runner policy

Normal operation must use `Run-SparrowCommentFix.ps1`, not direct `SparrowCommentFix --rules ...` calls.
When `-Rules` is omitted, the runner asks for the solution/folder path, then asks Y/N for `flatten`, then
asks Y/N for the layout group (`memberblank`, `onedeclaration`, `onestatement`, `linqalign`,
`continuation`), then asks whether to commit. Direct `-Rules` usage is reserved for tests, automation, and
precise re-runs.

Default rules are `trailing`, `space`, and `period`. `flatten` and layout rules are opt-in.

For an operator who just wants to point at a solution/folder and go, `Run-SparrowCommentFix.ps1`
(mirrors `Run-SparrowSyntaxFix.ps1`) wraps the tool:

```
.\Run-SparrowCommentFix.ps1 -Solution C:\Work\MyApp\MyApp.sln          # apply; asks about commit if neither -Commit/-DryRun
.\Run-SparrowCommentFix.ps1 -Solution ...\MyApp.sln -Commit            # per-rule git commit (no prompt)
.\Run-SparrowCommentFix.ps1 -Solution C:\Work\MyApp -DryRun            # report only, writes nothing
.\Run-SparrowCommentFix.ps1 -Solution ...\MyApp.sln -Rules period      # tests/automation/precise re-run only
.\Run-SparrowCommentFix.ps1 -Solution ...\MyApp.sln -FilesFrom files.csv   # (precise) skip auto-glob, use this CSV
.\Run-SparrowCommentFix.ps1 -Solution ...\MyApp -IncludeGenerated      # keep generated/backup files (default: excluded)
.\Run-SparrowCommentFix.ps1 -Solution ...\MyApp.sln -ExePath C:\tools\SparrowCommentFix.exe  # air-gap: bundled exe
```

Because the tool itself does **not** accept a directory, the runner does the `.cs` recursion + exclusion
in PowerShell, then hands the resulting full paths to the tool via a temp `--files-from` CSV:

- **Auto-globs** `*.cs` recursively under the source root (a `.sln`/`.csproj` resolves to its folder).
- **Excludes generated/backup** files unless `-IncludeGenerated`: any path under a `\obj\` or `\bin\`
  segment, or a filename matching `*.g.cs` / `*.g.i.cs` / `*.Designer.cs` / `AssemblyInfo.cs`, or a name
  containing `복사본` / `TemporaryGeneratedFile` / `GeneratedInternalTypeHelper` (case-insensitive). Counts
  are reported transparently (found / excluded / targeted) — no silent drops.
- Runner defaults to `trailing`, `space`, and `period`. `flatten` and layout rules are opt-in because they
  can change documentation output or are best-effort structural rewrites.
- Runs each selected rule in a fixed order (`flatten`, `trailing`, `space`, `period`, `onedeclaration`,
  `onestatement`, `memberblank`, `linqalign`, `continuation`), honoring `-DryRun`.
- **Commit UX mirrors `Run-SparrowSyntaxFix.ps1`**: with `-Commit` it makes a per-rule
  git commit (`sparrow: <label> (SparrowCommentFix)`, staging `*.cs`); with `-DryRun` it commits nothing;
  with neither, an interactive run **prompts** `규칙별로 커밋할까요? (Y/N)` (non-interactive → no commit).
  Writes a timestamped `.log` next to the working dir.

## Per-checker commits

Run **one rule at a time** (e.g. `--rules period`) so that run's diff equals exactly that one Sparrow
checker's fixes — matching the pipeline's "규칙/체커별 커밋" gate.

## Encoding / newline preservation

Each edited file keeps its original **UTF-8 BOM presence** and newline style. Files whose bytes do not round-trip as UTF-8 are
skipped with a `WARN` (never risk corrupting non-UTF-8 bytes). Writes are atomic (temp file in the same
directory, then replace) and happen **only when the bytes actually change** (no mtime churn).

## How it's tested

`tests/sparrow-commentfix-fixtures.ps1` builds the tool and runs synthetic `.cs` fixtures covering each
rule (before/after), the string-literal safety guarantee, idempotency, `--dry-run`, `--files-from`, and the
unknown-rule exit code. Wired into the validate gate:

```powershell
./validate.ps1 -IncludeCommentE2E          # from the repo root
```

## Air-gap bundling

`tools/publish-airgap.ps1` publishes this tool (self-contained `win-x64` by default) to
`tools/_internal/SparrowCommentFix/publish/SparrowCommentFix.exe`, alongside SparrowSyntaxFix,
SparrowXlsExport and the WPF GUI. The runner picks that `publish\` exe up automatically, so the
air-gapped machine needs no .NET SDK and no NuGet restore. See
[../../../CONTRIBUTING.md](../../../CONTRIBUTING.md#폐쇄망-발행) and
[../../../docs/usage.md](../../../docs/usage.md#폐쇄망-반입오프라인-배포).

## Adding a rule

See [`docs/extending.md` 레시피 1](../../../docs/extending.md#레시피-1-기존-c-갈래에-규칙-추가) —
it lists every touch point (rule implementation, rule-key registry, runner labels, GUI checkbox, fixtures).
