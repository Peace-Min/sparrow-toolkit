using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparrowSyntaxFix
{
    // foreachcast: foreach (T x in expr) -> foreach (var x in System.Linq.Enumerable.Cast<T>(expr)).
    internal sealed class ForeachCastRewriter : CSharpSyntaxRewriter
    {
        public int Count { get; private set; }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            ForEachStatementSyntax visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
            // Skip when the loop already uses `var` (nothing to widen) or when the source is already a
            // Cast<T>(...) / OfType<T>(...) invocation (idempotent — a second pass must be a no-op).
            if (VarRewriteHelpers.IsVarType(visited.Type)) return visited;
            if (IsAlreadyCastOrOfType(visited.Expression)) return visited;
            if (visited.Expression == null) return visited;

            // VALUE-TYPE GUARD (runtime-safety, syntax-only): `foreach (T x in expr)` performs an ELEMENT
            // CONVERSION per iteration, not a plain reference cast. For reference/other named types that
            // conversion IS a cast, so Enumerable.Cast<T>(expr) is semantics-equivalent (the rewrite proceeds).
            // But when T is a numeric/implicit-conversion value type, foreach relies on an IMPLICIT NUMERIC
            // conversion (e.g. `foreach (double d in intList)` — int→double, or `foreach (long l in intArray)`)
            // that Cast<T> does NOT perform: Cast unboxes to the EXACT runtime type and throws
            // InvalidCastException at the first iteration (unbox int→double is invalid). That survives the build
            // gate and explodes at loop execution. So we SKIP (leave the foreach unchanged) whenever the
            // declared element type is a known value-type keyword / well-known value type or any nullable form.
            if (IsNumericConversionRisk(visited.Type)) return visited;

            // Generalized beyond XmlNode: `foreach (T x in expr)` already casts EACH element to the explicit
            // type T per iteration, so `Enumerable.Cast<T>(expr)` is semantics-equivalent for ANY collection
            // expression (identifier, member access, invocation) whether the collection is generic
            // (List<T>) or non-generic (XmlNodeList, DataColumnCollection, UIElementCollection, ItemCollection).
            // Fully-qualified System.Linq.Enumerable.Cast needs no `using`. Remains review-needed + build-gated.
            TypeSyntax oldType = visited.Type;
            TypeSyntax varType = VarRewriteHelpers.VarLike(oldType);

            ExpressionSyntax castExpression = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ParseName("System.Linq.Enumerable"),
                        SyntaxFactory.GenericName("Cast")
                            .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                                SyntaxFactory.SingletonSeparatedList(oldType.WithoutTrivia())))))
                .WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(visited.Expression.WithoutTrivia()))))
                .WithLeadingTrivia(visited.Expression.GetLeadingTrivia())
                .WithTrailingTrivia(visited.Expression.GetTrailingTrivia());

            Count++;
            return visited.WithType(varType).WithExpression(castExpression);
        }

        // True when the declared foreach element type is a value type for which foreach's per-element
        // conversion is a NUMERIC/IMPLICIT conversion rather than a reference cast — making Enumerable.Cast<T>
        // an unsafe substitute (unbox-to-exact-type throws at runtime). Syntax-only, deliberately CONSERVATIVE:
        // when in doubt we skip (leaving the loop unchanged is always safe; a missed conversion is harmless).
        //
        // Covered:
        //   - any nullable form `T?` (NullableTypeSyntax) — unbox-to-nullable kept out conservatively;
        //   - predefined value-type keywords: sbyte/byte/short/ushort/int/uint/long/ulong/char/float/double/decimal/bool;
        //   - well-known value-type names (bare or `System.`-qualified): Int16/Int32/Int64/UInt16/UInt32/UInt64/
        //     Byte/SByte/Single/Double/Decimal/Char/Boolean, plus generic `Nullable<...>`.
        //
        // KNOWN LIMITATION (accepted residual risk, tracked by this rule's review-needed status): ENUMS declared
        // as explicit named types are syntactically indistinguishable from classes, so `foreach (MyEnum e in ints)`
        // (enum↔int implicit conversions) cannot be detected here and would still be rewritten. Human review of the
        // review-needed foreachcast commit + the build/Sparrow gates remain the backstop for that case.
        private static bool IsNumericConversionRisk(TypeSyntax type)
        {
            if (type is NullableTypeSyntax) return true;
            if (VarRewriteHelpers.IsPredefinedValueType(type)) return true;
            switch (GetSimpleTypeName(type))
            {
                case "Int16":
                case "Int32":
                case "Int64":
                case "UInt16":
                case "UInt32":
                case "UInt64":
                case "Byte":
                case "SByte":
                case "Single":
                case "Double":
                case "Decimal":
                case "Char":
                case "Boolean":
                case "Nullable":
                    return true;
                default:
                    return false;
            }
        }

        // Rightmost simple identifier of a type name (handles bare, generic, `A.B.C`-qualified and alias-qualified
        // names). Returns "" for forms we do not classify (arrays, pointers, tuples, predefined) — those are
        // handled elsewhere or are not in the value-type risk set.
        private static string GetSimpleTypeName(TypeSyntax type)
        {
            switch (type)
            {
                case IdentifierNameSyntax id: return id.Identifier.ValueText;
                case GenericNameSyntax generic: return generic.Identifier.ValueText;
                case QualifiedNameSyntax qualified: return GetSimpleTypeName(qualified.Right);
                case AliasQualifiedNameSyntax alias: return GetSimpleTypeName(alias.Name);
                default: return "";
            }
        }

        private static bool IsAlreadyCastOrOfType(ExpressionSyntax expression)
        {
            if (expression is not InvocationExpressionSyntax invocation) return false;
            ExpressionSyntax invoked = invocation.Expression;
            if (invoked is MemberAccessExpressionSyntax member)
            {
                string name = member.Name switch
                {
                    GenericNameSyntax generic => generic.Identifier.ValueText,
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    _ => "",
                };
                return name == "Cast" || name == "OfType";
            }
            return false;
        }
    }
}
