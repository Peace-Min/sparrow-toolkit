using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparrowSyntaxFix
{
    // localconst: const T x = literal; -> var x = literal/cast-literal.
    internal sealed class LocalConstRewriter : CSharpSyntaxRewriter
    {
        public int Count { get; private set; }

        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            if (!node.Modifiers.Any(SyntaxKind.ConstKeyword)) return base.VisitLocalDeclarationStatement(node);
            if (!VarRewriteHelpers.IsSingleLocal(node, out VariableDeclarationSyntax decl, out VariableDeclaratorSyntax declarator))
                return base.VisitLocalDeclarationStatement(node);
            if (declarator.Initializer == null) return base.VisitLocalDeclarationStatement(node);
            if (IsUsedAsCaseLabel(node, declarator.Identifier.ValueText)) return base.VisitLocalDeclarationStatement(node);

            ExpressionSyntax? newValue = ObviousVarRewriter.RewriteInitializer(decl.Type, declarator.Initializer.Value);
            if (newValue == null) return base.VisitLocalDeclarationStatement(node);

            SyntaxTriviaList leading = node.Modifiers.First(m => m.IsKind(SyntaxKind.ConstKeyword)).LeadingTrivia;
            SyntaxTokenList newModifiers = SyntaxFactory.TokenList(node.Modifiers.Where(m => !m.IsKind(SyntaxKind.ConstKeyword)));
            VariableDeclaratorSyntax newDeclarator = declarator.WithInitializer(declarator.Initializer.WithValue(newValue));
            TypeSyntax varType = SyntaxFactory.IdentifierName("var")
                .WithLeadingTrivia(leading)
                .WithTrailingTrivia(decl.Type.GetTrailingTrivia());
            VariableDeclarationSyntax newDecl = decl.WithType(varType)
                .WithVariables(SyntaxFactory.SingletonSeparatedList(newDeclarator));

            Count++;
            return node.WithModifiers(newModifiers).WithDeclaration(newDecl);
        }

        private static bool IsUsedAsCaseLabel(SyntaxNode node, string identifier)
        {
            SyntaxNode? scope = node.FirstAncestorOrSelf<MethodDeclarationSyntax>()
                                ?? (SyntaxNode?)node.FirstAncestorOrSelf<AccessorDeclarationSyntax>()
                                ?? node.FirstAncestorOrSelf<ConstructorDeclarationSyntax>();
            if (scope == null) return false;

            foreach (CaseSwitchLabelSyntax label in scope.DescendantNodes().OfType<CaseSwitchLabelSyntax>())
            {
                if (label.Value is IdentifierNameSyntax id && id.Identifier.ValueText == identifier) return true;
            }
            return false;
        }
    }
}
