using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparrowSyntaxFix
{
    // forhoist: resolve a MULTI-declarator for-initializer that Sparrow flags simultaneously with
    // LOOP_VARIABLE + USE_ONE_DECLARATION + OBVIOUS_VARIABLE_TYPE and that CANNOT be fixed in place
    // (`var` forbids multiple declarators [CS0819], and a for-init is a single declaration slot that
    // cannot be split). Example (real MyApp issues_sample_6888.xls):
    //
    //     for (int i = 0, count = queue.Count; i < count; i++)   ->   var count = queue.Count;
    //                                                                  for (var i = 0; i < count; i++)
    //
    // The loop variable(s) = the declarator name(s) referenced in the for's Incrementors (fallback:
    // those in the Condition when no incrementor references any declarator). Exactly the loop variable
    // stays in the for-init; every OTHER declarator is HOISTED to its own local statement immediately
    // before the for, at the SAME indentation, preserving original order among the hoisted ones.
    //
    // Not a CSharpSyntaxRewriter because ONE for statement must become several statements (hoisted
    // locals + the trimmed for). We repeatedly find the first qualifying for and ReplaceNode(node,
    // IEnumerable) to splice them into the enclosing block's statement list. The trimmed for is a
    // single-declarator init, so it never re-qualifies -> the loop terminates (and is idempotent).
    internal static class ForHoistRewriter
    {
        public static SyntaxNode Rewrite(SyntaxNode root, string source, out int count)
        {
            count = 0;
            SyntaxTrivia eol = SyntaxFactory.EndOfLine(source.Contains("\r\n") ? "\r\n" : "\n");

            while (true)
            {
                ForStatementSyntax? target = root.DescendantNodes()
                    .OfType<ForStatementSyntax>()
                    .FirstOrDefault(f => TryPlan(f, out _, out _, out _));
                if (target == null) break;

                if (!TryPlan(target, out VariableDeclaratorSyntax kept,
                             out List<VariableDeclaratorSyntax> hoisted, out VariableDeclarationSyntax decl))
                {
                    break; // defensive: should not happen because FirstOrDefault already proved a plan
                }

                IReadOnlyList<StatementSyntax> parts = BuildReplacement(target, decl, kept, hoisted, eol);
                if (parts.Count <= 1) break; // defensive: never loop forever
                root = root.ReplaceNode(target, parts);
                count++;
            }
            return root;
        }

        // A for qualifies iff we can build a safe plan: multi-declarator decl in a block, exactly one loop
        // variable determinable, at least one hoistable declarator, no dependency/collision hazard.
        private static bool TryPlan(ForStatementSyntax node, out VariableDeclaratorSyntax kept,
                                    out List<VariableDeclaratorSyntax> hoisted, out VariableDeclarationSyntax decl)
        {
            kept = default!;
            hoisted = default!;
            decl = default!;

            // Must live directly in a block so we can splice statements in before it.
            if (node.Parent is not BlockSyntax) return false;

            VariableDeclarationSyntax? d = node.Declaration;
            if (d == null) return false;                // for-init is an expression list, not a declaration
            if (d.Variables.Count < 2) return false;    // single declarator: nothing to hoist (also idempotent stop)

            var declNames = new HashSet<string>(d.Variables.Select(v => v.Identifier.ValueText), System.StringComparer.Ordinal);

            // Loop variable(s): declarator name(s) referenced in the Incrementors. Fallback: the Condition.
            var loopNames = ReferencedDeclNames(node.Incrementors, declNames);
            if (loopNames.Count == 0 && node.Condition != null)
                loopNames = ReferencedDeclNames(new[] { node.Condition }, declNames);
            if (loopNames.Count == 0) return false;     // cannot determine the loop variable -> skip

            var keptList = d.Variables.Where(v => loopNames.Contains(v.Identifier.ValueText)).ToList();
            var hoistList = d.Variables.Where(v => !loopNames.Contains(v.Identifier.ValueText)).ToList();
            if (hoistList.Count == 0) return false;      // nothing to hoist
            if (keptList.Count != 1) return false;       // the for must remain a SINGLE-declarator init

            var keptNames = new HashSet<string>(keptList.Select(v => v.Identifier.ValueText), System.StringComparer.Ordinal);

            // Dependency guard: a hoisted declarator's initializer must not reference the kept loop variable
            // (moving it before the for would read a not-yet-declared name / change evaluation semantics).
            foreach (var h in hoistList)
            {
                if (h.Initializer == null) continue;
                if (ReferencesAny(h.Initializer.Value, keptNames)) return false;
            }

            // Name-collision guard: a hoisted name already declared as a local/param in the enclosing scope
            // would be shadowed/redeclared -> skip.
            if (HasEnclosingCollision(node, hoistList.Select(v => v.Identifier.ValueText))) return false;

            kept = keptList[0];
            hoisted = hoistList;
            decl = d;
            return true;
        }

        private static HashSet<string> ReferencedDeclNames(IEnumerable<ExpressionSyntax> expressions, HashSet<string> declNames)
        {
            var result = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (ExpressionSyntax e in expressions)
            {
                foreach (IdentifierNameSyntax id in e.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                {
                    string n = id.Identifier.ValueText;
                    if (declNames.Contains(n)) result.Add(n);
                }
            }
            return result;
        }

        private static bool ReferencesAny(ExpressionSyntax expr, HashSet<string> names)
        {
            foreach (IdentifierNameSyntax id in expr.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                if (names.Contains(id.Identifier.ValueText)) return true;
            return false;
        }

        // Walk to the nearest enclosing body (method / accessor / local-function / lambda / property) and
        // conservatively collect every parameter + declared local + foreach/catch variable name; a hoisted
        // name matching any of those is treated as a collision. Over-conservative on purpose (avoids shadowing).
        private static bool HasEnclosingCollision(ForStatementSyntax forNode, IEnumerable<string> hoistedNames)
        {
            var names = new HashSet<string>(hoistedNames, System.StringComparer.Ordinal);
            if (names.Count == 0) return false;

            SyntaxNode scope = forNode.Ancestors().FirstOrDefault(a =>
                a is BaseMethodDeclarationSyntax
                || a is AccessorDeclarationSyntax
                || a is LocalFunctionStatementSyntax
                || a is AnonymousFunctionExpressionSyntax
                || a is PropertyDeclarationSyntax) ?? forNode.SyntaxTree.GetRoot();

            foreach (ParameterSyntax p in scope.DescendantNodes().OfType<ParameterSyntax>())
                if (names.Contains(p.Identifier.ValueText)) return true;

            foreach (VariableDeclaratorSyntax v in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                // The target for's OWN init declarators are not a collision with themselves.
                if (forNode.Declaration != null && forNode.Declaration.Variables.Contains(v)) continue;
                if (names.Contains(v.Identifier.ValueText)) return true;
            }

            foreach (ForEachStatementSyntax fe in scope.DescendantNodes().OfType<ForEachStatementSyntax>())
                if (names.Contains(fe.Identifier.ValueText)) return true;

            foreach (CatchDeclarationSyntax c in scope.DescendantNodes().OfType<CatchDeclarationSyntax>())
                if (!c.Identifier.IsKind(SyntaxKind.None) && names.Contains(c.Identifier.ValueText)) return true;

            return false;
        }

        private static IReadOnlyList<StatementSyntax> BuildReplacement(
            ForStatementSyntax node, VariableDeclarationSyntax decl,
            VariableDeclaratorSyntax kept, List<VariableDeclaratorSyntax> hoisted, SyntaxTrivia eol)
        {
            SyntaxTriviaList forLeading = node.GetLeadingTrivia();
            SyntaxTriviaList forTrailing = node.GetTrailingTrivia();

            // Indentation of the for's own physical line = the trailing whitespace run of its leading trivia.
            SyntaxTrivia indent = forLeading.Count > 0 && forLeading[forLeading.Count - 1].IsKind(SyntaxKind.WhitespaceTrivia)
                ? forLeading[forLeading.Count - 1]
                : SyntaxFactory.Whitespace("");

            var result = new List<StatementSyntax>(hoisted.Count + 1);
            for (int k = 0; k < hoisted.Count; k++)
            {
                string text = BuildHoistedText(decl.Type, hoisted[k]);
                var stmt = (StatementSyntax)SyntaxFactory.ParseStatement(text);

                // First hoisted line inherits the for's original leading trivia (the newline + indent that put
                // the for on its own line). Later hoisted lines only need the indentation (previous line's EOL
                // already broke the line).
                stmt = k == 0
                    ? stmt.WithLeadingTrivia(forLeading)
                    : stmt.WithLeadingTrivia(SyntaxFactory.TriviaList(indent));
                stmt = stmt.WithTrailingTrivia(SyntaxFactory.TriviaList(eol));
                result.Add(stmt);
            }

            ForStatementSyntax newFor = BuildTrimmedFor(node, decl, kept)
                .WithLeadingTrivia(SyntaxFactory.TriviaList(indent))
                .WithTrailingTrivia(forTrailing);
            result.Add(newFor);
            return result;
        }

        // Type judgment for a HOISTED declarator: emit `var name = init;` for any well-typed initializer
        // (this is what clears OBVIOUS_VARIABLE_TYPE and matches the exact required transform, e.g.
        // `var count = queue.Count;`). Reuse ObviousVarRewriter.RewriteInitializer so numeric literals keep
        // their element type via a cast (e.g. `var d = (double)20;`). Keep the EXPLICIT type only where var
        // cannot or should not infer: null / default / a lambda-or-method-group (no target type outside the
        // declaration). A declarator with no initializer also keeps its explicit type.
        private static string BuildHoistedText(TypeSyntax sharedType, VariableDeclaratorSyntax d)
        {
            string typeText = sharedType.WithoutTrivia().ToFullString();
            string name = d.Identifier.ValueText;

            if (d.Initializer == null)
                return typeText + " " + name + ";";

            ExpressionSyntax value = d.Initializer.Value;
            string valText = value.WithoutTrivia().ToFullString();

            if (KeepExplicit(value))
                return typeText + " " + name + " = " + valText + ";";

            ExpressionSyntax? rewritten = ObviousVarRewriter.RewriteInitializer(sharedType, value);
            string finalVal = rewritten != null ? rewritten.WithoutTrivia().ToFullString() : valText;
            return "var " + name + " = " + finalVal + ";";
        }

        // var cannot / should not infer from these -> keep the explicit declared type.
        private static bool KeepExplicit(ExpressionSyntax value)
        {
            if (value.IsKind(SyntaxKind.NullLiteralExpression)) return true;
            if (value.IsKind(SyntaxKind.DefaultLiteralExpression)) return true;
            if (value is DefaultExpressionSyntax) return true;
            if (value is AnonymousFunctionExpressionSyntax) return true; // lambda / anonymous method
            return false;
        }

        // Trim the for-init to just the kept loop variable. Convert its type to `var` ONLY when the loop-var
        // initializer is obvious by ObviousVarRewriter's strict judgment (e.g. `i = 0`); otherwise leave the
        // explicit type (still valid because it is now a single declarator).
        private static ForStatementSyntax BuildTrimmedFor(ForStatementSyntax node,
                                                          VariableDeclarationSyntax decl, VariableDeclaratorSyntax kept)
        {
            VariableDeclaratorSyntax normalizedKept = kept.WithLeadingTrivia(); // drop any post-comma leading space

            VariableDeclarationSyntax newDecl;
            if (normalizedKept.Initializer != null)
            {
                ExpressionSyntax? rewritten = ObviousVarRewriter.RewriteInitializer(decl.Type, normalizedKept.Initializer.Value);
                if (rewritten != null)
                {
                    VariableDeclaratorSyntax varKept = normalizedKept.WithInitializer(normalizedKept.Initializer.WithValue(rewritten));
                    newDecl = VarRewriteHelpers.WithVarType(decl)
                        .WithVariables(SyntaxFactory.SingletonSeparatedList(varKept));
                    return node.WithDeclaration(newDecl);
                }
            }

            newDecl = decl.WithVariables(SyntaxFactory.SingletonSeparatedList(normalizedKept));
            return node.WithDeclaration(newDecl);
        }
    }
}
