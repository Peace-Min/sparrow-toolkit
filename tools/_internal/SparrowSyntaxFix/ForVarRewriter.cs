using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparrowSyntaxFix
{
    // forvar: `for (int i = 0; ...)` -> `for (var i = 0; ...)` when the for-initializer is a SINGLE declarator
    // whose initializer is "obvious" by the exact same judgment obviousvar uses (ObviousVarRewriter.RewriteInitializer:
    // literal / cast / obvious numeric). Reusing that judgment means a method-call / member-access / `new` / `null`
    // initializer returns null and is skipped (no narrowing risk). Multi-declarator inits are NEVER touched
    // (`for (int i = 0, count = q.Count; ...)` -> `var i = 0, count = ...` would be CS0819). Already-`var` is a no-op.
    internal sealed class ForVarRewriter : CSharpSyntaxRewriter
    {
        public int Count { get; private set; }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        {
            var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
            VariableDeclarationSyntax? decl = visited.Declaration;
            if (decl == null) return visited;                                   // for(; ...) has no init declaration
            if (VarRewriteHelpers.IsVarType(decl.Type)) return visited;         // already var -> no-op (idempotent)
            if (decl.Variables.Count != 1) return visited;                      // multi-declarator: never touch (CS0819)

            VariableDeclaratorSyntax declarator = decl.Variables[0];
            if (declarator.Initializer == null) return visited;                 // `for (int i; ...)` (rare) -> skip
            ExpressionSyntax value = declarator.Initializer.Value;
            if (value.IsKind(SyntaxKind.NullLiteralExpression)) return visited;  // `var i = null` cannot infer -> skip

            // Reuse obviousvar's judgment. Method-call / member-access / `new` initializers return null here and
            // are skipped (축소위험). A numeric literal for a non-int target comes back wrapped in a cast so the
            // element type is preserved (mirrors obviousvar), e.g. `for (double d = 20; ...)` -> `var d = (double)20`.
            ExpressionSyntax? newValue = ObviousVarRewriter.RewriteInitializer(decl.Type, value);
            if (newValue == null) return visited;

            VariableDeclaratorSyntax newDeclarator = declarator.WithInitializer(declarator.Initializer.WithValue(newValue));
            VariableDeclarationSyntax newDecl = VarRewriteHelpers.WithVarType(decl)
                .WithVariables(SyntaxFactory.SingletonSeparatedList(newDeclarator));

            Count++;
            return visited.WithDeclaration(newDecl);
        }
    }
}
