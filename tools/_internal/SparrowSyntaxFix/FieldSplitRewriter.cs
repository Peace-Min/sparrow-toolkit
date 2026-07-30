using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparrowSyntaxFix
{
    // fieldsplit: USE_ONE_DECLARATION.PER_LINE, FIELDS ONLY. A field with multiple declarators becomes one
    // field-per-line, preserving modifiers/type/each declarator's initializer, at the SAME indentation. A
    // leading comment / XML-doc block stays on the FIRST split line (never duplicated). Locals / for-init /
    // events / const / attributed fields are out of scope and left untouched.
    //
    // Not a CSharpSyntaxRewriter because one member must become several members. Instead we repeatedly find the
    // first qualifying field and use ReplaceNode(node, IEnumerable) to splice the replacements into its member
    // list. Each replacement has Variables.Count == 1, so it never re-qualifies -> the loop terminates.
    internal static class FieldSplitRewriter
    {
        public static SyntaxNode Rewrite(SyntaxNode root, string source, out int count)
        {
            count = 0;
            SyntaxTrivia eol = SyntaxFactory.EndOfLine(source.Contains("\r\n") ? "\r\n" : "\n");

            while (true)
            {
                FieldDeclarationSyntax? target = root.DescendantNodes()
                    .OfType<FieldDeclarationSyntax>()
                    .FirstOrDefault(Qualifies);
                if (target == null) break;

                IReadOnlyList<MemberDeclarationSyntax> parts = Split(target, eol);
                if (parts.Count <= 1) break;   // defensive: never loop forever
                root = root.ReplaceNode(target, parts);
                count++;
            }
            return root;
        }

        private static bool Qualifies(FieldDeclarationSyntax field)
        {
            if (field.Declaration.Variables.Count <= 1) return false;      // single declarator: nothing to split
            if (field.AttributeLists.Count > 0) return false;              // attributes apply to all -> skip (safety)
            if (field.Modifiers.Any(SyntaxKind.ConstKeyword)) return false; // const out of scope per brief
            if (field.Modifiers.Any(SyntaxKind.FixedKeyword)) return false; // fixed-size buffer: not a plain field
            if (HasRiskyInteriorTrivia(field)) return false;               // directive / comment between declarators
            return true;
        }

        // A directive anywhere, or a comment sitting BETWEEN declarators (inside the declaration), makes the split
        // unsafe (we would drop or misplace it). A leading comment ABOVE the field lives in leading trivia and is
        // preserved on the first line, so it does not count here.
        private static bool HasRiskyInteriorTrivia(FieldDeclarationSyntax field)
        {
            foreach (SyntaxTrivia trivia in field.Declaration.DescendantTrivia(descendIntoTrivia: true))
            {
                if (trivia.IsDirective) return true;
                if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                    || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                    || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                    || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
                    || trivia.IsKind(SyntaxKind.DisabledTextTrivia)
                    || trivia.IsKind(SyntaxKind.SkippedTokensTrivia))
                {
                    return true;
                }
            }
            return false;
        }

        private static IReadOnlyList<MemberDeclarationSyntax> Split(FieldDeclarationSyntax field, SyntaxTrivia eol)
        {
            SyntaxTriviaList origLeading = field.GetLeadingTrivia();
            SyntaxTriviaList origTrailing = field.GetTrailingTrivia();

            // Indentation of the field's own physical line = the trailing whitespace run of its leading trivia.
            SyntaxTrivia indent = origLeading.Count > 0 && origLeading[origLeading.Count - 1].IsKind(SyntaxKind.WhitespaceTrivia)
                ? origLeading[origLeading.Count - 1]
                : SyntaxFactory.Whitespace("");

            string mods = string.Join(" ", field.Modifiers.Select(m => m.Text));
            string typeText = field.Declaration.Type.WithoutTrivia().ToFullString();
            SeparatedSyntaxList<VariableDeclaratorSyntax> vars = field.Declaration.Variables;

            var result = new List<MemberDeclarationSyntax>(vars.Count);
            for (int k = 0; k < vars.Count; k++)
            {
                string declText = vars[k].WithoutTrivia().ToFullString();
                string lineText = (mods.Length > 0 ? mods + " " : "") + typeText + " " + declText + ";";
                var parsed = (FieldDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(lineText)!;

                // First line keeps the original leading trivia (comment/XML-doc + indent). Later lines are separated
                // by the previous line's trailing EOL, so they only need the indentation as leading trivia.
                parsed = k == 0
                    ? parsed.WithLeadingTrivia(origLeading)
                    : parsed.WithLeadingTrivia(SyntaxFactory.TriviaList(indent));

                // Last line keeps the field's original trailing trivia (its EOL). Earlier lines get a single EOL.
                parsed = k == vars.Count - 1
                    ? parsed.WithTrailingTrivia(origTrailing)
                    : parsed.WithTrailingTrivia(SyntaxFactory.TriviaList(eol));

                result.Add(parsed);
            }
            return result;
        }
    }
}
