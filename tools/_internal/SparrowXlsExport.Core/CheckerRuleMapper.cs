// CheckerRuleMapper: OPTIONAL, self-contained checker-rule attachment layer for the Track C exporter.
//
// The exporter (SparrowExporter) is left PURE — it only ever writes <체커 키>\{ID}_{파일명}_{라인}.md with no
// rule text. This mapper runs AFTER the exporter, over its output tree, and — when the user has EXPLICITLY
// assigned a named library rule to a checker — embeds that rule's text into EVERY item md of the matching
// checker folder as a "## 매핑 규칙 (<키>)" section. Self-contained by design: the air-gapped LLM ingests one md
// at a time, so each md must carry its own rule + source without depending on sibling files.
//
// Assignment model (NO name-based auto-mapping): the rule library and the checker→rule assignments live in a
// guides directory and are owned by CheckerRuleStore. A checker X is attached ONLY when _assignments.json maps
// "X" -> "<규칙 이름>" AND that rule file (<guidesDir>\<규칙 이름>.md) exists. A rule file merely NAMED like a
// checker key is NEVER applied on that basis — the user must assign it. This is the deliberate reversal of the
// old "매핑 있음 = 파일 있음" (filename==key) behavior.
//
// Contracts baked in:
//  - Matching is by the ORIGINAL checker key read from the md's "| 체커 키 | X |" field, NOT the folder name
//    (the folder name may have been filesystem-sanitized by SparrowExporter.San/CheckerDirName; the field is
//    the source of truth). The assignment lookup key is that original checker key.
//  - Embed position: BETWEEN "## 체커 설명" and "## 소스 코드" (무엇 -> 어떻게 -> 코드).
//  - Idempotent: an existing "## 매핑 규칙 ..." section is REPLACED, never duplicated; two runs are byte-equal.
//  - Pure fallback: no assignment (or an assignment whose rule file is missing) -> the folder's md stay pure;
//    any stale mapping section is stripped back out (self-healing).
//  - Missing/empty guidesDir, or no _assignments.json -> everything Unmapped, no md touched, NEVER throws.
//  - Encoding: item md are read/written as UTF-8 preserving the exporter's BOM choice (no BOM) and newlines.
//    Rule files are read (by CheckerRuleStore) as UTF-8 with a leading U+FEFF (BOM) stripped.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SparrowXlsExport.Core
{
    /// <summary>Per-checker outcome of a <see cref="CheckerRuleMapper.Apply"/> run — the evidence a later reader
    /// (or an AI reading the Track C run report) needs to tell "no rule assigned" from "assigned but the rule file
    /// is gone" from "attached to N items".</summary>
    public sealed class CheckerMapDetail
    {
        public CheckerMapDetail(string checkerKey, string? ruleName, bool ruleExists, int itemsAttached, int itemCount)
        {
            CheckerKey = checkerKey;
            RuleName = ruleName;
            RuleExists = ruleExists;
            ItemsAttached = itemsAttached;
            ItemCount = itemCount;
        }

        /// <summary>Original 체커 키 (from the item md field), i.e. the assignment lookup key.</summary>
        public string CheckerKey { get; }

        /// <summary>Rule name the user assigned to this checker; null when unassigned.</summary>
        public string? RuleName { get; }

        /// <summary>True when <see cref="RuleName"/> is set AND its "&lt;이름&gt;.md" was readable (so it was embedded).
        /// False with a non-null RuleName = dangling assignment (rule deleted) — the md stayed pure.</summary>
        public bool RuleExists { get; }

        /// <summary>Item md files that received the rule embed for this checker (0 when unmapped).</summary>
        public int ItemsAttached { get; }

        /// <summary>Item md files in this checker's folder.</summary>
        public int ItemCount { get; }
    }

    /// <summary>Outcome of a <see cref="CheckerRuleMapper.Apply"/> run.</summary>
    public sealed class MapResult
    {
        public MapResult(IReadOnlyList<string> mapped, IReadOnlyList<string> unmapped, int itemsTouched,
                         IReadOnlyList<CheckerMapDetail>? details = null)
        {
            Mapped = mapped;
            Unmapped = unmapped;
            ItemsTouched = itemsTouched;
            Details = details ?? Array.Empty<CheckerMapDetail>();
        }

        /// <summary>Original checker keys that had an explicit assignment (with an existing rule) embedded into
        /// their folder's md.</summary>
        public IReadOnlyList<string> Mapped { get; }

        /// <summary>Original checker keys with no assignment — or an assignment whose rule file was missing —
        /// so their md were left pure.</summary>
        public IReadOnlyList<string> Unmapped { get; }

        /// <summary>Number of item md files that received a rule embed (across all mapped checker folders).</summary>
        public int ItemsTouched { get; }

        /// <summary>One entry per checker folder visited (ordinal by folder), mapped or not. Diagnostic detail for
        /// the run report; empty when a caller constructed a MapResult without it.</summary>
        public IReadOnlyList<CheckerMapDetail> Details { get; }
    }

    /// <summary>Per-checker-folder view of an export tree (for the GUI mapping panel).</summary>
    public sealed class CheckerFolderInfo
    {
        public CheckerFolderInfo(string key, string folderName, string folderPath, int itemCount)
        {
            Key = key;
            FolderName = folderName;
            FolderPath = folderPath;
            ItemCount = itemCount;
        }

        /// <summary>Original checker key (from the md field); falls back to the folder name when absent.</summary>
        public string Key { get; }

        public string FolderName { get; }
        public string FolderPath { get; }
        public int ItemCount { get; }
    }

    /// <summary>Attaches cached per-checker rule guides to exporter output. Pure/idempotent; never throws on
    /// missing inputs. Does NOT modify <see cref="SparrowExporter"/> output semantics — runs strictly after it.</summary>
    public static class CheckerRuleMapper
    {
        private const string CheckerKeyLabel = "체커 키";
        private const string DescHeader = "\n## 체커 설명\n";
        private const string SourceHeader = "\n## 소스 코드\n";
        private const string MappingHeaderPrefix = "\n## 매핑 규칙 ";

        /// <summary>
        /// For each first-level checker folder under <paramref name="exportDir"/>: read its original checker key
        /// from an item md field, look up the user's assignment for that key in <paramref name="guidesDir"/>'s
        /// _assignments.json, and — when an assigned rule exists — embed that rule's text as a "## 매핑 규칙 (key)"
        /// section into EVERY item md of that folder (idempotent replace). Checkers with no assignment (or an
        /// assignment whose rule file is missing) are left pure (stale mapping sections stripped). There is NO
        /// name-based auto-mapping: a rule file named like the checker key does nothing unless it is assigned.
        /// Missing/empty guidesDir or no assignments => all Unmapped. Never throws for missing directories/files.
        /// </summary>
        public static MapResult Apply(string exportDir, string guidesDir)
        {
            var mapped = new List<string>();
            var unmapped = new List<string>();
            var details = new List<CheckerMapDetail>();
            int itemsTouched = 0;

            if (string.IsNullOrWhiteSpace(exportDir) || !Directory.Exists(exportDir))
            {
                return new MapResult(mapped, unmapped, itemsTouched, details);
            }

            bool guidesAvailable = !string.IsNullOrWhiteSpace(guidesDir) && Directory.Exists(guidesDir);
            Dictionary<string, string> assignments = guidesAvailable
                ? CheckerRuleStore.LoadAssignments(guidesDir!)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string folder in Directory.GetDirectories(exportDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                List<string> mdFiles;
                try
                {
                    mdFiles = Directory.GetFiles(folder, "*.md", SearchOption.TopDirectoryOnly)
                                       .OrderBy(p => p, StringComparer.Ordinal).ToList();
                }
                catch
                {
                    continue;
                }
                if (mdFiles.Count == 0) continue;

                string key = ReadCheckerKey(mdFiles[0]) ?? Path.GetFileName(folder);

                // Attach ONLY when the user explicitly assigned a rule to this checker AND that rule file exists.
                // No assignment, or an assignment whose rule was deleted, falls through to the pure/self-heal path.
                string? rule = null;
                string? assignedRuleName = null;
                if (guidesAvailable
                    && assignments.TryGetValue(key, out string? ruleName)
                    && !string.IsNullOrWhiteSpace(ruleName))
                {
                    assignedRuleName = ruleName;
                    rule = CheckerRuleStore.ReadRule(guidesDir!, ruleName!);
                }

                if (rule != null)
                {
                    foreach (string md in mdFiles)
                    {
                        RewriteItem(md, original => EmbedRule(original, key, rule));
                        itemsTouched++;
                    }
                    mapped.Add(key);
                    details.Add(new CheckerMapDetail(key, assignedRuleName, true, mdFiles.Count, mdFiles.Count));
                }
                else
                {
                    // Unassigned (or dangling assignment): keep pure. Strip any previously-embedded mapping section.
                    foreach (string md in mdFiles)
                    {
                        RewriteItem(md, StripMappingSection);
                    }
                    unmapped.Add(key);
                    // assignedRuleName != null here means the assignment survived but its rule file did not
                    // (dangling) — recorded as RuleExists=false so the report can name the real cause.
                    details.Add(new CheckerMapDetail(key, assignedRuleName, false, 0, mdFiles.Count));
                }
            }

            return new MapResult(mapped, unmapped, itemsTouched, details);
        }

        /// <summary>Enumerate the checker folders of an export tree with their original key and item count.
        /// Read-only; does not modify any md. Used by the GUI to render the mapping panel.</summary>
        public static IReadOnlyList<CheckerFolderInfo> ListCheckers(string exportDir)
        {
            var list = new List<CheckerFolderInfo>();
            if (string.IsNullOrWhiteSpace(exportDir) || !Directory.Exists(exportDir)) return list;

            foreach (string folder in Directory.GetDirectories(exportDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                string[] mdFiles;
                try
                {
                    mdFiles = Directory.GetFiles(folder, "*.md", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }
                if (mdFiles.Length == 0) continue;

                string first = mdFiles.OrderBy(p => p, StringComparer.Ordinal).First();
                string key = ReadCheckerKey(first) ?? Path.GetFileName(folder);
                list.Add(new CheckerFolderInfo(key, Path.GetFileName(folder), folder, mdFiles.Length));
            }

            return list;
        }

        /// <summary>Path of the "&lt;name&gt;.md" file in the guides directory (does not check existence). Retained
        /// for back-compat; prefer <see cref="CheckerRuleStore.RulePathFor"/> for named library rules.</summary>
        public static string GuidePathFor(string guidesDir, string key) => Path.Combine(guidesDir, key + ".md");

        // --- per-file rewrite (BOM/newline preserving, write-only-when-changed) ---
        private static void RewriteItem(string path, Func<string, string> transform)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch { return; }

            bool hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string original = new UTF8Encoding(false).GetString(hadBom ? bytes.Skip(3).ToArray() : bytes);

            string updated = transform(original);
            if (string.Equals(updated, original, StringComparison.Ordinal)) return;   // no-op: leave bytes untouched

            try { File.WriteAllText(path, updated, new UTF8Encoding(hadBom)); }
            catch { /* best-effort; a locked/read-only md is skipped rather than aborting the batch */ }
        }

        // Insert (or replace) the "## 매핑 규칙 (key)" section between 체커 설명 and 소스 코드.
        private static string EmbedRule(string md, string key, string guide)
        {
            md = StripMappingSection(md);   // idempotent: never stack two sections

            int srcIdx = md.IndexOf(SourceHeader, StringComparison.Ordinal);
            if (srcIdx < 0) return md;      // malformed md (no 소스 코드 section) -> leave untouched

            string body = guide.EndsWith("\n", StringComparison.Ordinal) ? guide : guide + "\n";
            string block = "\n## 매핑 규칙 (" + key + ")\n" + body;
            return md.Substring(0, srcIdx) + block + md.Substring(srcIdx);
        }

        // Remove an existing mapping section (from its "\n## 매핑 규칙 " header up to the following "\n## 소스 코드\n").
        // Anchoring on the KNOWN next section (소스 코드) — not a generic "\n## " — is required because the guide
        // body itself contains "## ..." headers, which a naive next-header scan would mistake for the boundary.
        private static string StripMappingSection(string md)
        {
            int mapIdx = md.IndexOf(MappingHeaderPrefix, StringComparison.Ordinal);
            if (mapIdx < 0) return md;
            int srcIdx = md.IndexOf(SourceHeader, mapIdx, StringComparison.Ordinal);
            if (srcIdx < 0) return md;      // defensive: no source section after the mapping section
            return md.Substring(0, mapIdx) + md.Substring(srcIdx);
        }

        // Read the ORIGINAL checker key from the item md's "| 체커 키 | X |" field row. Returns null when the row
        // is absent (e.g. the xls had no 체커 키 value, i.e. the _no-checker fallback folder).
        private static string? ReadCheckerKey(string mdPath)
        {
            string md;
            try { md = ReadUtf8(mdPath); }
            catch { return null; }

            string prefix = "| " + CheckerKeyLabel + " | ";
            foreach (string raw in md.Split('\n'))
            {
                string line = raw.EndsWith("\r", StringComparison.Ordinal) ? raw.Substring(0, raw.Length - 1) : raw;
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!line.EndsWith(" |", StringComparison.Ordinal)) continue;
                string cell = line.Substring(prefix.Length, line.Length - prefix.Length - " |".Length);
                // reverse SparrowExporter.TableCell: <br> -> newline, \| -> | (no-op for real A.B.C_D keys)
                cell = cell.Replace("<br>", "\n").Replace("\\|", "|");
                cell = cell.Trim();
                return cell.Length > 0 ? cell : null;
            }
            return null;
        }

        private static string ReadUtf8(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string text = new UTF8Encoding(false).GetString(bytes);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            return text;
        }
    }
}
