using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparrowSyntaxFix
{
    // obviousvar: obvious literal/cast/Convert initializer -> var, with casts when needed to preserve type.
    internal sealed class ObviousVarRewriter : CSharpSyntaxRewriter
    {
        public int Count { get; private set; }

        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            if (!VarRewriteHelpers.IsSingleLocal(node, out VariableDeclarationSyntax decl, out VariableDeclaratorSyntax declarator))
                return base.VisitLocalDeclarationStatement(node);
            if (node.Modifiers.Any(SyntaxKind.ConstKeyword)) return base.VisitLocalDeclarationStatement(node);
            if (declarator.Initializer == null) return base.VisitLocalDeclarationStatement(node);

            ExpressionSyntax value = declarator.Initializer.Value;
            if (value.IsKind(SyntaxKind.NullLiteralExpression)) return base.VisitLocalDeclarationStatement(node);
            if (value is ObjectCreationExpressionSyntax) return base.VisitLocalDeclarationStatement(node);

            ExpressionSyntax? newValue = RewriteInitializer(decl.Type, value);
            if (newValue == null) return base.VisitLocalDeclarationStatement(node);

            VariableDeclaratorSyntax newDeclarator = declarator.WithInitializer(declarator.Initializer.WithValue(newValue));
            VariableDeclarationSyntax newDecl = VarRewriteHelpers.WithVarType(decl)
                .WithVariables(SyntaxFactory.SingletonSeparatedList(newDeclarator));

            Count++;
            return node.WithDeclaration(newDecl);
        }

        internal static ExpressionSyntax? RewriteInitializer(TypeSyntax targetType, ExpressionSyntax value)
        {
            if (value is CastExpressionSyntax cast)
            {
                return VarRewriteHelpers.TypeEquals(targetType, cast.Type) ? value : null;
            }
            if (VarRewriteHelpers.IsStringCharOrBoolLiteral(value))
            {
                return LiteralMatchesTargetType(targetType, value) ? value : null;
            }

            if (VarRewriteHelpers.IsNumericLiteral(value))
            {
                string typeKey = VarRewriteHelpers.TypeKey(targetType);
                if (typeKey == "int" || typeKey == "System.Int32" || typeKey == "Int32") return value;
                return VarRewriteHelpers.CastValue(targetType, value);
            }

            return null;
        }

        private static bool LiteralMatchesTargetType(TypeSyntax targetType, ExpressionSyntax value)
        {
            if (value.IsKind(SyntaxKind.StringLiteralExpression))
                return VarRewriteHelpers.TypeMatchesAny(targetType, "string", "System.String", "String");
            if (value.IsKind(SyntaxKind.CharacterLiteralExpression))
                return VarRewriteHelpers.TypeMatchesAny(targetType, "char", "System.Char", "Char");
            if (value.IsKind(SyntaxKind.TrueLiteralExpression) || value.IsKind(SyntaxKind.FalseLiteralExpression))
                return VarRewriteHelpers.TypeMatchesAny(targetType, "bool", "System.Boolean", "Boolean");
            return false;
        }
    }
}
