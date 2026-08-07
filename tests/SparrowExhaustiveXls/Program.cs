// SparrowExhaustiveXls: EXHAUSTIVE generator for the Sparrow [코드 규칙]·[주석·레이아웃] xls coverage test.
//
// Reads the real MyApp issues .xls (NPOI, BIFF), and for EVERY [코드 규칙]·[주석·레이아웃]-relevant finding (none skipped)
// extracts the REAL flagged code line + just enough of the "소스 코드" context to PARSE, wraps it minimally
// into a compilation unit, validates that it parses with the SAME Roslyn version the tools use, writes a
// per-checker batch of .cs files, and emits a manifest mapping finding -> generated file + original flagged
// text + wrapper + parse status. The ps1 runner then runs the matching tool+rule over each batch and reports
// per-finding transformed vs not-transformed.
//
// This is a PARSE+TRANSFORM coverage measurement (not a Sparrow re-analysis, not a compile check). The tools'
// rewriters use CSharpSyntaxTree.ParseText (syntax only), so a snippet only needs to PARSE, not compile.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NPOI.SS.UserModel;

namespace SparrowExhaustiveXls
{
    internal static class Program
    {
        // ---- [코드 규칙]·[주석·레이아웃] checker configuration -----------------------------------------------------------
        // slug     : output subdir + manifest key
        // tool     : "syntax" | "comment"
        // rules    : the --rules string passed to the tool for this checker
        // mode     : extraction/wrapping strategy
        private sealed class CheckerCfg
        {
            public string Key;
            public string Slug;
            public string Tool;
            public string Rules;
            public string Mode;      // single | fwd | sym | member | block
            public string Wrapper;   // method | classOrMethod | member | comment
            public string Note;      // annotation for the report (e.g. inactive rule / replacement)
        }

        private static readonly CheckerCfg[] Checkers =
        {
            // ---- [코드 규칙] (SparrowSyntaxFix) ----
            new CheckerCfg{ Key="PRACTICE.OBVIOUS_VARIABLE_TYPE.NOT_USED_IMPLICIT_TYPING", Slug="A_obviousvar", Tool="syntax", Rules="nullvar,obviousvar,objectvar-safe,foreachcast", Mode="single", Wrapper="method" },
            new CheckerCfg{ Key="PRACTICE.OBJECT_INSTANTIATION.NOT_USED_IMPLICIT_TYPING", Slug="A_objinst", Tool="syntax", Rules="nullvar,obviousvar,objectvar-safe,foreachcast", Mode="single", Wrapper="method" },
            new CheckerCfg{ Key="PRACTICE.LOOP_VARIABLE.NOT_USED_IMPLICIT_TYPING", Slug="A_loopvar", Tool="syntax", Rules="nullvar,obviousvar,objectvar-safe,foreachcast", Mode="single", Wrapper="method" },
            new CheckerCfg{ Key="MISSING_PARENTHESIS_IN_EXPRESSION", Slug="A_parens", Tool="syntax", Rules="parens", Mode="sym", Wrapper="method" },
            new CheckerCfg{ Key="PRACTICE.OBJECT_INITIALIZATION.NOT_USED_INITIALIZER", Slug="A_objinit", Tool="syntax", Rules="objectinitializer", Mode="fwd", Wrapper="method", Note="review-needed; guarded/conditional inits are expected NOT to transform" },
            // ---- [주석·레이아웃] (SparrowCommentFix) ----
            new CheckerCfg{ Key="FORMATTING.COMMENT.MISSING_SPACE_AFTER_DELIMITER", Slug="B_space", Tool="comment", Rules="space", Mode="single", Wrapper="comment" },
            new CheckerCfg{ Key="FORMATTING.COMMENT.MISSING_PERIOD", Slug="B_period", Tool="comment", Rules="period", Mode="single", Wrapper="comment" },
            new CheckerCfg{ Key="FORMATTING.COMMENT.LOWERCASE_FIRST_LETTER", Slug="B_capitalize", Tool="comment", Rules="capitalize", Mode="single", Wrapper="comment" },
            new CheckerCfg{ Key="FORMATTING.COMMENT.BLOCK_OF_ASTERISK", Slug="B_asterisk", Tool="comment", Rules="flatten", Mode="block", Wrapper="comment", Note="matching rule 'asterisk' is INACTIVE (deferred); ran active replacement 'flatten'" },
            new CheckerCfg{ Key="FORMATTING.BETWEEN_MEMBER_DEFINITION.MISSING_BLANK_LINE", Slug="B_memberblank", Tool="comment", Rules="memberblank", Mode="member", Wrapper="member" },
            new CheckerCfg{ Key="FORMATTING.CONTINUATION_LINE.BAD_INDENTATION", Slug="B_continuation", Tool="comment", Rules="continuation", Mode="sym", Wrapper="method" },
            new CheckerCfg{ Key="USE_ONE_STATEMENT_PER_LINE", Slug="B_onestatement", Tool="comment", Rules="onestatement", Mode="single", Wrapper="method" },
            new CheckerCfg{ Key="USE_ONE_DECLARATION_PER_LINE", Slug="B_onedeclaration", Tool="comment", Rules="onedeclaration", Mode="single", Wrapper="classOrMethod" },
            new CheckerCfg{ Key="FORMATTING.LINQ.QUERY_CLAUSE_ALIGNMENT", Slug="B_linqalign", Tool="comment", Rules="linqalign", Mode="sym", Wrapper="method" },
            new CheckerCfg{ Key="MISSING_BLANK_LINE_BEFORE_COMMENT", Slug="B_blankbefore", Tool="comment", Rules="trailing", Mode="single", Wrapper="comment", Note="matching rule 'blankline' is REMOVED; ran active replacement 'trailing' (real target = inline trailing comments)" },
        };

        // Well-known Sparrow columns.
        private const string CKey = "체커 키";
        private const string CLine = "라인";
        private const string CFile = "파일명";
        private const string CPath = "경로";
        private const string CSource = "소스 코드";

        private static readonly Regex PrefixRe = new Regex(@"^\s*(\d+)\.", RegexOptions.Compiled);
        private static readonly CSharpParseOptions ParseOpts =
            new CSharpParseOptions(LanguageVersion.Latest, documentationMode: DocumentationMode.None);

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

            string xls = null, outDir = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--xls": xls = args[++i]; break;
                    case "--out": outDir = args[++i]; break;
                    default: Console.Error.WriteLine("unknown arg: " + args[i]); return 2;
                }
            }
            if (xls == null || outDir == null) { Console.Error.WriteLine("usage: --xls <path> --out <dir>"); return 2; }
            // 보안: 실 xls 의 경로/파일명은 stdout/stderr 에 찍지 않는다. 이 출력은 tests\_logs\validate-*.log 로
            // 들어가고 CONTRIBUTING 은 실패 신고 시 그 로그 첨부를 지시한다 → 사내 리포트 파일명(사람 ID 포함)이
            // 공개 레포 이슈에 실리면 안 된다.
            if (!File.Exists(xls)) { Console.Error.WriteLine("xls not found (path withheld from log)"); return 3; }

            var cfgByKey = Checkers.ToDictionary(c => c.Key, c => c);
            string genRoot = Path.Combine(outDir, "gen");
            if (Directory.Exists(genRoot)) Directory.Delete(genRoot, true);
            Directory.CreateDirectory(genRoot);
            foreach (var c in Checkers) Directory.CreateDirectory(Path.Combine(genRoot, c.Slug));

            var fmt = new DataFormatter(CultureInfo.InvariantCulture);
            IWorkbook wb;
            using (var fs = File.OpenRead(xls)) wb = WorkbookFactory.Create(fs);
            var sheet = wb.GetSheet("issues") ?? wb.GetSheetAt(0);

            // Header map (first non-empty row).
            var col = new Dictionary<string, int>(StringComparer.Ordinal);
            int headerIdx = -1;
            for (int r = sheet.FirstRowNum; r <= sheet.LastRowNum; r++)
            {
                var row = sheet.GetRow(r); if (row == null) continue;
                bool any = false;
                for (int c = row.FirstCellNum; c < row.LastCellNum; c++)
                    if (Cell(row.GetCell(c), fmt).Trim().Length > 0) { any = true; break; }
                if (!any) continue;
                headerIdx = r;
                for (int c = row.FirstCellNum; c < row.LastCellNum; c++)
                {
                    string h = Cell(row.GetCell(c), fmt).Trim();
                    if (h.Length > 0 && !col.ContainsKey(h)) col[h] = c;
                }
                break;
            }
            int keyC = col[CKey], lineC = col[CLine], fileC = col[CFile], srcC = col[CSource];
            int pathC = col.TryGetValue(CPath, out int pc) ? pc : -1;

            var manifest = new StringBuilder();
            manifest.Append("slug,checker,tool,rules,file,line,wrapper,parse_ok,flagged_b64\n");

            var perCheckerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var parseFailCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var noFlaggedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var fileCounters = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in Checkers) { perCheckerCounts[c.Slug] = 0; parseFailCounts[c.Slug] = 0; noFlaggedCounts[c.Slug] = 0; fileCounters[c.Slug] = 0; }

            int totalRows = 0, abTotal = 0, excludedMeta = 0;
            var utf8 = new UTF8Encoding(false);

            for (int r = headerIdx + 1; r <= sheet.LastRowNum; r++)
            {
                var row = sheet.GetRow(r); if (row == null) continue;
                string key = Cell(row.GetCell(keyC), fmt).Trim();
                if (key.Length == 0) continue;
                totalRows++;
                if (!cfgByKey.TryGetValue(key, out CheckerCfg cfg)) continue;

                string file = Cell(row.GetCell(fileC), fmt);
                string path = pathC >= 0 ? Cell(row.GetCell(pathC), fmt) : "";
                string low = (path + "\\" + file).ToLowerInvariant();
                string fn = file.ToLowerInvariant();
                if (low.Contains("\\obj\\") || low.Contains("\\bin\\")
                    || fn.EndsWith(".g.cs") || fn.EndsWith(".g.i.cs") || fn.EndsWith(".designer.cs")
                    || fn == "assemblyinfo.cs" || file.Contains("복사본"))
                { excludedMeta++; continue; }

                abTotal++;

                string lineStr = Cell(row.GetCell(lineC), fmt).Trim();
                string src = Cell(row.GetCell(srcC), fmt);
                int flaggedLineNo = int.TryParse(lineStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fl) ? fl : -1;

                // Parse the numbered context lines.
                var ctx = ParseContext(src);   // list of (num, code)
                int flaggedIdx = -1;
                for (int i = 0; i < ctx.Count; i++) if (ctx[i].Num == flaggedLineNo) { flaggedIdx = i; break; }

                string flaggedText = flaggedIdx >= 0 ? ctx[flaggedIdx].Code : "";
                bool parseOk;
                string generated;

                if (flaggedIdx < 0)
                {
                    noFlaggedCounts[cfg.Slug]++;
                    parseOk = false;
                    generated = "// NO FLAGGED LINE FOUND (라인=" + lineStr + " absent from 소스 코드)\n";
                }
                else
                {
                    generated = BuildSnippet(cfg, ctx, flaggedIdx, out parseOk);
                }

                int idx = ++fileCounters[cfg.Slug];
                string name = "f" + idx.ToString("D5", CultureInfo.InvariantCulture) + ".cs";
                File.WriteAllText(Path.Combine(genRoot, cfg.Slug, name), generated, utf8);

                perCheckerCounts[cfg.Slug]++;
                if (!parseOk) parseFailCounts[cfg.Slug]++;

                manifest.Append(string.Join(",", new[]
                {
                    cfg.Slug, Csv(cfg.Key), cfg.Tool, Csv(cfg.Rules), Csv(file), lineStr,
                    cfg.Wrapper, parseOk ? "1" : "0",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(flaggedText)),
                })).Append('\n');
            }

            File.WriteAllText(Path.Combine(outDir, "manifest.csv"), manifest.ToString(), new UTF8Encoding(true));

            // Console summary.
            // 보안: 경로/파일명 대신 크기만 남긴다(위 주석 참조).
            Console.WriteLine("xls:               <supplied by caller, path withheld from log> ("
                              + (new FileInfo(xls).Length / 1024) + " KB)");
            Console.WriteLine("gen root:          " + Path.GetFullPath(genRoot));
            Console.WriteLine("total checker rows:" + totalRows);
            Console.WriteLine("autofix kept:      " + abTotal);
            Console.WriteLine("excluded metadata: " + excludedMeta);
            Console.WriteLine("--- per checker (generated / parse-fail / no-flagged) ---");
            foreach (var c in Checkers)
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,6} / {1,5} / {2,4}  {3}",
                    perCheckerCounts[c.Slug], parseFailCounts[c.Slug], noFlaggedCounts[c.Slug], c.Slug));
            return 0;
        }

        // ---- context parsing --------------------------------------------------------------------------
        private struct CtxLine { public int Num; public string Code; }

        private static List<CtxLine> ParseContext(string src)
        {
            var list = new List<CtxLine>();
            string norm = src.Replace("\r\n", "\n").Replace("\r", "\n");
            foreach (string raw in norm.Split('\n'))
            {
                var m = PrefixRe.Match(raw);
                if (!m.Success) { continue; }   // lines without a numeric prefix are wrapped-text artifacts; skip
                int num = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                string rest = raw.Substring(m.Length);
                if (rest.StartsWith(" ", StringComparison.Ordinal)) rest = rest.Substring(1);   // drop the single separator space
                rest = rest.TrimStart('﻿');   // BOM sometimes embedded on line 1
                list.Add(new CtxLine { Num = num, Code = rest });
            }
            return list;
        }

        // ---- snippet building -------------------------------------------------------------------------
        private static string BuildSnippet(CheckerCfg cfg, List<CtxLine> ctx, int f, out bool parseOk)
        {
            if (cfg.Mode == "member")
                return BuildMember(ctx, f, out parseOk);

            // Candidate [start,end] ranges to try, ordered from most-focused to most-context per mode.
            var ranges = RangeOrder(cfg.Mode, ctx.Count, f);

            foreach (var (s, e) in ranges)
            {
                var lines = new List<string>();
                for (int i = s; i <= e; i++) lines.Add(ctx[i].Code);
                string body = LeadingFix(string.Join("\n", lines));
                foreach (string cand in Completions(body))
                {
                    foreach (string wrapped in Wrap(cfg.Wrapper, cand))
                    {
                        if (Parses(wrapped)) { parseOk = true; return wrapped; }
                    }
                }
            }

            // Nothing parsed: emit the flagged line wrapped best-effort, flagged as parse-fail.
            string fb = Wrap(cfg.Wrapper, LeadingFix(ctx[f].Code)).First();
            parseOk = false;
            return "// PARSE-FAIL (no wrapping produced a clean parse)\n" + fb;
        }

        private static string BuildMember(List<CtxLine> ctx, int f, out bool parseOk)
        {
            // Include contiguous leading comment lines immediately above the flagged member (they belong to it,
            // and memberblank inserts the blank BEFORE that comment block). Then grow forward to a complete member.
            int start = f;
            while (start - 1 >= 0)
            {
                string prev = ctx[start - 1].Code.TrimStart();
                if (prev.StartsWith("//", StringComparison.Ordinal) || prev.StartsWith("/*", StringComparison.Ordinal)
                    || prev.StartsWith("[", StringComparison.Ordinal))   // attribute list also belongs to the member
                    start--;
                else break;
            }

            for (int end = f; end < Math.Min(ctx.Count, f + 12); end++)
            {
                var memberLines = new List<string>();
                for (int i = start; i <= end; i++) memberLines.Add(ctx[i].Code);
                string member = string.Join("\n", memberLines).TrimEnd();

                foreach (string completed in CompleteMember(member))
                {
                    // class wrapper with a synthetic preceding member to create the missing-blank adjacency.
                    string clsBody = "    void __Prev() { }\n" + completed;
                    string cls = "class C\n{\n" + clsBody + "\n}\n";
                    if (Parses(cls)) { parseOk = true; return cls; }

                    // interface wrapper (for interface member findings).
                    string ifBody = "    void __Prev();\n" + completed;
                    string iface = "interface I\n{\n" + ifBody + "\n}\n";
                    if (Parses(iface)) { parseOk = true; return iface; }

                    // constructor case: name the class after the leading identifier before '('.
                    string ctorName = CtorName(completed);
                    if (ctorName != null)
                    {
                        string cb = "    void __Prev() { }\n" + completed;
                        string cc = "class " + ctorName + "\n{\n" + cb + "\n}\n";
                        if (Parses(cc)) { parseOk = true; return cc; }
                    }
                }
            }

            parseOk = false;
            return "// PARSE-FAIL member (could not assemble a parseable two-member type)\nclass C\n{\n    void __Prev() { }\n" + ctx[f].Code + "\n}\n";
        }

        private static IEnumerable<(int, int)> RangeOrder(string mode, int n, int f)
        {
            var seen = new HashSet<(int, int)>();
            IEnumerable<(int, int)> Raw()
            {
                if (mode == "single")
                {
                    for (int e = f; e < n; e++) yield return (f, e);
                }
                else if (mode == "block")
                {
                    // Doxygen /** ... */ block: prefer the FULL forward context (the closing */ is usually below
                    // the window, so completion synthesizes it), then shrink.
                    yield return (f, n - 1);
                    for (int e = f + 3; e < n; e++) yield return (f, e);
                    yield return (f, f + 2);
                    yield return (f, f + 1);
                    yield return (f, f);
                }
                else if (mode == "fwd")
                {
                    // Prefer including a few following lines (needed for objectinitializer collapse).
                    for (int e = f + 2; e < n; e++) yield return (f, e);
                    yield return (f, f + 1);
                    yield return (f, f);
                }
                else // sym (continuation / linq): prefer including preceding statement start + following body.
                {
                    yield return (Math.Max(0, f - 1), f);
                    yield return (Math.Max(0, f - 1), Math.Min(n - 1, f + 2));
                    yield return (Math.Max(0, f - 2), Math.Min(n - 1, f + 2));
                    yield return (Math.Max(0, f - 3), Math.Min(n - 1, f + 4));
                    yield return (Math.Max(0, f - 1), Math.Min(n - 1, f + 6));
                    yield return (Math.Max(0, f - 2), f);
                    yield return (f, Math.Min(n - 1, f + 4));
                    yield return (f, f);
                }
            }
            foreach (var pair in Raw())
            {
                int s = Math.Max(0, Math.Min(pair.Item1, n - 1));
                int e = Math.Max(s, Math.Min(pair.Item2, n - 1));
                if (seen.Add((s, e))) yield return (s, e);
            }
        }

        // Minimal wrappers (return ordered candidates so classOrMethod can try both).
        private static IEnumerable<string> Wrap(string wrapper, string body)
        {
            switch (wrapper)
            {
                case "method":
                case "comment":
                    yield return "class C\n{\n    void M()\n    {\n" + body + "\n    }\n}\n";
                    break;
                case "classOrMethod":
                    yield return "class C\n{\n" + body + "\n}\n";                       // field-level decl
                    yield return "class C\n{\n    void M()\n    {\n" + body + "\n    }\n}\n"; // local decl
                    break;
                default:
                    yield return "class C\n{\n    void M()\n    {\n" + body + "\n    }\n}\n";
                    break;
            }
        }

        // Drop a leading dangling token that can never start a parseable snippet: `else`, a stray `}` / `)`,
        // a `case X:` / `default:` label. Applied to the first line only.
        private static string LeadingFix(string code)
        {
            string s = code;
            string trimmed = s.TrimStart();
            if (trimmed.StartsWith("else if", StringComparison.Ordinal))
                return trimmed.Substring("else".Length).TrimStart();
            if (trimmed.StartsWith("else", StringComparison.Ordinal))
            {
                string rest = trimmed.Substring("else".Length).TrimStart();
                return rest;   // `else { ... }` -> `{ ... }`, `else` alone -> ""
            }
            // strip leading stray closers / separators at the very start
            while (trimmed.Length > 0 && (trimmed[0] == '}' || trimmed[0] == ')' || trimmed[0] == ','))
                trimmed = trimmed.Substring(1).TrimStart();
            return trimmed.Length == s.TrimStart().Length ? s : trimmed;
        }

        // Ordered completion candidates for a code fragment. First a computed balance, then simple fallbacks.
        private static IEnumerable<string> Completions(string code)
        {
            // Close any unterminated block comment first (the Doxygen /** ... */ blocks whose */ sits below the
            // context window) so the wrapper braces are not swallowed into the comment.
            string t = CloseOpenComment(code.TrimEnd());
            if (t.Length == 0) { yield return ""; yield break; }

            // If the snippet ENDS in a block comment, it is comment-only at the tail: a bare comment is a valid
            // statement position (trivia), so NEVER append a statement terminator on the comment's line -- doing
            // so ( `*/ 0;` ) would put code on the comment line and defeat the flatten rule's standalone-line
            // requirement. Only close any still-open braces, on their own lines.
            if (t.EndsWith("*/", StringComparison.Ordinal))
            {
                var (_, bc, _) = CountDelims(t);
                yield return bc > 0 ? t + "\n" + new string('}', bc) : t;
                yield return t + "\n}";
                yield return t;
                yield break;
            }

            // Fix a trailing dangling binary operator / comma / dot / lambda-arrow so balancing can close cleanly.
            string tf = t;
            if (tf.EndsWith("=>", StringComparison.Ordinal)) tf += " new object()";
            else if (EndsWithAny(tf, "||", "&&")) tf += " true";
            else if (EndsWithAny(tf, "<<", ">>", "==", "!=", "<=", ">=")) tf += " 0";
            else if (tf.EndsWith(",", StringComparison.Ordinal)) tf = tf.Substring(0, tf.Length - 1);
            else if (EndsWithSingleOp(tf)) tf += " 0";
            else if (tf.EndsWith(".", StringComparison.Ordinal)) tf = tf.Substring(0, tf.Length - 1);

            foreach (string basis in Dedup(new[] { tf, t }))
            {
                yield return basis + Balance(basis);
            }

            // Simple fallbacks (order by frequency). Includes object/collection-initializer + lambda tails.
            string[] sfx = { "", ";", " { }", "\n}", ")\n{ }", ");", ")\n{ }\n}", " { }\n}", ";\n}",
                             "\n}\n}", "))\n{ }", "))", ")));", " { };", " { });", "() { };", "> { };", "]{};", "] { };" };
            foreach (string basis in Dedup(new[] { tf, t }))
                foreach (string s in sfx)
                    yield return basis + s;
        }

        // If code ends inside an unterminated /* block comment, append the closing */ (on its own line).
        private static string CloseOpenComment(string code)
        {
            bool inBlock = false, inLine = false, inStr = false, inChar = false, verbatim = false;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (inLine) { if (c == '\n') inLine = false; continue; }
                if (inBlock) { if (c == '*' && i + 1 < code.Length && code[i + 1] == '/') { inBlock = false; i++; } continue; }
                if (inStr)
                {
                    if (c == '\\' && !verbatim) { i++; continue; }
                    if (c == '"') { if (verbatim && i + 1 < code.Length && code[i + 1] == '"') { i++; continue; } inStr = false; }
                    continue;
                }
                if (inChar) { if (c == '\\') { i++; continue; } if (c == '\'') inChar = false; continue; }
                if (c == '/' && i + 1 < code.Length && code[i + 1] == '/') { inLine = true; i++; continue; }
                if (c == '/' && i + 1 < code.Length && code[i + 1] == '*') { inBlock = true; i++; continue; }
                if (c == '"') { inStr = true; verbatim = i > 0 && code[i - 1] == '@'; continue; }
                if (c == '\'') { inChar = true; continue; }
            }
            if (inBlock) return code + "\n*/";
            return code;
        }

        // Compute a closing suffix: balance [], (), decide body-vs-statement, balance {}.
        private static string Balance(string code)
        {
            var (p, b, k) = CountDelims(code);
            var sb = new StringBuilder();
            if (k > 0) sb.Append(new string(']', k));
            if (p > 0) sb.Append(new string(')', p));

            string firstWord = FirstWord(code);
            bool needsBody = firstWord == "if" || firstWord == "for" || firstWord == "foreach"
                             || firstWord == "while" || firstWord == "using" || firstWord == "lock"
                             || firstWord == "fixed" || firstWord == "switch";

            if (b > 0) sb.Append('\n').Append(new string('}', b));
            else if (needsBody) sb.Append(" { }");
            else
            {
                string tt = (code + sb).TrimEnd();
                if (!tt.EndsWith(";", StringComparison.Ordinal) && !tt.EndsWith("}", StringComparison.Ordinal)
                    && !tt.EndsWith("{", StringComparison.Ordinal))
                    sb.Append(';');
            }
            return sb.ToString();
        }

        // Member completion: ensure braces balance (append `{ }` for a signature, or close open bodies).
        private static IEnumerable<string> CompleteMember(string member)
        {
            string t = member.TrimEnd();
            var (p, b, k) = CountDelims(t);
            var sb = new StringBuilder();
            if (k > 0) sb.Append(new string(']', k));
            if (p > 0) sb.Append(new string(')', p));
            if (b > 0) { sb.Append('\n').Append(new string('}', b)); }
            else if (t.EndsWith(")", StringComparison.Ordinal) || (p > 0)) { sb.Append(" { }"); }
            else if (t.EndsWith(";", StringComparison.Ordinal) || t.EndsWith("}", StringComparison.Ordinal)) { }
            else if (t.EndsWith("{", StringComparison.Ordinal)) { sb.Append("\n}"); }
            yield return t + sb;

            // fallbacks
            foreach (string s in new[] { "", " { }", "\n}", ";", ") { }", ")\n{ }", "));\n}" })
                yield return t + s;
        }

        private static string CtorName(string completed)
        {
            // Look at the first non-empty, non-comment, non-synthetic line for `... Identifier ( ...`.
            foreach (string raw in completed.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("/*", StringComparison.Ordinal) || line.StartsWith("[", StringComparison.Ordinal)
                    || line.StartsWith("__Prev", StringComparison.Ordinal)) continue;
                var m = Regex.Match(line, @"([A-Za-z_][A-Za-z0-9_]*)\s*\(");
                if (m.Success) return m.Groups[1].Value;
                return null;
            }
            return null;
        }

        // ---- helpers ----------------------------------------------------------------------------------
        private static bool Parses(string text)
        {
            var tree = CSharpSyntaxTree.ParseText(text, ParseOpts);
            foreach (var d in tree.GetDiagnostics())
                if (d.Severity == DiagnosticSeverity.Error) return false;
            return true;
        }

        // Count net unmatched (), {}, [] ignoring string/char/comment content.
        private static (int p, int b, int k) CountDelims(string s)
        {
            int p = 0, b = 0, k = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                    i++;
                    continue;
                }
                if (c == '"')
                {
                    // handle verbatim/interp roughly: skip until unescaped closing quote on same scan
                    bool verbatim = i > 0 && s[i - 1] == '@';
                    i++;
                    while (i < s.Length)
                    {
                        if (s[i] == '\\' && !verbatim) { i += 2; continue; }
                        if (s[i] == '"')
                        {
                            if (verbatim && i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
                            break;
                        }
                        i++;
                    }
                    continue;
                }
                if (c == '\'')
                {
                    i++;
                    while (i < s.Length)
                    {
                        if (s[i] == '\\') { i += 2; continue; }
                        if (s[i] == '\'') break;
                        i++;
                    }
                    continue;
                }
                if (c == '(') p++; else if (c == ')') p--;
                else if (c == '{') b++; else if (c == '}') b--;
                else if (c == '[') k++; else if (c == ']') k--;
            }
            return (Math.Max(0, p), Math.Max(0, b), Math.Max(0, k));
        }

        private static string FirstWord(string code)
        {
            string s = code.TrimStart();
            int i = 0;
            while (i < s.Length && (char.IsLetter(s[i]) || s[i] == '_')) i++;
            return s.Substring(0, i);
        }

        private static bool EndsWithAny(string s, params string[] ops)
        {
            foreach (string o in ops) if (s.EndsWith(o, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool EndsWithSingleOp(string s)
        {
            if (s.Length == 0) return false;
            char c = s[s.Length - 1];
            // avoid treating ++/-- or ) etc.; only clear binary single-char ops
            if ("+*/%&|^".IndexOf(c) >= 0) return true;
            if (c == '-' && !(s.Length >= 2 && s[s.Length - 2] == '-')) return true;
            return false;
        }

        private static IEnumerable<string> Dedup(IEnumerable<string> items)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string x in items) if (seen.Add(x)) yield return x;
        }

        private static string Cell(ICell cell, DataFormatter fmt)
        {
            if (cell == null) return "";
            switch (cell.CellType)
            {
                case CellType.String: return cell.StringCellValue ?? "";
                case CellType.Boolean: return cell.BooleanCellValue ? "true" : "false";
                case CellType.Numeric:
                    double d = cell.NumericCellValue;
                    if (!double.IsNaN(d) && d == Math.Truncate(d) && Math.Abs(d) < 9e15)
                        return ((long)d).ToString(CultureInfo.InvariantCulture);
                    return d.ToString("R", CultureInfo.InvariantCulture);
                case CellType.Formula:
                    try { return fmt.FormatCellValue(cell) ?? ""; } catch { return ""; }
                default: return "";
            }
        }

        private static string Csv(string s) =>
            s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
