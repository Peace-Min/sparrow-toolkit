using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparrowSyntaxFix
{
    // emptystmt: remove a redundant EmptyStatement (`;`) that is an element of a block / switch-section statement
    // list -- the classic `stmt; ;` double-semicolon. Semantically-meaningful empties are NEVER touched because
    // they are NOT list elements: `for (;;) ;`, `while (x) ;`, `if (c) ;`, `do ; while(x);`, and `label: ;` all
    // have the EmptyStatement as an embedded/labeled statement (Parent is the loop/if/label, not a Block), so they
    // never enter FilterEmpties. An empty whose trivia carries a comment/directive is left alone (would lose text).
    internal sealed class EmptyStmtRewriter : CSharpSyntaxRewriter
    {
        public int Count { get; private set; }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var visited = (BlockSyntax)base.VisitBlock(node)!;
            if (!visited.Statements.Any(s => s is EmptyStatementSyntax)) return visited;
            List<StatementSyntax> filtered = FilterEmpties(visited.Statements, out int removed);
            if (removed == 0) return visited;
            Count += removed;
            return visited.WithStatements(SyntaxFactory.List(filtered));
        }

        public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        {
            var visited = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;
            if (!visited.Statements.Any(s => s is EmptyStatementSyntax)) return visited;
            List<StatementSyntax> filtered = FilterEmpties(visited.Statements, out int removed);
            if (removed == 0) return visited;
            // A switch section must keep at least one statement; if everything collapsed away, keep the original.
            if (filtered.Count == 0) return visited;
            Count += removed;
            return visited.WithStatements(SyntaxFactory.List(filtered));
        }

        private static List<StatementSyntax> FilterEmpties(SyntaxList<StatementSyntax> statements, out int removed)
        {
            removed = 0;
            SyntaxTriviaList pendingLeading = default;
            var output = new List<StatementSyntax>(statements.Count);
            foreach (StatementSyntax original in statements)
            {
                StatementSyntax stmt = original;
                if (stmt is EmptyStatementSyntax empty && IsRemovable(empty))
                {
                    removed++;
                    if (output.Count > 0)
                    {
                        // Fold the removed `;`'s line terminator onto the previous statement: strip any trailing
                        // whitespace-only trivia (the space in `x; ;`) and ensure exactly one EOL closes the line.
                        int last = output.Count - 1;
                        output[last] = MergeTrailing(output[last], empty);
                    }
                    else
                    {
                        // No previous statement to fold onto: preserve the empty's trailing EOL (if any) by
                        // prepending it to whatever statement follows, so line structure is not corrupted.
                        pendingLeading = ExtractEol(empty.GetTrailingTrivia());
                    }
                    continue;
                }

                if (pendingLeading.Count > 0)
                {
                    stmt = stmt.WithLeadingTrivia(pendingLeading.AddRange(stmt.GetLeadingTrivia()));
                    pendingLeading = default;
                }
                output.Add(stmt);
            }
            return output;
        }

        private static StatementSyntax MergeTrailing(StatementSyntax prev, EmptyStatementSyntax empty)
        {
            var trailing = prev.GetTrailingTrivia().ToList();
            while (trailing.Count > 0 && trailing[trailing.Count - 1].IsKind(SyntaxKind.WhitespaceTrivia))
                trailing.RemoveAt(trailing.Count - 1);

            bool endsWithEol = trailing.Count > 0 && trailing[trailing.Count - 1].IsKind(SyntaxKind.EndOfLineTrivia);
            SyntaxTriviaList emptyEol = ExtractEol(empty.GetTrailingTrivia());
            if (!endsWithEol) trailing.AddRange(emptyEol);
            return prev.WithTrailingTrivia(SyntaxFactory.TriviaList(trailing));
        }

        private static SyntaxTriviaList ExtractEol(SyntaxTriviaList trivia)
        {
            foreach (SyntaxTrivia t in trivia)
                if (t.IsKind(SyntaxKind.EndOfLineTrivia)) return SyntaxFactory.TriviaList(t);
            return default;
        }

        // Removable = a purely-redundant `;`: no comment/directive/disabled/skipped trivia anywhere on it (so no
        // text is lost). Whitespace and end-of-line trivia are fine (that is exactly the `x; ;` / lone `;` case).
        private static bool IsRemovable(EmptyStatementSyntax empty)
        {
            foreach (SyntaxTrivia t in empty.GetLeadingTrivia().Concat(empty.GetTrailingTrivia()))
            {
                if (t.IsKind(SyntaxKind.WhitespaceTrivia) || t.IsKind(SyntaxKind.EndOfLineTrivia)) continue;
                return false;
            }
            return true;
        }
    }
}
