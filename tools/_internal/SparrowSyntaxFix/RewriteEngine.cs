// SparrowSyntaxFix: deterministic Roslyn source rewriter for Sparrow (파수 정적분석) Track A code-rule
// residuals that `SparrowSyntaxFix` does not fully clear on the legacy MyApp (.NET Framework 4.7.2) project.
//
// RewriteEngine is the pure, IO-free core: parse source text with Roslyn syntax APIs, apply the enabled
// CSharpSyntaxRewriter(s), and return the round-tripped text + per-rule edit counts. Roslyn guarantees
// ToFullString() reproduces the source byte-for-byte when nothing matched, so an unmatched file reports
// as unchanged and is never rewritten. This is a syntax-level transform: never string/regex replacement.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SparrowSyntaxFix
{
    // Which deterministic rewrite rules to apply.
    [Flags]
    internal enum SyntaxRule
    {
        None = 0,
        NullVar = 1 << 0,
        Parens = 1 << 1,
        ObjectVarSafe = 1 << 2,
        ForeachCast = 1 << 3,
        ObviousVar = 1 << 4,
        ObjectVarNarrowing = 1 << 5,
        LocalConst = 1 << 6,
        ObjectInitializer = 1 << 7,
        ArrayVarSafe = 1 << 8,
        ArrayVarNarrowing = 1 << 9,
        ForVar = 1 << 10,
        FieldSplit = 1 << 11,
        EmptyStmt = 1 << 12,
        ForHoist = 1 << 13,
        Default = Parens | ObjectVarSafe | ObviousVar | ArrayVarSafe,
        All = NullVar | Parens | ObjectVarSafe | ForeachCast | ObviousVar | ObjectVarNarrowing | LocalConst
              | ObjectInitializer | ArrayVarSafe | ArrayVarNarrowing | ForVar | FieldSplit | EmptyStmt | ForHoist,
    }

    // Result of a single-file rewrite: the new full text + per-rule edit counts + whether text changed.
    internal sealed class RewriteResult
    {
        public RewriteResult(string newText, Dictionary<string, int> counts, bool changed)
        {
            NewText = newText;
            Counts = counts;
            Changed = changed;
        }

        public string NewText { get; }
        public Dictionary<string, int> Counts { get; }
        public int NullVarEdits => Get("nullvar");
        public int NullCastEdits => NullVarEdits;  // Back-compat for the old fixture wording.
        public int ParensEdits => Get("parens");
        public int ObjectVarSafeEdits => Get("objectvar-safe");
        public int ForeachCastEdits => Get("foreachcast");
        public int ObviousVarEdits => Get("obviousvar");
        public int ObjectVarNarrowingEdits => Get("objectvar-narrowing");
        public int LocalConstEdits => Get("localconst");
        public int ObjectInitializerEdits => Get("objectinitializer");
        public int ArrayVarSafeEdits => Get("arrayvar-safe");
        public int ArrayVarNarrowingEdits => Get("arrayvar-narrowing");
        public int ForVarEdits => Get("forvar");
        public int FieldSplitEdits => Get("fieldsplit");
        public int EmptyStmtEdits => Get("emptystmt");
        public int ForHoistEdits => Get("forhoist");
        public bool Changed { get; }

        private int Get(string key) => Counts.TryGetValue(key, out int value) ? value : 0;
    }

    internal static class RewriteEngine
    {
        // Latest lang version parses a superset of the C# 7.x MyApp sources; we only read syntax, never compile.
        private static readonly CSharpParseOptions ParseOpts = new CSharpParseOptions(LanguageVersion.Latest);

        public static RewriteResult Rewrite(string source, SyntaxRule rules)
        {
            SyntaxNode root = CSharpSyntaxTree.ParseText(source, ParseOpts).GetRoot();
            SyntaxNode current = root;

            var counts = NewCounts();

            if ((rules & SyntaxRule.ForHoist) != 0)
            {
                current = ForHoistRewriter.Rewrite(current, source, out int forHoistCount);
                counts["forhoist"] = forHoistCount;
            }

            if ((rules & SyntaxRule.ForVar) != 0)
            {
                var rw = new ForVarRewriter();
                current = rw.Visit(current) ?? current;
                counts["forvar"] = rw.Count;
            }

            if ((rules & SyntaxRule.FieldSplit) != 0)
            {
                current = FieldSplitRewriter.Rewrite(current, source, out int fieldSplitCount);
                counts["fieldsplit"] = fieldSplitCount;
            }

            if ((rules & SyntaxRule.EmptyStmt) != 0)
            {
                var rw = new EmptyStmtRewriter();
                current = rw.Visit(current) ?? current;
                counts["emptystmt"] = rw.Count;
            }

            if ((rules & SyntaxRule.NullVar) != 0)
            {
                var rw = new NullVarRewriter();
                current = rw.Visit(current) ?? current;
                counts["nullvar"] = rw.Count;
            }

            if ((rules & SyntaxRule.Parens) != 0)
            {
                var rw = new ParensRewriter();
                current = rw.Visit(current) ?? current;
                counts["parens"] = rw.Count;
            }

            if ((rules & SyntaxRule.ObjectInitializer) != 0)
            {
                var rw = new ObjectInitializerRewriter();
                current = rw.Visit(current) ?? current;
                counts["objectinitializer"] = rw.Count;
            }

            if ((rules & SyntaxRule.ObjectVarSafe) != 0)
            {
                var rw = new ObjectVarRewriter(allowNarrowing: false);
                current = rw.Visit(current) ?? current;
                counts["objectvar-safe"] = rw.Count;
            }

            if ((rules & SyntaxRule.ForeachCast) != 0)
            {
                var rw = new ForeachCastRewriter();
                current = rw.Visit(current) ?? current;
                counts["foreachcast"] = rw.Count;
            }

            if ((rules & SyntaxRule.ObviousVar) != 0)
            {
                var rw = new ObviousVarRewriter();
                current = rw.Visit(current) ?? current;
                counts["obviousvar"] = rw.Count;
            }

            if ((rules & SyntaxRule.ObjectVarNarrowing) != 0)
            {
                var rw = new ObjectVarRewriter(allowNarrowing: true);
                current = rw.Visit(current) ?? current;
                counts["objectvar-narrowing"] = rw.Count;
            }

            if ((rules & SyntaxRule.LocalConst) != 0)
            {
                var rw = new LocalConstRewriter();
                current = rw.Visit(current) ?? current;
                counts["localconst"] = rw.Count;
            }

            if ((rules & SyntaxRule.ArrayVarSafe) != 0)
            {
                var rw = new ArrayVarRewriter(allowNarrowing: false);
                current = rw.Visit(current) ?? current;
                counts["arrayvar-safe"] = rw.Count;
            }

            if ((rules & SyntaxRule.ArrayVarNarrowing) != 0)
            {
                var rw = new ArrayVarRewriter(allowNarrowing: true);
                current = rw.Visit(current) ?? current;
                counts["arrayvar-narrowing"] = rw.Count;
            }

            string newText = current.ToFullString();
            bool changed = !string.Equals(newText, source, StringComparison.Ordinal);
            return new RewriteResult(newText, counts, changed);
        }

        private static Dictionary<string, int> NewCounts()
        {
            return new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["nullvar"] = 0,
                ["parens"] = 0,
                ["objectvar-safe"] = 0,
                ["foreachcast"] = 0,
                ["obviousvar"] = 0,
                ["objectvar-narrowing"] = 0,
                ["localconst"] = 0,
                ["objectinitializer"] = 0,
                ["arrayvar-safe"] = 0,
                ["arrayvar-narrowing"] = 0,
                ["forvar"] = 0,
                ["fieldsplit"] = 0,
                ["emptystmt"] = 0,
                ["forhoist"] = 0,
            };
        }
    }
}
