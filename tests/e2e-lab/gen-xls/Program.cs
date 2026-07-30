// GenXls: emits Sparrow-style .xls workbooks (real BIFF via NPOI HSSF) for the E2E + G2 gate tests.
//
// Default (usage: GenXls <outDir>):
//   sample-before.xls  = all 5 planted defects (미해결)
//   sample-after.xls   = post-fix rescan: only the OVERLY_BROAD_CATCH row remains (보류 — not fixed yet);
//                        the 4 수정 checkers' rows are removed. No NEW (checker,file,line).
//
// --scenarios (usage: GenXls <outDir> --scenarios) additionally emits three before/after pairs that
// exercise the count-based / full-path / scan-hygiene semantics of Compare-Sparrow.ps1:
//   lineshift-{before,after}.xls : same file has 2 findings of one checker; after = one resolved, the
//                                  other's LINE shifted (+3). Counts only decrease -> gate must PASS
//                                  (a line-exact key would misreport the shifted row as new).
//   regress-{before,after}.xls   : after has a genuine (checker, full-path) count increase -> FAIL.
//   scope-{before,after}.xls     : after is missing a whole path present in before (scan-scope change,
//                                  no count increase) -> WARNING only by default, FAIL with -StrictScope.
//
// Sheet name 'issues'. Header + the well-known Sparrow columns the parser keys on:
//   ID, 체커 키, 체커명, 위험도, 파일명, 라인, 이슈 상태, 체커 설명, 소스 코드, 경로
// ('경로' = full path dir+file, as in real Sparrow xls; drives full-path identity in the G2 gate.)
// Values (체커 키/체커명/위험도/체커 설명) are verbatim from real Sparrow findings.

using System;
using System.IO;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;

namespace GenXls
{
    internal sealed class Defect
    {
        public int Id;
        public string CheckerKey = "";
        public string CheckerName = "";
        public string Severity = "";
        public string File = "";
        public int Line;
        public string Status = "";
        public string Desc = "";
        public string Source = "";
        public string Dir = "src/SampleApp";   // '경로' = Dir + "/" + File (real Sparrow xls carries a full path)
        public bool InAfter;   // true = still present after the fix rescan (보류 item, not fixed yet)

        public string FullPath { get { return Dir.Length > 0 ? Dir + "/" + File : File; } }
    }

    internal static class Program
    {
        private static readonly string[] Headers =
        {
            "ID", "체커 키", "체커명", "위험도", "파일명", "라인", "이슈 상태", "체커 설명", "소스 코드", "경로",
        };

        private static readonly Defect[] Defects =
        {
            new Defect {
                Id = 9001, CheckerKey = "FORWARD_NULL", CheckerName = "널 역참조", Severity = "매우위험",
                File = "NullDeref.cs", Line = 11, Status = "미해결",
                Desc = "널 값 역참조 체커는 널 상수나 널이 할당된 변수를 역참조하는 경우를 검출합니다.",
                Source = "            return node.Value;", InAfter = false,
            },
            new Defect {
                Id = 9002, CheckerKey = "RESOURCE_LEAK", CheckerName = "자원 누수", Severity = "매우위험",
                File = "LeakFile.cs", Line = 9, Status = "미해결",
                Desc = "리소스 누수 체커는 파일, 소켓 등 리소스를 할당한 후에 해제하지 않는 코드를 검출합니다.",
                Source = "            var fs = new FileStream(path, FileMode.Open);", InAfter = false,
            },
            new Defect {
                Id = 9003, CheckerKey = "EMPTY_CATCH_BLOCK", CheckerName = "빈 catch 블록", Severity = "높음",
                File = "SwallowEx.cs", Line = 13, Status = "미해결",
                Desc = "빈 예외 처리 블록 체커는 예외를 처리하는 코드 내용이 없는 예외 처리 블록을 검출합니다.",
                Source = "            catch { }", InAfter = false,
            },
            new Defect {
                Id = 9004, CheckerKey = "OVERLY_BROAD_CATCH", CheckerName = "지나치게 일반적인 예외 처리", Severity = "보통",
                File = "BroadCatch.cs", Line = 13, Status = "미해결",
                Desc = "지나치게 일반적인 예외 처리 체커는 너무 다양한 예외를 포괄적으로 처리하는 코드를 검출합니다.",
                Source = "            catch (Exception ex)", InAfter = true,
            },
            new Defect {
                Id = 9005, CheckerKey = "NULL_RETURN_STD", CheckerName = "표준 라이브러리의 널 반환 값 역참조", Severity = "매우위험",
                File = "BclNull.cs", Line = 10, Status = "미해결",
                Desc = "표준 라이브러리 널 반환 값 역참조 체커는 C# 표준 라이브러리 메소드 중에서 널을 반환할 가능성이 있는 메소드의 반환 값을 확인 없이 역참조하는 경우를 검출합니다.",
                Source = "            return Activator.CreateInstance(t);", InAfter = false,
            },
        };

        // --scenarios raw material. One checker reused verbatim (FORWARD_NULL) — scenario xls only need
        // parser-recognizable rows; the G2 gate keys on (체커 키, 경로) counts.
        private static Defect Mk(int id, string file, int line, string dir)
        {
            return new Defect
            {
                Id = id, CheckerKey = "FORWARD_NULL", CheckerName = "널 역참조", Severity = "매우위험",
                File = file, Line = line, Status = "미해결", Dir = dir,
                Desc = "널 값 역참조 체커는 널 상수나 널이 할당된 변수를 역참조하는 경우를 검출합니다.",
                Source = "            return node.Value;",
            };
        }

        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("usage: GenXls <outDir> [--scenarios]");
                return 2;
            }
            string outDir = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(outDir);
            bool scenarios = Array.IndexOf(args, "--scenarios") >= 0;

            string beforePath = Path.Combine(outDir, "sample-before.xls");
            string afterPath = Path.Combine(outDir, "sample-after.xls");

            WriteWorkbook(beforePath, Filter(Defects, onlyAfter: false));
            WriteWorkbook(afterPath, Filter(Defects, onlyAfter: true));
            Console.WriteLine("wrote " + beforePath);
            Console.WriteLine("wrote " + afterPath);

            if (scenarios)
            {
                // 1) line shift: same (checker, path); before = 2 findings (lines 10, 40);
                //    after = line-10 finding resolved, line-40 finding shifted to 43. Count 2 -> 1: PASS.
                WriteScenario(outDir, "lineshift",
                    before: new[] { Mk(8101, "Shifty.cs", 10, "src/App"), Mk(8102, "Shifty.cs", 40, "src/App") },
                    after: new[] { Mk(8102, "Shifty.cs", 43, "src/App") });

                // 2) true regression: after gains a genuinely new (checker, path) finding. 0 -> 1: FAIL.
                WriteScenario(outDir, "regress",
                    before: new[] { Mk(8201, "Stable.cs", 10, "src/App") },
                    after: new[] { Mk(8201, "Stable.cs", 10, "src/App"), Mk(8202, "Fresh.cs", 5, "src/App") });

                // 3) scope mismatch: a whole path present in before is absent in after (scan-scope change),
                //    counts never increase. Default: WARNING only (PASS); -StrictScope: FAIL.
                WriteScenario(outDir, "scope",
                    before: new[] { Mk(8301, "Kept.cs", 10, "src/App"), Mk(8302, "Dropped.cs", 20, "src/Other") },
                    after: new[] { Mk(8301, "Kept.cs", 10, "src/App") });
            }
            return 0;
        }

        private static Defect[] Filter(Defect[] all, bool onlyAfter)
        {
            if (!onlyAfter) return all;
            var keep = new System.Collections.Generic.List<Defect>();
            foreach (Defect d in all) if (d.InAfter) keep.Add(d);
            return keep.ToArray();
        }

        private static void WriteScenario(string outDir, string name, Defect[] before, Defect[] after)
        {
            string b = Path.Combine(outDir, name + "-before.xls");
            string a = Path.Combine(outDir, name + "-after.xls");
            WriteWorkbook(b, before);
            WriteWorkbook(a, after);
            Console.WriteLine("wrote " + b);
            Console.WriteLine("wrote " + a);
        }

        private static void WriteWorkbook(string path, Defect[] rows)
        {
            IWorkbook wb = new HSSFWorkbook();
            ISheet sheet = wb.CreateSheet("issues");

            IRow header = sheet.CreateRow(0);
            for (int c = 0; c < Headers.Length; c++) header.CreateCell(c).SetCellValue(Headers[c]);

            int rowIdx = 1;
            foreach (Defect d in rows)
            {
                IRow row = sheet.CreateRow(rowIdx++);
                row.CreateCell(0).SetCellValue((double)d.Id);     // ID numeric -> renders without ".0"
                row.CreateCell(1).SetCellValue(d.CheckerKey);
                row.CreateCell(2).SetCellValue(d.CheckerName);
                row.CreateCell(3).SetCellValue(d.Severity);
                row.CreateCell(4).SetCellValue(d.File);
                row.CreateCell(5).SetCellValue((double)d.Line);   // 라인 numeric
                row.CreateCell(6).SetCellValue(d.Status);
                row.CreateCell(7).SetCellValue(d.Desc);
                row.CreateCell(8).SetCellValue(d.Source);
                row.CreateCell(9).SetCellValue(d.FullPath);       // 경로 = full path (dir+file)
            }

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (FileStream fs = File.Create(path)) wb.Write(fs);
        }
    }
}
