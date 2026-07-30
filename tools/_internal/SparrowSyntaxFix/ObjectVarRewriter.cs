using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparrowSyntaxFix
{
    // objectvar-safe / objectvar-narrowing.
    internal sealed class ObjectVarRewriter : CSharpSyntaxRewriter
    {
        private readonly bool _allowNarrowing;

        public ObjectVarRewriter(bool allowNarrowing)
        {
            _allowNarrowing = allowNarrowing;
        }

        public int Count { get; private set; }

        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            if (!VarRewriteHelpers.IsSingleLocal(node, out VariableDeclarationSyntax decl, out VariableDeclaratorSyntax declarator))
                return base.VisitLocalDeclarationStatement(node);
            if (node.Modifiers.Any(SyntaxKind.ConstKeyword)) return base.VisitLocalDeclarationStatement(node);
            if (declarator.Initializer?.Value is not ObjectCreationExpressionSyntax creation)
                return base.VisitLocalDeclarationStatement(node);
            if (creation.Type == null) return base.VisitLocalDeclarationStatement(node);

            bool sameType = VarRewriteHelpers.TypeKey(decl.Type) == VarRewriteHelpers.TypeKey(creation.Type);
            if (_allowNarrowing == sameType) return base.VisitLocalDeclarationStatement(node);

            Count++;
            return node.WithDeclaration(VarRewriteHelpers.WithVarType(decl));
        }
    }
}
