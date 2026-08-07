// SparrowXlsExport.Core: deterministic parsing library that reads a Sparrow (파수 정적분석) result .xls
// (real BIFF/OLE2, or .xlsx) WITHOUT Excel/COM, and splits it into per-checker directories of per-item
// markdown. Shared by the console tool (thin CLI wrapper) and the WPF GUI (in-process).
//
// Output layout (the ONLY thing written — no index/summary/README byproducts):
//   <OutDir>/<체커 키>/{ID}_{파일명}_{라인}.md
//
// Design points baked in:
//  - GENERIC header mapping: whatever headers exist become table columns; a fixed set of Sparrow columns
//    is treated as WELL-KNOWN only for directory/file naming, filters and the summary (ID / 체커 키 /
//    위험도 / 파일명 / 라인 / 이슈 상태 / 체커 설명 / 소스 코드 / 경로).
//  - deterministic: sheet order preserved; filters AND-combined; --max caps the written set.
//  - encodings: md is UTF-8 WITHOUT BOM, LF line endings.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NPOI.SS.UserModel;

namespace SparrowXlsExport.Core
{
    /// <summary>Inputs for a single export run. Mirrors the console CLI options.</summary>
    public sealed class ExportOptions
    {
        /// <summary>Path to the input .xls/.xlsx (required).</summary>
        public string InputPath = "";

        /// <summary>Output directory; null =&gt; &lt;input dir&gt;\&lt;name&gt;.items next to the input.</summary>
        public string? OutDir;

        /// <summary>Case-insensitive substring filter on 체커 키; null =&gt; no checker filter.</summary>
        public string? Checker;

        /// <summary>Case-insensitive substring filter on 이슈 상태; null =&gt; no status filter.</summary>
        public string? Status;

        /// <summary>Exact-match severity set (AND-combined with the other filters). Empty =&gt; no severity filter.</summary>
        public ISet<string> Severities = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Cap on the number of written items; null =&gt; no cap.</summary>
        public int? Max;

        /// <summary>Source root used to resolve relative XLS paths and relative files-from entries.</summary>
        public string? RootPath;

        /// <summary>CSV/newline list of selected source files. When set, rows outside this file set are skipped.
        /// Two selection models are supported: a LOCAL source selection (paths from a checkout, pass
        /// <see cref="RootPath"/> too — Tier 1-3 matching incl. cross-PC relative tails), or an XLS-DERIVED
        /// selection (the paths <see cref="SparrowExporter.ListPaths"/> reported for this very xls, RootPath left
        /// empty — exact verbatim matching, any language).</summary>
        public string? FilesFrom;
    }

    /// <summary>Structured result of a successful export (also written as the human summary to the log).</summary>
    public sealed class ExportResult
    {
        public string InputPath = "";
        public string OutputDir = "";
        public string SheetName = "";
        public int SheetIndex;
        public string SheetPick = "";
        public int Columns;
        public int TotalDataRows;
        public int MatchedCount;
        public int WrittenCount;
        public int UniqueCheckers;
        public IReadOnlyList<(string Sev, int Count)> SeverityCounts = Array.Empty<(string, int)>();
        public int MergedRegions;

        /// <summary>Per-checker written-item counts (체커 키 -&gt; number of md written), ordinal by key. Diagnostic
        /// only (fed to the [XLS 분리] run report); computed in-memory and NEVER changes what is written to disk.</summary>
        public IReadOnlyList<(string Key, int Count)> CheckerCounts = Array.Empty<(string, int)>();

        /// <summary>
        /// True when scope filtering (FilesFrom) was active with a non-empty selection over a non-empty xls,
        /// yet NOTHING matched (Tier-1 absolute AND Tier-2 relative-tail both failed for every row). This is the
        /// tell-tale "wrong project / different checkout path structure" situation — distinct from a legitimate
        /// selection that genuinely has zero findings.
        /// </summary>
        public bool ScopeMismatch;

        /// <summary>Actionable Korean diagnostic ([범위 불일치] block) when <see cref="ScopeMismatch"/>; else null.</summary>
        public string? ScopeDiagnostic;

        /// <summary>Softer Korean note ([범위 경고]) when some rows were kept via an AMBIGUOUS Tier-2 relative-tail
        /// over-match (matched more than one distinct selected file); null when no ambiguous matches occurred.</summary>
        public string? ScopeAmbiguousWarning;
    }

    /// <summary>Lightweight per-checker detection count (from <see cref="SparrowExporter.ListCheckers"/>).
    /// <see cref="Key"/> = 체커 키 exactly as it appears in the xls; <see cref="Count"/> = number of detections
    /// carrying that key.</summary>
    public sealed class CheckerCount
    {
        public CheckerCount(string key, int count)
        {
            Key = key;
            Count = count;
        }

        public string Key { get; }
        public int Count { get; }
    }

    /// <summary>
    /// One distinct source path that the xls itself reports (from <see cref="SparrowExporter.ListPaths"/>).
    /// <see cref="Path"/> is the detection's source path as the xls carries it (경로, joined with 파일명 when 경로
    /// only holds the directory; 파일명 alone when there is no 경로) — used verbatim as a scope selection, so a
    /// selection built from an xls matches THAT xls exactly, on any PC and in any language.
    /// <see cref="FileName"/> is the leaf name for display, <see cref="Count"/> the number of detections on it.
    /// </summary>
    public sealed class XlsPathEntry
    {
        public XlsPathEntry(string path, string fileName, int count)
        {
            Path = path;
            FileName = fileName;
            Count = count;
        }

        public string Path { get; }
        public string FileName { get; }
        public int Count { get; }
    }

    /// <summary>Deterministic Sparrow .xls -&gt; split-outputs exporter. Stateless; safe to call repeatedly.</summary>
    public static class SparrowExporter
    {
        // Well-known Sparrow columns (used for directory/file naming, filters, scope matching and the summary;
        // NOT required to exist). Every column, well-known or not, still renders in the per-item 필드 table.
        private const string CID = "ID";
        private const string CCheckerKey = "체커 키";
        private const string CSeverity = "위험도";
        private const string CFileName = "파일명";
        private const string CLine = "라인";
        private const string CStatus = "이슈 상태";
        private const string CDesc = "체커 설명";
        private const string CSource = "소스 코드";
        private const string CPath = "경로";   // full source path (dir+file); disambiguates same-named files across projects

        // Columns dropped from the per-item 필드 table: constant across the whole codebase
        // (보안약점 / C# / SEMANTIC / 미확인), or workflow/bookkeeping metadata that carries no signal for the
        // fix decision (A.S / 이슈 담당자 / 검출 시간 / 유사 이슈 그룹 / 레퍼런스). Both groups only add tokens to
        // the request md the worker reads. Explicit exclusion set so any future xls column keeps rendering by default.
        private static readonly HashSet<string> TableExcludedColumns = new HashSet<string>(StringComparer.Ordinal)
        {
            "유형", "언어", "체커 타입", "이슈 상태",
            "A.S", "이슈 담당자", "검출 시간", "유사 이슈 그룹", "레퍼런스",
        };

        /// <summary>
        /// Parse the workbook and write &lt;체커 키&gt;/{ID}_{파일명}_{라인}.md, exactly as the console tool.
        /// Nothing else is written — no index, no summary, no worklist. Writes the human summary lines
        /// (incl. the "output dir:" line) to <paramref name="log"/> when it is non-null. Throws
        /// FileNotFoundException / InvalidDataException / IO exceptions on failure (caller maps to exit
        /// codes; nothing is caught here).
        /// </summary>
        public static ExportResult Run(ExportOptions opts, TextWriter? log = null)
        {
            string input = opts.InputPath;
            string? outDir = opts.OutDir;
            string? checker = opts.Checker;
            string? status = opts.Status;
            ISet<string> severities = opts.Severities ?? new HashSet<string>(StringComparer.Ordinal);
            int? max = opts.Max;

            string inputFull = Path.GetFullPath(input);
            if (!File.Exists(inputFull)) throw new FileNotFoundException("input file not found: " + inputFull);

            outDir ??= Path.Combine(Path.GetDirectoryName(inputFull) ?? ".", Path.GetFileNameWithoutExtension(inputFull) + ".items");
            outDir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(outDir);

            // Parse via the shared path (identical sheet pick / header detection / row skip that ListCheckers uses),
            // then continue with the exact same downstream logic — the written output stays byte-for-byte unchanged.
            WorkbookData wb = ParseWorkbook(inputFull);
            ISheet sheet = wb.Sheet;
            int sheetIdx = wb.SheetIndex;
            string sheetPick = wb.SheetPick;
            int mergedRegions = wb.MergedRegions;
            var columns = wb.Columns;
            var headerToIdx = wb.HeaderToIdx;
            var records = wb.Records;

            string GV(string[] vals, string name) => headerToIdx.TryGetValue(name, out int i) ? vals[i] : "";

            var scopedRecords = records;
            SourceScopeMatcher? scopeMatcher = SourceScopeMatcher.Create(opts.RootPath, opts.FilesFrom);
            if (scopeMatcher != null)
            {
                scopedRecords = records.Where(rec => scopeMatcher.Keep(GV(rec.Vals, CPath), GV(rec.Vals, CFileName))).ToList();
            }

            // Scope diagnostics: distinguish a total path-structure mismatch (0 kept from a non-empty selection over a
            // non-empty xls — the cross-PC "wrong checkout root" case) from a legitimate zero-finding selection, and
            // surface an ambiguous Tier-2 over-match note. Populated on ExportResult for the CLI (stderr) and GUI (log).
            string? scopeDiagnostic = null;
            string? scopeAmbiguousWarning = null;
            scopeMatcher?.BuildDiagnostics(records.Count, out scopeDiagnostic, out scopeAmbiguousWarning);

            // Filters (AND-combined). Scope is applied first; severity = exact-match set; checker/status = case-insensitive substring.
            var matched = scopedRecords.Where(rec =>
            {
                if (severities.Count > 0 && !severities.Contains(GV(rec.Vals, CSeverity).Trim())) return false;
                if (checker != null && GV(rec.Vals, CCheckerKey).IndexOf(checker, StringComparison.OrdinalIgnoreCase) < 0) return false;
                if (status != null && GV(rec.Vals, CStatus).IndexOf(status, StringComparison.OrdinalIgnoreCase) < 0) return false;
                return true;
            }).ToList();

            var written = max.HasValue ? matched.Take(Math.Max(0, max.Value)).ToList() : matched;

            var utf8NoBom = new UTF8Encoding(false);

            // Per-item markdown, grouped into one directory PER 체커 키 (the directory IS the checker key, so the
            // file name no longer repeats it — and is no longer truncated). Nothing else is written.
            foreach (var rec in written)
            {
                string id = GV(rec.Vals, CID);
                string checkerKey = GV(rec.Vals, CCheckerKey);
                string fileName = GV(rec.Vals, CFileName);
                string line = GV(rec.Vals, CLine);
                string idPart = id.Length > 0 ? id : rec.Ord.ToString("D5", CultureInfo.InvariantCulture);

                string checkerDir = Path.Combine(outDir, CheckerDirName(checkerKey));
                Directory.CreateDirectory(checkerDir);

                // Uniqueness is carried by the ID (or the row ordinal when the xls has no ID), exactly as before.
                string mdName = San(idPart) + "_" + San(fileName) + "_" + San(line) + ".md";
                File.WriteAllText(Path.Combine(checkerDir, mdName), BuildItemMd(rec.Vals, columns, GV), utf8NoBom);
            }

            // Console summary.
            var sevCounts = written.GroupBy(rec => GV(rec.Vals, CSeverity))
                .Select(g => new { Sev = g.Key, C = g.Count() })
                .OrderByDescending(x => x.C).ThenBy(x => x.Sev, StringComparer.Ordinal).ToList();
            int uniqueCheckers = written.Select(rec => GV(rec.Vals, CCheckerKey)).Distinct(StringComparer.Ordinal).Count();

            // Per-checker written counts for the run report. Purely additive bookkeeping: not logged, not written
            // to the output tree, so the exporter's on-disk bytes and stdout summary stay unchanged.
            var checkerCounts = written.GroupBy(rec => GV(rec.Vals, CCheckerKey), StringComparer.Ordinal)
                .Select(g => (Key: g.Key, Count: g.Count()))
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .ToList();

            if (log != null)
            {
                log.WriteLine("input:            " + inputFull);
                log.WriteLine("sheet:            " + sheet.SheetName + " (index " + sheetIdx.ToString(CultureInfo.InvariantCulture) + ", " + sheetPick + ")");
                log.WriteLine("columns:          " + columns.Count.ToString(CultureInfo.InvariantCulture));
                log.WriteLine("total data rows:  " + records.Count.ToString(CultureInfo.InvariantCulture));
                log.WriteLine("matched filters:  " + matched.Count.ToString(CultureInfo.InvariantCulture));
                log.WriteLine("written md files: " + written.Count.ToString(CultureInfo.InvariantCulture));
                log.WriteLine("checker folders:  " + uniqueCheckers.ToString(CultureInfo.InvariantCulture));
                log.WriteLine("severity counts:  " + (sevCounts.Count == 0 ? "(none)" :
                    string.Join(" ", sevCounts.Select(x => (x.Sev.Length > 0 ? x.Sev : "(없음)") + ":" + x.C.ToString(CultureInfo.InvariantCulture)))));
                if (mergedRegions > 0) log.WriteLine("NOTE: sheet has " + mergedRegions.ToString(CultureInfo.InvariantCulture) + " merged region(s); only top-left cell values are read");
                if (matched.Count == 0) log.WriteLine("NOTE: 0 rows matched filters");
                log.WriteLine("output dir:       " + outDir);
            }

            return new ExportResult
            {
                InputPath = inputFull,
                OutputDir = outDir,
                SheetName = sheet.SheetName,
                SheetIndex = sheetIdx,
                SheetPick = sheetPick,
                Columns = columns.Count,
                TotalDataRows = records.Count,
                MatchedCount = matched.Count,
                WrittenCount = written.Count,
                UniqueCheckers = uniqueCheckers,
                SeverityCounts = sevCounts.Select(x => (x.Sev, x.C)).ToList(),
                MergedRegions = mergedRegions,
                CheckerCounts = checkerCounts,
                ScopeMismatch = scopeDiagnostic != null,
                ScopeDiagnostic = scopeDiagnostic,
                ScopeAmbiguousWarning = scopeAmbiguousWarning,
            };
        }

        /// <summary>
        /// Lightweight, WRITE-FREE checker census for the GUI's pre-run mapping panel: parse the workbook and
        /// aggregate the number of detections per 체커 키, WITHOUT producing any items/md/index — nothing is
        /// written to disk. Reuses the exact shared parse path as <see cref="Run"/> (same sheet pick / header
        /// detection / empty-row skip), so its keys and counts match what a full export would group. Grouping is
        /// by the raw 체커 키 value, exactly as Run's own severity/UniqueCheckers summary groups it (no trimming).
        /// Ordering is deterministic (체커 키 ordinal). An empty path, a missing file, or an unparseable workbook
        /// yields an EMPTY list and NEVER throws — a bad selection just shows the panel's empty state instead of
        /// crashing the GUI.
        /// </summary>
        public static IReadOnlyList<CheckerCount> ListCheckers(string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath)) return Array.Empty<CheckerCount>();

            string inputFull;
            try { inputFull = Path.GetFullPath(inputPath.Trim().Trim('"')); }
            catch { return Array.Empty<CheckerCount>(); }
            if (!File.Exists(inputFull)) return Array.Empty<CheckerCount>();

            WorkbookData wb;
            try { wb = ParseWorkbook(inputFull); }
            catch { return Array.Empty<CheckerCount>(); }   // empty / not-a-workbook / no header -> empty census

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var rec in wb.Records)
            {
                string key = wb.Get(rec.Vals, CCheckerKey);
                counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
            }

            return counts
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new CheckerCount(kv.Key, kv.Value))
                .ToList();
        }

        /// <summary>
        /// Lightweight, WRITE-FREE source-path census: parse the workbook and aggregate the number of detections per
        /// distinct source path, WITHOUT producing any items/md/index — nothing is written to disk. Reuses the exact
        /// shared parse path as <see cref="Run"/> (same sheet pick / header detection / empty-row skip).
        ///
        /// The path of a row is 경로 as the xls carries it, with 파일명 appended when 경로 only holds the directory
        /// (both Sparrow conventions are absorbed), or 파일명 alone when there is no 경로. Rows carrying neither are
        /// skipped — they cannot be placed under any path. Aggregation is case-insensitive (Windows paths); ordering
        /// is deterministic. An empty path, a missing file, or an unparseable workbook yields an EMPTY list and NEVER
        /// throws.
        ///
        /// This is what the GUI's [XLS 분리] scope tree is built from: the selection it produces is fed straight back
        /// as <see cref="ExportOptions.FilesFrom"/> (no RootPath), so filtering the xls by its OWN paths is an exact
        /// string match — language-independent and immune to cross-PC checkout differences.
        /// </summary>
        public static IReadOnlyList<XlsPathEntry> ListPaths(string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath)) return Array.Empty<XlsPathEntry>();

            string inputFull;
            try { inputFull = Path.GetFullPath(inputPath.Trim().Trim('"')); }
            catch { return Array.Empty<XlsPathEntry>(); }
            if (!File.Exists(inputFull)) return Array.Empty<XlsPathEntry>();

            WorkbookData wb;
            try { wb = ParseWorkbook(inputFull); }
            catch { return Array.Empty<XlsPathEntry>(); }   // empty / not-a-workbook / no header -> empty census

            // key = 항목 경로(대소문자 무시 집계, 첫 등장 표기를 보존), value = (표시용 파일명, 건수).
            var byPath = new Dictionary<string, (string Path, string FileName, int Count)>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in wb.Records)
            {
                string rawPath = wb.Get(rec.Vals, CPath).Trim();
                string fileName = wb.Get(rec.Vals, CFileName).Trim();
                string itemPath = ComposeItemPath(rawPath, fileName);
                if (itemPath.Length == 0) continue;   // 경로도 파일명도 없는 행은 트리에 놓을 자리가 없다

                if (byPath.TryGetValue(itemPath, out var cur))
                {
                    byPath[itemPath] = (cur.Path, cur.FileName, cur.Count + 1);
                }
                else
                {
                    string leaf = fileName.Length > 0 ? fileName : LastPathSegment(itemPath);
                    byPath[itemPath] = (itemPath, leaf, 1);
                }
            }

            return byPath.Values
                .OrderBy(v => v.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Path, StringComparer.Ordinal)
                .Select(v => new XlsPathEntry(v.Path, v.FileName, v.Count))
                .ToList();
        }

        // Absorb both Sparrow conventions for 경로: a full source path (dir+file) is taken as-is; a directory-only
        // 경로 gets 파일명 appended (keeping the separator style the xls used). No 경로 => 파일명 IS the path.
        private static string ComposeItemPath(string rawPath, string fileName)
        {
            if (rawPath.Length == 0) return fileName;
            if (fileName.Length == 0) return rawPath;
            if (PathEndsWithSegment(rawPath, fileName)) return rawPath;

            char sep = rawPath.IndexOf('\\') < 0 && rawPath.IndexOf('/') >= 0 ? '/' : '\\';
            return rawPath.TrimEnd('/', '\\') + sep + fileName;
        }

        // Does path end with segment at a directory boundary? (separator/case-insensitive)
        private static bool PathEndsWithSegment(string path, string segment)
        {
            string p = NormalizePathForMatch(path);
            string s = NormalizePathForMatch(segment);
            if (s.Length == 0 || p.Length < s.Length) return false;
            if (!p.EndsWith(s, StringComparison.Ordinal)) return false;
            return p.Length == s.Length || p[p.Length - s.Length - 1] == Path.DirectorySeparatorChar;
        }

        private static string LastPathSegment(string path)
        {
            string trimmed = path.TrimEnd('/', '\\');
            int i = trimmed.LastIndexOfAny(new[] { '/', '\\' });
            return i >= 0 ? trimmed.Substring(i + 1) : trimmed;
        }

        // Normalize a path for comparison: fold '/' and '\' to the platform separator and lowercase (Windows paths
        // are case-insensitive). Shared by the path census and every scope-matching tier so they never drift.
        private static string NormalizePathForMatch(string s)
        {
            return s.Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Trim()
                    .ToLowerInvariant();
        }

        // Shared workbook parse used by BOTH Run and ListCheckers so the two never drift: open the .xls/.xlsx,
        // pick the sheet (named 'issues' else sheet 0), detect the header row (first non-empty), and read every
        // non-empty data row with a stable 1-based ordinal. Deterministic and side-effect-free (writes nothing).
        private static WorkbookData ParseWorkbook(string inputFull)
        {
            var fmt = new DataFormatter(CultureInfo.InvariantCulture);

            IWorkbook workbook;
            using (FileStream fs = File.OpenRead(inputFull))
            {
                workbook = WorkbookFactory.Create(fs);   // auto-detects HSSF (.xls) vs XSSF (.xlsx)
            }

            // Sheet pick: prefer the sheet named "issues", else sheet 0.
            ISheet sheet;
            int sheetIdx;
            string sheetPick;
            ISheet? named = workbook.GetSheet("issues");
            if (named != null) { sheet = named; sheetIdx = workbook.GetSheetIndex(named); sheetPick = "named 'issues'"; }
            else { sheet = workbook.GetSheetAt(0); sheetIdx = 0; sheetPick = "first sheet (no 'issues')"; }

            int mergedRegions = sheet.NumMergedRegions;   // reported as an anomaly if > 0

            // Header row = first non-empty row. Map non-empty header cell text -> column order.
            var columns = new List<(string Header, int Col)>();
            var headerToIdx = new Dictionary<string, int>(StringComparer.Ordinal);   // header -> position in columns
            int headerRowIdx = -1;
            for (int r = sheet.FirstRowNum; r <= sheet.LastRowNum; r++)
            {
                IRow? row = sheet.GetRow(r);
                if (row == null) continue;
                bool any = false;
                for (int c = row.FirstCellNum; c < row.LastCellNum; c++)
                {
                    if (CellToString(row.GetCell(c), fmt).Trim().Length > 0) { any = true; break; }
                }
                if (!any) continue;
                headerRowIdx = r;
                for (int c = row.FirstCellNum; c < row.LastCellNum; c++)
                {
                    string h = CellToString(row.GetCell(c), fmt).Trim();
                    if (h.Length == 0) continue;
                    if (!headerToIdx.ContainsKey(h)) headerToIdx[h] = columns.Count;   // first wins on duplicate header
                    columns.Add((h, c));
                }
                break;
            }
            if (headerRowIdx < 0 || columns.Count == 0) throw new InvalidDataException("no header row found on sheet '" + sheet.SheetName + "'");

            // Read data rows (skip fully-empty). Ordinal = 1-based position among data rows (stable across filters).
            var records = new List<(int Ord, string[] Vals)>();
            int ord = 0;
            for (int r = headerRowIdx + 1; r <= sheet.LastRowNum; r++)
            {
                IRow? row = sheet.GetRow(r);
                if (row == null) continue;
                var vals = new string[columns.Count];
                bool any = false;
                for (int i = 0; i < columns.Count; i++)
                {
                    string v = CellToString(row.GetCell(columns[i].Col), fmt);
                    vals[i] = v;
                    if (v.Length > 0) any = true;
                }
                if (!any) continue;
                ord++;
                records.Add((ord, vals));
            }

            return new WorkbookData(sheet, sheetIdx, sheetPick, mergedRegions, columns, headerToIdx, records);
        }

        // Carrier for the shared parse result (see ParseWorkbook). Private to Core.
        private sealed class WorkbookData
        {
            public WorkbookData(ISheet sheet, int sheetIndex, string sheetPick, int mergedRegions,
                                List<(string Header, int Col)> columns, Dictionary<string, int> headerToIdx,
                                List<(int Ord, string[] Vals)> records)
            {
                Sheet = sheet;
                SheetIndex = sheetIndex;
                SheetPick = sheetPick;
                MergedRegions = mergedRegions;
                Columns = columns;
                HeaderToIdx = headerToIdx;
                Records = records;
            }

            public ISheet Sheet { get; }
            public int SheetIndex { get; }
            public string SheetPick { get; }
            public int MergedRegions { get; }
            public List<(string Header, int Col)> Columns { get; }
            public Dictionary<string, int> HeaderToIdx { get; }
            public List<(int Ord, string[] Vals)> Records { get; }

            public string Get(string[] vals, string name) => HeaderToIdx.TryGetValue(name, out int i) ? vals[i] : "";
        }

        // Cross-PC scope filter. The collaboration model is ONE authoritative Sparrow xls (paths from PC-A's
        // checkout, e.g. D:\Work\MyApp\...) whose findings a team divides by file; each teammate selects files
        // from their OWN checkout at their OWN root (e.g. C:\myproj\MyApp\...). So matching MUST be drive/prefix-
        // independent. Four tiers, applied in order per row:
        //   Tier 0 — xls-path verbatim (ONLY when no RootPath was given): the selection was taken from the xls
        //            itself (GUI [XLS 분리] 범위 트리 = ListPaths), so the row's own 경로 (or 경로+파일명, or 파일명
        //            when 경로 is empty) is compared as a STRING against the selection. Exact by construction,
        //            language-independent (.cs/.cpp/.h/...), and impossible to break by a different checkout root.
        //            Inert for every caller that passes RootPath (i.e. a LOCAL source selection) — those keep the
        //            existing Tier 1-3 semantics byte-for-byte.
        //   Tier 1 — absolute exact (same-PC, fastest): any BuildCandidates() absolute path is in _selected.
        //   Tier 2 — relative-tail (cross-PC): the xls 경로 ENDS WITH a selected file's path-relative-to-_root at a
        //            directory boundary (full relative tail, not just basename, to minimize over-match).
        //   Tier 3 — empty-경로 basename fallback: only when the xls 경로 is empty AND the basename is unique both in
        //            the selection and under _root.
        private sealed class SourceScopeMatcher
        {
            private readonly string? _root;
            private readonly HashSet<string> _selected;
            private readonly Dictionary<string, List<string>> _byName;
            private readonly Dictionary<string, List<string>> _allByName;

            // Tier 0 index: the files-from entries EXACTLY as written (only separator/case folded), plus the first
            // couple in original case for diagnostics. Non-empty whenever the selection is non-empty, regardless of
            // extension — this is the index that makes an xls-derived (any-language) selection match.
            private readonly HashSet<string> _rawSelected;
            private readonly List<string> _rawDisplay;

            // Tier 2 index: normalized (separator + case folded) relative tail -> selected absolute paths that
            // produced it. A tail keyed to >1 selected path, or one row hitting >1 tail, is an ambiguous over-match.
            private readonly Dictionary<string, List<string>> _relTailMap;
            private readonly List<string> _relTailDisplay = new List<string>();   // original-case tails, for diagnostics

            // Outcome accounting across the whole run (Keep is called once per data row).
            private int _examined;        // rows the scope filter looked at
            private int _kept;            // rows kept by any tier
            private int _ambiguousKept;   // rows kept via an AMBIGUOUS Tier-2 match (>=2 distinct tails)
            private readonly List<string> _sampleXlsPaths = new List<string>();   // first couple non-empty 경로 values

            private SourceScopeMatcher(string? root, HashSet<string> selected, IEnumerable<string> allSourceFiles,
                                       HashSet<string> rawSelected, List<string> rawDisplay)
            {
                _root = root;
                _selected = selected;
                _rawSelected = rawSelected;
                _rawDisplay = rawDisplay;
                _byName = selected
                    .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key ?? "", g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                _allByName = allSourceFiles
                    .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key ?? "", g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                // Precompute the relative-tail -> selected map once. Only files genuinely UNDER _root yield a clean
                // relative tail; anything outside (rooted elsewhere / .. traversal) is skipped for Tier 2.
                _relTailMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                if (_root != null)
                {
                    foreach (string sel in selected)
                    {
                        string? tail = GetRelativeTail(_root, sel);
                        if (tail == null) continue;
                        string norm = NormalizeTail(tail);
                        if (norm.Length == 0) continue;
                        if (!_relTailMap.TryGetValue(norm, out List<string>? list))
                        {
                            list = new List<string>();
                            _relTailMap[norm] = list;
                            _relTailDisplay.Add(tail);
                        }
                        list.Add(sel);
                    }
                }
            }

            public static SourceScopeMatcher? Create(string? rootPath, string? filesFrom)
            {
                if (string.IsNullOrWhiteSpace(filesFrom)) return null;
                string filesFromFull = Path.GetFullPath(filesFrom.Trim().Trim('"'));
                if (!File.Exists(filesFromFull)) throw new FileNotFoundException("files-from not found: " + filesFromFull);

                string? root = string.IsNullOrWhiteSpace(rootPath) ? null : Path.GetFullPath(rootPath.Trim().Trim('"'));
                var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var rawSelected = new HashSet<string>(StringComparer.Ordinal);
                var rawDisplay = new List<string>();
                foreach (string entry in ReadFilesFrom(filesFromFull))
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;

                    // Tier 0 index: keep the entry as written (only folded). No extension filter — the selection may
                    // name C/C++/Java/... sources ([XLS 분리] is language-agnostic).
                    string norm = NormalizePathForMatch(entry);
                    if (norm.Length > 0 && rawSelected.Add(norm) && rawDisplay.Count < 2) rawDisplay.Add(entry.Trim());

                    // Tier 1-3 index: absolute C# source paths (the LOCAL source selection model).
                    string full;
                    try
                    {
                        full = Path.IsPathRooted(entry)
                            ? Path.GetFullPath(entry)
                            : Path.GetFullPath(Path.Combine(root ?? Directory.GetCurrentDirectory(), entry));
                    }
                    catch
                    {
                        continue;   // an entry that is not a usable local path still counts for Tier 0
                    }
                    if (full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    {
                        selected.Add(full);
                    }
                }

                IEnumerable<string> allSourceFiles = root != null && Directory.Exists(root)
                    ? EnumerateCsFiles(root)
                    : selected;
                return new SourceScopeMatcher(root, selected, allSourceFiles, rawSelected, rawDisplay);
            }

            public bool Keep(string pathCell, string fileNameCell)
            {
                _examined++;
                string fileName = (fileNameCell ?? "").Trim();
                string path = (pathCell ?? "").Trim();
                RecordSampleXlsPath(path);

                // Tier 0 — xls-path verbatim. Only when no RootPath was given (the xls-derived selection model);
                // callers that pass a local source root keep the original Tier 1-3 behaviour untouched.
                if (_root == null && _rawSelected.Count > 0 && MatchesRawSelection(path, fileName))
                {
                    _kept++;
                    return true;
                }

                // Tier 1 — absolute exact (same-PC).
                foreach (string candidate in BuildCandidates(path, fileName))
                {
                    if (_selected.Contains(candidate)) { _kept++; return true; }
                }

                // Tier 2 — relative-tail (cross-PC). Requires _root; skipped when 경로 is empty.
                if (TryMatchRelativeTail(path, out bool ambiguous))
                {
                    _kept++;
                    if (ambiguous) _ambiguousKept++;
                    return true;
                }

                // Tier 3 — empty-경로 basename fallback (unchanged): basename unique in selection AND under root.
                if (path.Length == 0 && fileName.Length > 0 &&
                    _byName.TryGetValue(Path.GetFileName(fileName), out List<string>? sameName) &&
                    sameName.Count == 1 &&
                    _allByName.TryGetValue(Path.GetFileName(fileName), out List<string>? sameNameInRoot) &&
                    sameNameInRoot.Count == 1)
                {
                    _kept++;
                    return true;
                }

                return false;
            }

            // Tier 0: is this row's own path string part of the selection? Mirrors ListPaths' path composition
            // (경로 / 경로+파일명 / 파일명), so a tree built from ListPaths selects exactly the rows it displayed.
            private bool MatchesRawSelection(string path, string fileName)
            {
                if (path.Length == 0)
                {
                    return fileName.Length > 0 && _rawSelected.Contains(NormalizePathForMatch(fileName));
                }
                if (_rawSelected.Contains(NormalizePathForMatch(path))) return true;
                return fileName.Length > 0
                       && _rawSelected.Contains(NormalizePathForMatch(path.TrimEnd('/', '\\') + Path.DirectorySeparatorChar + fileName));
            }

            // Tier 2: does the xls 경로 end with any selected file's full relative-to-root tail, at a directory
            // boundary? A boundary means the whole normalized path equals the tail, or the char just before the tail
            // is a separator — so tail "View\Foo.cs" matches "...\View\Foo.cs" and "...\SubView\View\Foo.cs" but NOT
            // "...\OtherView\Foo.cs". Sets ambiguous=true (still a match — fail open) when the row hits >=2 distinct
            // selected tails, i.e. the finding could belong to more than one selected file.
            private bool TryMatchRelativeTail(string path, out bool ambiguous)
            {
                ambiguous = false;
                if (_root == null || _relTailMap.Count == 0 || path.Length == 0) return false;

                string norm = NormalizeTail(path);
                int matchedTails = 0;
                foreach (string tail in _relTailMap.Keys)
                {
                    if (norm.Length < tail.Length) continue;
                    if (norm.Equals(tail, StringComparison.Ordinal) ||
                        norm.EndsWith(Path.DirectorySeparatorChar + tail, StringComparison.Ordinal))
                    {
                        matchedTails++;
                        if (matchedTails >= 2) break;
                    }
                }

                if (matchedTails == 0) return false;
                ambiguous = matchedTails >= 2;
                return true;
            }

            private void RecordSampleXlsPath(string path)
            {
                if (path.Length == 0 || _sampleXlsPaths.Count >= 2) return;
                if (!_sampleXlsPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) _sampleXlsPaths.Add(path);
            }

            // Full relative tail of a selected absolute path under root (original case, separators preserved), or null
            // when it is not genuinely under root (rooted elsewhere / different drive / .. traversal).
            private static string? GetRelativeTail(string root, string selected)
            {
                string relative;
                try { relative = Path.GetRelativePath(root, selected); }
                catch { return null; }
                if (string.IsNullOrEmpty(relative) || relative == ".") return null;
                if (Path.IsPathRooted(relative)) return null;   // GetRelativePath returns absolute when on another drive
                if (relative == ".." ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    relative.StartsWith("../", StringComparison.Ordinal))
                    return null;
                return relative;
            }

            // Normalize a path/tail for cross-PC comparison: fold '/' and '\' to the platform separator and lowercase
            // (Windows paths are case-insensitive). Used for both the tail keys and the xls 경로 at match time.
            private static string NormalizeTail(string s)
            {
                return NormalizePathForMatch(s);
            }

            // Build the run-level scope diagnostics. mismatch = the actionable [범위 불일치] block emitted ONLY for a
            // total zero-match under a non-empty selection over a non-empty xls (the cross-PC wrong-root case);
            // ambiguousWarning = the softer [범위 경고] note when some rows were kept via ambiguous Tier-2 over-match.
            public void BuildDiagnostics(int totalDataRows, out string? mismatch, out string? ambiguousWarning)
            {
                mismatch = null;
                ambiguousWarning = null;

                if ((_selected.Count > 0 || _rawSelected.Count > 0) && _examined > 0 && _kept == 0)
                {
                    string xlsEx = _sampleXlsPaths.Count > 0 ? string.Join("  |  ", _sampleXlsPaths) : "(경로 없음)";
                    string selEx = _relTailDisplay.Count > 0
                        ? string.Join("  |  ", _relTailDisplay.Take(2))
                        : _rawDisplay.Count > 0
                            ? string.Join("  |  ", _rawDisplay)
                            : "(상대경로 없음)";
                    mismatch =
                        "[범위 불일치] 선택한 소스(" + (_root ?? "(root 미지정)") + ")의 상대경로가 이 xls의 검출 경로와 하나도 "
                        + "일치하지 않습니다. 같은 프로젝트의 다른 체크아웃인지, 선택 폴더가 맞는지 확인하세요. "
                        + "(xls 검출 " + totalDataRows.ToString(CultureInfo.InvariantCulture) + "건 중 0건 매칭)\n"
                        + "  xls 예: " + xlsEx + "   /   선택 예: " + selEx;
                }

                if (_ambiguousKept > 0)
                {
                    ambiguousWarning =
                        "[범위 경고] 상대경로가 여러 선택 파일과 겹치는 검출 "
                        + _ambiguousKept.ToString(CultureInfo.InvariantCulture) + "건은 포함했습니다.";
                }
            }

            private IEnumerable<string> BuildCandidates(string path, string fileName)
            {
                if (path.Length == 0) yield break;

                string normalizedPath = path.Replace('/', Path.DirectorySeparatorChar).Trim();
                var rawCandidates = new List<string>();

                if (Path.IsPathRooted(normalizedPath))
                {
                    rawCandidates.Add(normalizedPath);
                    if (fileName.Length > 0) rawCandidates.Add(Path.Combine(normalizedPath, fileName));
                }
                else if (_root != null)
                {
                    rawCandidates.Add(Path.Combine(_root, normalizedPath));
                    if (fileName.Length > 0) rawCandidates.Add(Path.Combine(_root, normalizedPath, fileName));
                }

                foreach (string raw in rawCandidates)
                {
                    string full;
                    try
                    {
                        full = Path.GetFullPath(raw);
                    }
                    catch
                    {
                        continue;
                    }

                    if (_root != null && !IsUnderRoot(full, _root)) continue;
                    yield return full;
                }
            }

            private static bool IsUnderRoot(string path, string root)
            {
                string rootWithSlash = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                      + Path.DirectorySeparatorChar;
                string full = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return full.Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                       || full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase);
            }

            private static IEnumerable<string> ReadFilesFrom(string path)
            {
                List<string[]> rows = ParseCsv(File.ReadAllText(path));
                if (rows.Count == 0) yield break;

                int col = PickColumn(rows[0]);
                int start = IsHeader(rows[0], col) ? 1 : 0;
                for (int i = start; i < rows.Count; i++)
                {
                    if (col >= rows[i].Length) continue;
                    string value = rows[i][col].Trim();
                    if (value.Length > 0) yield return value;
                }
            }

            private static IEnumerable<string> EnumerateCsFiles(string root)
            {
                var stack = new Stack<string>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    string dir = stack.Pop();
                    IEnumerable<string> files;
                    try
                    {
                        files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly).ToList();
                    }
                    catch
                    {
                        files = Array.Empty<string>();
                    }

                    foreach (string file in files)
                    {
                        string full;
                        try { full = Path.GetFullPath(file); }
                        catch { continue; }
                        yield return full;
                    }

                    IEnumerable<string> dirs;
                    try
                    {
                        dirs = Directory.EnumerateDirectories(dir).ToList();
                    }
                    catch
                    {
                        dirs = Array.Empty<string>();
                    }

                    foreach (string child in dirs)
                    {
                        stack.Push(child);
                    }
                }
            }

            private static int PickColumn(string[] header)
            {
                string[] names = { "파일명", "경로", "path", "filepath", "file", "fullpath" };
                foreach (string name in names)
                {
                    for (int i = 0; i < header.Length; i++)
                    {
                        if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
                    }
                }

                return 0;
            }

            private static bool IsHeader(string[] row, int col)
            {
                if (col < 0 || col >= row.Length) return false;
                string cell = row[col].Trim();
                return string.Equals(cell, "파일명", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(cell, "경로", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(cell, "path", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(cell, "filepath", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(cell, "file", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(cell, "fullpath", StringComparison.OrdinalIgnoreCase);
            }

            private static List<string[]> ParseCsv(string text)
            {
                var rows = new List<string[]>();
                var row = new List<string>();
                var cell = new StringBuilder();
                bool quoted = false;

                for (int i = 0; i < text.Length; i++)
                {
                    char ch = text[i];
                    if (quoted)
                    {
                        if (ch == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"')
                            {
                                cell.Append('"');
                                i++;
                            }
                            else
                            {
                                quoted = false;
                            }
                        }
                        else
                        {
                            cell.Append(ch);
                        }
                    }
                    else
                    {
                        if (ch == '"') quoted = true;
                        else if (ch == ',')
                        {
                            row.Add(cell.ToString());
                            cell.Clear();
                        }
                        else if (ch == '\r' || ch == '\n')
                        {
                            if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                            row.Add(cell.ToString());
                            rows.Add(row.ToArray());
                            row.Clear();
                            cell.Clear();
                        }
                        else
                        {
                            cell.Append(ch);
                        }
                    }
                }

                if (cell.Length > 0 || row.Count > 0)
                {
                    row.Add(cell.ToString());
                    rows.Add(row.ToArray());
                }

                return rows;
            }
        }

        private static string BuildItemMd(string[] vals, List<(string Header, int Col)> columns,
                                          Func<string[], string, string> gv)
        {
            string checkerKey = gv(vals, CCheckerKey);
            string fileName = gv(vals, CFileName);
            string line = gv(vals, CLine);

            var sb = new StringBuilder();
            sb.Append("# ").Append(checkerKey).Append(" @ ").Append(fileName).Append(':').Append(line).Append("\n\n");
            sb.Append("| 필드 | 값 |\n|---|---|\n");
            for (int i = 0; i < columns.Count; i++)
            {
                string h = columns[i].Header;
                if (h == CSource || h == CDesc) continue;   // these get their own verbatim sections below
                if (TableExcludedColumns.Contains(h)) continue;   // constant/no-signal columns
                string v = vals[i];
                if (v.Length == 0) continue;
                sb.Append("| ").Append(TableCell(h)).Append(" | ").Append(TableCell(v)).Append(" |\n");
            }

            string desc = gv(vals, CDesc);
            sb.Append("\n## 체커 설명\n");
            sb.Append(desc);
            if (!desc.EndsWith("\n", StringComparison.Ordinal)) sb.Append('\n');

            // xls의 '소스 코드' 컬럼을 그대로 옮긴다. 어떤 지시문·앵커 마커도 주입하지 않는다
            // (수정 범위/작업 지시는 도구가 아니라 사용자·체커별로 정할 몫이다).
            string src = gv(vals, CSource);
            string fence = src.Contains("```") ? "````" : "```";   // escape source that itself contains a fence
            sb.Append("\n## 소스 코드\n");
            sb.Append(fence).Append("text\n");
            sb.Append(src);
            if (!src.EndsWith("\n", StringComparison.Ordinal)) sb.Append('\n');
            sb.Append(fence).Append('\n');
            return sb.ToString();
        }

        // --- cell -> string ---
        private static string CellToString(ICell? cell, DataFormatter fmt)
        {
            if (cell == null) return "";
            switch (cell.CellType)
            {
                case CellType.String: return cell.StringCellValue ?? "";
                case CellType.Boolean: return cell.BooleanCellValue ? "true" : "false";
                case CellType.Numeric: return NumericToString(cell);
                case CellType.Formula:
                    switch (cell.CachedFormulaResultType)
                    {
                        case CellType.String: return cell.StringCellValue ?? "";
                        case CellType.Boolean: return cell.BooleanCellValue ? "true" : "false";
                        case CellType.Numeric: return NumericToString(cell);
                        default:
                            try { return fmt.FormatCellValue(cell) ?? ""; } catch { return ""; }
                    }
                default: return "";   // Blank / Error / Unknown
            }
        }

        private static string NumericToString(ICell cell)
        {
            if (DateUtil.IsCellDateFormatted(cell))
            {
                // Date-formatted numeric: render deterministically (invariant) rather than as a raw serial.
                DateTime dt = cell.DateCellValue ?? DateTime.MinValue;
                return dt.TimeOfDay == TimeSpan.Zero
                    ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            double d = cell.NumericCellValue;
            // Integral values render without a trailing ".0" (ID/라인 must be 6464794, not 6464794.0).
            if (!double.IsNaN(d) && !double.IsInfinity(d) && d == Math.Truncate(d) && Math.Abs(d) < 9.007199254740992e15)
                return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        // --- helpers ---
        private static string San(string s)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
                sb.Append(ch == ' ' || Array.IndexOf(invalid, ch) >= 0 ? '-' : ch);
            return sb.ToString();
        }

        // Directory name for a 체커 키. Real Sparrow keys are safe (A.B.C_D), so this only defends against
        // filesystem-invalid characters; the key is otherwise kept verbatim (never truncated). A row with no
        // 체커 키 at all still needs a home, hence the explicit fallback folder.
        private static string CheckerDirName(string checkerKey)
        {
            string dir = San(checkerKey).Trim().TrimEnd('.');
            return dir.Length > 0 ? dir : "_no-checker";
        }

        // Table cell: escape pipe, collapse newlines to <br> so the row stays a single markdown line.
        private static string TableCell(string s) =>
            s.Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");
    }
}
