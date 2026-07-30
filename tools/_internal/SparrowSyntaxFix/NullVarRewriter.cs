using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparrowSyntaxFix
{
    // nullvar: Foo x = null; / Foo x; -> var x = (Foo)null;
    internal sealed class NullVarRewriter : CSharpSyntaxRewriter
    {
        public int Count { get; private set; }

        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            if (!VarRewriteHelpers.IsSingleLocal(node, out VariableDeclarationSyntax decl, out VariableDeclaratorSyntax declarator))
                return base.VisitLocalDeclarationStatement(node);
            if (node.Modifiers.Any(SyntaxKind.ConstKeyword)) return base.VisitLocalDeclarationStatement(node);
            if (VarRewriteHelpers.IsPredefinedValueType(decl.Type)) return base.VisitLocalDeclarationStatement(node);

            EqualsValueClauseSyntax? oldInitializer = declarator.Initializer;
            if (oldInitializer != null && !oldInitializer.Value.IsKind(SyntaxKind.NullLiteralExpression))
                return base.VisitLocalDeclarationStatement(node);

            ExpressionSyntax nullLiteral = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
            ExpressionSyntax valueForTrivia = oldInitializer?.Value ?? nullLiteral;
            ExpressionSyntax cast = VarRewriteHelpers.CastValue(decl.Type, nullLiteral)
                .WithLeadingTrivia(valueForTrivia.GetLeadingTrivia())
                .WithTrailingTrivia(valueForTrivia.GetTrailingTrivia());

            EqualsValueClauseSyntax newInitializer = oldInitializer == null
                ? SyntaxFactory.EqualsValueClause(cast)
                    .WithEqualsToken(SyntaxFactory.Token(SyntaxKind.EqualsToken)
                        .WithLeadingTrivia(SyntaxFactory.Space)
                        .WithTrailingTrivia(SyntaxFactory.Space))
                    .WithTrailingTrivia(default(SyntaxTriviaList))
                : oldInitializer.WithValue(cast);

            VariableDeclaratorSyntax newDeclarator = declarator.WithInitializer(newInitializer);
            VariableDeclarationSyntax newDecl = VarRewriteHelpers.WithVarType(decl)
                .WithVariables(SyntaxFactory.SingletonSeparatedList(newDeclarator));

            Count++;
            return node.WithDeclaration(newDecl);
        }
    }
}
