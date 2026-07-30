// SparrowSyntaxFix CLI. Argument-parsing + console-output style mirrors the sibling SparrowXlsExport:
// manual arg loop, aligned summary block, exit codes 0 = success (changed or not), 1 = real error,
// 2 = usage. Discovers .cs files, reads each preserving encoding/BOM/newline, applies the enabled
// Roslyn rewriter(s), and writes back atomically ONLY when the tree text actually changed.
//
//   SparrowSyntaxFix <file-or-dir>... [--files-from index.csv] [--root dir] [--rules list] [--dry-run]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SparrowSyntaxFix
{
    internal static class Program
    {
        private static readonly (string Key, SyntaxRule Rule)[] RuleOrder =
        {
            ("nullvar", SyntaxRule.NullVar),
            ("parens", SyntaxRule.Parens),
            ("objectinitializer", SyntaxRule.ObjectInitializer),
            ("objectvar-safe", SyntaxRule.ObjectVarSafe),
            ("foreachcast", SyntaxRule.ForeachCast),
            ("obviousvar", SyntaxRule.ObviousVar),
            ("objectvar-narrowing", SyntaxRule.ObjectVarNarrowing),
            ("localconst", SyntaxRule.LocalConst),
            ("arrayvar-safe", SyntaxRule.ArrayVarSafe),
            ("arrayvar-narrowing", SyntaxRule.ArrayVarNarrowing),
            ("forvar", SyntaxRule.ForVar),
            ("fieldsplit", SyntaxRule.FieldSplit),
            ("emptystmt", SyntaxRule.EmptyStmt),
            ("forhoist", SyntaxRule.ForHoist),
        };

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* stdout may be redirected */ }

            var targets = new List<string>();
            string? filesFrom = null;
            string? root = null;
            SyntaxRule rules = SyntaxRule.Default;
            bool dryRun = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--files-from": if (!Next(args, ref i, out filesFrom)) return Usage("--files-from requires a value"); break;
                    case "--root": if (!Next(args, ref i, out root)) return Usage("--root requires a value"); break;
                    case "--rules":
                        if (!Next(args, ref i, out string rulesArg)) return Usage("--rules requires a value");
                        if (!TryParseRules(rulesArg, out rules))
                            return Usage("invalid --rules value: " + rulesArg + " (expected: all | "
                                         + string.Join(",", RuleOrder.Select(r => r.Key)) + ")");
                        break;
                    case "--dry-run": dryRun = true; break;
                    case "-h":
                    case "--help": return Usage(null);
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal)) return Usage("unknown option: " + a);
                        targets.Add(a);
                        break;
                }
            }

            if (targets.Count == 0 && filesFrom == null)
                return Usage("at least one <file-or-dir> or --files-from is required");

            try
            {
                return Run(targets, filesFrom, root, rules, dryRun);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;   // real error: unreadable path / IO failure
            }
        }

        private static int Run(List<string> targets, string? filesFrom, string? rootArg, SyntaxRule rules, bool dryRun)
        {
            string root = Path.GetFullPath(rootArg ?? Directory.GetCurrentDirectory());
            if (rootArg != null && !Directory.Exists(root)) throw new DirectoryNotFoundException("--root not found: " + root);

            bool missingExplicit = false;
            var files = FileDiscovery.ExpandTargets(targets, root, m =>
            {
                Console.Error.WriteLine("error: path not found: " + m);
                missingExplicit = true;
            });
            if (missingExplicit) return 1;   // an explicit target that does not exist is a real error

            if (filesFrom != null)
            {
                string idx = Path.IsPathRooted(filesFrom) ? filesFrom : Path.Combine(root, filesFrom);
                idx = Path.GetFullPath(idx);
                if (!File.Exists(idx)) throw new FileNotFoundException("--files-from not found: " + idx);

                // A referenced-but-absent file is a warning, not fatal (the index may list files not in this checkout).
                var indexFiles = FileDiscovery.FromIndex(idx, root, m =>
                    Console.Error.WriteLine("warn: --files-from entry not found (skipped): " + m));

                var merged = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
                foreach (string f in indexFiles) if (merged.Add(f)) files.Add(f);
                files.Sort(StringComparer.OrdinalIgnoreCase);
            }

            int found = files.Count;
            int generatedSkipped = 0, encodingSkipped = 0, changed = 0, errorSkipped = 0;
            var totalCounts = NewCountMap();

            foreach (string file in files)
            {
                if (FileDiscovery.IsGeneratedName(Path.GetFileName(file))) { generatedSkipped++; continue; }

                SourceFile? sf = SourceFileIo.TryRead(file);
                if (sf == null)
                {
                    encodingSkipped++;
                    Console.Error.WriteLine("warn: skipped (not clean UTF-8): " + file);
                    continue;
                }
                if (FileDiscovery.HasAutoGeneratedHeader(sf.Text)) { generatedSkipped++; continue; }

                RewriteResult result;
                try
                {
                    result = RewriteEngine.Rewrite(sf.Text, rules);
                }
                catch (Exception ex)
                {
                    // A rewriter throwing on one pathological file must NOT crash the whole run.
                    // Nothing is written on a throw; log the culprit (file + exception) and continue.
                    errorSkipped++;
                    Console.Error.WriteLine("warn: rewrite failed, skipping file (unchanged): " + file
                        + " :: " + ex.GetType().Name + ": " + ex.Message);
                    continue;
                }
                if (!result.Changed) continue;

                foreach (var kv in result.Counts) totalCounts[kv.Key] += kv.Value;
                changed++;

                string tail = BuildTail(result.Counts, rules);
                if (dryRun)
                {
                    Console.WriteLine("would-change " + file + tail);
                }
                else
                {
                    SourceFileIo.WriteAtomic(file, result.NewText, sf.HasBom);
                    Console.WriteLine("changed " + file + tail);
                }
            }

            string N(long v) => v.ToString(CultureInfo.InvariantCulture);
            Console.WriteLine("rules:            " + RulesText(rules) + (dryRun ? "   (dry-run: no files written)" : ""));
            Console.WriteLine("root:             " + root);
            Console.WriteLine("files found:      " + N(found));
            Console.WriteLine("generated skip:   " + N(generatedSkipped));
            Console.WriteLine("non-UTF8 skip:    " + N(encodingSkipped));
            if (errorSkipped > 0) Console.WriteLine("error skip:       " + N(errorSkipped) + "  (see warn lines above for the file + exception)");
            Console.WriteLine((dryRun ? "files to change:  " : "files changed:    ") + N(changed));
            foreach (var r in RuleOrder)
            {
                if ((rules & r.Rule) == 0) continue;
                Console.WriteLine((r.Key + " edits:").PadRight(18) + N(totalCounts[r.Key]));
            }
            return 0;
        }

        private static bool TryParseRules(string arg, out SyntaxRule rules)
        {
            rules = SyntaxRule.None;
            foreach (string tokenRaw in arg.Split(','))
            {
                string t = tokenRaw.Trim().ToLowerInvariant();
                if (t.Length == 0) continue;
                switch (t)
                {
                    case "all": rules |= SyntaxRule.All; break;
                    case "nullcast":   // legacy alias
                    case "nullvar": rules |= SyntaxRule.NullVar; break;
                    case "parens": rules |= SyntaxRule.Parens; break;
                    case "objectvar-safe": rules |= SyntaxRule.ObjectVarSafe; break;
                    case "foreachcast": rules |= SyntaxRule.ForeachCast; break;
                    case "obviousvar": rules |= SyntaxRule.ObviousVar; break;
                    case "objectvar-narrowing": rules |= SyntaxRule.ObjectVarNarrowing; break;
                    case "localconst": rules |= SyntaxRule.LocalConst; break;
                    case "objectinitializer": rules |= SyntaxRule.ObjectInitializer; break;
                    case "arrayvar-safe": rules |= SyntaxRule.ArrayVarSafe; break;
                    case "arrayvar-narrowing": rules |= SyntaxRule.ArrayVarNarrowing; break;
                    case "forvar": rules |= SyntaxRule.ForVar; break;
                    case "fieldsplit": rules |= SyntaxRule.FieldSplit; break;
                    case "emptystmt": rules |= SyntaxRule.EmptyStmt; break;
                    case "forhoist": rules |= SyntaxRule.ForHoist; break;
                    default: return false;
                }
            }
            return rules != SyntaxRule.None;
        }

        private static string RulesText(SyntaxRule r)
        {
            var parts = new List<string>();
            foreach (var rule in RuleOrder)
            {
                if ((r & rule.Rule) != 0) parts.Add(rule.Key);
            }
            return string.Join(",", parts);
        }

        private static Dictionary<string, long> NewCountMap()
        {
            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var r in RuleOrder) map[r.Key] = 0;
            return map;
        }

        private static string BuildTail(Dictionary<string, int> counts, SyntaxRule rules)
        {
            var parts = new List<string>();
            foreach (var r in RuleOrder)
            {
                if ((rules & r.Rule) == 0) continue;
                parts.Add(r.Key + "=" + counts[r.Key]);
            }
            return "  " + string.Join(" ", parts);
        }

        private static bool Next(string[] args, ref int i, out string value)
        {
            if (i + 1 >= args.Length) { value = ""; return false; }
            value = args[++i];
            return true;
        }

        private static int Usage(string? message)
        {
            var w = message == null ? Console.Out : Console.Error;
            if (message != null) w.WriteLine("error: " + message);
            w.WriteLine("usage: SparrowSyntaxFix <file-or-dir>... [options]");
            w.WriteLine("options:");
            w.WriteLine("  --files-from <index.csv>  read target .cs paths from a CSV (파일명/경로 column) or newline list");
            w.WriteLine("  --root <dir>              base dir for resolving relative paths (default: current dir)");
            w.WriteLine("  --rules <list>            comma list of rules or 'all' (default: safe subset)");
            w.WriteLine("                            rules: nullvar(nullcast alias),parens,objectvar-safe,foreachcast,");
            w.WriteLine("                                   obviousvar,objectvar-narrowing,localconst,");
            w.WriteLine("                                   objectinitializer,arrayvar-safe,arrayvar-narrowing,");
            w.WriteLine("                                   forvar,fieldsplit,emptystmt,forhoist  (last four: opt-in, not in default)");
            w.WriteLine("  --dry-run                 report per-file/per-rule counts without writing");
            w.WriteLine("exit codes: 0 = success (changed or not), 1 = error, 2 = usage");
            return message == null ? 0 : 2;
        }
    }
}
