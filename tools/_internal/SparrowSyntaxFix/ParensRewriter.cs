// Rule 2 (parens): for a logical &&/|| expression, wrap EVERY operand in parentheses — atoms
// (identifier/literal/member `a.b.c`/invocation `f()`/element access/`this`/unary `!x`/cast), comparisons
// (`< > <= >= == !=`), arithmetic/bitwise (`+ - * / % << >> & | ^`), and the OTHER logical operator —
// UNLESS the operand is already a ParenthesizedExpression, or is a same-operator logical chain
// (`a && b && c` stays flat; its leaf operands are wrapped by recursion).
//
// Sparrow's MISSING_PARENTHESIS_IN_EXPRESSION requires ALL operands wrapped, not just the ambiguous ones:
// re-analysis confirmed `(a) || b` is still flagged while `(a) || (b)` clears. Wrapping any complete
// subexpression in parens is always semantics-preserving. Idempotent: `(a > 0)` is already parenthesized,
// and a same-operator chain is left flat so a second pass changes nothing.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparrowSyntaxFix
{
    internal sealed class ParensRewriter : CSharpSyntaxRewriter
    {
        public int Count { get; private set; }

        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            // Recurse first so nested &&/|| operands are processed before we wrap at this level.
            var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;

            SyntaxKind parentKind = node.Kind();
            if (parentKind != SyntaxKind.LogicalAndExpression && parentKind != SyntaxKind.LogicalOrExpression)
                return visited;

            ExpressionSyntax newLeft = visited.Left;
            ExpressionSyntax newRight = visited.Right;

            if (ShouldWrap(visited.Left, parentKind)) { newLeft = Wrap(visited.Left); Count++; }
            if (ShouldWrap(visited.Right, parentKind)) { newRight = Wrap(visited.Right); Count++; }

            if (newLeft != visited.Left || newRight != visited.Right)
                return visited.WithLeft(newLeft).WithRight(newRight);
            return visited;
        }

        // The inserted `(` takes the operand's leading trivia and `)` its trailing trivia, so surrounding
        // whitespace/newlines (multi-line conditions) are preserved; the operand's inner trivia is kept.
        private static ExpressionSyntax Wrap(ExpressionSyntax operand) =>
            SyntaxFactory.ParenthesizedExpression(operand.WithoutTrivia())
                .WithLeadingTrivia(operand.GetLeadingTrivia())
                .WithTrailingTrivia(operand.GetTrailingTrivia());

        private static bool ShouldWrap(ExpressionSyntax operand, SyntaxKind parentKind)
        {
            if (operand is ParenthesizedExpressionSyntax) return false;   // already parenthesized -> idempotent
            // A same-operator logical chain (a && b && c) stays flat; its leaf operands are wrapped by recursion,
            // yielding (a) && (b) && (c) rather than ((a) && (b)) && (c).
            if (operand.IsKind(parentKind)) return false;
            // Everything else is wrapped: atoms (identifier/literal/member/invocation/element/this/unary/cast),
            // comparisons, arithmetic/bitwise, and the OTHER logical operator. Sparrow requires EVERY operand of
            // &&/|| to be parenthesized (confirmed: `(a) || b` is still flagged; `(a) || (b)` clears).
            return true;
        }
    }
}
