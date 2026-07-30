using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparrowSyntaxFix
{
    // arrayvar-safe: `T[] a = new T[] { ... };` -> `T[] a = { ... };`.
    // arrayvar-narrowing: `object[] a = new string[] { ... };` -> `var a = new[] { ... };`.
    internal sealed class ArrayVarRewriter : CSharpSyntaxRewriter
    {
        private readonly bool _allowNarrowing;

        public ArrayVarRewriter(bool allowNarrowing)
        {
            _allowNarrowing = allowNarrowing;
        }

        public int Count { get; private set; }

        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            if (!VarRewriteHelpers.IsSingleLocal(node, out VariableDeclarationSyntax decl, out VariableDeclaratorSyntax declarator))
                return base.VisitLocalDeclarationStatement(node);
            if (node.Modifiers.Any(SyntaxKind.ConstKeyword)) return base.VisitLocalDeclarationStatement(node);
            if (decl.Type is not ArrayTypeSyntax declaredArray) return base.VisitLocalDeclarationStatement(node);
            if (declarator.Initializer?.Value is not ArrayCreationExpressionSyntax creation)
                return base.VisitLocalDeclarationStatement(node);
            if (creation.Initializer == null) return base.VisitLocalDeclarationStatement(node);
            if (!IsSimpleRankOneArray(declaredArray)) return base.VisitLocalDeclarationStatement(node);
            if (!IsSimpleRankOneArray(creation.Type)) return base.VisitLocalDeclarationStatement(node);
            if (creation.Type.RankSpecifiers[0].Sizes.Count != 1) return base.VisitLocalDeclarationStatement(node);
            if (!creation.Type.RankSpecifiers[0].Sizes[0].IsKind(SyntaxKind.OmittedArraySizeExpression))
                return base.VisitLocalDeclarationStatement(node);
            if (creation.Initializer.Expressions.Count == 0) return base.VisitLocalDeclarationStatement(node);

            bool sameType = VarRewriteHelpers.TypeKey(declaredArray) == VarRewriteHelpers.TypeKey(creation.Type);
            if (_allowNarrowing == sameType) return base.VisitLocalDeclarationStatement(node);
            if (_allowNarrowing && HasImplicitArrayInferenceRisk(creation.Initializer))
                return base.VisitLocalDeclarationStatement(node);

            ExpressionSyntax newValue;
            VariableDeclarationSyntax newDecl;
            if (_allowNarrowing)
            {
                string newArrayText = "new[] " + creation.Initializer.WithoutTrivia().ToFullString();
                newValue = SyntaxFactory.ParseExpression(newArrayText)
                    .WithLeadingTrivia(creation.GetLeadingTrivia())
                    .WithTrailingTrivia(creation.GetTrailingTrivia());
                VariableDeclaratorSyntax newDeclarator = declarator.WithInitializer(declarator.Initializer.WithValue(newValue));
                newDecl = VarRewriteHelpers.WithVarType(decl).WithVariables(SyntaxFactory.SingletonSeparatedList(newDeclarator));
            }
            else
            {
                newValue = creation.Initializer
                    .WithLeadingTrivia(creation.GetLeadingTrivia())
                    .WithTrailingTrivia(creation.GetTrailingTrivia());
                VariableDeclaratorSyntax newDeclarator = declarator.WithInitializer(declarator.Initializer.WithValue(newValue));
                newDecl = decl.WithVariables(SyntaxFactory.SingletonSeparatedList(newDeclarator));
            }

            Count++;
            return node.WithDeclaration(newDecl);
        }

        private static bool IsSimpleRankOneArray(ArrayTypeSyntax type)
        {
            return type.RankSpecifiers.Count == 1
                   && type.RankSpecifiers[0].Rank == 1
                   && type.ElementType is not ArrayTypeSyntax;
        }

        private static bool HasImplicitArrayInferenceRisk(InitializerExpressionSyntax initializer)
        {
            foreach (ExpressionSyntax expression in initializer.Expressions)
            {
                ExpressionSyntax unwrapped = WalkDownParentheses(expression);
                if (unwrapped.IsKind(SyntaxKind.NullLiteralExpression)
                    || unwrapped.IsKind(SyntaxKind.DefaultLiteralExpression)
                    || unwrapped is DefaultExpressionSyntax
                    || unwrapped is AnonymousFunctionExpressionSyntax
                    || unwrapped is IdentifierNameSyntax
                    || unwrapped is MemberAccessExpressionSyntax)
                {
                    return true;
                }
            }
            return false;
        }

        private static ExpressionSyntax WalkDownParentheses(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
            }
            return expression;
        }
    }
}
