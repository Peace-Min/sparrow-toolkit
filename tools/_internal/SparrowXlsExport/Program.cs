// SparrowXlsExport: deterministic CLI that reads a Sparrow (파수 정적분석) result .xls (real BIFF/OLE2,
// or .xlsx) WITHOUT Excel/COM, and splits it into one directory PER 체커 키 holding that checker's per-item
// markdown — <OutDir>\<체커 키>\{ID}_{파일명}_{라인}.md, and nothing else (no index/summary byproducts).
// Purpose: take all xls/xlsx parsing out of a weak local LLM's hands in an air-gapped environment.
//
// This is a THIN CLI wrapper. All parsing/output logic lives in SparrowXlsExport.Core.SparrowExporter,
// which the WPF GUI also calls in-process. This file only parses args + maps exceptions to exit codes;
// the stdout summary is produced by Core writing to Console.Out, so it stays byte-identical.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SparrowXlsExport.Core;

namespace SparrowXlsExport
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* stdout may be redirected */ }

            string? input = null, outDir = null, checker = null, status = null, rootPath = null, filesFrom = null, guides = null;
            string? report = null;
            var severities = new HashSet<string>(StringComparer.Ordinal);
            int? max = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--out": if (!TryNext(args, ref i, out outDir)) return Usage("--out requires a value"); break;
                    case "--root": if (!TryNext(args, ref i, out rootPath)) return Usage("--root requires a value"); break;
                    case "--files-from": if (!TryNext(args, ref i, out filesFrom)) return Usage("--files-from requires a value"); break;
                    case "--guides": if (!TryNext(args, ref i, out guides)) return Usage("--guides requires a value"); break;
                    case "--report": if (!TryNext(args, ref i, out report)) return Usage("--report requires a value"); break;
                    case "--checker": if (!TryNext(args, ref i, out checker)) return Usage("--checker requires a value"); break;
                    case "--status": if (!TryNext(args, ref i, out status)) return Usage("--status requires a value"); break;
                    case "--severity":
                        if (!TryNext(args, ref i, out string sevArg)) return Usage("--severity requires a value");
                        foreach (string s in sevArg.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0)) severities.Add(s);
                        break;
                    case "--max":
                        if (!TryNext(args, ref i, out string maxArg)) return Usage("--max requires a value");
                        if (!int.TryParse(maxArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mv)) return Usage("--max must be an integer");
                        max = mv;
                        break;
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal)) return Usage("unknown option: " + a);
                        if (input == null) input = a; else return Usage("unexpected argument: " + a);
                        break;
                }
            }

            if (input == null) return Usage("input file is required");

            try
            {
                var opts = new ExportOptions
                {
                    InputPath = input,
                    OutDir = outDir,
                    Checker = checker,
                    Status = status,
                    RootPath = rootPath,
                    FilesFrom = filesFrom,
                    Severities = severities,
                    Max = max,
                };
                DateTime startedUtc = DateTime.UtcNow;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ExportResult result = SparrowExporter.Run(opts, Console.Out);   // Core writes the identical stdout summary.

                // Scope diagnostics go to STDERR so they never perturb the byte-compared stdout summary. Total
                // zero-match under a non-empty selection is NOT a crash (exit stays 0) but must be loud, not silent.
                if (result.ScopeDiagnostic != null)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine(result.ScopeDiagnostic);
                }
                if (result.ScopeAmbiguousWarning != null)
                {
                    Console.Error.WriteLine(result.ScopeAmbiguousWarning);
                }

                // Optional checker->rule attachment. WITHOUT --guides the run is byte-identical to before (the
                // exporter output is untouched). WITH --guides, the guides folder's _assignments.json is read and
                // only EXPLICITLY assigned checkers (체커 키 -> 규칙 이름) get their rule embedded self-contained into
                // each item md; there is NO name-based auto-mapping. A summary is printed to stdout. The exporter's
                // own stdout summary is emitted first and stays byte-stable; this appends after it and only ever
                // appears when --guides is supplied.
                MapResult? mapResult = null;
                string? guidesFull = guides != null ? Path.GetFullPath(guides.Trim().Trim('"')) : null;
                if (guides != null)
                {
                    MapResult map = CheckerRuleMapper.Apply(result.OutputDir, guidesFull!);
                    mapResult = map;
                    Console.Out.WriteLine();
                    Console.Out.WriteLine("mapped checkers:   " + map.Mapped.Count.ToString(CultureInfo.InvariantCulture));
                    Console.Out.WriteLine("unmapped checkers: " + map.Unmapped.Count.ToString(CultureInfo.InvariantCulture));
                    Console.Out.WriteLine("items touched:     " + map.ItemsTouched.ToString(CultureInfo.InvariantCulture));
                    if (map.Unmapped.Count > 0)
                    {
                        Console.Out.WriteLine("unmapped keys: " + string.Join(", ", map.Unmapped));
                    }
                }

                // Optional run report (machine-readable evidence of THIS run). Written ONLY to the explicit --report
                // path, never into the output tree, so the exporter's "체커 폴더 + 항목 md만" contract is untouched and a
                // run without --report is byte-identical to before. A failed report write is a warning, not a failure.
                if (report != null)
                {
                    sw.Stop();
                    TrackCRunReport payload = TrackCReportWriter.Build(opts, result, mapResult, guidesFull,
                                                                      startedUtc, sw.ElapsedMilliseconds);
                    string reportFull = Path.GetFullPath(report.Trim().Trim('"'));
                    if (TrackCReportWriter.TryWrite(reportFull, payload, out string? reportError))
                    {
                        Console.Out.WriteLine();
                        Console.Out.WriteLine("run report:        " + reportFull);
                        Console.Out.WriteLine("run report (log):  " + TrackCReportWriter.CompanionLogPath(reportFull));
                    }
                    else
                    {
                        Console.Error.WriteLine("warning: run report 기록 실패: " + reportError);
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;   // runtime error: file unreadable / not a workbook / IO failure
            }
        }

        private static bool TryNext(string[] args, ref int i, out string value)
        {
            if (i + 1 >= args.Length) { value = ""; return false; }
            value = args[++i];
            return true;
        }

        private static int Usage(string message)
        {
            Console.Error.WriteLine("error: " + message);
            Console.Error.WriteLine("usage: SparrowXlsExport <input.xls> [--out DIR] [--root SRC_ROOT] [--files-from FILES.csv] [--severity 낮음,보통,높음] [--checker SUBSTR] [--status SUBSTR] [--max N] [--guides GUIDES_DIR] [--report REPORT.json]");
            Console.Error.WriteLine("output: DIR\\<체커 키>\\{ID}_{파일명}_{라인}.md  (체커별 폴더 + 항목 md만 생성)");
            Console.Error.WriteLine("--report: 있으면 이 실행의 진단 리포트(json + 같은 이름 .log 요약)를 그 경로에 기록. 출력 폴더는 건드리지 않으므로 없을 때와 산출물 바이트 동일");
            Console.Error.WriteLine("--guides: 있으면 GUIDES_DIR\\_assignments.json 에 지정된(체커키->규칙이름) 체커만 GUIDES_DIR\\<규칙이름>.md 규칙을 각 항목 md에 self-contained 부착. 지정 없으면 순수 유지(이름 자동매핑 없음)");
            return 2;
        }
    }
}
