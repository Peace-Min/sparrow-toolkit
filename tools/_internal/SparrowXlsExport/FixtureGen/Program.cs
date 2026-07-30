// FixtureGen: writes a tiny real BIFF (.xls, HSSFWorkbook) fixture for the SparrowXlsExport E2E test.
// Deterministic: sheet 'issues', the exact 20-column Sparrow header, 4 data rows (IDs 101..104) that
// exercise: (a) a normal row, (b) newline + '|' + comma + quote in a value and multi-line 소스 코드,
// (c) a different 위험도 (높음) + different 체커 키 + a comma in 체커명 (CSV quoting), (d) empty 함수/레퍼런스.
// Never committed; generated fresh each test run.

using System;
using System.IO;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;

namespace FixtureGen
{
    internal static class Program
    {
        private static readonly string[] Headers =
        {
            "ID", "유형", "위험도", "언어", "레퍼런스", "체커 타입", "체커 키", "체커명", "라인", "파일명",
            "함수", "경로", "A.S", "유사 이슈 그룹", "이슈 상태", "이슈 담당자", "검출 시간", "체커 설명", "이슈 의견", "소스 코드",
        };

        private const string K1 = "MISSING_BLANK_LINE_BEFORE_COMMENT";
        private const string K2 = "PRACTICE.OBJECT_INSTANTIATION.NOT_USED_IMPLICITLY_TYPE";
        private const string DescK1 = "주석 앞에는 빈 줄이 있어야 합니다.";

        private static int Main(string[] args)
        {
            if (args.Length < 1) { Console.Error.WriteLine("usage: FixtureGen <out.xls>"); return 2; }
            string outPath = args[0];

            IWorkbook wb = new HSSFWorkbook();
            ISheet sheet = wb.CreateSheet("issues");

            IRow header = sheet.CreateRow(0);
            for (int c = 0; c < Headers.Length; c++) header.CreateCell(c).SetCellValue(Headers[c]);

            // (a) normal row
            SetRow(sheet, 1, id: 101, type: "코딩 규칙", severity: "낮음", lang: "C++", reference: "MISRA",
                checkerType: "STYLE", checkerKey: K1, checkerName: "주석 앞 빈 줄 누락", line: 42, file: "main.cpp",
                func: "main", path: "src/main.cpp", asv: "N", group: "G1", statusVal: "미확인", owner: "hong",
                detected: "2026-07-01", desc: DescK1, comment: "확인 필요",
                source: "  41: int main() {\n  42:   // comment\n  43: }");

            // (b) tricky value: pipe + newline + comma + quote in 이슈 의견; multi-line 소스 코드
            SetRow(sheet, 2, id: 102, type: "코딩 규칙", severity: "낮음", lang: "C++", reference: "MISRA",
                checkerType: "STYLE", checkerKey: K1, checkerName: "주석 앞 빈 줄 누락", line: 7, file: "util.cpp",
                func: "f", path: "src/util.cpp", asv: "N", group: "G1", statusVal: "확인", owner: "kim",
                detected: "2026-07-02", desc: DescK1, comment: "a|b\nc,d\"e",
                source: "   6: void f() {\n   7:   int x=0; // x\n   8: }");

            // (c) 높음 + different 체커 키 + comma in 체커명
            SetRow(sheet, 3, id: 103, type: "코딩 실무", severity: "높음", lang: "C#", reference: "",
                checkerType: "PRACTICE", checkerKey: K2, checkerName: "사용되지 않는 객체, 암시적 타입", line: 15, file: "Service.cs",
                func: "Configure", path: "src/Service.cs", asv: "Y", group: "G2", statusVal: "확인", owner: "lee",
                detected: "2026-07-03", desc: "객체가 생성되었으나 사용되지 않습니다.", comment: "",
                source: "  15: var svc = new Service();");

            // (d) empty 함수/레퍼런스, 보통
            SetRow(sheet, 4, id: 104, type: "코딩 규칙", severity: "보통", lang: "C", reference: "",
                checkerType: "STYLE", checkerKey: K1, checkerName: "주석 앞 빈 줄 누락", line: 99, file: "legacy.c",
                func: "", path: "src/legacy.c", asv: "N", group: "G3", statusVal: "미확인", owner: "",
                detected: "2026-07-04", desc: DescK1, comment: "",
                source: "  99: /* legacy */");

            string? dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (FileStream fs = File.Create(outPath)) wb.Write(fs);
            Console.WriteLine("wrote " + Path.GetFullPath(outPath));
            return 0;
        }

        private static void SetRow(ISheet sheet, int rowIdx, int id, string type, string severity, string lang,
            string reference, string checkerType, string checkerKey, string checkerName, int line, string file,
            string func, string path, string asv, string group, string statusVal, string owner, string detected,
            string desc, string comment, string source)
        {
            IRow row = sheet.CreateRow(rowIdx);
            row.CreateCell(0).SetCellValue(id);        // numeric -> must render without ".0"
            row.CreateCell(1).SetCellValue(type);
            row.CreateCell(2).SetCellValue(severity);
            row.CreateCell(3).SetCellValue(lang);
            row.CreateCell(4).SetCellValue(reference);
            row.CreateCell(5).SetCellValue(checkerType);
            row.CreateCell(6).SetCellValue(checkerKey);
            row.CreateCell(7).SetCellValue(checkerName);
            row.CreateCell(8).SetCellValue(line);      // numeric
            row.CreateCell(9).SetCellValue(file);
            row.CreateCell(10).SetCellValue(func);
            row.CreateCell(11).SetCellValue(path);
            row.CreateCell(12).SetCellValue(asv);
            row.CreateCell(13).SetCellValue(group);
            row.CreateCell(14).SetCellValue(statusVal);
            row.CreateCell(15).SetCellValue(owner);
            row.CreateCell(16).SetCellValue(detected);
            row.CreateCell(17).SetCellValue(desc);
            row.CreateCell(18).SetCellValue(comment);
            row.CreateCell(19).SetCellValue(source);
        }
    }
}
