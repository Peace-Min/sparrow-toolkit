---
name: sparrow-static-analysis
description: Use when handling Sparrow static-analysis XLS findings for C#/.NET Framework 4.7.2, including deterministic Roslyn CLI fixes for coding/comment rules and splitting the remaining findings into per-checker Markdown files for LLM/human review.
---

# Sparrow Static Analysis

Use this skill when processing Sparrow static-analysis findings for an MyApp-style C#/.NET Framework 4.7.2 codebase.

## Scope

Work only inside `skills/sparrow-static-analysis` and the explicit Sparrow XLS/source inputs provided by the user. Do not inspect unrelated skills unless the user asks.

The workflow has two kinds of work:

- Deterministic fixes (Track A/B): coding-rule and comment/layout findings that match predefined, repeatable patterns are handled by Roslyn-based CLI tools.
- Per-checker split (Track C): the remaining findings are split out of the Sparrow XLS into one Markdown file per finding, grouped into a folder per checker. Each md renders only the XLS columns (file/line/function/path, checker description, source code) — no adjudication or instruction text is injected.

Every Sparrow finding remains a work item. Do not drop findings as false positives unless the user explicitly changes the policy.

### Language support

- **Track A/B are C#-only.** They parse and rewrite code with the Roslyn C# parser (`CSharpSyntaxTree`), so they cannot be used on C, C++, or other languages.
- **Track C is language-agnostic.** It never reads the XLS `언어` (language) column and copies the source-code cell verbatim without parsing, so it splits Sparrow findings for any language (C, C++, C#, Java, …) into per-checker Markdown.
- A **C/C++ project therefore uses Track C only**; the automated fixes (A/B) apply to C# projects.

## Entry Points

Normal users should start from one of these:

- `SparrowRunner.Gui/SparrowRunner.Gui.sln`: Visual Studio entry point. This top-level folder intentionally contains only the solution file.
- `tools/Run-SparrowRunnerGui.cmd`: launches the integrated GUI.
- `tools/Run-SparrowAll.cmd`: runs deterministic coding/comment fixers from the console.

The WPF GUI is the single closed-network helper surface. Its top level is split into **two sections** because the two jobs have different inputs and different risk:

| Section | Sub-tabs | Input | Nature |
|---|---|---|---|
| **코드 자동수정 (C#)** | **[코드 규칙]** / **[주석·레이아웃]** | target `.sln`/`.csproj`/folder + **local source scope tree** | rewrites source files (never commits) — **C# only** |
| **XLS 분리 (모든 언어)** | (none) | **one Sparrow XLS** + **scope tree built from the XLS's own paths** | read-only · **no project path needed** · language-agnostic |

Screen label ↔ internal track: **[코드 규칙] = Track A · [주석·레이아웃] = Track B · [XLS 분리] = Track C.** Track names live in code, docs, and commit messages only — the UI never shows them.

**The GUI always modifies files without committing** — it passes a fixed `-NoCommit` to the runners and, when a run ends, prints `N개 파일 수정됨 — 커밋하지 않았습니다. git diff 로 검토 후 커밋하세요.` to the log and the summary bar. Review with `git diff` and commit yourself. Per-rule auto-commit (`-Commit`), `-DryRun`, generated-file inclusion (`-IncludeGenerated`), and the per-rule compile gate (`-VerifyCmd`) remain **CLI runner options** for automation/CI.

Only the selected section runs; the log pane is shared. `--trackc-xls` auto-selects the [XLS 분리] section. A C/C++ user never needs the [코드 자동수정] section (and its inputs are not even rendered while [XLS 분리] is active).

Keep implementation logic out of the GUI:

- Coding-rule fixer: `tools/_internal/SparrowSyntaxFix`
- Comment/layout fixer: `tools/_internal/SparrowCommentFix`
- XLS parser and per-checker exporter: `tools/_internal/SparrowXlsExport` and `tools/_internal/SparrowXlsExport.Core`
- Before/after regression gate (G2): `tools/Compare-Sparrow.ps1`

## 폐쇄망 반입(오프라인 배포)

The GUI/runners normally `dotnet run`/`dotnet build`, which needs a .NET SDK and NuGet restore — impossible on an air-gapped PC. For offline use, publish the tools once on an internet PC and carry the whole skill folder over:

1. On an internet PC with the .NET SDK, run `tools\publish-airgap.ps1` (default: self-contained `win-x64`; add `-FrameworkDependent` for smaller output, `-DryRun` to preview). It publishes all four projects into per-project `publish\` folders.
2. Copy the **entire `skills/sparrow-static-analysis` tree** — including the generated `publish\` folders — to the air-gapped PC. No prerequisite documents are needed: the Track C exporter reads only the Sparrow XLS.
3. On the target, run `tools\Run-SparrowRunnerGui.cmd`. It auto-uses `SparrowRunner.Gui\publish\SparrowRunner.Gui.exe` when present, and the runners auto-pick `publish\SparrowSyntaxFix.exe` / `publish\SparrowCommentFix.exe` (no build/restore). Self-contained needs no .NET runtime on the target; framework-dependent needs the .NET 8 Desktop Runtime (GUI) / .NET 8 Runtime (CLI).

See `docs/sparrow-static-analysis-usage.md` (폐쇄망 반입 절) for the operator steps and runtime checklist.

## Deterministic CLI Fixes

Use deterministic CLI fixes only for predefined patterns. These tools are not general-purpose repair agents.

For coding-rule fixes, prefer:

```powershell
.\skills\sparrow-static-analysis\tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1
```

For comment/layout fixes, prefer:

```powershell
.\skills\sparrow-static-analysis\tools\_internal\SparrowCommentFix\Run-SparrowCommentFix.ps1
```

Normal operation should use the GUI or runner prompts. Direct `-Rules` use is reserved for tests, automation, and precise re-runs.

## Per-Checker Markdown Export (Track C)

Track C is a plain XLS-to-Markdown splitter. It requires **no prerequisite documents** — no checker guide, no prompt template, no adjudication contract. The only input is the Sparrow XLS.

Workflow:

1. Run `tools/Run-SparrowRunnerGui.cmd` and use the **[XLS 분리]** section (no project path — the input is the XLS), or run `tools/_internal/SparrowXlsExport` directly.
2. Pick the output folder. The exporter writes **one directory per checker key and nothing else**:
   - `<CHECKER_KEY>/{ID}_{FILE}_{LINE}.md` — one file per finding, carrying the checker metadata, the file/line location, the target-line anchor, and the surrounding source the XLS supplied. The directory name is the checker key, so the file name no longer repeats (or truncates) it.
   - No index, no rollup, no README/worklist byproducts — only finding files.
3. Optionally narrow the export with the scope tree (`--files-from`); scope selection is for splitting work across a team, not for dropping findings.
   - In the GUI the tree is built from the **XLS's own detection paths** (`SparrowExporter.ListPaths`, write-free): directories with per-file finding counts and folder subtotals. The selection is passed back **verbatim** (`--files-from`, no `--root`), so filtering an XLS by its own paths is an exact string match — language-agnostic and immune to per-teammate checkout differences. Checking nothing exports everything.
   - The cross-PC **relative-tail** matching (Tier 2) applies only when a **local source path** is supplied instead (the [코드 자동수정] scope tree, or `--root` + `--files-from` on the CLI).

Every Sparrow row is exported. There is no severity/checker filter and no fallback/unresolved concept — an unknown checker key is just another item file.

Checker-specific fix guidance is **not shipped by this repo**. It lives as a **named rule library**: each `references/checkers/<NAME>.md` (files starting with `_` excluded) is a named rule (name = filename, content = body), reusable across checkers. It is a gitignored, local asset; feed it to your reviewer/LLM alongside the exported item file.

There is **NO name-based auto-mapping**. A rule is attached to a checker only when the user **explicitly assigns** it — a rule file merely named like a checker key does nothing on its own. Assignments live in `references/checkers/_assignments.json` (`{ "<CHECKER_KEY>": "<RULE_NAME>" }`) and are **remembered** — the next time the same checker appears, its assignment is pre-filled.

From the GUI, the [XLS 분리] section shows only a summary ("검출 체커 N종 · 매핑 M · 미매핑 K") and a **[체커 규칙 관리]** button. Rule CRUD and checker assignment happen in a **separate window** (RuleManagerWindow): a left **규칙 라이브러리** area (create/edit/delete named rules; the first rule is auto-selected when the window opens, and the destructive [선택 규칙 삭제] sits apart from [새 규칙], under the list) and a right **체커 매핑** area (one row per detected checker with a rule ComboBox; unassigned shows "— 없음 —", unassigned checkers sorted first; [지정 저장] writes `_assignments.json`). When you run, **only assigned checkers** get their rule embedded self-contained into every item md (idempotent); unassigned checkers stay pure. Flow: **select XLS → assign rules in the manager window → run (only assignments are attached)**.

Do not auto-edit target source code for judgment-required findings from this skill. The exported item file is input for the developer or LLM working against the real source tree.

## Regression Gate (G2)

After a fix round, re-run Sparrow and compare the before/after XLS:

```powershell
.\tools\Compare-Sparrow.ps1 -Before before.xls -After after.xls [-Checker FORWARD_NULL] [-StrictScope]
```

Identity is `(checker key, full path)` with counts — line shifts do not register as new findings. Exit 0 = PASS, 1 = FAIL.

## Real Fix Pattern Corpus

When manually fixed closed-network findings should be reused without exposing source code, document only anonymized patterns under `references/real-fix-patterns/`.

- Use `references/real-fix-patterns/README.md` as the workflow.
- Use `references/real-fix-patterns/TEMPLATE.md` for each checker file.
- Extract only the minimum before/after shape needed to explain the checker fix.
- Anonymize filenames, symbols, string literals, paths, and domain terms.
- Classify the pattern as deterministic CLI, Markdown/LLM guidance, or human-review only.
- Do not copy closed-network source code, full functions, or business logic into this repo.

## Diagnostic Logs (진단 로그)

Screen output disappears when the app closes, so every run leaves evidence on disk. All of it is best-effort — a
read-only log folder never breaks the tool.

1. **GUI session transcript** — `%LOCALAPPDATA%\SparrowRunner\logs\session-<yyyyMMdd-HHmmss>.log`. Same lines as the
   on-screen log with an `HH:mm:ss.fff` stamp, preceded by a start header (app version, executable, startup args,
   skill root, guides dir, OS, .NET runtime, PID). Newest 20 kept. Override with `--log-dir <DIR>`.
2. **Track C run report** — `trackc-<stamp>.json` (+ human `.log` summary) in the same folder, one per Track C run:
   input xls path/size/**sha256**, out/guides dirs, elapsed ms, tool version, effective options (files-from scope,
   root, severity/checker/status/max), sheet, total/matched/written/checker-folder counts, **per-checker counts**,
   **per-checker assignment + attached-item counts**, unmapped checkers, scope diagnostics, warnings.
   The report is **never written into the export folder** — the "one directory per checker + item md only, zero
   byproducts" contract is untouched. The CLI writes it only with `--report <PATH>`; without it, output bytes are
   identical to before.
3. **Test diagnostics** — `tests/_logs/` (gitignored): `uia-<stamp>/result.log` (per-check PASS/FAIL with expected vs
   actual), `uia-<stamp>/tree-*.txt` (**UIA tree dumps** — per element `ControlType | AutomationId | Name |
   Rect(x,y,w,h) | IsOffscreen | IsEnabled | Value`), `uia-<stamp>/gui-logs/` (that run's own session log + report),
   `FAILURE-CONTEXT-iter<N>.txt` on failure, and `validate-<stamp>.log` (full `validate.ps1` transcript; its path is
   printed on failure). Newest 10 of each kept.
4. **Window snapshots (PNG)** — `tests/_logs/uia-<stamp>/shots/iter<N>/*.png`: the app **renders its own windows** to
   PNG (`RenderTargetBitmap` at the real DPI scale, opaque backdrop). Automatic at three points (main window loaded /
   rule manager opened / a run finished — Track C) plus one per on-demand request. Enabled only by
   `SparrowRunner.Gui.exe --screenshot-dir <DIR>`; without that argument the feature is entirely off. Dropping a
   `capture.request` file into that folder captures the ACTIVE window at that instant (the file's content becomes the
   filename suffix; the request file is then deleted). Every capture is logged as `snapshot: <file> (WxHpx)` — or
   `snapshot 실패: <reason>` — in the session transcript, and never affects app behaviour.

The tree dumps and the PNGs exist because **nobody can screenshot this WPF window from the outside** — it is a custom,
non-installed exe, so it cannot be added to an OS automation allow-list. The rectangles are the numeric eyes; the
self-rendered PNGs are the literal ones. `tests/gui-uia-tests.ps1` asserts on both: every key element inside its
window rect (no clipping), `w>0`/`h>0`/`IsOffscreen=false`, rule editor height ≥ 120px, rule list and editor rects
not intersecting, manager window at least 900x560 — plus ≥ 6 valid PNGs per iteration (PNG signature + IHDR, > 10KB
so a blank/transparent render fails) whose pixel size matches the UIA window rect within ±10% (catches a wrong-DPI
render). Thresholds are constants at the top of that script.

**When you need to judge this UI, open the PNGs.** Attach them to any UI/UX report alongside the tree dump.

## Evidence Priority

Use sources in this order:

1. User-provided Sparrow XLS/exported item Markdown.
2. Local Sparrow rule originals, if you exported them from your own Sparrow (not shipped with this repo).
3. Your own local checker notes under `references/checkers` (gitignored; only present if you built them).
4. Target source code context from the inspected project.
5. Optional local .NET reference XML evidence for exception candidates.
6. External official references only when local materials are insufficient.

## Validation

After changing scripts, run PowerShell parser checks. For deterministic tool changes, run the matching fixture tests. For exporter/gate changes:

```powershell
powershell -ExecutionPolicy Bypass -File .\validate.ps1
powershell -ExecutionPolicy Bypass -File .\tests\g2-gate-tests.ps1
powershell -ExecutionPolicy Bypass -File .\tests\e2e-lab\run-e2e.ps1
```
