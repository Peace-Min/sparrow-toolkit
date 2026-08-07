// XlsSplitRunReport: machine-readable evidence of ONE [XLS 분리] run (xls -> per-checker md), plus a short human
// summary. Its whole reason to exist is post-hoc judgement: "이 실행이 왜 이런 결과를 냈나?" — which xls (by
// sha256, so a later re-run can prove it is the same input), which scope filter, how many rows/checkers, which
// checker got which rule attached, and every warning the run produced.
//
// HARD CONTRACT — the report NEVER lands in the export output folder. [XLS 분리]'s output contract is "체커 폴더 +
// 항목 md만, 부산물 0", so the caller passes an explicit path OUTSIDE the output tree (the GUI uses its log
// folder; the CLI uses --report <PATH>). Without that explicit path NOTHING is written, so a plain run stays
// byte-identical to before.
// The contract is ENFORCED, not merely documented: TryWrite rejects any report path that is the output folder
// or sits under it (see IsInsideOutputTree) BEFORE creating anything. Callers used to be trusted to pass a safe
// path, so `--out X --report X\r.json` — and a GUI --log-dir aimed at the output folder — quietly broke it.
//
// Everything here is best-effort: a failed report write is reported as false and never breaks the export.
// Encoding: json + summary are UTF-8 WITHOUT BOM, LF newlines, 한글 unescaped (readable in any editor).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SparrowXlsExport.Core
{
    /// <summary>The run's effective options (what actually shaped the result), as serialized under "options".</summary>
    public sealed class XlsSplitReportOptions
    {
        /// <summary>True when a files-from scope filter was in effect (team split by file).</summary>
        public bool FilesFromUsed { get; set; }

        /// <summary>Path of the files-from manifest; null when unused.</summary>
        public string? FilesFrom { get; set; }

        /// <summary>Source root the manifest entries / xls 경로 were resolved against; null when unused.</summary>
        public string? Root { get; set; }

        /// <summary>Exact-match 위험도 filter; empty = no severity filter ([XLS 분리] default = 전건).</summary>
        public IReadOnlyList<string> Severities { get; set; } = Array.Empty<string>();

        /// <summary>체커 키 substring filter; null = none.</summary>
        public string? Checker { get; set; }

        /// <summary>이슈 상태 substring filter; null = none.</summary>
        public string? Status { get; set; }

        /// <summary>Written-item cap; null = none.</summary>
        public int? Max { get; set; }
    }

    /// <summary>One "체커 키 -> 기록된 항목 수" pair.</summary>
    public sealed class XlsSplitReportCheckerCount
    {
        public string Key { get; set; } = "";
        public int Count { get; set; }
    }

    /// <summary>What the rule-attachment layer did for one checker (see <see cref="CheckerMapDetail"/>).</summary>
    public sealed class XlsSplitReportAssignment
    {
        public string CheckerKey { get; set; } = "";
        public string? RuleName { get; set; }
        public bool RuleExists { get; set; }
        public int ItemsAttached { get; set; }
        public int ItemCount { get; set; }
    }

    /// <summary>Scope-filter outcome: the cross-PC "wrong checkout" tell-tale and the ambiguous-match note.</summary>
    public sealed class XlsSplitReportScope
    {
        public bool Mismatch { get; set; }
        public string? Diagnostic { get; set; }
        public string? AmbiguousWarning { get; set; }
    }

    /// <summary>Full report payload. Serialized camelCase, so <c>InputXls</c> becomes <c>"inputXls"</c>.</summary>
    public sealed class XlsSplitRunReport
    {
        public string InputXls { get; set; } = "";
        public long InputSizeBytes { get; set; }
        public string InputSha256 { get; set; } = "";
        public string OutDir { get; set; } = "";
        public string? GuidesDir { get; set; }
        public string StartedUtc { get; set; } = "";
        public long ElapsedMs { get; set; }
        public string ToolVersion { get; set; } = "";

        public XlsSplitReportOptions Options { get; set; } = new XlsSplitReportOptions();

        /// <summary>Sheet the rows came from (sheet pick is part of "why these rows").</summary>
        public string Sheet { get; set; } = "";

        public int TotalRows { get; set; }

        /// <summary>Rows surviving scope + filters (== TotalRows on a default 전건 run).</summary>
        public int MatchedRows { get; set; }

        public int WrittenMd { get; set; }
        public int CheckerFolders { get; set; }

        public IReadOnlyList<XlsSplitReportCheckerCount> CheckerCounts { get; set; } = Array.Empty<XlsSplitReportCheckerCount>();
        public IReadOnlyList<XlsSplitReportAssignment> Assignments { get; set; } = Array.Empty<XlsSplitReportAssignment>();
        public IReadOnlyList<string> UnmappedCheckers { get; set; } = Array.Empty<string>();

        public XlsSplitReportScope Scope { get; set; } = new XlsSplitReportScope();

        public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
    }

    /// <summary>Builds and writes the [XLS 분리] run report. Never throws; a failed write returns false.</summary>
    public static class XlsSplitReportWriter
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep 한글 readable
        };

        /// <summary>
        /// Assemble the report from one run's inputs and results. <paramref name="map"/> null = the rule-attachment
        /// layer did not run (no guides), which is recorded as "every checker unmapped" + a warning rather than
        /// silently empty. Pure/allocation-only: touches the filesystem ONLY to size+hash the input xls.
        /// </summary>
        public static XlsSplitRunReport Build(ExportOptions opts, ExportResult result, MapResult? map,
                                            string? guidesDir, DateTime startedUtc, long elapsedMs)
        {
            var warnings = new List<string>();

            long size = -1;
            string sha = "";
            try
            {
                var fi = new FileInfo(result.InputPath.Length > 0 ? result.InputPath : opts.InputPath);
                if (fi.Exists)
                {
                    size = fi.Length;
                    sha = Sha256OfFile(fi.FullName);
                }
                else
                {
                    warnings.Add("입력 xls 를 다시 읽을 수 없어 크기/해시를 남기지 못했습니다: " + fi.FullName);
                }
            }
            catch (Exception ex)
            {
                warnings.Add("입력 xls 크기/해시 계산 실패: " + ex.Message);
            }

            bool filesFromUsed = !string.IsNullOrWhiteSpace(opts.FilesFrom);
            var reportOpts = new XlsSplitReportOptions
            {
                FilesFromUsed = filesFromUsed,
                FilesFrom = filesFromUsed ? opts.FilesFrom : null,
                Root = string.IsNullOrWhiteSpace(opts.RootPath) ? null : opts.RootPath,
                Severities = (opts.Severities ?? new HashSet<string>(StringComparer.Ordinal))
                             .OrderBy(s => s, StringComparer.Ordinal).ToList(),
                Checker = opts.Checker,
                Status = opts.Status,
                Max = opts.Max,
            };

            var assignments = new List<XlsSplitReportAssignment>();
            var unmapped = new List<string>();
            if (map != null)
            {
                foreach (CheckerMapDetail d in map.Details)
                {
                    assignments.Add(new XlsSplitReportAssignment
                    {
                        CheckerKey = d.CheckerKey,
                        RuleName = d.RuleName,
                        RuleExists = d.RuleExists,
                        ItemsAttached = d.ItemsAttached,
                        ItemCount = d.ItemCount,
                    });
                    if (d.RuleName != null && !d.RuleExists)
                    {
                        warnings.Add("지정된 규칙 파일이 없어 부착하지 못했습니다(지정 유실): "
                                     + d.CheckerKey + " -> " + d.RuleName);
                    }
                }
                unmapped.AddRange(map.Unmapped);
            }
            else
            {
                // No guides => nothing was attached. Say so explicitly and list every checker as unmapped, so a
                // reader never mistakes "mapping did not run" for "mapping ran and found nothing".
                warnings.Add("규칙 매핑을 실행하지 않았습니다(guides 폴더 미지정) — 모든 체커가 순수 출력입니다.");
                unmapped.AddRange(result.CheckerCounts.Select(c => c.Key));
            }

            if (!string.IsNullOrWhiteSpace(guidesDir) && !SafeDirExists(guidesDir!))
            {
                warnings.Add("guides 폴더가 존재하지 않습니다: " + guidesDir);
            }
            if (result.MergedRegions > 0)
            {
                warnings.Add("시트에 병합 영역 " + result.MergedRegions.ToString(CultureInfo.InvariantCulture)
                             + "개 — 좌상단 셀 값만 읽었습니다.");
            }
            if (result.MatchedCount == 0)
            {
                warnings.Add("필터/범위를 통과한 행이 0건입니다(생성된 md 없음).");
            }
            if (result.WrittenCount < result.MatchedCount)
            {
                warnings.Add("--max 로 " + result.WrittenCount.ToString(CultureInfo.InvariantCulture) + "건만 기록했습니다(매칭 "
                             + result.MatchedCount.ToString(CultureInfo.InvariantCulture) + "건).");
            }
            if (result.ScopeDiagnostic != null) warnings.Add("[범위 불일치] 선택 소스와 xls 검출 경로가 하나도 일치하지 않습니다.");
            if (result.ScopeAmbiguousWarning != null) warnings.Add(result.ScopeAmbiguousWarning);

            return new XlsSplitRunReport
            {
                InputXls = result.InputPath.Length > 0 ? result.InputPath : opts.InputPath,
                InputSizeBytes = size,
                InputSha256 = sha,
                OutDir = result.OutputDir,
                GuidesDir = string.IsNullOrWhiteSpace(guidesDir) ? null : guidesDir,
                StartedUtc = startedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                ElapsedMs = elapsedMs,
                ToolVersion = ResolveToolVersion(),
                Options = reportOpts,
                Sheet = result.SheetName + " (index " + result.SheetIndex.ToString(CultureInfo.InvariantCulture)
                        + ", " + result.SheetPick + ")",
                TotalRows = result.TotalDataRows,
                MatchedRows = result.MatchedCount,
                WrittenMd = result.WrittenCount,
                CheckerFolders = result.UniqueCheckers,
                CheckerCounts = result.CheckerCounts
                    .Select(c => new XlsSplitReportCheckerCount { Key = c.Key, Count = c.Count }).ToList(),
                Assignments = assignments,
                UnmappedCheckers = unmapped,
                Scope = new XlsSplitReportScope
                {
                    Mismatch = result.ScopeMismatch,
                    Diagnostic = result.ScopeDiagnostic,
                    AmbiguousWarning = result.ScopeAmbiguousWarning,
                },
                Warnings = warnings,
            };
        }

        /// <summary>
        /// Write <paramref name="report"/> as json to <paramref name="reportPath"/> and a human-readable summary to
        /// the companion "&lt;stem&gt;.log". Creates the parent directory. Returns false (with <paramref name="error"/>
        /// set) instead of throwing, so a read-only/locked log folder can never fail an export.
        /// <para>
        /// ENFORCES the hard contract stated at the top of this file: a report path that lands INSIDE the export
        /// output tree (<see cref="XlsSplitRunReport.OutDir"/>, or any subdirectory of it) is REJECTED before anything
        /// is created — the caller only had a comment promising this before, so <c>--out X --report X\r.json</c>
        /// (or a GUI <c>--log-dir</c> pointed at the output folder) silently broke "체커 폴더 + 항목 md만, 부산물 0".
        /// Rejection is a normal false/error return, so a rejected report never fails the export.
        /// </para>
        /// </summary>
        public static bool TryWrite(string reportPath, XlsSplitRunReport report, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(reportPath)) { error = "report path is empty"; return false; }

            try
            {
                string full = Path.GetFullPath(reportPath.Trim().Trim('"'));

                // Output-tree guard. Checked BEFORE Directory.CreateDirectory so a rejected path cannot even
                // create a folder inside (or as) the output tree.
                if (IsInsideOutputTree(full, report.OutDir, out string? outFull))
                {
                    error = "리포트 경로가 [XLS 분리] 출력 폴더 안입니다 — 출력 계약(체커 폴더 + 항목 md만, 부산물 0)을 "
                            + "깨므로 기록하지 않았습니다. 출력 폴더 밖 경로를 지정하세요. "
                            + "report=" + full + " / out=" + outFull;
                    return false;
                }

                string? dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // json: UTF-8 WITHOUT BOM + LF (a BOM trips PowerShell 5.1's ConvertFrom-Json).
                string json = JsonSerializer.Serialize(report, JsonOpts).Replace("\r\n", "\n");
                File.WriteAllText(full, json + "\n", new UTF8Encoding(false));

                // human summary: UTF-8 WITH BOM + CRLF, because it is read by operators in 메모장 and by PS 5.1
                // Get-Content, both of which mangle BOM-less UTF-8 한글.
                File.WriteAllText(CompanionLogPath(full),
                                  BuildHumanSummary(report).Replace("\r\n", "\n").Replace("\n", "\r\n"),
                                  new UTF8Encoding(true));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// True when <paramref name="reportFullPath"/> IS the export output folder or sits anywhere under it.
        /// The companion "&lt;stem&gt;.log" lives in the same directory, so guarding the json path guards both.
        /// An empty/unresolvable <paramref name="outDir"/> means "no output tree to protect" → false (never
        /// blocks a write on missing information).
        /// </summary>
        public static bool IsInsideOutputTree(string reportFullPath, string? outDir, out string? outFullPath)
        {
            outFullPath = null;
            if (string.IsNullOrWhiteSpace(outDir)) return false;

            string outFull;
            try { outFull = Path.GetFullPath(outDir!.Trim().Trim('"')); }
            catch { return false; }

            outFull = outFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (outFull.Length == 0) return false;
            outFullPath = outFull;

            // Windows paths: case-insensitive. Compare against "<out>" and "<out>\" so a sibling folder whose
            // name merely STARTS with the output folder name (out2 vs out) is not falsely rejected.
            const StringComparison Cmp = StringComparison.OrdinalIgnoreCase;
            if (string.Equals(reportFullPath, outFull, Cmp)) return true;
            return reportFullPath.StartsWith(outFull + Path.DirectorySeparatorChar, Cmp)
                || reportFullPath.StartsWith(outFull + Path.AltDirectorySeparatorChar, Cmp);
        }

        /// <summary>Path of the human summary that accompanies a json report ("&lt;stem&gt;.log").</summary>
        public static string CompanionLogPath(string reportJsonPath)
        {
            string dir = Path.GetDirectoryName(reportJsonPath) ?? ".";
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(reportJsonPath) + ".log");
        }

        /// <summary>The short "read this first" transcript: the same facts as the json, in run order.</summary>
        public static string BuildHumanSummary(XlsSplitRunReport r)
        {
            var sb = new StringBuilder();
            sb.Append("[XLS 분리] 실행 리포트\n");
            sb.Append("시작(UTC)   : ").Append(r.StartedUtc).Append('\n');
            sb.Append("소요(ms)    : ").Append(r.ElapsedMs.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("도구 버전   : ").Append(r.ToolVersion).Append('\n');
            sb.Append("입력 xls    : ").Append(r.InputXls).Append('\n');
            sb.Append("입력 크기   : ").Append(r.InputSizeBytes.ToString(CultureInfo.InvariantCulture)).Append(" bytes\n");
            sb.Append("입력 sha256 : ").Append(r.InputSha256).Append('\n');
            sb.Append("시트        : ").Append(r.Sheet).Append('\n');
            sb.Append("출력 폴더   : ").Append(r.OutDir).Append('\n');
            sb.Append("guides      : ").Append(r.GuidesDir ?? "(없음)").Append('\n');
            sb.Append("범위 필터   : ").Append(r.Options.FilesFromUsed
                ? "사용 (root=" + (r.Options.Root ?? "(없음)") + ", files-from=" + (r.Options.FilesFrom ?? "") + ")"
                : "없음 (전건)").Append('\n');
            sb.Append("필터        : severity=")
              .Append(r.Options.Severities.Count == 0 ? "(없음)" : string.Join(",", r.Options.Severities))
              .Append(" checker=").Append(r.Options.Checker ?? "(없음)")
              .Append(" status=").Append(r.Options.Status ?? "(없음)")
              .Append(" max=").Append(r.Options.Max?.ToString(CultureInfo.InvariantCulture) ?? "(없음)").Append('\n');
            sb.Append("행/기록     : 전체 ").Append(r.TotalRows.ToString(CultureInfo.InvariantCulture))
              .Append(" · 매칭 ").Append(r.MatchedRows.ToString(CultureInfo.InvariantCulture))
              .Append(" · 기록 md ").Append(r.WrittenMd.ToString(CultureInfo.InvariantCulture))
              .Append(" · 체커 폴더 ").Append(r.CheckerFolders.ToString(CultureInfo.InvariantCulture)).Append('\n');

            sb.Append("\n[체커별 건수]\n");
            foreach (XlsSplitReportCheckerCount c in r.CheckerCounts)
            {
                sb.Append("  ").Append(c.Key.Length > 0 ? c.Key : "(체커 키 없음)")
                  .Append(" : ").Append(c.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            if (r.CheckerCounts.Count == 0) sb.Append("  (없음)\n");

            sb.Append("\n[규칙 지정/부착]\n");
            foreach (XlsSplitReportAssignment a in r.Assignments)
            {
                sb.Append("  ").Append(a.CheckerKey).Append(" -> ").Append(a.RuleName ?? "(미지정)")
                  .Append(" · 규칙파일 ").Append(a.RuleExists ? "있음" : "없음")
                  .Append(" · 부착 ").Append(a.ItemsAttached.ToString(CultureInfo.InvariantCulture))
                  .Append('/').Append(a.ItemCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            if (r.Assignments.Count == 0) sb.Append("  (매핑 미실행)\n");
            sb.Append("미매핑 체커 : ")
              .Append(r.UnmappedCheckers.Count == 0 ? "(없음)" : string.Join(", ", r.UnmappedCheckers)).Append('\n');

            sb.Append("\n[범위 진단]\n");
            sb.Append("  불일치     : ").Append(r.Scope.Mismatch ? "예" : "아니오").Append('\n');
            if (r.Scope.Diagnostic != null) sb.Append("  ").Append(r.Scope.Diagnostic.Replace("\n", "\n  ")).Append('\n');
            if (r.Scope.AmbiguousWarning != null) sb.Append("  ").Append(r.Scope.AmbiguousWarning).Append('\n');

            sb.Append("\n[경고]\n");
            foreach (string w in r.Warnings) sb.Append("  - ").Append(w.Replace("\n", " ")).Append('\n');
            if (r.Warnings.Count == 0) sb.Append("  (없음)\n");
            return sb.ToString();
        }

        private static string Sha256OfFile(string path)
        {
            using FileStream fs = File.OpenRead(path);
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(fs);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // Version of the Core assembly (all [XLS 분리] paths — CLI and GUI — share it), so a report can be tied back
        // to the build that produced it. Informational version first (it carries any +sha suffix).
        private static string ResolveToolVersion()
        {
            try
            {
                Assembly asm = typeof(XlsSplitReportWriter).Assembly;
                string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                string name = asm.GetName().Name ?? "SparrowXlsExport.Core";
                string ver = !string.IsNullOrWhiteSpace(info) ? info! : (asm.GetName().Version?.ToString() ?? "0.0.0.0");
                return name + " " + ver;
            }
            catch { return "unknown"; }
        }

        private static bool SafeDirExists(string dir)
        {
            try { return Directory.Exists(dir); }
            catch { return false; }
        }
    }
}
