// Rule 1 (nullcast): rewrite  <ExplicitType> x = null;  ->  var x = (<ExplicitType>)null;
//
// The cast keeps the EXACT original static type, so the transform is 100% semantics-preserving. This is
// the residual that `SparrowSyntaxFix`'s IDE0007 declines: `var x = null;` is illegal C#, so the analyzer
// leaves the finding, but a cast lets `var` be inferred to the identical type.
//
// ONLY a plain single-declarator local statement whose sole initializer is the bare `null` literal is
// matched. HARD skips (leave byte-identical): `= new ...`, `= default`/`default(T)`, any non-null
// initializer, already-`var`, multi-declarator, `const`, `using` locals. Idempotent by construction:
// once the type is `var` (and the value a cast), it is never re-matched.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparrowSyntaxFix
{
    internal sealed class NullCastRewriter : CSharpSyntaxRewriter
    {
        public int Count { get; private set; }

        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            if (!IsMatch(node))
                return base.VisitLocalDeclarationStatement(node);   // recurse (e.g. locals inside lambda bodies)

            VariableDeclarationSyntax decl = node.Declaration;
            TypeSyntax oldType = decl.Type;
            VariableDeclaratorSyntax declarator = decl.Variables[0];
            EqualsValueClauseSyntax initializer = declarator.Initializer!;   // guaranteed non-null by IsMatch
            ExpressionSyntax oldValue = initializer.Value;

            // `var` inherits the original type's OUTER trivia: leading indentation/comments before the
            // statement, and the single trailing space before the variable name.
            TypeSyntax varType = SyntaxFactory.IdentifierName("var")
                .WithLeadingTrivia(oldType.GetLeadingTrivia())
                .WithTrailingTrivia(oldType.GetTrailingTrivia());

            // `(OriginalType)null` occupies the initializer value's slot and inherits ITS outer trivia so
            // spacing around `=` and `;` is untouched. The type inside the cast is stripped of outer trivia
            // (it moved to `var`); its inner trivia (generics, qualified dots, array ranks) is preserved.
            ExpressionSyntax cast = SyntaxFactory.CastExpression(
                    oldType.WithoutTrivia(),
                    SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))
                .WithLeadingTrivia(oldValue.GetLeadingTrivia())
                .WithTrailingTrivia(oldValue.GetTrailingTrivia());

            VariableDeclaratorSyntax newDeclarator = declarator.WithInitializer(initializer.WithValue(cast));
            VariableDeclarationSyntax newDecl = decl
                .WithType(varType)
                .WithVariables(SyntaxFactory.SingletonSeparatedList(newDeclarator));

            Count++;
            return node.WithDeclaration(newDecl);
        }

        private static bool IsMatch(LocalDeclarationStatementSyntax node)
        {
            if (node.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) return false;   // `using var`/`using T x = ...`
            if (node.Modifiers.Any(SyntaxKind.ConstKeyword)) return false;         // `const T x = null;`

            VariableDeclarationSyntax decl = node.Declaration;
            if (IsVarType(decl.Type)) return false;                                // already `var`
            if (decl.Variables.Count != 1) return false;                           // multi-declarator: skip whole stmt

            VariableDeclaratorSyntax v = decl.Variables[0];
            if (v.Initializer is null) return false;                               // no `= ...`
            return v.Initializer.Value.IsKind(SyntaxKind.NullLiteralExpression);   // must be the bare `null` literal
        }

        // Deterministic `var` detection (avoids relying on TypeSyntax.IsVar version nuances).
        private static bool IsVarType(TypeSyntax type) =>
            type is IdentifierNameSyntax id && id.Identifier.ValueText == "var";
    }
}
