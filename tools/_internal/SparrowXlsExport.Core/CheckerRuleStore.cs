// CheckerRuleStore: the persistence layer for the Track C "named rule library + explicit checker assignments"
// model. It owns two things under a guides directory (default: <skill>\references\checkers, overridable in the
// GUI/tests via --guides-dir):
//
//   1) RULE LIBRARY  — every "<이름>.md" in the folder whose name does NOT start with '_' is a named rule
//                       (name = filename without extension, content = file body). '_'-prefixed files
//                       (_TEMPLATE.md, _BACKLOG.md, ...) and the assignments file are library-invisible.
//   2) ASSIGNMENTS   — "_assignments.json": { "<체커 키>": "<규칙 이름>", ... }. ONLY entries the user made
//                       explicitly. There is NO name-based auto-mapping: a rule file named exactly like a
//                       checker key is NOT applied to that checker unless an assignment row points to it.
//
// Deliberately separate from CheckerRuleMapper (which consumes assignments to embed rule text into exporter
// output). This class is pure storage: list/read/write/delete rules and load/save/set/remove assignments.
//
// Encoding contract: rule md are written UTF-8 WITHOUT BOM, LF-normalized, with exactly one trailing newline;
// reads strip a leading U+FEFF so BOM-saved rules round-trip cleanly. _assignments.json is UTF-8 without BOM.
// Every read path is defensive: missing/corrupt inputs yield empty results, never throw.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SparrowXlsExport.Core
{
    /// <summary>Storage for the named rule library and the explicit checker→rule assignments. See file header.</summary>
    public static class CheckerRuleStore
    {
        /// <summary>Assignments file name inside the guides directory. Not a rule (never listed as one).</summary>
        public const string AssignmentsFileName = "_assignments.json";

        /// <summary>Absolute path of the assignments json for a guides directory (existence not checked).</summary>
        public static string AssignmentsPath(string guidesDir) => Path.Combine(guidesDir, AssignmentsFileName);

        /// <summary>Absolute path of the "<name>.md" file backing a named rule (existence not checked).</summary>
        public static string RulePathFor(string guidesDir, string ruleName) => Path.Combine(guidesDir, ruleName + ".md");

        // --- rule library ------------------------------------------------------

        /// <summary>Named rules in the library: every "<name>.md" whose name does NOT start with '_'.
        /// Ordinal-sorted; missing/unreadable directory => empty. Never throws.</summary>
        public static IReadOnlyList<string> ListRules(string guidesDir)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(guidesDir) || !SafeDirExists(guidesDir)) return names;

            string[] files;
            try { files = Directory.GetFiles(guidesDir, "*.md", SearchOption.TopDirectoryOnly); }
            catch { return names; }

            foreach (string f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (name.Length == 0 || name[0] == '_') continue;   // '_'-prefixed = reserved/library-invisible
                names.Add(name);
            }
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        /// <summary>True when the named rule's md file exists. Never throws.</summary>
        public static bool RuleExists(string guidesDir, string ruleName)
        {
            if (string.IsNullOrWhiteSpace(guidesDir) || !IsValidRuleName(ruleName)) return false;
            try { return File.Exists(RulePathFor(guidesDir, ruleName)); }
            catch { return false; }
        }

        /// <summary>Rule body (UTF-8, leading U+FEFF stripped), or null when the rule file is absent/unreadable.</summary>
        public static string? ReadRule(string guidesDir, string ruleName)
        {
            if (string.IsNullOrWhiteSpace(guidesDir) || !IsValidRuleName(ruleName)) return null;
            string path = RulePathFor(guidesDir, ruleName);
            try
            {
                if (!File.Exists(path)) return null;
                return ReadUtf8(path);
            }
            catch { return null; }
        }

        /// <summary>Write (create/overwrite) a named rule. LF-normalized, one trailing newline, UTF-8 WITHOUT BOM.
        /// Creates the guides directory if needed. Throws <see cref="ArgumentException"/> on an invalid rule name.</summary>
        public static void WriteRule(string guidesDir, string ruleName, string content)
        {
            if (string.IsNullOrWhiteSpace(guidesDir)) throw new ArgumentException("guidesDir is required", nameof(guidesDir));
            if (!IsValidRuleName(ruleName)) throw new ArgumentException("invalid rule name: '" + ruleName + "'", nameof(ruleName));

            Directory.CreateDirectory(guidesDir);
            string normalized = (content ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            if (normalized.Length > 0 && !normalized.EndsWith("\n", StringComparison.Ordinal)) normalized += "\n";
            File.WriteAllText(RulePathFor(guidesDir, ruleName), normalized, new UTF8Encoding(false));
        }

        /// <summary>Delete a named rule's md file. Returns true when a file was deleted. Never throws.</summary>
        public static bool DeleteRule(string guidesDir, string ruleName)
        {
            if (string.IsNullOrWhiteSpace(guidesDir) || !IsValidRuleName(ruleName)) return false;
            string path = RulePathFor(guidesDir, ruleName);
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch { return false; }
        }

        // --- assignments -------------------------------------------------------

        /// <summary>Load explicit checker→rule assignments. Missing/corrupt json => empty map. Never throws.
        /// Entries with an empty key or value are dropped defensively.</summary>
        public static Dictionary<string, string> LoadAssignments(string guidesDir)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(guidesDir)) return map;

            string path = AssignmentsPath(guidesDir);
            string json;
            try
            {
                if (!File.Exists(path)) return map;
                json = File.ReadAllText(path);   // ReadAllText auto-detects + strips a BOM
            }
            catch { return map; }

            try
            {
                Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (parsed == null) return map;
                foreach (var kv in parsed)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                    map[kv.Key] = kv.Value;
                }
            }
            catch { /* corrupt json => empty map (defensive) */ }
            return map;
        }

        /// <summary>Overwrite _assignments.json with the given map (indented, UTF-8 WITHOUT BOM). Empty/blank
        /// entries are skipped. Creates the guides directory if needed.</summary>
        public static void SaveAssignments(string guidesDir, IReadOnlyDictionary<string, string> assignments)
        {
            if (string.IsNullOrWhiteSpace(guidesDir)) throw new ArgumentException("guidesDir is required", nameof(guidesDir));

            // Deterministic on-disk order (ordinal by checker key) so re-saves diff minimally.
            var clean = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (assignments != null)
            {
                foreach (var kv in assignments)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                    clean[kv.Key] = kv.Value;
                }
            }

            Directory.CreateDirectory(guidesDir);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep 한글 readable
            };
            string json = JsonSerializer.Serialize(clean, options);
            File.WriteAllText(AssignmentsPath(guidesDir), json, new UTF8Encoding(false));
        }

        /// <summary>Set (or replace) one checker's assignment and persist. Merges with existing entries so
        /// assignments for checkers not present in the current xls are preserved (assignment memory).</summary>
        public static void SaveAssignment(string guidesDir, string checkerKey, string ruleName)
        {
            if (string.IsNullOrWhiteSpace(checkerKey) || string.IsNullOrWhiteSpace(ruleName)) return;
            var map = LoadAssignments(guidesDir);
            map[checkerKey] = ruleName;
            SaveAssignments(guidesDir, map);
        }

        /// <summary>Remove one checker's assignment and persist. Returns true when an entry was removed.</summary>
        public static bool RemoveAssignment(string guidesDir, string checkerKey)
        {
            if (string.IsNullOrWhiteSpace(checkerKey)) return false;
            var map = LoadAssignments(guidesDir);
            if (!map.Remove(checkerKey)) return false;
            SaveAssignments(guidesDir, map);
            return true;
        }

        // --- helpers -----------------------------------------------------------

        /// <summary>A rule name is a bare filename stem: non-empty, no path separators or invalid filename
        /// characters, and not '_'-prefixed (reserved for library-invisible files like _TEMPLATE / _assignments).</summary>
        public static bool IsValidRuleName(string? ruleName)
        {
            if (string.IsNullOrWhiteSpace(ruleName)) return false;
            if (ruleName[0] == '_') return false;
            if (ruleName.IndexOf('/') >= 0 || ruleName.IndexOf('\\') >= 0) return false;
            foreach (char c in Path.GetInvalidFileNameChars())
                if (ruleName.IndexOf(c) >= 0) return false;
            // reject a trailing dot/space (Windows path quirk) and pure-dot names
            string trimmed = ruleName.Trim();
            if (trimmed != ruleName) return false;
            if (trimmed == "." || trimmed == "..") return false;
            return true;
        }

        private static bool SafeDirExists(string dir)
        {
            try { return Directory.Exists(dir); }
            catch { return false; }
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
