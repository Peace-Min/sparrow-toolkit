# C/C++ Clang AST integration

## Runtime flow

```text
SparrowRunner.Gui
  -> SparrowCFamilyPipeline
      -> Sparrow.ClangAnalyzer.exe (JSON over stdin/stdout)
          -> bundled clang.exe -Xclang -ast-dump=json
          -> compile_commands.json auto discovery
      -> AST source ranges are validated and applied first
      -> SparrowCFamily.Core token/comment rules
      -> token-only fallback when Clang cannot parse the file
```

The analyzer never writes source files. It returns versioned JSON containing parse status, diagnostics, AST statistics, and insertion-only logical-parenthesis edits. The .NET core validates every returned range before applying it.

## Compile configuration discovery

For each source file the analyzer searches the file directory and its parents, plus common build folders, for `compile_commands.json`. The matching entry supplies the working directory, include paths, macros, language standard, and other parse options. Output, dependency-generation, and compilation-only arguments are removed before invoking Clang.

If no matching compilation command exists, the analyzer uses:

- C: `-x c -std=gnu11`
- C++: `-x c++ -std=gnu++17`
- the source directory as an include path

Callers may also provide additional Clang arguments through `CFamilyPipelineEngine.Options.ClangAdditionalArguments`. If parsing fails because headers, macros, or a sysroot are unavailable, the pipeline logs the reason and runs the existing token engine.

## Safety rules

- AST edits are accepted only for the main source file.
- Logical edits are limited to the condition subtrees of `if`, `while`, `do-while`, and `for`; a `for` initializer, increment expression, and body are never treated as its condition.
- Macro spelling/expansion ranges are not edited.
- Only UTF-8/ASCII files are edited by the AST layer; other encodings use the encoding-preserving token engine.
- `for`, `if`, `while`, and `do-while` conditions preserve the Clang AST and add parentheses between nested binary operators of different precedence levels. This makes evaluation order explicit without regrouping operands or changing behavior.
- The AST layer only inserts parentheses and cannot replace or delete source text.
- Analyzer failures never stop C/C++ processing unless the caller cancels the entire run.

## Distribution layout

```text
publish/
├─ SparrowRunner.Gui.exe
├─ clang-analyzer/
│  └─ Sparrow.ClangAnalyzer.exe (+ .NET runtime files as required)
├─ clang/
│  ├─ clang.exe
│  └─ clang-format.exe
├─ lib/clang/20/include/
└─ licenses/
   ├─ LLVM-LICENSE.txt
   └─ THIRD-PARTY-NOTICES.txt
```

`tools/publish-airgap.ps1` verifies the approved LLVM/Clang 20.1.0 SHA-256 values before publishing. An LLVM upgrade must update the version, hashes, complete license, bundled notices, Clang resource directory, and the Program Information dialog together.

LLVM/Clang remains under `Apache-2.0 WITH LLVM-exception`. Sparrow Helper remains under its own MIT license. LLVM names are used only for truthful component identification and do not imply endorsement.

## Validation

`tests/CFamilyBasicFixerTests` performs the following integration loop:

1. Generate UTF-8 C and C++ sources containing a Korean comment before the target expressions.
2. Generate `compile_commands.json` entries for both files.
3. Analyze and transform both files with Clang AST.
4. Repeat analysis 20 times and require zero additional edits.
5. Parse a file with a deliberately missing header and verify token-engine fallback.
6. Run the same loop against the binaries placed in the final `publish` directory.
