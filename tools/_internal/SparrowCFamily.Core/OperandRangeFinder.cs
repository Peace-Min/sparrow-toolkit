using System;
using System.Collections.Generic;

namespace SparrowCFamily.Core
{
    internal readonly struct TokenRange
    {
        internal TokenRange(int startIndex, int endIndex)
        {
            StartIndex = startIndex;
            EndIndex = endIndex;
        }

        internal int StartIndex { get; }
        internal int EndIndex { get; }
    }

    /// <summary>
    /// 비교 연산자 양쪽의 전체 피연산자 범위를 찾습니다. 함수 호출·그룹 괄호·배열 접근·멤버 접근과
    /// 산술/비트 연산은 범위에 포함하고, 논리식 경계·다른 비교·대입·삼항식·쉼표식에서는 멈춥니다.
    /// </summary>
    internal static class OperandRangeFinder
    {
        internal static bool TryFind(
            IReadOnlyList<CodeToken> tokens,
            int operatorIndex,
            out TokenRange left,
            out TokenRange right)
        {
            left = default;
            right = default;
            if (operatorIndex <= 0 || operatorIndex + 1 >= tokens.Count) return false;
            int leftStart = FindLeftStart(tokens, operatorIndex - 1);
            int rightEnd = FindRightEnd(tokens, operatorIndex + 1);
            if (leftStart > operatorIndex - 1 || rightEnd < operatorIndex + 1) return false;
            left = new TokenRange(leftStart, operatorIndex - 1);
            right = new TokenRange(operatorIndex + 1, rightEnd);
            return true;
        }

        internal static bool ContainsUnsafeExpression(IReadOnlyList<CodeToken> tokens, TokenRange range)
        {
            var callParentheses = new Stack<bool>();
            int bracketDepth = 0;
            int braceDepth = 0;
            for (int i = range.StartIndex; i <= range.EndIndex; i++)
            {
                CodeToken token = tokens[i];
                if (token.Is("?")) return true;
                if (IsAssignment(token)) return true;
                if (token.Is("("))
                {
                    bool call = i > range.StartIndex && IsCallableSuffix(tokens[i - 1]);
                    callParentheses.Push(call);
                    continue;
                }
                if (token.Is(")"))
                {
                    if (callParentheses.Count > 0) callParentheses.Pop();
                    continue;
                }
                if (token.Is("[")) { bracketDepth++; continue; }
                if (token.Is("]")) { if (bracketDepth > 0) bracketDepth--; continue; }
                if (token.Is("{")) { braceDepth++; continue; }
                if (token.Is("}")) { if (braceDepth > 0) braceDepth--; continue; }
                if (token.Is(":") && callParentheses.Count == 0 && bracketDepth == 0 && braceDepth == 0) return true;
                if (token.Is(","))
                {
                    // 함수 호출의 인자 구분 쉼표만 허용합니다. 그룹 괄호의 comma operator와
                    // 배열 인덱스/초기화 목록의 쉼표는 평가 순서를 바꿀 위험이 있어 제외합니다.
                    if (callParentheses.Count == 0 || !callParentheses.Peek()) return true;
                }
            }
            return false;
        }

        private static int FindLeftStart(IReadOnlyList<CodeToken> tokens, int end)
        {
            int parentheses = 0;
            int brackets = 0;
            int braces = 0;
            int start = end;
            for (int i = end; i >= 0; i--)
            {
                CodeToken token = tokens[i];
                if (token.Is(")")) parentheses++;
                else if (token.Is("]")) brackets++;
                else if (token.Is("}")) braces++;
                else if (token.Is("("))
                {
                    if (parentheses == 0) break;
                    parentheses--;
                }
                else if (token.Is("["))
                {
                    if (brackets == 0) break;
                    brackets--;
                }
                else if (token.Is("{"))
                {
                    if (braces == 0) break;
                    braces--;
                }
                else if (parentheses == 0 && brackets == 0 && braces == 0 &&
                         (IsBoundary(token) || IsLeftKeywordBoundary(token)))
                {
                    break;
                }
                start = i;
            }
            return start;
        }

        private static int FindRightEnd(IReadOnlyList<CodeToken> tokens, int start)
        {
            int parentheses = 0;
            int brackets = 0;
            int braces = 0;
            int end = start;
            for (int i = start; i < tokens.Count; i++)
            {
                CodeToken token = tokens[i];
                if (token.Is("(")) parentheses++;
                else if (token.Is("[")) brackets++;
                else if (token.Is("{")) braces++;
                else if (token.Is(")"))
                {
                    if (parentheses == 0) break;
                    parentheses--;
                }
                else if (token.Is("]"))
                {
                    if (brackets == 0) break;
                    brackets--;
                }
                else if (token.Is("}"))
                {
                    if (braces == 0) break;
                    braces--;
                }
                else if (parentheses == 0 && brackets == 0 && braces == 0 && IsBoundary(token))
                {
                    break;
                }
                end = i;
            }
            return end;
        }

        private static bool IsCallableSuffix(CodeToken token) =>
            token.IsIdentifier || token.Is(")") || token.Is("]");

        private static bool IsLeftKeywordBoundary(CodeToken token) =>
            token.Is("return") || token.Is("case") || token.Is("throw") || token.Is("co_return");

        private static bool IsBoundary(CodeToken token) =>
            token.Is(";") || token.Is(",") || token.Is("?") || token.Is(":") ||
            token.Is("&&") || token.Is("||") || IsAssignment(token) || IsComparison(token);

        private static bool IsAssignment(CodeToken token) =>
            token.Is("=") || token.Is("+=") || token.Is("-=") || token.Is("*=") || token.Is("/=") ||
            token.Is("%=") || token.Is("&=") || token.Is("|=") || token.Is("^=") || token.Is("<<=") || token.Is(">>=");

        private static bool IsComparison(CodeToken token) =>
            token.Is("==") || token.Is("!=") || token.Is("<") || token.Is("<=") ||
            token.Is(">") || token.Is(">=") || token.Is("<=>");
    }
}
