using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SparrowSyntaxFix
{
    internal static class VarRewriteHelpers
    {
        public static bool IsVarType(TypeSyntax type) =>
            type is IdentifierNameSyntax id && id.Identifier.ValueText == "var";

        public static TypeSyntax VarLike(TypeSyntax oldType) =>
            SyntaxFactory.IdentifierName("var")
                .WithLeadingTrivia(oldType.GetLeadingTrivia())
                .WithTrailingTrivia(oldType.GetTrailingTrivia());

        public static bool IsSingleLocal(LocalDeclarationStatementSyntax node, out VariableDeclarationSyntax decl,
                                         out VariableDeclaratorSyntax declarator)
        {
            decl = node.Declaration;
            declarator = default!;
            if (node.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) return false;
            if (IsVarType(decl.Type)) return false;
            if (decl.Variables.Count != 1) return false;
            declarator = decl.Variables[0];
            return true;
        }

        public static VariableDeclarationSyntax WithVarType(VariableDeclarationSyntax decl) =>
            decl.WithType(VarLike(decl.Type));

        public static ExpressionSyntax CastValue(TypeSyntax type, ExpressionSyntax value) =>
            SyntaxFactory.CastExpression(type.WithoutTrivia(), value.WithoutTrivia())
                .WithLeadingTrivia(value.GetLeadingTrivia())
                .WithTrailingTrivia(value.GetTrailingTrivia());

        public static string TypeKey(TypeSyntax type) => RemoveWhitespace(type.WithoutTrivia().ToFullString());

        public static bool TypeEquals(TypeSyntax left, TypeSyntax right) =>
            TypeKey(left) == TypeKey(right);

        public static bool TypeMatchesAny(TypeSyntax type, params string[] keys)
        {
            string typeKey = TypeKey(type);
            string canonicalTypeKey = CanonicalTypeKey(typeKey);
            foreach (string key in keys)
            {
                if (typeKey == key) return true;
                if (canonicalTypeKey == CanonicalTypeKey(key)) return true;
            }
            return false;
        }

        private static string CanonicalTypeKey(string key)
        {
            return key switch
            {
                "bool" or "Boolean" or "System.Boolean" => "System.Boolean",
                "byte" or "Byte" or "System.Byte" => "System.Byte",
                "char" or "Char" or "System.Char" => "System.Char",
                "decimal" or "Decimal" or "System.Decimal" => "System.Decimal",
                "double" or "Double" or "System.Double" => "System.Double",
                "float" or "Single" or "System.Single" => "System.Single",
                "int" or "Int32" or "System.Int32" => "System.Int32",
                "long" or "Int64" or "System.Int64" => "System.Int64",
                "object" or "Object" or "System.Object" => "System.Object",
                "sbyte" or "SByte" or "System.SByte" => "System.SByte",
                "short" or "Int16" or "System.Int16" => "System.Int16",
                "string" or "String" or "System.String" => "System.String",
                "uint" or "UInt32" or "System.UInt32" => "System.UInt32",
                "ulong" or "UInt64" or "System.UInt64" => "System.UInt64",
                "ushort" or "UInt16" or "System.UInt16" => "System.UInt16",
                _ => key,
            };
        }

        public static string RemoveWhitespace(string text) =>
            new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

        public static bool IsPredefinedValueType(TypeSyntax type)
        {
            if (type is not PredefinedTypeSyntax predefined) return false;
            SyntaxKind kind = predefined.Keyword.Kind();
            return kind == SyntaxKind.BoolKeyword
                   || kind == SyntaxKind.ByteKeyword
                   || kind == SyntaxKind.SByteKeyword
                   || kind == SyntaxKind.ShortKeyword
                   || kind == SyntaxKind.UShortKeyword
                   || kind == SyntaxKind.IntKeyword
                   || kind == SyntaxKind.UIntKeyword
                   || kind == SyntaxKind.LongKeyword
                   || kind == SyntaxKind.ULongKeyword
                   || kind == SyntaxKind.FloatKeyword
                   || kind == SyntaxKind.DoubleKeyword
                   || kind == SyntaxKind.DecimalKeyword
                   || kind == SyntaxKind.CharKeyword;
        }

        public static bool IsNumericLiteral(ExpressionSyntax expression)
        {
            if (!expression.IsKind(SyntaxKind.NumericLiteralExpression)) return false;
            return expression is LiteralExpressionSyntax;
        }

        public static bool IsStringCharOrBoolLiteral(ExpressionSyntax expression)
        {
            return expression.IsKind(SyntaxKind.StringLiteralExpression)
                   || expression.IsKind(SyntaxKind.CharacterLiteralExpression)
                   || expression.IsKind(SyntaxKind.TrueLiteralExpression)
                   || expression.IsKind(SyntaxKind.FalseLiteralExpression);
        }

        public static bool IsConvertToInvocation(ExpressionSyntax expression)
        {
            return TryGetConvertToResultType(expression, out _);
        }

        public static bool TryGetConvertToResultType(ExpressionSyntax expression, out string resultTypeKey)
        {
            resultTypeKey = "";
            if (expression is not InvocationExpressionSyntax invocation) return false;
            if (invocation.Expression is MemberAccessExpressionSyntax member
                && IsSystemConvertExpression(member.Expression))
            {
                resultTypeKey = member.Name.Identifier.ValueText switch
                {
                    "ToBoolean" => "bool",
                    "ToByte" => "byte",
                    "ToChar" => "char",
                    "ToDateTime" => "System.DateTime",
                    "ToDecimal" => "decimal",
                    "ToDouble" => "double",
                    "ToInt16" => "short",
                    "ToInt32" => "int",
                    "ToInt64" => "long",
                    "ToSByte" => "sbyte",
                    "ToSingle" => "float",
                    "ToString" => "string",
                    "ToUInt16" => "ushort",
                    "ToUInt32" => "uint",
                    "ToUInt64" => "ulong",
                    _ => "",
                };
                return resultTypeKey.Length > 0;
            }
            return false;
        }

        private static bool IsSystemConvertExpression(ExpressionSyntax expression)
        {
            string text = RemoveWhitespace(expression.WithoutTrivia().ToFullString());
            return text == "global::System.Convert";
        }
    }
}
