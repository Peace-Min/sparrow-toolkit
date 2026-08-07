---
name: sparrow-toolkit
description: Use when handling Sparrow static-analysis XLS findings, including deterministic Roslyn CLI fixes for C# coding/comment rules and splitting the remaining findings (any language) into per-checker Markdown files for LLM/human review.
---

# Sparrow Static Analysis — skill manifest

> **This file is optional for using the toolkit, but must not be deleted or moved.**
> `SKILL.md` at the repository root is also the **root marker** that
> `MainWindow.xaml.cs` (`ResolveSkillRoot()`), `validate.ps1`, and `SparrowXlsExport.Core/CoreTests`
> use to locate the repo. Removing it breaks the GUI's tools/guides path resolution.
>
> Everything substantive lives in the normal docs — this file only tells an agent *when* to reach for the toolkit:
> [README.md](README.md) · [docs/architecture.md](docs/architecture.md) ·
> [docs/extending.md](docs/extending.md) · [docs/usage.md](docs/usage.md) · [CONTRIBUTING.md](CONTRIBUTING.md)

## When to use

The user has a Sparrow (Fasoo) static-analysis `.xls` report and wants the findings processed —
auto-fixed where that is deterministic, and split per checker where a human or an LLM must judge.

## What the toolkit does (one paragraph)

Three tracks. **A** (`SparrowSyntaxFix`) and **B** (`SparrowCommentFix`) are deterministic Roslyn
*syntax-only* rewriters for C# coding-rule and comment/layout findings — they never load or compile a
project, so they work on legacy non-SDK `.csproj` targets. **C** (`SparrowXlsExport`) is a
language-agnostic exporter: one folder per checker key, one Markdown file per finding, **zero
byproducts**, no prerequisite documents. A WPF GUI (`tools/Run-SparrowRunnerGui.cmd`) drives all three.
Every Sparrow finding stays a work item — nothing is dropped as a false positive unless the user says so.

## Entry points

| Goal | Command |
|---|---|
| GUI (권장) | `tools/Run-SparrowRunnerGui.cmd` |
| Visual Studio | `SparrowRunner.Gui/SparrowRunner.Gui.sln` |
| Console, A→B 순차 | `tools/Run-SparrowAll.cmd` |
| Track A/B 러너 직접 | `tools/_internal/SparrowSyntaxFix/Run-SparrowSyntaxFix.ps1`, `tools/_internal/SparrowCommentFix/Run-SparrowCommentFix.ps1` |
| Track C CLI 직접 | `tools/_internal/SparrowXlsExport` |
| 전/후 회귀 게이트 (G2) | `tools/Compare-Sparrow.ps1 -Before before.xls -After after.xls` |

Normal operation goes through the GUI or the runner prompts. Direct `-Rules` / `--rules` use is
reserved for tests, automation, and precise re-runs.

## Language support

- **Track A/B are C#-only** (Roslyn C# parser). They cannot be used on C, C++, or other languages.
- **Track C is language-agnostic** — it never reads the XLS `언어` column and copies the source cell
  verbatim without parsing.
- A **C/C++ project therefore uses Track C only** today. To add C/C++ auto-fixing, follow
  [docs/extending.md 레시피 2](docs/extending.md#레시피-2-새-언어-트랙-추가-cc-예시).

## Rules of engagement

- **Do not auto-edit target source for judgment-required findings.** The exported item md is input for
  the developer or LLM working against the real source tree.
- **Do not drop findings as false positives** unless the user explicitly changes the policy.
- **Never write anything besides checker folders and item md into a Track C output folder.**
- Checker-specific fix guidance is **not shipped by this repo**. It is a local, gitignored named-rule
  library under `references/checkers/` with **explicit** checker assignments (no name-based auto-mapping).
- Do not copy closed-network source code, full functions, or business logic into this repo. Anonymized
  patterns go to `references/real-fix-patterns/` — see [CONTRIBUTING.md](CONTRIBUTING.md#6-실-데이터-반입-금지-중요).

## Evidence priority

1. User-provided Sparrow XLS / exported item Markdown.
2. Local Sparrow rule originals, if the user exported them from their own Sparrow (not shipped here).
3. Local checker notes under `references/checkers/` (gitignored; only present if the user built them).
4. Target source code context from the inspected project.
5. External official references only when local materials are insufficient.

## Validation

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\validate.ps1        # fast: source presence + PowerShell syntax check
powershell -NoProfile -ExecutionPolicy Bypass -File .\validate.ps1 -All   # full gate (build + E2E). PRs must pass this.
```

`-All` includes the GUI UI-Automation harness, so it **opens a real WPF window and takes minutes**.
It ends with a `실행 N · 스킵 M · 실패 K` banner and exits non-zero on any failure — **`실행 0` means no
assertion ran at all and is not a pass.** Details: [CONTRIBUTING.md](CONTRIBUTING.md#3-테스트-게이트).
