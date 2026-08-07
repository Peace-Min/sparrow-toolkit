// CoreTests: harness proving the Core exporter is byte-faithful and its output contract is stable.
//
//   I. Per-item md field table: no-signal columns dropped, decision-carrying columns + dual anchor kept.
//   L. Output layout: <OutDir>\<체커 키>\{ID}_{파일명}_{라인}.md and NOTHING else (no index/summary files).
//   F. --files-from source scope filter (absolute / dir+file / relative / unique-basename).
//   G. Cross-PC relative-tail scope match + [범위 불일치] / [범위 경고] diagnostics.
//   R. Run-report output-tree guard: a --report path inside <OutDir> is refused (부산물 0 계약 강제).
//   A. Console parse (optional real xls) exits 0 and writes checker folders.
//   B. Core.Run == console parse: byte-identical output tree.
//
// Prints PASS/FAIL per assertion; exits nonzero if any assertion fails. Run after a Release build of the
// Core, console, and CoreTests projects. By default this runs fixture-only (synthetic xls, no console exe
// needed). Pass a real XLS path as argv[0] to add the A/B console-equivalence checks.

using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Text;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using SparrowXlsExport.Core;

internal static class Program
{
    private static int _fails;
    private static int _checks;

    private static void Check(bool cond, string name, string detail = "")
    {
        _checks++;
        if (cond) Console.WriteLine("  [PASS] " + name);
        else { _fails++; Console.WriteLine("  [FAIL] " + name + (detail.Length > 0 ? "  -- " + detail : "")); }
    }

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

        string? realXlsArg = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        bool fixturesOnly = args.Any(a => string.Equals(a, "--fixtures-only", StringComparison.OrdinalIgnoreCase)) || realXlsArg == null;
        string realXls = realXlsArg ?? "";

        string? skillRoot = FindSkillRoot(AppContext.BaseDirectory);
        if (skillRoot == null) { Console.Error.WriteLine("repo root (SKILL.md + tools\\_internal) not found"); return 3; }

        string consoleExe = Path.Combine(skillRoot, "tools", "_internal", "SparrowXlsExport", "bin", "Release", "net8.0", "SparrowXlsExport.exe");

        string work = Path.Combine(Path.GetTempPath(), "sparrow-coretests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        Console.WriteLine("================= CoreTests (I/F/G + A/B) =================");
        Console.WriteLine("skillRoot : " + skillRoot);
        Console.WriteLine("consoleExe: " + consoleExe);
        Console.WriteLine("realXls   : " + realXls);
        Console.WriteLine("work      : " + work);
        Console.WriteLine();

        try
        {
            // ================================================================ I. item md 필드표 (상수 컬럼 제외)
            Console.WriteLine("\n==== I. 항목 md 필드표 (무기여 컬럼 제외 / 이중 앵커 보존) ====");
            TestItemMdFieldTable(work);

            Console.WriteLine("\n==== L. 출력 레이아웃 (체커별 폴더 + 항목 md 만) ====");
            TestOutputLayout(work);

            Console.WriteLine("\n==== N. ListCheckers (write-free 체커 집계 / 결정적 정렬 / graceful empties) ====");
            TestListCheckers(work);

            Console.WriteLine("\n==== P. ListPaths (write-free 경로 집계) + xls 자기경로 범위 필터(언어 무관) ====");
            TestListPaths(work);

            Console.WriteLine("\n==== M. Assignment-based checker→rule mapping (NO name auto-map / explicit assign only) ====");
            TestCheckerRuleMapper(work);

            Console.WriteLine("\n==== S. CheckerRuleStore (rule library CRUD + assignment load/save/remove) ====");
            TestCheckerRuleStore(work);

            Console.WriteLine("\n==== F. FilesFrom source scope filter ====");
            TestFilesFromScopeFilter(work);

            Console.WriteLine("\n==== G. Cross-PC relative-tail scope match + diagnostics ====");
            TestCrossPcScopeFilter(work);

            Console.WriteLine("\n==== R. 실행 리포트 출력-트리 가드 (부산물 0 계약 강제) ====");
            TestReportOutputTreeGuard(work);

            // A/B compare the Core against the console exe on a real xls; fixtures-only mode stops here.
            if (fixturesOnly) return Done();

            Check(File.Exists(consoleExe), "precondition: console exe exists", consoleExe);
            Check(File.Exists(realXls), "precondition: real xls exists", realXls);
            if (_fails > 0) return Done();

            // ================================================================ A
            Console.WriteLine("\n==== A. Console parse identical (real xls) ====");
            string dirA = Path.Combine(work, "A_console");
            var (exitA, stdoutA) = RunProcess(consoleExe, new[] { realXls, "--out", dirA });
            Check(exitA == 0, "A: console exit 0", "exit=" + exitA);
            Check(stdoutA.Contains("total data rows:"), "A: stdout has total data rows summary");
            Check(stdoutA.Contains("checker folders:"), "A: stdout has checker folders summary");
            int dirCountA = Directory.Exists(dirA) ? Directory.GetDirectories(dirA).Length : 0;
            int countA = Directory.Exists(dirA) ? Directory.GetFiles(dirA, "*.md", SearchOption.AllDirectories).Length : 0;
            Check(dirCountA > 0, "A: checker folders generated", "found=" + dirCountA);
            Check(countA > 0, "A: item md generated", "found=" + countA);
            Check(Directory.Exists(dirA) && Directory.GetFiles(dirA).Length == 0,
                  "A: 출력 루트에 파일 없음 (index.csv/checkers.md/요약 md 미생성)");

            // ================================================================ B
            Console.WriteLine("\n==== B. Core.Run == console parse (byte-identical) ====");
            string dirB = Path.Combine(work, "B_core");
            SparrowExporter.Run(new ExportOptions { InputPath = realXls, OutDir = dirB }, TextWriter.Null);
            Check(TreesByteIdentical(dirA, dirB, out string bTreeDiff), "B: 출력 트리 전체 byte-identical", bTreeDiff);

            // Real-data content assertions are intentionally not tied to one historical XLS: the optional
            // real mode proves console/Core parser equivalence only.
            return Done();
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    private static int Done()
    {
        Console.WriteLine("\n============================================================");
        Console.WriteLine("checks: " + _checks + "   fails: " + _fails);
        if (_fails == 0) { Console.WriteLine("== CoreTests PASS =="); return 0; }
        Console.WriteLine("== CoreTests FAIL (" + _fails + ") =="); return 1;
    }

    // I. Per-item md field table: constant/no-signal Sparrow columns (유형 / 언어 / 체커 타입 / 이슈 상태) are
    // dropped, decision-carrying columns stay, and the 체커 설명 / 소스 코드 sections survive. The md carries
    // ONLY rendered xls columns — no injected instruction text or anchor markers. Uses a synthetic xls so it
    // runs in fixtures-only mode too.
    private static void TestItemMdFieldTable(string work)
    {
        string xls = Path.Combine(work, "fieldtable.xls");
        WriteSyntheticXls(xls,
            new[]
            {
                "ID", "유형", "위험도", "언어", "레퍼런스", "체커 타입", "체커 키", "체커명", "라인", "파일명",
                "함수", "경로", "A.S", "유사 이슈 그룹", "이슈 상태", "이슈 담당자", "검출 시간", "체커 설명", "소스 코드",
            },
            new[]
            {
                new[]
                {
                    "7001", "보안약점", "매우위험", "C#", "CWE-476", "SEMANTIC", "FORWARD_NULL", "널 값 역참조", "88", "Foo.cs",
                    "Process", "src/Foo.cs", "N", "G-12", "미확인", "없음", "2026-07-19 10:00:00", "널 값을 역참조합니다.",
                    "  87: // no guard\n  88: Process(node.Value);\n  89: return;",
                },
            });

        string outDir = Path.Combine(work, "fieldtable_out");
        SparrowExporter.Run(new ExportOptions { InputPath = xls, OutDir = outDir }, TextWriter.Null);
        string checkerDir = Path.Combine(outDir, "FORWARD_NULL");   // 폴더명 = 체커 키
        string? item = Directory.Exists(checkerDir)
            ? Directory.GetFiles(checkerDir, "*.md").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault()
            : null;
        Check(item != null, "I: item md generated", checkerDir);
        if (item == null) return;
        string md = ReadText(item);

        // dropped: constant across the codebase, or workflow/bookkeeping metadata — no bearing on the fix decision
        foreach (string dropped in new[]
                 {
                     "유형", "언어", "체커 타입", "이슈 상태",
                     "A.S", "이슈 담당자", "검출 시간", "유사 이슈 그룹", "레퍼런스",
                 })
            Check(!md.Contains("| " + dropped + " |", StringComparison.Ordinal),
                  "I: 필드표에서 '" + dropped + "' 행 제거됨");

        // kept: identity + location + checker meta
        foreach (string kept in new[] { "ID", "위험도", "체커 키", "체커명", "라인", "파일명", "함수", "경로" })
            Check(md.Contains("| " + kept + " |", StringComparison.Ordinal),
                  "I: 필드표에 '" + kept + "' 행 유지됨");

        // sections that must never be trimmed (xls 컬럼에서 온 것만)
        Check(md.Contains("## 체커 설명", StringComparison.Ordinal), "I: 체커 설명 섹션 보존");
        Check(md.Contains("널 값을 역참조합니다.", StringComparison.Ordinal), "I: 체커 설명 본문 보존");
        Check(md.Contains("## 소스 코드", StringComparison.Ordinal), "I: 소스 코드 섹션 보존");
        Check(md.Contains("  88: Process(node.Value);", StringComparison.Ordinal), "I: 소스 코드 원문 그대로 보존");
        Check(!md.Contains("| 소스 코드 |", StringComparison.Ordinal) && !md.Contains("| 체커 설명 |", StringComparison.Ordinal),
              "I: 소스 코드/체커 설명은 표가 아닌 전용 섹션에만 존재");

        // md 는 xls 컬럼 렌더링만 담는다. 수정 범위·작업 지시는 도구가 아니라 사용자·체커별로 정할 몫이므로
        // 어떤 지시문도 앵커 마커도 주입하지 않는다(과거 주입하던 문구의 재발 방지).
        Check(!md.Contains("## 수정 대상", StringComparison.Ordinal), "I: '수정 대상' 주입 섹션 없음");
        Check(!md.Contains("TARGET LINE", StringComparison.Ordinal), "I: TARGET LINE 마커 주입 없음");
        Check(!md.Contains("ANCHOR", StringComparison.Ordinal), "I: ANCHOR 마커 주입 없음");
        Check(!md.Contains("수정 기준점", StringComparison.Ordinal), "I: '수정 기준점' 지시문 주입 없음");
        Check(!md.Contains("- 지시:", StringComparison.Ordinal), "I: '지시:' 항목 주입 없음");
        Check(("" + md).Split(new[] { "\n## " }, StringSplitOptions.None).Length - 1 == 2,
              "I: 섹션은 '체커 설명'·'소스 코드' 둘 뿐");
    }

    // L. 출력 레이아웃 계약: 최상위 = 체커 키 디렉토리, 그 안에 {ID}_{파일명}_{라인}.md.
    // items\ 하위폴더도, index.csv 도, checkers.md 도, 요약/작업지침 md 도 만들지 않는다(사용자 명시 거부).
    private static void TestOutputLayout(string work)
    {
        string xls = Path.Combine(work, "layout.xls");
        WriteSyntheticXls(xls,
            new[] { "ID", "체커 키", "위험도", "파일명", "라인", "경로", "체커 설명", "소스 코드" },
            new[]
            {
                new[] { "5001", "FORWARD_NULL", "매우위험", "Foo.cs", "88", @"src\Foo.cs", "널 역참조", "  88: A();" },
                new[] { "5002", "FORWARD_NULL", "매우위험", "Bar.cs", "12", @"src\Bar.cs", "널 역참조", "  12: B();" },
                // 긴 체커 키: 예전에는 파일명에 들어가 40자에서 잘렸다 -> 이제 폴더명으로 온전히 남는다.
                new[] { "5003", "PRACTICE.OBJECT_INITIALIZATION.NOT_USED_INITIALIZER", "낮음", "Baz.cs", "7", @"src\Baz.cs", "초기화자 미사용", "   7: C();" },
            });

        string outDir = Path.Combine(work, "layout_out");
        SparrowExporter.Run(new ExportOptions { InputPath = xls, OutDir = outDir }, TextWriter.Null);

        var dirs = Directory.GetDirectories(outDir).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Check(dirs.Count == 2 && dirs[0] == "FORWARD_NULL"
              && dirs[1] == "PRACTICE.OBJECT_INITIALIZATION.NOT_USED_INITIALIZER",
              "L: 최상위 = 체커 키 디렉토리(2개, 키 원문 그대로·잘림 없음)", string.Join(", ", dirs));
        Check(Directory.GetFiles(outDir).Length == 0,
              "L: 출력 루트에 파일 0개 (index.csv/checkers.md/요약 md 미생성)",
              string.Join(", ", Directory.GetFiles(outDir).Select(Path.GetFileName)));
        Check(!Directory.Exists(Path.Combine(outDir, "items")), "L: items\\ 하위폴더 없음");
        Check(Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).All(p => p.EndsWith(".md", StringComparison.Ordinal)),
              "L: md 외 파일 없음 (부산물 0)");

        var fn = Directory.GetFiles(Path.Combine(outDir, "FORWARD_NULL"), "*.md")
                          .Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Check(fn.Count == 2 && fn[0] == "5001_Foo.cs_88.md" && fn[1] == "5002_Bar.cs_12.md",
              "L: 파일명 = {ID}_{파일명}_{라인}.md (체커 키 미포함)", string.Join(", ", fn));

        var longKey = Directory.GetFiles(Path.Combine(outDir, "PRACTICE.OBJECT_INITIALIZATION.NOT_USED_INITIALIZER"), "*.md")
                               .Select(Path.GetFileName).ToList();
        Check(longKey.Count == 1 && longKey[0] == "5003_Baz.cs_7.md",
              "L: 긴 체커 키도 폴더로 분리 (파일명 잘림 없음)", string.Join(", ", longKey));
    }

    // N. ListCheckers: the lightweight, WRITE-FREE checker census that fills the GUI's pre-run mapping panel the
    // moment an xls is chosen. Proves: detections aggregated per 체커 키, deterministic ordinal ordering, counts
    // consistent with a full Run's grouping, NOTHING written to disk (no .items sibling / no byproducts), and
    // graceful empties (missing / empty / non-workbook input -> empty list, never throws).
    private static void TestListCheckers(string work)
    {
        string xls = Path.Combine(work, "census.xls");
        WriteSyntheticXls(xls,
            new[] { "ID", "체커 키", "위험도", "파일명", "라인", "경로", "체커 설명", "소스 코드" },
            new[]
            {
                new[] { "1", "EMPTY_CATCH_BLOCK", "높음", "Alpha.cs", "13", @"src\Alpha.cs", "빈 catch", "  13: A();" },
                new[] { "2", "EMPTY_CATCH_BLOCK", "높음", "Beta.cs", "27", @"src\Beta.cs", "빈 catch", "  27: B();" },
                new[] { "3", "FORWARD_NULL", "매우위험", "Gamma.cs", "11", @"src\Gamma.cs", "널 역참조", "  11: C();" },
                new[] { "4", "RESOURCE_LEAK", "매우위험", "Delta.cs", "9", @"src\Delta.cs", "자원 누수", "  9: D();" },
            });

        var census = SparrowExporter.ListCheckers(xls);
        Check(census.Count == 3, "N: 체커 3종 집계", "count=" + census.Count);
        Check(census.Count == 3 && census[0].Key == "EMPTY_CATCH_BLOCK"
              && census[1].Key == "FORWARD_NULL" && census[2].Key == "RESOURCE_LEAK",
              "N: 키 사전순(ordinal) 정렬", string.Join(", ", census.Select(c => c.Key)));
        var byKey = census.ToDictionary(c => c.Key, c => c.Count, StringComparer.Ordinal);
        Check(byKey.TryGetValue("EMPTY_CATCH_BLOCK", out int ec) && ec == 2, "N: EMPTY_CATCH_BLOCK 건수 2 (다건 집계)");
        Check(byKey.TryGetValue("FORWARD_NULL", out int fn2) && fn2 == 1, "N: FORWARD_NULL 건수 1");
        Check(byKey.TryGetValue("RESOURCE_LEAK", out int rl) && rl == 1, "N: RESOURCE_LEAK 건수 1");

        // write-free: no .items sibling, and the only census* file in work is the input xls itself.
        Check(!Directory.Exists(Path.Combine(work, "census.items")), "N: ListCheckers 는 무작성(.items 미생성)");
        Check(Directory.GetFiles(work, "census*", SearchOption.AllDirectories).Length == 1,
              "N: census.xls 외 어떤 산출물도 없음(부산물 0)");

        // graceful empties: missing path / empty path / non-workbook file -> empty list, no throw.
        Check(SparrowExporter.ListCheckers(Path.Combine(work, "nope.xls")).Count == 0, "N: 없는 파일 → 빈 목록");
        Check(SparrowExporter.ListCheckers("").Count == 0, "N: 빈 경로 → 빈 목록");
        string notXls = Path.Combine(work, "not-a-workbook.dat");
        File.WriteAllText(notXls, "this is not an OLE2/xlsx workbook", new UTF8Encoding(false));
        Check(SparrowExporter.ListCheckers(notXls).Count == 0, "N: 파싱불가 입력 → 빈 목록(예외 아님)");
    }

    // P. ListPaths + the xls-derived scope filter (the GUI's [XLS 분리] 범위 트리 contract). Proves:
    //   - per-path detection counts, both 경로 conventions absorbed (full path as-is / directory + 파일명),
    //     empty 경로 falls back to 파일명, rows with neither are skipped, deterministic ordering, WRITE-FREE,
    //     graceful empties (missing / empty / non-workbook -> empty list, never throws);
    //   - feeding those very paths back as FilesFrom (no RootPath) keeps EXACTLY the selected rows — for C/C++
    //     paths too (language-agnostic) and for relative xls paths that no local root could resolve.
    private static void TestListPaths(string work)
    {
        string xls = Path.Combine(work, "paths.xls");
        WriteSyntheticXls(xls,
            new[] { "ID", "체커 키", "위험도", "파일명", "라인", "경로", "체커 설명", "소스 코드" },
            new[]
            {
                // core/ : 2 detections on the SAME file + 1 on another -> counts 2 and 1
                new[] { "1", "EMPTY_CATCH_BLOCK", "높음", "Alpha.cs", "13", @"src\core\Alpha.cs", "빈 catch", "  13: A();" },
                new[] { "2", "FORWARD_NULL", "높음", "Alpha.cs", "31", @"src\core\Alpha.cs", "널 역참조", "  31: A2();" },
                new[] { "3", "EMPTY_CATCH_BLOCK", "높음", "Beta.cpp", "27", @"src\core\Beta.cpp", "빈 catch", "  27: B();" },
                // ui/ : 경로 holds the DIRECTORY only -> 파일명 must be appended
                new[] { "4", "RESOURCE_LEAK", "매우위험", "Gamma.cpp", "11", @"src\ui", "자원 누수", "  11: C();" },
                // no 경로 at all -> 파일명 IS the path
                new[] { "5", "NULL_RETURN", "보통", "Delta.h", "9", "", "널 반환", "   9: D();" },
                // neither 경로 nor 파일명 -> skipped (nowhere to place it)
                new[] { "6", "NO_PLACE", "보통", "", "0", "", "경로 없음", "   0: X();" },
            });

        var paths = SparrowExporter.ListPaths(xls);
        Check(paths.Count == 4, "P: 서로 다른 경로 4종 집계(경로/파일명 모두 없는 행은 제외)", "count=" + paths.Count);
        var byPath = paths.ToDictionary(p => p.Path, p => p, StringComparer.OrdinalIgnoreCase);
        Check(byPath.TryGetValue(@"src\core\Alpha.cs", out XlsPathEntry? alpha) && alpha!.Count == 2 && alpha.FileName == "Alpha.cs",
              "P: 같은 파일의 검출 2건이 한 경로로 집계(건수 2)",
              alpha == null ? "(없음)" : "count=" + alpha.Count);
        Check(byPath.ContainsKey(@"src\core\Beta.cpp") && byPath[@"src\core\Beta.cpp"].Count == 1,
              "P: C++ 경로도 그대로 집계(.cpp)");
        Check(byPath.TryGetValue(@"src\ui\Gamma.cpp", out XlsPathEntry? gamma) && gamma!.Count == 1,
              "P: 경로가 디렉토리만 담은 행은 파일명을 붙여 경로화", string.Join(" | ", paths.Select(p => p.Path)));
        Check(byPath.ContainsKey("Delta.h") && byPath["Delta.h"].FileName == "Delta.h",
              "P: 경로가 없으면 파일명이 경로가 된다");
        Check(!paths.Any(p => p.Path.Length == 0), "P: 빈 경로 엔트리 없음");

        var sorted = paths.Select(p => p.Path).ToList();
        var expectedOrder = sorted.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ThenBy(p => p, StringComparer.Ordinal).ToList();
        Check(sorted.SequenceEqual(expectedOrder, StringComparer.Ordinal), "P: 결정적 정렬(경로 사전순)", string.Join(" | ", sorted));

        // write-free: no .items sibling and no byproduct next to the input.
        Check(!Directory.Exists(Path.Combine(work, "paths.items")), "P: ListPaths 는 무작성(.items 미생성)");
        Check(Directory.GetFiles(work, "paths*", SearchOption.AllDirectories).Length == 1,
              "P: paths.xls 외 어떤 산출물도 없음(부산물 0)");

        // graceful empties (same contract as ListCheckers).
        Check(SparrowExporter.ListPaths(Path.Combine(work, "nope.xls")).Count == 0, "P: 없는 파일 → 빈 목록");
        Check(SparrowExporter.ListPaths("").Count == 0, "P: 빈 경로 → 빈 목록");
        string notXls = Path.Combine(work, "not-a-workbook-paths.dat");
        File.WriteAllText(notXls, "this is not an OLE2/xlsx workbook", new UTF8Encoding(false));
        Check(SparrowExporter.ListPaths(notXls).Count == 0, "P: 파싱불가 입력 → 빈 목록(예외 아님)");

        // --- xls 자기경로 범위 필터: ListPaths 가 준 경로를 그대로 FilesFrom 으로 되먹인다(RootPath 없음) ---
        // 고른 것: core\Alpha.cs(2건, C#) + ui\Gamma.cpp(1건, C++ · 경로는 디렉토리만) → 정확히 3건만 남아야 한다.
        string filesFrom = Path.Combine(work, "paths-scope.csv");
        File.WriteAllText(filesFrom,
            "파일명\n" + CsvLine(@"src\core\Alpha.cs") + "\n" + CsvLine(@"src\ui\Gamma.cpp") + "\n",
            new UTF8Encoding(false));

        string scopedOut = Path.Combine(work, "paths_scoped_out");
        ExportResult scoped = SparrowExporter.Run(new ExportOptions
        {
            InputPath = xls,
            OutDir = scopedOut,
            FilesFrom = filesFrom,   // RootPath 의도적으로 미지정 = xls 자기경로 매칭
        }, TextWriter.Null);
        var scopedDirs = CheckerDirs(scopedOut);
        Check(scoped.WrittenCount == 3, "P: xls 경로 선택 3건만 기록(Alpha 2 + Gamma 1)", "written=" + scoped.WrittenCount);
        Check(scopedDirs.Contains("EMPTY_CATCH_BLOCK") && scopedDirs.Contains("FORWARD_NULL")
              && scopedDirs.Contains("RESOURCE_LEAK"),
              "P: 선택한 경로의 체커 폴더만 생성", string.Join(", ", scopedDirs));
        Check(!scopedDirs.Contains("NULL_RETURN"), "P: 선택 안 한 경로(Delta.h)는 제외");
        Check(Directory.GetFiles(Path.Combine(scopedOut, "EMPTY_CATCH_BLOCK"), "*.md").Length == 1,
              "P: Beta.cpp(같은 폴더의 미선택 파일) 항목은 제외 — 폴더 단위가 아니라 파일 단위 정확 매칭");
        Check(!scoped.ScopeMismatch && scoped.ScopeDiagnostic == null, "P: 매칭 성공이므로 [범위 불일치] 진단 없음");

        // 슬래시/역슬래시·대소문자 표기 차이는 흡수한다(같은 파일을 다르게 적어도 매칭).
        string filesFromAlt = Path.Combine(work, "paths-scope-alt.csv");
        File.WriteAllText(filesFromAlt, "파일명\n" + CsvLine("SRC/core/alpha.CS") + "\n", new UTF8Encoding(false));
        ExportResult alt = SparrowExporter.Run(new ExportOptions
        {
            InputPath = xls,
            OutDir = Path.Combine(work, "paths_alt_out"),
            FilesFrom = filesFromAlt,
        }, TextWriter.Null);
        Check(alt.WrittenCount == 2, "P: 구분자/대소문자 표기 차이 흡수(2건)", "written=" + alt.WrittenCount);

        // 하나도 안 맞으면 조용한 빈 결과가 아니라 [범위 불일치] 진단.
        string filesFromMiss = Path.Combine(work, "paths-scope-miss.csv");
        File.WriteAllText(filesFromMiss, "파일명\n" + CsvLine(@"other\Nope.cpp") + "\n", new UTF8Encoding(false));
        ExportResult miss = SparrowExporter.Run(new ExportOptions
        {
            InputPath = xls,
            OutDir = Path.Combine(work, "paths_miss_out"),
            FilesFrom = filesFromMiss,
        }, TextWriter.Null);
        Check(miss.WrittenCount == 0 && miss.ScopeMismatch && miss.ScopeDiagnostic != null
              && miss.ScopeDiagnostic!.Contains("[범위 불일치]", StringComparison.Ordinal),
              "P: 전혀 안 맞는 선택 → 0건 + [범위 불일치] 진단", miss.ScopeDiagnostic ?? "(null)");
    }

    // M. Assignment-based checker→rule mapping. The exporter output stays PURE; CheckerRuleMapper.Apply embeds a
    // rule ONLY when the user explicitly assigned it (_assignments.json: 체커 키 -> 규칙 이름) AND the rule file
    // exists — as a "## 매핑 규칙 (키)" section between 체커 설명 and 소스 코드. Proves: NO name-based auto-mapping
    // (a rule file named like a checker key does nothing unless assigned), rule name independent of checker key,
    // self-contained embed across ALL md of a multi-item folder, pure fallback for unassigned checkers, correct
    // Mapped/Unmapped/ItemsTouched, embed position, idempotency (byte-equal re-run, no duplicate section, rule-
    // internal "## " headers not mistaken for the boundary), original-key matching by md field (folder SPACE-KEY
    // vs original key "SPACE KEY"), BOM-stripped rule read, dangling assignment self-heal, and the missing/empty
    // guidesDir / no-assignments => all-Unmapped/pure/no-throw path.
    private static void TestCheckerRuleMapper(string work)
    {
        const string DottedKey = "PRACTICE.OBJECT_INSTANTIATION.NOT_USED_IMPLICIT_TYPING";
        const string SpaceKey = "SPACE KEY";        // San() maps this folder to "SPACE-KEY" -> proves md-field match
        const string UnassignedKey = "FORWARD_NULL"; // no assignment, no rule file
        const string AutoNameKey = "RESOURCE_LEAK";  // a rule file is NAMED like this key but NOT assigned -> pure

        string xls = Path.Combine(work, "map.xls");
        WriteSyntheticXls(xls,
            new[] { "ID", "체커 키", "위험도", "파일명", "라인", "경로", "체커 설명", "소스 코드" },
            new[]
            {
                new[] { "8001", DottedKey, "낮음", "Foo.cs", "10", @"src\Foo.cs", "암시적 타입 미사용", "  10: Foo a = new Foo();" },
                new[] { "8002", DottedKey, "낮음", "Bar.cs", "20", @"src\Bar.cs", "암시적 타입 미사용", "  20: Bar b = new Bar();" },
                new[] { "8003", UnassignedKey, "매우위험", "Baz.cs", "30", @"src\Baz.cs", "널 역참조", "  30: x.Do();" },
                new[] { "8004", SpaceKey, "보통", "Qux.cs", "40", @"src\Qux.cs", "공백 키", "  40: Q();" },
                new[] { "8005", AutoNameKey, "매우위험", "Leak.cs", "50", @"src\Leak.cs", "자원 누수", "  50: new FileStream();" },
            });

        string outDir = Path.Combine(work, "map_out");
        SparrowExporter.Run(new ExportOptions { InputPath = xls, OutDir = outDir }, TextWriter.Null);

        // Rule LIBRARY (names deliberately NOT equal to checker keys, to prove independence), plus one rule file
        // NAMED exactly like a checker key ("RESOURCE_LEAK") that we intentionally DO NOT assign. Rule files carry
        // a BOM (mapper must strip the leading U+FEFF) and an internal "## 근거" header (StripMappingSection must
        // anchor on 소스 코드, not a generic next-header scan).
        string guidesDir = Path.Combine(work, "guides");
        Directory.CreateDirectory(guidesDir);
        string dottedRule = "규칙: 선언 타입과 생성 타입이 같으면 var 를 쓴다.\n\n## 근거\nSparrow 권장 사항.\n";
        string spaceRule = "규칙: 공백 키 규칙 본문.\n";
        string leakRule = "규칙: using 으로 자원을 감싼다.\n";
        WriteGuideWithBom(Path.Combine(guidesDir, "var-규칙.md"), dottedRule);       // rule name != checker key
        WriteGuideWithBom(Path.Combine(guidesDir, "공백-규칙.md"), spaceRule);
        WriteGuideWithBom(Path.Combine(guidesDir, AutoNameKey + ".md"), leakRule);   // named like a key, UNASSIGNED

        // Assignments: ONLY dotted + space. FORWARD_NULL and RESOURCE_LEAK are left unassigned on purpose.
        CheckerRuleStore.SaveAssignments(guidesDir, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DottedKey] = "var-규칙",
            [SpaceKey] = "공백-규칙",
        });

        MapResult r = CheckerRuleMapper.Apply(outDir, guidesDir);

        Check(r.Mapped.Contains(DottedKey) && r.Mapped.Contains(SpaceKey) && r.Mapped.Count == 2,
              "M: Mapped = {dotted, space} (지정된 체커만)", string.Join(", ", r.Mapped));
        Check(r.Unmapped.Count == 2 && r.Unmapped.Contains(UnassignedKey) && r.Unmapped.Contains(AutoNameKey),
              "M: Unmapped = {FORWARD_NULL, RESOURCE_LEAK}", string.Join(", ", r.Unmapped));
        Check(r.ItemsTouched == 3, "M: ItemsTouched == 3 (dotted 2 + space 1)", "touched=" + r.ItemsTouched);

        // *** THE core no-auto-map proof ***: RESOURCE_LEAK.md rule file EXISTS and its name equals the checker
        // key, yet the RESOURCE_LEAK folder stays PURE because it was never assigned.
        string autoDir = Path.Combine(outDir, AutoNameKey);
        var autoMd = Directory.GetFiles(autoDir, "*.md").FirstOrDefault();
        Check(File.Exists(Path.Combine(guidesDir, AutoNameKey + ".md")),
              "M: 체커키와 동일한 이름의 규칙 파일이 라이브러리에 존재(RESOURCE_LEAK.md)");
        Check(autoMd != null && !ReadText(autoMd).Contains("## 매핑 규칙", StringComparison.Ordinal),
              "M: [핵심] 이름이 체커키와 같아도 지정 안 하면 부착 안 됨(RESOURCE_LEAK 순수)");

        // self-contained across ALL md of the multi-item dotted folder
        string dottedDir = Path.Combine(outDir, DottedKey);   // dotted key: folder == key (San no-op)
        var dottedMds = Directory.GetFiles(dottedDir, "*.md").OrderBy(p => p, StringComparer.Ordinal).ToList();
        Check(dottedMds.Count == 2, "M: dotted 폴더에 항목 md 2건", "count=" + dottedMds.Count);
        string mapHeader = "## 매핑 규칙 (" + DottedKey + ")";
        foreach (string md in dottedMds)
        {
            string t = ReadText(md);
            Check(t.Contains(mapHeader, StringComparison.Ordinal),
                  "M: 모든 항목 md에 매핑 규칙 헤더 (self-contained): " + Path.GetFileName(md));
            Check(t.Contains("선언 타입과 생성 타입이 같으면 var", StringComparison.Ordinal),
                  "M: 지정된 규칙 본문 임베드(규칙명 != 체커키): " + Path.GetFileName(md));
            Check(t.Contains("## 근거", StringComparison.Ordinal),
                  "M: 규칙 내부 '## 근거' 헤더 보존: " + Path.GetFileName(md));
            Check(!t.Contains('\uFEFF'), "M: 임베드된 규칙에 BOM(U+FEFF) 없음: " + Path.GetFileName(md));
            // embed position: 체커 설명 < 매핑 규칙 < 소스 코드
            int descIdx = t.IndexOf("## 체커 설명", StringComparison.Ordinal);
            int mapIdx = t.IndexOf("## 매핑 규칙", StringComparison.Ordinal);
            int srcIdx = t.IndexOf("## 소스 코드", StringComparison.Ordinal);
            Check(descIdx >= 0 && mapIdx > descIdx && srcIdx > mapIdx,
                  "M: 임베드 위치 = 체커 설명 → 매핑 규칙 → 소스 코드: " + Path.GetFileName(md),
                  "desc=" + descIdx + " map=" + mapIdx + " src=" + srcIdx);
            // exactly one mapping section (no duplicate)
            Check(CountOccurrences(t, "## 매핑 규칙") == 1, "M: 매핑 규칙 섹션 단 1개(중복 없음): " + Path.GetFileName(md));
        }

        // original-key matching by md field: folder name is SPACE-KEY, embedded header uses the original "SPACE KEY"
        string spaceDir = Path.Combine(outDir, "SPACE-KEY");
        Check(Directory.Exists(spaceDir), "M: 공백 키 폴더는 San 치환된 'SPACE-KEY'");
        var spaceMd = Directory.GetFiles(spaceDir, "*.md").FirstOrDefault();
        Check(spaceMd != null, "M: SPACE-KEY 폴더에 항목 md 존재");
        if (spaceMd != null)
        {
            string t = ReadText(spaceMd);
            Check(t.Contains("## 매핑 규칙 (SPACE KEY)", StringComparison.Ordinal),
                  "M: 폴더명(SPACE-KEY) 아닌 md 필드 원본 키(SPACE KEY)로 매칭·임베드");
            Check(t.Contains("공백 키 규칙 본문", StringComparison.Ordinal), "M: 공백 키 규칙 본문 임베드");
        }

        // unassigned folder stays pure
        string fwdDir = Path.Combine(outDir, UnassignedKey);
        var fwdMd = Directory.GetFiles(fwdDir, "*.md").FirstOrDefault();
        Check(fwdMd != null, "M: FORWARD_NULL 폴더에 항목 md 존재");
        if (fwdMd != null)
        {
            string t = ReadText(fwdMd);
            Check(!t.Contains("## 매핑 규칙", StringComparison.Ordinal), "M: 미지정 체커 md 는 순수(매핑 규칙 없음)");
        }

        // idempotency: a second Apply produces byte-identical md across the whole tree
        var before = SnapshotBytes(outDir);
        MapResult r2 = CheckerRuleMapper.Apply(outDir, guidesDir);
        var after = SnapshotBytes(outDir);
        Check(r2.Mapped.Count == 2 && r2.Unmapped.Count == 2, "M: 재실행 Mapped/Unmapped 동일");
        Check(SnapshotsEqual(before, after, out string snapDiff), "M: Apply 2회 → 트리 byte-identical(중복 삽입 없음)", snapDiff);

        // no _assignments.json AT ALL, but rule files named exactly like every checker key are present ->
        // still all-Unmapped/pure. This is the strongest no-auto-map proof at the Apply layer.
        string outNoAssign = Path.Combine(work, "map_out_noassign");
        SparrowExporter.Run(new ExportOptions { InputPath = xls, OutDir = outNoAssign }, TextWriter.Null);
        string guidesNoAssign = Path.Combine(work, "guides_noassign");
        Directory.CreateDirectory(guidesNoAssign);
        foreach (string key in new[] { DottedKey, UnassignedKey, SpaceKey, AutoNameKey })
            File.WriteAllText(Path.Combine(guidesNoAssign, key + ".md"), "규칙: 이름만 같은 규칙.\n", new UTF8Encoding(false));
        MapResult rNoAssign = CheckerRuleMapper.Apply(outNoAssign, guidesNoAssign);
        Check(rNoAssign.Mapped.Count == 0 && rNoAssign.ItemsTouched == 0,
              "M: [핵심] _assignments.json 부재 + 체커키 동명 규칙 파일 다수 → 전부 Unmapped(자동매핑 없음)",
              "mapped=" + rNoAssign.Mapped.Count);
        Check(Directory.GetFiles(outNoAssign, "*.md", SearchOption.AllDirectories)
                       .All(p => !ReadText(p).Contains("## 매핑 규칙", StringComparison.Ordinal)),
              "M: _assignments.json 부재 → 모든 md 순수");

        // dangling assignment: assign a checker to a rule whose file does not exist -> that checker stays pure.
        string outDangle = Path.Combine(work, "map_out_dangle");
        SparrowExporter.Run(new ExportOptions { InputPath = xls, OutDir = outDangle }, TextWriter.Null);
        string guidesDangle = Path.Combine(work, "guides_dangle");
        Directory.CreateDirectory(guidesDangle);
        CheckerRuleStore.SaveAssignments(guidesDangle, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [UnassignedKey] = "없는-규칙",   // no 없는-규칙.md file
        });
        MapResult rDangle = CheckerRuleMapper.Apply(outDangle, guidesDangle);
        Check(rDangle.Mapped.Count == 0 && rDangle.Unmapped.Contains(UnassignedKey),
              "M: 지정은 있으나 규칙 파일이 없으면(dangling) 부착 안 함 → 순수");

        // missing guidesDir => all Unmapped, md pure, no throw
        string outAbsent = Path.Combine(work, "map_out_absent");
        SparrowExporter.Run(new ExportOptions { InputPath = xls, OutDir = outAbsent }, TextWriter.Null);
        MapResult rAbsent = CheckerRuleMapper.Apply(outAbsent, Path.Combine(work, "no-such-guides-dir"));
        Check(rAbsent.Mapped.Count == 0 && rAbsent.Unmapped.Count == 4 && rAbsent.ItemsTouched == 0,
              "M: guidesDir 부재 → 전부 Unmapped, ItemsTouched 0");
        Check(Directory.GetFiles(outAbsent, "*.md", SearchOption.AllDirectories)
                       .All(p => !ReadText(p).Contains("## 매핑 규칙", StringComparison.Ordinal)),
              "M: guidesDir 부재 → 모든 md 순수");
    }

    private static void WriteGuideWithBom(string path, string content)
    {
        File.WriteAllText(path, content, new UTF8Encoding(true));   // BOM on purpose: mapper must strip it
    }

    // S. CheckerRuleStore: the storage layer behind the rule library + assignments. Proves rule CRUD (write UTF-8
    // no-BOM/LF-normalized/one-trailing-newline; read BOM-stripped; list excludes '_'-prefixed, ordinal-sorted;
    // delete), assignment load/save/set/remove with merge semantics, corrupt-json tolerance, and name validation.
    private static void TestCheckerRuleStore(string work)
    {
        string g = Path.Combine(work, "store");
        Directory.CreateDirectory(g);

        // WriteRule: CRLF content -> LF-normalized, exactly one trailing newline, UTF-8 WITHOUT BOM.
        CheckerRuleStore.WriteRule(g, "규칙-A", "첫 줄\r\n둘째 줄");
        byte[] aBytes = File.ReadAllBytes(CheckerRuleStore.RulePathFor(g, "규칙-A"));
        Check(!(aBytes.Length >= 3 && aBytes[0] == 0xEF && aBytes[1] == 0xBB && aBytes[2] == 0xBF),
              "S: WriteRule 은 BOM 없이 기록");
        string aText = new UTF8Encoding(false).GetString(aBytes);
        Check(!aText.Contains("\r", StringComparison.Ordinal), "S: WriteRule LF 정규화(CR 없음)");
        Check(aText.EndsWith("둘째 줄\n", StringComparison.Ordinal), "S: WriteRule 말미 개행 1개 보장");

        // ReadRule strips a leading BOM even if the file was saved with one.
        File.WriteAllText(CheckerRuleStore.RulePathFor(g, "규칙-B"), "본문 B\n", new UTF8Encoding(true));
        string? bText = CheckerRuleStore.ReadRule(g, "규칙-B");
        Check(bText != null && !bText.Contains('\uFEFF') && bText.StartsWith("본문 B", StringComparison.Ordinal),
              "S: ReadRule 은 선행 BOM 제거");
        Check(CheckerRuleStore.ReadRule(g, "없는규칙") == null, "S: 없는 규칙 ReadRule → null");

        // ListRules: '_'-prefixed files excluded; assignments file excluded; ordinal-sorted.
        File.WriteAllText(Path.Combine(g, "_TEMPLATE.md"), "tpl\n", new UTF8Encoding(false));
        CheckerRuleStore.SaveAssignment(g, "SOME_CHECKER", "규칙-A");   // also creates _assignments.json
        var rules = CheckerRuleStore.ListRules(g);
        Check(rules.Count == 2 && rules[0] == "규칙-A" && rules[1] == "규칙-B",
              "S: ListRules 는 '_'접두 파일/assignments 제외 + 사전순", string.Join(", ", rules));

        // Assignments: SaveAssignment merges (a second checker keeps the first), LoadAssignments round-trips.
        CheckerRuleStore.SaveAssignment(g, "OTHER_CHECKER", "규칙-B");
        var asg = CheckerRuleStore.LoadAssignments(g);
        Check(asg.Count == 2 && asg["SOME_CHECKER"] == "규칙-A" && asg["OTHER_CHECKER"] == "규칙-B",
              "S: SaveAssignment 병합 + LoadAssignments 왕복", string.Join(", ", asg.Select(kv => kv.Key + "=" + kv.Value)));

        // RemoveAssignment removes just one.
        Check(CheckerRuleStore.RemoveAssignment(g, "SOME_CHECKER"), "S: RemoveAssignment true(존재)");
        Check(!CheckerRuleStore.RemoveAssignment(g, "SOME_CHECKER"), "S: RemoveAssignment false(이미 없음)");
        var asg2 = CheckerRuleStore.LoadAssignments(g);
        Check(asg2.Count == 1 && asg2.ContainsKey("OTHER_CHECKER"), "S: RemoveAssignment 후 나머지 유지");

        // DeleteRule removes the md; ListRules shrinks.
        Check(CheckerRuleStore.DeleteRule(g, "규칙-A"), "S: DeleteRule true(존재)");
        Check(!CheckerRuleStore.RuleExists(g, "규칙-A"), "S: DeleteRule 후 RuleExists false");
        Check(CheckerRuleStore.ListRules(g).Count == 1, "S: DeleteRule 후 목록 축소");

        // corrupt json => empty map, no throw.
        File.WriteAllText(CheckerRuleStore.AssignmentsPath(g), "{ not valid json", new UTF8Encoding(false));
        Check(CheckerRuleStore.LoadAssignments(g).Count == 0, "S: 손상된 assignments json → 빈 맵(예외 아님)");

        // missing assignments file => empty map.
        string g2 = Path.Combine(work, "store_empty");
        Directory.CreateDirectory(g2);
        Check(CheckerRuleStore.LoadAssignments(g2).Count == 0, "S: assignments 파일 부재 → 빈 맵");

        // name validation: reject path separators, invalid chars, '_'-prefix, empty.
        Check(!CheckerRuleStore.IsValidRuleName("_reserved"), "S: '_' 접두 규칙명 거부");
        Check(!CheckerRuleStore.IsValidRuleName("a/b"), "S: 경로 구분자 규칙명 거부");
        Check(!CheckerRuleStore.IsValidRuleName("   "), "S: 공백 규칙명 거부");
        Check(CheckerRuleStore.IsValidRuleName("정상-규칙_x"), "S: 정상 규칙명 허용");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private static Dictionary<string, byte[]> SnapshotBytes(string root)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string p in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            map[Path.GetRelativePath(root, p)] = File.ReadAllBytes(p);
        return map;
    }

    private static bool SnapshotsEqual(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b, out string diff)
    {
        if (a.Count != b.Count) { diff = "file count " + a.Count + " vs " + b.Count; return false; }
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out byte[]? other)) { diff = "missing after: " + kv.Key; return false; }
            if (kv.Value.Length != other.Length) { diff = "length differ: " + kv.Key; return false; }
            for (int i = 0; i < kv.Value.Length; i++)
                if (kv.Value[i] != other[i]) { diff = "byte @" + i + " in " + kv.Key; return false; }
        }
        diff = "";
        return true;
    }

    private static void TestFilesFromScopeFilter(string work)
    {
        string root = Path.Combine(work, "scope-root");
        string src = Path.Combine(root, "src");
        string lib = Path.Combine(root, "lib");
        string dup1 = Path.Combine(root, "dup1");
        string dup2 = Path.Combine(root, "dup2");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(lib);
        Directory.CreateDirectory(dup1);
        Directory.CreateDirectory(dup2);

        string fileA = Path.Combine(src, "FileA.cs");
        string fileB = Path.Combine(lib, "FileB.cs");
        string fileC = Path.Combine(src, "FileC.cs");
        string unique = Path.Combine(src, "Unique.cs");
        string duplicate1 = Path.Combine(dup1, "Duplicate.cs");
        string duplicate2 = Path.Combine(dup2, "Duplicate.cs");
        foreach (string file in new[] { fileA, fileB, fileC, unique, duplicate1, duplicate2 })
        {
            File.WriteAllText(file, "class X {}\n", new UTF8Encoding(false));
        }

        string filesFrom = Path.Combine(work, "scope.csv");
        File.WriteAllText(filesFrom,
            "파일명\n"
            + CsvLine(fileA) + "\n"
            + CsvLine(fileB) + "\n"
            + CsvLine(Path.Combine("src", "FileC.cs")) + "\n"
            + CsvLine(unique) + "\n"
            + CsvLine(duplicate1) + "\n",
            new UTF8Encoding(false));

        string xls = Path.Combine(work, "scope.xls");
        WriteSyntheticXls(xls,
            new[]
            {
                "ID", "체커 키", "위험도", "파일명", "라인", "경로", "체커 설명", "소스 코드",
            },
            new[]
            {
                new[] { "9001", "SCOPE_ABSOLUTE", "보통", "FileA.cs", "10", fileA, "absolute selected", "  10: A();" },
                new[] { "9002", "SCOPE_DIRECTORY", "보통", "FileB.cs", "11", lib, "directory + filename selected", "  11: B();" },
                new[] { "9003", "SCOPE_RELATIVE", "보통", "FileC.cs", "12", "src/FileC.cs", "relative selected", "  12: C();" },
                new[] { "9004", "SCOPE_BASENAME", "보통", "Unique.cs", "13", "", "unique basename selected", "  13: U();" },
                new[] { "9005", "SCOPE_DUPLICATE", "보통", "Duplicate.cs", "14", "", "duplicate basename skipped", "  14: D();" },
                new[] { "9006", "SCOPE_OUTSIDE", "보통", "Outside.cs", "15", Path.Combine(root, "outside", "Outside.cs"), "outside skipped", "  15: O();" },
            });

        string outDir = Path.Combine(work, "scope_out");
        ExportResult result = SparrowExporter.Run(new ExportOptions
        {
            InputPath = xls,
            OutDir = outDir,
            RootPath = root,
            FilesFrom = filesFrom,
        }, TextWriter.Null);
        Check(result.WrittenCount == 4, "F: scope filter keeps only absolute/dir+file/relative/unique basename rows",
              "written=" + result.WrittenCount);
        var scopeDirs = CheckerDirs(outDir);
        Check(scopeDirs.Contains("SCOPE_ABSOLUTE") && scopeDirs.Contains("SCOPE_DIRECTORY")
              && scopeDirs.Contains("SCOPE_RELATIVE") && scopeDirs.Contains("SCOPE_BASENAME"),
              "F: expected scoped checker folders are present", string.Join(", ", scopeDirs));
        Check(!scopeDirs.Contains("SCOPE_DUPLICATE") && !scopeDirs.Contains("SCOPE_OUTSIDE"),
              "F: duplicate basename in full source root and outside rows are excluded");

        string maxOut = Path.Combine(work, "scope_max_out");
        ExportResult maxResult = SparrowExporter.Run(new ExportOptions
        {
            InputPath = xls,
            OutDir = maxOut,
            RootPath = root,
            FilesFrom = filesFrom,
            Max = 2,
        }, TextWriter.Null);
        Check(maxResult.WrittenCount == 2, "F: Max applies after scope filter", "written=" + maxResult.WrittenCount);

        string emptyFilesFrom = Path.Combine(work, "scope-empty.csv");
        File.WriteAllText(emptyFilesFrom, "파일명\n", new UTF8Encoding(false));
        ExportResult emptyResult = SparrowExporter.Run(new ExportOptions
        {
            InputPath = xls,
            OutDir = Path.Combine(work, "scope_empty_out"),
            RootPath = root,
            FilesFrom = emptyFilesFrom,
        }, TextWriter.Null);
        Check(emptyResult.WrittenCount == 0, "F: empty files-from matches zero rows", "written=" + emptyResult.WrittenCount);
    }

    // G. Cross-PC scope: ONE shared xls (paths from PC-A's D:\ checkout) filtered against a teammate's selection
    // taken from a DIFFERENT root (their own checkout). Tier-2 relative-tail matching must make same-named files
    // at different absolute roots match, at a directory boundary, and must emit the [범위 불일치] diagnostic when a
    // non-empty selection matches nothing (the real, empirically-proven failure), plus a [범위 경고] on ambiguity.
    private static void TestCrossPcScopeFilter(string work)
    {
        // Teammate-B's OWN checkout root; the xls carries PC-A's unrelated D:\ paths that DO NOT exist here.
        string root = Path.Combine(work, "g-myproj", "MyApp");
        string viewDir = Path.Combine(root, "View");
        Directory.CreateDirectory(viewDir);
        string fooSel = Path.Combine(viewDir, "Foo.cs");
        string rootFooSel = Path.Combine(root, "Foo.cs");   // a second file whose tail is just "Foo.cs" (ambiguity source)
        foreach (string f in new[] { fooSel, rootFooSel }) File.WriteAllText(f, "class X {}\n", new UTF8Encoding(false));

        // --- G1: cross-drive relative match (THE failing scenario) + directory-boundary correctness ---
        string filesFrom1 = Path.Combine(work, "g1.csv");
        File.WriteAllText(filesFrom1, "파일명\n" + CsvLine(fooSel) + "\n", new UTF8Encoding(false));   // select ONLY View\Foo.cs

        string xls1 = Path.Combine(work, "g1.xls");
        WriteSyntheticXls(xls1,
            new[] { "ID", "체커 키", "위험도", "파일명", "라인", "경로", "체커 설명", "소스 코드" },
            new[]
            {
                // different DRIVE, same relative tail View\Foo.cs -> Tier-2 KEEP
                new[] { "1", "G_CROSS_DRIVE", "보통", "Foo.cs", "10", @"D:\Work\MyApp\branches\rel\MyApp\View\Foo.cs", "cross-drive", "  10: A();" },
                // same basename, WRONG parent dir (OtherView) -> boundary must REJECT
                new[] { "2", "G_WRONG_BOUNDARY", "보통", "Foo.cs", "11", @"D:\Work\MyApp\OtherView\Foo.cs", "false boundary", "  11: B();" },
                // deeper path ending at a real boundary (SubView\View\Foo.cs) -> Tier-2 KEEP
                new[] { "3", "G_DEEP_BOUNDARY", "보통", "Foo.cs", "12", @"D:\Work\MyApp\SubView\View\Foo.cs", "deep boundary", "  12: C();" },
            });

        string out1 = Path.Combine(work, "g1_out");
        ExportResult r1 = SparrowExporter.Run(new ExportOptions
        {
            InputPath = xls1, OutDir = out1, RootPath = root, FilesFrom = filesFrom1,
        }, TextWriter.Null);
        var dirs1 = CheckerDirs(out1);
        Check(r1.WrittenCount == 2, "G1: cross-drive + deep-boundary kept, wrong-boundary dropped", "written=" + r1.WrittenCount);
        Check(dirs1.Contains("G_CROSS_DRIVE"), "G1: cross-drive (D:\\ vs local root, same tail) KEPT via Tier-2");
        Check(dirs1.Contains("G_DEEP_BOUNDARY"), "G1: SubView\\View\\Foo.cs matches View\\Foo.cs at boundary");
        Check(!dirs1.Contains("G_WRONG_BOUNDARY"), "G1: OtherView\\Foo.cs does NOT match View\\Foo.cs (no false boundary)");
        Check(!r1.ScopeMismatch && r1.ScopeDiagnostic == null, "G1: some rows matched -> no [범위 불일치] diagnostic");
        Check(r1.ScopeAmbiguousWarning == null, "G1: single selected tail -> no ambiguity warning");

        // --- G2: total zero-match under a non-empty selection -> [범위 불일치] diagnostic + WrittenCount 0 ---
        string filesFrom2 = Path.Combine(work, "g2.csv");
        File.WriteAllText(filesFrom2, "파일명\n" + CsvLine(fooSel) + "\n", new UTF8Encoding(false));   // select View\Foo.cs

        string xls2 = Path.Combine(work, "g2.xls");
        WriteSyntheticXls(xls2,
            new[] { "ID", "체커 키", "위험도", "파일명", "라인", "경로", "체커 설명", "소스 코드" },
            new[]
            {
                new[] { "1", "G_NOMATCH_A", "보통", "Bar.cs", "10", @"D:\Work\MyApp\Service\Bar.cs", "no tail match", "  10: A();" },
                new[] { "2", "G_NOMATCH_B", "보통", "Baz.cs", "11", @"D:\Work\MyApp\Model\Baz.cs", "no tail match", "  11: B();" },
            });

        string out2 = Path.Combine(work, "g2_out");
        ExportResult r2 = SparrowExporter.Run(new ExportOptions
        {
            InputPath = xls2, OutDir = out2, RootPath = root, FilesFrom = filesFrom2,
        }, TextWriter.Null);
        Check(r2.WrittenCount == 0, "G2: non-empty selection matching no xls row writes 0", "written=" + r2.WrittenCount);
        Check(r2.ScopeMismatch, "G2: ScopeMismatch flag set on total zero-match under a non-empty selection");
        Check(r2.ScopeDiagnostic != null && r2.ScopeDiagnostic!.Contains("[범위 불일치]", StringComparison.Ordinal),
              "G2: [범위 불일치] diagnostic present", r2.ScopeDiagnostic ?? "(null)");
        Check(r2.ScopeDiagnostic != null && r2.ScopeDiagnostic!.Contains("2건 중 0건", StringComparison.Ordinal),
              "G2: diagnostic reports N-of-0 matched (2건 중 0건)");
        Check(r2.ScopeDiagnostic != null && r2.ScopeDiagnostic!.Contains(@"D:\Work\MyApp\Service\Bar.cs", StringComparison.Ordinal),
              "G2: diagnostic includes an example xls 경로");
        Check(r2.ScopeDiagnostic != null && r2.ScopeDiagnostic!.Contains(Path.Combine("View", "Foo.cs"), StringComparison.Ordinal),
              "G2: diagnostic includes an example selected relative tail");

        // --- G2b: legitimate zero-finding selection is NOT flagged as a mismatch (empty selection) ---
        string emptySel = Path.Combine(work, "g2b.csv");
        File.WriteAllText(emptySel, "파일명\n", new UTF8Encoding(false));
        ExportResult r2b = SparrowExporter.Run(new ExportOptions
        {
            InputPath = xls2, OutDir = Path.Combine(work, "g2b_out"), RootPath = root, FilesFrom = emptySel,
        }, TextWriter.Null);
        Check(r2b.WrittenCount == 0 && !r2b.ScopeMismatch && r2b.ScopeDiagnostic == null,
              "G2b: empty selection -> 0 written but NO mismatch diagnostic (not the wrong-root case)");

        // --- G3: ambiguous over-match. Selecting both View\Foo.cs (tail View\Foo.cs) and Foo.cs (tail Foo.cs)
        // makes an xls row D:\...\View\Foo.cs hit BOTH tails -> kept + [범위 경고] warning. ---
        string filesFrom3 = Path.Combine(work, "g3.csv");
        File.WriteAllText(filesFrom3, "파일명\n" + CsvLine(fooSel) + "\n" + CsvLine(rootFooSel) + "\n", new UTF8Encoding(false));

        string xls3 = Path.Combine(work, "g3.xls");
        WriteSyntheticXls(xls3,
            new[] { "ID", "체커 키", "위험도", "파일명", "라인", "경로", "체커 설명", "소스 코드" },
            new[]
            {
                new[] { "1", "G_AMBIGUOUS", "보통", "Foo.cs", "10", @"D:\Work\MyApp\rel\View\Foo.cs", "matches View\\Foo.cs and Foo.cs", "  10: A();" },
            });

        string out3 = Path.Combine(work, "g3_out");
        ExportResult r3 = SparrowExporter.Run(new ExportOptions
        {
            InputPath = xls3, OutDir = out3, RootPath = root, FilesFrom = filesFrom3,
        }, TextWriter.Null);
        Check(r3.WrittenCount == 1, "G3: ambiguous row kept (fail-open for coverage)", "written=" + r3.WrittenCount);
        Check(r3.ScopeAmbiguousWarning != null && r3.ScopeAmbiguousWarning!.Contains("[범위 경고]", StringComparison.Ordinal),
              "G3: [범위 경고] ambiguous over-match warning present", r3.ScopeAmbiguousWarning ?? "(null)");
        Check(!r3.ScopeMismatch, "G3: ambiguous-but-matched -> not a mismatch");
    }

    private static string CsvLine(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    // Minimal BIFF (.xls) writer for the synthetic GetXlsCheckerKeys check.
    private static void WriteSyntheticXls(string path, string[] headers, string[][] rows)
    {
        IWorkbook wb = new HSSFWorkbook();
        ISheet sheet = wb.CreateSheet("issues");
        IRow header = sheet.CreateRow(0);
        for (int c = 0; c < headers.Length; c++) header.CreateCell(c).SetCellValue(headers[c]);
        for (int r = 0; r < rows.Length; r++)
        {
            IRow row = sheet.CreateRow(r + 1);
            for (int c = 0; c < rows[r].Length; c++) row.CreateCell(c).SetCellValue(rows[r][c]);
        }
        using FileStream fs = File.Create(path);
        wb.Write(fs);
    }

    // --- process helper ---
    private static (int exit, string stdout) RunProcess(string exe, string[] argv)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string a in argv) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string outText = p.StandardOutput.ReadToEnd();
        string errText = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, outText + errText);
    }

    // --- comparison helpers ---
    private static bool FilesByteIdentical(string a, string b, out string diff)
    {
        if (!File.Exists(a)) { diff = "missing PS file: " + a; return false; }
        if (!File.Exists(b)) { diff = "missing Core file: " + b; return false; }
        byte[] ba = File.ReadAllBytes(a), bb = File.ReadAllBytes(b);
        if (ba.Length != bb.Length) { diff = "length " + ba.Length + " vs " + bb.Length + " (" + Path.GetFileName(a) + ")"; return false; }
        for (int i = 0; i < ba.Length; i++)
            if (ba[i] != bb[i]) { diff = "byte @" + i + " in " + Path.GetFileName(a); return false; }
        diff = ""; return true;
    }

    // 출력 트리 전체(체커별 폴더 + 그 안의 md) 비교: 상대경로 집합과 각 파일 바이트가 모두 같아야 한다.
    private static bool TreesByteIdentical(string a, string b, out string diff)
    {
        bool aEx = Directory.Exists(a), bEx = Directory.Exists(b);
        if (!aEx && !bEx) { diff = ""; return true; }
        if (!aEx) { diff = "missing console dir: " + a; return false; }
        if (!bEx) { diff = "missing Core dir: " + b; return false; }
        var na = RelativeFiles(a);
        var nb = RelativeFiles(b);
        if (na.Count != nb.Count) { diff = "file count " + na.Count + " vs " + nb.Count; return false; }
        for (int i = 0; i < na.Count; i++)
        {
            if (!string.Equals(na[i], nb[i], StringComparison.Ordinal)) { diff = "path mismatch: " + na[i] + " vs " + nb[i]; return false; }
            if (!FilesByteIdentical(Path.Combine(a, na[i]), Path.Combine(b, nb[i]), out string d)) { diff = d; return false; }
        }
        diff = ""; return true;
    }

    // ---- R. 실행 리포트 출력-트리 가드 -------------------------------------------------------------
    // [XLS 분리] 출력 계약은 "체커 폴더 + 항목 md만, 부산물 0" 이고 XlsSplitRunReport 헤더도 "리포트는 절대 출력
    // 폴더에 안 들어간다"고 못박는다. 그런데 예전 TryWrite 는 호출자를 믿기만 하고 가드가 없었고, 오히려
    // 부모 폴더를 만들어 줬다 → `--out X --report X\r.json`(또는 GUI --log-dir 을 출력 폴더로 지정)이면
    // json + 동반 .log 2개가 출력 트리에 생겨 계약이 조용히 깨졌다. 이제 거부한다.
    // 거부는 예외가 아니라 false + 사유 문자열이다(best-effort: 리포트 실패가 익스포트를 깨지 않는다).
    private static void TestReportOutputTreeGuard(string work)
    {
        string outDir = Path.Combine(work, "r-out");
        Directory.CreateDirectory(outDir);
        var report = new XlsSplitRunReport { OutDir = outDir };

        string inside = Path.Combine(outDir, "run-report.json");
        Check(!XlsSplitReportWriter.TryWrite(inside, report, out string? errInside),
              "R: 출력 폴더 안 리포트 경로는 거부");
        Check(errInside != null && errInside.Contains("report=", StringComparison.Ordinal)
                                && errInside.Contains("out=", StringComparison.Ordinal),
              "R: 거부 사유에 report/out 실제 경로가 담긴다", errInside ?? "(null)");
        Check(!File.Exists(inside) && !File.Exists(XlsSplitReportWriter.CompanionLogPath(inside)),
              "R: 거부 시 json 도 동반 .log 도 만들지 않는다");

        string deep = Path.Combine(outDir, "logs", "run-report.json");
        Check(!XlsSplitReportWriter.TryWrite(deep, report, out _), "R: 출력 폴더 '하위' 경로도 거부");
        Check(!Directory.Exists(Path.Combine(outDir, "logs")),
              "R: 거부 시 출력 폴더 밑에 폴더조차 만들지 않는다");

        Check(!XlsSplitReportWriter.TryWrite(outDir, report, out _), "R: 출력 폴더 경로 자체도 거부");
        Check(Directory.GetFileSystemEntries(outDir).Length == 0,
              "R: 거부 3회 뒤에도 출력 폴더는 완전히 비어 있다(부산물 0)");

        // 과잉 차단 금지: 이름이 출력 폴더로 '시작만' 하는 형제 폴더(r-out-logs)는 정상 기록돼야 한다.
        string sibling = Path.Combine(work, "r-out-logs", "run-report.json");
        Check(XlsSplitReportWriter.TryWrite(sibling, report, out string? errSibling),
              "R: 접두만 같은 형제 폴더는 정상 기록(과잉 차단 아님)", errSibling ?? "");
        Check(File.Exists(sibling) && File.Exists(XlsSplitReportWriter.CompanionLogPath(sibling)),
              "R: 정상 경로에는 json + 동반 .log 를 쓴다");
        Check(Directory.GetFileSystemEntries(outDir).Length == 0,
              "R: 형제 폴더에 기록해도 출력 폴더는 그대로 비어 있다");
    }

    private static List<string> RelativeFiles(string root)
    {
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                        .Select(p => Path.GetRelativePath(root, p))
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .ToList();
    }

    // 체커 키 = 출력 폴더의 최상위 디렉토리명.
    private static HashSet<string> CheckerDirs(string outDir)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(outDir)) return set;
        foreach (string d in Directory.GetDirectories(outDir)) set.Add(Path.GetFileName(d));
        return set;
    }

    // Read UTF-8 (BOM-stripped) text for content assertions.
    private static string ReadText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string t = new UTF8Encoding(false).GetString(bytes);
        if (t.Length > 0 && t[0] == '\uFEFF') t = t.Substring(1);
        return t;
    }

    // Repo root = the folder carrying SKILL.md next to the exporter project (no references/ dependency:
    // the exporter needs no prerequisite documents).
    private static string? FindSkillRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        for (int i = 0; dir != null && i < 12; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SKILL.md")) &&
                File.Exists(Path.Combine(dir.FullName, "tools", "_internal", "SparrowXlsExport", "SparrowXlsExport.csproj")))
                return dir.FullName;
        return null;
    }
}
