// GuiUiaFixture: writes a tiny real BIFF (.xls, HSSFWorkbook) fixture for the GUI UIA harness
// (tests\gui-uia-tests.ps1). Deterministic: sheet 'issues', the well-known Sparrow columns, and THIRTEEN data
// rows across THREE checker keys.
//
// WHY IT LOOKS LIKE THIS (실 규모 모사): the first version of this fixture had 4 rows in 2 shallow folders
// (src/core, src/ui). It passed everything — and hid three defects that only showed up on a REAL Sparrow xls
// (247건 · 83파일 · 35디렉토리): a 6-level single-child common prefix that buried the real structure, truncated
// node names with a horizontal scrollbar, and TreeItem UIA names falling back to the class name. So the shape
// here now mirrors the real thing:
//   * 모든 경로가 깊은 공통 접두를 공유한다 (D:\Work\Proj\branches\Proj\release\2026-01-01\)
//     -> the 범위 트리 must FOLD that chain away and start at the real branch point.
//   * 그 아래 분기 폴더 3개 (ModuleA · ModuleB · 공통모듈), 총 디렉토리 9개, 파일 13개
//   * 한글 폴더 2개 (공통모듈 · 한글폴더) + 30자 넘는 긴 파일명 2개
//     -> UIA 이름/말줄임/ToolTip 이 실제 이름으로 검증된다.
//
// Checker roles (unchanged contract for the harness):
//   EMPTY_CATCH_BLOCK  x5   -- 미매핑 checkers 중 ordinal 최소 = 패널 첫 행. 규칙을 여기에 지정한다(다건 부착 증명).
//   FORWARD_NULL       x4   -- 규칙 없음. 끝까지 순수해야 한다.
//   RESOURCE_LEAK      x4   -- 라이브러리에 이 키와 "이름이 같은" 규칙이 있지만 지정하지 않는다(자동매핑 없음 증명).
//
// 범위(팀 분담) 검증용 배치: ModuleA\core 에는 EMPTY_CATCH_BLOCK 3건만 있고, 다른 두 체커는 그 폴더 밖에 있다.
// 그래서 'core' 폴더 하나만 체크해 실행하면 EMPTY_CATCH_BLOCK 3건만 나와야 한다(다른 폴더의 EMPTY_CATCH_BLOCK
// 2건까지 빠지므로 "체커 필터"가 아니라 "경로 필터"임이 증명된다). Never committed; generated fresh each test run.

using System;
using System.IO;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;

namespace GuiUiaFixture
{
    internal static class Program
    {
        private static readonly string[] Headers =
        {
            "ID", "체커 키", "체커명", "위험도", "파일명", "라인", "이슈 상태", "체커 설명", "소스 코드", "경로",
        };

        // 모든 검출 경로가 공유하는 접두(자식이 하나뿐인 6단계 체인) — 트리는 이걸 접고 ModuleA/ModuleB/공통모듈 부터 시작해야 한다.
        private const string Prefix = @"D:\Work\Proj\branches\Proj\release\2026-01-01\";

        private const string DescEmpty = "빈 예외 처리 블록 체커는 예외를 처리하는 코드 내용이 없는 예외 처리 블록을 검출합니다.";
        private const string DescNull = "널 값 역참조 체커는 널 상수나 널이 할당된 변수를 역참조하는 경우를 검출합니다.";
        private const string DescLeak = "리소스 누수 체커는 파일, 소켓 등 리소스를 할당한 후에 해제하지 않는 코드를 검출합니다.";

        private const string SrcEmpty = "            catch { }";
        private const string SrcNull = "            return node.Value;";
        private const string SrcLeak = "            var fs = new FileStream(path, FileMode.Open);";

        private sealed class Row
        {
            public int Id;
            public string Key = "";
            public string Name = "";
            public string Severity = "";
            public string File = "";
            public int Line;
            public string Status = "";
            public string Desc = "";
            public string Source = "";
            public string Dir = "";     // 접두 아래의 폴더 경로(경로 셀 = Prefix + Dir + File)
        }

        private static readonly Row[] Rows =
        {
            // ---- 분기 1: ModuleA ----
            // ModuleA\core = 범위 필터 검증용 "선택 폴더". EMPTY_CATCH_BLOCK 만 3건.
            new Row {
                Id = 7001, Key = "EMPTY_CATCH_BLOCK", Name = "빈 catch 블록", Severity = "높음",
                File = "Alpha.cs", Line = 13, Status = "미해결", Desc = DescEmpty, Source = SrcEmpty,
                Dir = @"ModuleA\core",
            },
            new Row {
                Id = 7002, Key = "EMPTY_CATCH_BLOCK", Name = "빈 catch 블록", Severity = "높음",
                File = "Beta.cs", Line = 27, Status = "미해결", Desc = DescEmpty, Source = SrcEmpty,
                Dir = @"ModuleA\core",
            },
            // 긴 파일명(34자) — 좁은 패널에서 잘리던 실제 이름 형태.
            new Row {
                Id = 7003, Key = "EMPTY_CATCH_BLOCK", Name = "빈 catch 블록", Severity = "높음",
                File = "WndLongSampleAnalyzeControlView.cs", Line = 118, Status = "미해결", Desc = DescEmpty, Source = SrcEmpty,
                Dir = @"ModuleA\core",
            },
            new Row {
                Id = 7004, Key = "FORWARD_NULL", Name = "널 역참조", Severity = "매우위험",
                File = "Gamma.cs", Line = 11, Status = "미해결", Desc = DescNull, Source = SrcNull,
                Dir = @"ModuleA\ui",
            },
            new Row {
                Id = 7005, Key = "RESOURCE_LEAK", Name = "자원 누수", Severity = "매우위험",
                File = "Delta.cs", Line = 9, Status = "미해결", Desc = DescLeak, Source = SrcLeak,
                Dir = @"ModuleA\ui",
            },

            // ---- 분기 2: ModuleB ----
            new Row {
                Id = 7006, Key = "FORWARD_NULL", Name = "널 역참조", Severity = "매우위험",
                File = "Epsilon.cs", Line = 23, Status = "미해결", Desc = DescNull, Source = SrcNull,
                Dir = @"ModuleB\src",
            },
            new Row {
                Id = 7007, Key = "RESOURCE_LEAK", Name = "자원 누수", Severity = "매우위험",
                File = "Zeta.cs", Line = 41, Status = "미해결", Desc = DescLeak, Source = SrcLeak,
                Dir = @"ModuleB\src",
            },
            // 긴 파일명(33자) 2번째.
            new Row {
                Id = 7008, Key = "FORWARD_NULL", Name = "널 역참조", Severity = "매우위험",
                File = "SampleDrawObjectRendererView.cs", Line = 76, Status = "미해결", Desc = DescNull, Source = SrcNull,
                Dir = @"ModuleB\src",
            },
            // 선택하지 않을 폴더에도 EMPTY_CATCH_BLOCK 이 있다 = 범위 필터가 "체커"가 아니라 "경로"로 걸러야 한다.
            new Row {
                Id = 7009, Key = "EMPTY_CATCH_BLOCK", Name = "빈 catch 블록", Severity = "높음",
                File = "Eta.cpp", Line = 55, Status = "미해결", Desc = DescEmpty, Source = SrcEmpty,
                Dir = @"ModuleB\test",
            },

            // ---- 분기 3: 공통모듈 (한글 폴더) ----
            new Row {
                Id = 7010, Key = "RESOURCE_LEAK", Name = "자원 누수", Severity = "매우위험",
                File = "Theta.c", Line = 17, Status = "미해결", Desc = DescLeak, Source = SrcLeak,
                Dir = @"공통모듈\util",
            },
            new Row {
                Id = 7011, Key = "FORWARD_NULL", Name = "널 역참조", Severity = "매우위험",
                File = "Iota.c", Line = 31, Status = "미해결", Desc = DescNull, Source = SrcNull,
                Dir = @"공통모듈\util",
            },
            new Row {
                Id = 7012, Key = "EMPTY_CATCH_BLOCK", Name = "빈 catch 블록", Severity = "높음",
                File = "Kappa.cpp", Line = 64, Status = "미해결", Desc = DescEmpty, Source = SrcEmpty,
                Dir = @"공통모듈\한글폴더",
            },
            new Row {
                Id = 7013, Key = "RESOURCE_LEAK", Name = "자원 누수", Severity = "매우위험",
                File = "Lambda.cpp", Line = 88, Status = "미해결", Desc = DescLeak, Source = SrcLeak,
                Dir = @"공통모듈\한글폴더",
            },
        };

        private static int Main(string[] args)
        {
            if (args.Length < 1) { Console.Error.WriteLine("usage: GuiUiaFixture <out.xls>"); return 2; }
            string outPath = args[0];

            IWorkbook wb = new HSSFWorkbook();
            ISheet sheet = wb.CreateSheet("issues");

            IRow header = sheet.CreateRow(0);
            for (int c = 0; c < Headers.Length; c++) header.CreateCell(c).SetCellValue(Headers[c]);

            int rowIdx = 1;
            foreach (Row d in Rows)
            {
                IRow row = sheet.CreateRow(rowIdx++);
                row.CreateCell(0).SetCellValue((double)d.Id);    // ID numeric -> renders without ".0"
                row.CreateCell(1).SetCellValue(d.Key);
                row.CreateCell(2).SetCellValue(d.Name);
                row.CreateCell(3).SetCellValue(d.Severity);
                row.CreateCell(4).SetCellValue(d.File);
                row.CreateCell(5).SetCellValue((double)d.Line);  // 라인 numeric
                row.CreateCell(6).SetCellValue(d.Status);
                row.CreateCell(7).SetCellValue(d.Desc);
                row.CreateCell(8).SetCellValue(d.Source);
                row.CreateCell(9).SetCellValue(Prefix + d.Dir + "\\" + d.File);   // 경로 = full path (dir+file)
            }

            string? dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (FileStream fs = File.Create(outPath)) wb.Write(fs);
            Console.WriteLine("wrote " + Path.GetFullPath(outPath));
            return 0;
        }
    }
}
