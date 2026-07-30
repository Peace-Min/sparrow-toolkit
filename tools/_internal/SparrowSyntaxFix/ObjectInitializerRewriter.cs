using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparrowSyntaxFix
{
    // objectinitializer: `Foo x = new Foo(); x.A = 1; x.B = 2;`
    // -> `var x = new Foo { A = 1, B = 2 };`.
    internal sealed class ObjectInitializerRewriter : CSharpSyntaxRewriter
    {
        public int Count { get; private set; }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            BlockSyntax visited = (BlockSyntax)(base.VisitBlock(node) ?? node);
            var rewritten = new List<StatementSyntax>(visited.Statements.Count);
            bool changed = false;

            for (int i = 0; i < visited.Statements.Count; i++)
            {
                StatementSyntax statement = visited.Statements[i];
                if (!TryBuild(statement, visited.Statements, i, out LocalDeclarationStatementSyntax replacement, out int consumed))
                {
                    rewritten.Add(statement);
                    continue;
                }

                rewritten.Add(replacement);
                i += consumed;
                Count++;
                changed = true;
            }

            return changed ? visited.WithStatements(SyntaxFactory.List(rewritten)) : visited;
        }

        private static bool TryBuild(StatementSyntax statement, SyntaxList<StatementSyntax> statements, int index,
                                     out LocalDeclarationStatementSyntax replacement, out int consumed)
        {
            replacement = default!;
            consumed = 0;

            if (statement is not LocalDeclarationStatementSyntax local) return false;
            if (!VarRewriteHelpers.IsSingleLocal(local, out VariableDeclarationSyntax decl, out VariableDeclaratorSyntax declarator))
                return false;
            if (local.Modifiers.Any(SyntaxKind.ConstKeyword)) return false;
            if (declarator.Initializer?.Value is not ObjectCreationExpressionSyntax creation) return false;
            if (creation.Initializer != null) return false;
            if (creation.Type == null) return false;
            if (VarRewriteHelpers.TypeKey(decl.Type) != VarRewriteHelpers.TypeKey(creation.Type)) return false;
            if (HasRiskyTrivia(local)) return false;

            string variableName = declarator.Identifier.ValueText;
            var assignments = new List<AssignmentExpressionSyntax>();
            var memberNames = new HashSet<string>(System.StringComparer.Ordinal);

            int j = index + 1;
            while (j < statements.Count && TryGetMemberAssignment(statements[j], variableName, out AssignmentExpressionSyntax assignment))
            {
                if (HasRiskyTrivia(statements[j])) return false;
                var left = (MemberAccessExpressionSyntax)assignment.Left;
                if (!memberNames.Add(left.Name.Identifier.ValueText)) return false;
                if (ReferencesIdentifier(assignment.Right, variableName)) return false;
                if (ContainsInvocation(assignment.Right)) return false;
                assignments.Add(assignment);
                j++;
            }

            if (assignments.Count == 0) return false;

            string creationText = creation.WithoutTrivia().ToFullString();
            if (creationText.EndsWith("()", System.StringComparison.Ordinal))
                creationText = creationText.Substring(0, creationText.Length - 2);

            var initializerParts = new List<string>(assignments.Count);
            foreach (AssignmentExpressionSyntax assignment in assignments)
            {
                var left = (MemberAccessExpressionSyntax)assignment.Left;
                initializerParts.Add(left.Name.WithoutTrivia().ToFullString() + " = " + assignment.Right.WithoutTrivia().ToFullString());
            }

            ExpressionSyntax newCreation = SyntaxFactory.ParseExpression(
                    creationText + " { " + string.Join(", ", initializerParts) + " }")
                .WithLeadingTrivia(creation.GetLeadingTrivia())
                .WithTrailingTrivia(creation.GetTrailingTrivia());

            VariableDeclaratorSyntax newDeclarator = declarator.WithInitializer(declarator.Initializer.WithValue(newCreation));
            VariableDeclarationSyntax newDecl = VarRewriteHelpers.WithVarType(decl).WithVariables(
                SyntaxFactory.SingletonSeparatedList(newDeclarator));

            replacement = local.WithDeclaration(newDecl);
            consumed = assignments.Count;
            return true;
        }

        private static bool TryGetMemberAssignment(StatementSyntax statement, string variableName,
                                                   out AssignmentExpressionSyntax assignment)
        {
            assignment = default!;
            if (statement is not ExpressionStatementSyntax exprStmt) return false;
            if (exprStmt.Expression is not AssignmentExpressionSyntax assign) return false;
            if (!assign.IsKind(SyntaxKind.SimpleAssignmentExpression)) return false;
            if (assign.Left is not MemberAccessExpressionSyntax member) return false;
            if (member.Expression is not IdentifierNameSyntax id) return false;
            if (id.Identifier.ValueText != variableName) return false;
            assignment = assign;
            return true;
        }

        private static bool ReferencesIdentifier(SyntaxNode node, string variableName)
        {
            foreach (IdentifierNameSyntax id in node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if (id.Identifier.ValueText == variableName) return true;
            }
            return false;
        }

        private static bool ContainsInvocation(SyntaxNode node)
        {
            return node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any();
        }

        private static bool HasRiskyTrivia(SyntaxNode node)
        {
            foreach (SyntaxTrivia trivia in node.DescendantTrivia(descendIntoTrivia: true))
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
    }
}
