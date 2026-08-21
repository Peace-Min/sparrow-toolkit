using System;
using System.Collections.Generic;

namespace SparrowCFamily.Core
{
    internal enum CodeTokenKind
    {
        Identifier,
        Number,
        StringLiteral,
        CharacterLiteral,
        Operator,
        Punctuation,
    }

    internal enum LexicalSpanKind
    {
        Preprocessor,
        LineComment,
        BlockComment,
        StringLiteral,
        CharacterLiteral,
    }

    internal readonly struct CodeToken
    {
        private readonly string _source;
        private readonly string? _knownText;

        internal CodeToken(string source, int start, int end, CodeTokenKind kind, string? knownText = null)
        {
            _source = source;
            _knownText = knownText;
            Start = start;
            End = end;
            Kind = kind;
        }

        internal string Text => _knownText ?? _source.Substring(Start, End - Start);
        internal int Start { get; }
        internal int End { get; }
        internal CodeTokenKind Kind { get; }
        internal bool IsIdentifier => Kind == CodeTokenKind.Identifier;
        internal ReadOnlySpan<char> Span => _source.AsSpan(Start, End - Start);
        internal bool Is(string value) => _knownText != null
            ? string.Equals(_knownText, value, StringComparison.Ordinal)
            : _source.AsSpan(Start, End - Start).SequenceEqual(value.AsSpan());
        internal string SliceText(int relativeStart, int length) =>
            _source.Substring(Start + relativeStart, length);
    }

    internal readonly struct LexicalSpan
    {
        internal LexicalSpan(LexicalSpanKind kind, int start, int end)
        {
            Kind = kind;
            Start = start;
            End = end;
        }

        internal LexicalSpanKind Kind { get; }
        internal int Start { get; }
        internal int End { get; }
    }

    internal sealed class TokenizationResult
    {
        internal TokenizationResult(List<CodeToken> tokens, List<LexicalSpan> protectedSpans)
        {
            Tokens = tokens;
            ProtectedSpans = protectedSpans;
        }

        internal IReadOnlyList<CodeToken> Tokens { get; }
        internal IReadOnlyList<LexicalSpan> ProtectedSpans { get; }
    }

    internal static class CFamilyTokenizer
    {
        internal static TokenizationResult Tokenize(string source)
        {
            var tokens = new List<CodeToken>(Math.Max(16, source.Length / 6));
            var protectedSpans = new List<LexicalSpan>();
            for (int i = 0; i < source.Length;)
            {
                char current = source[i];
                if (char.IsWhiteSpace(current))
                {
                    i++;
                    continue;
                }

                if (current == '#' && IsLinePrefixWhitespace(source, i))
                {
                    int start = i++;
                    while (i < source.Length)
                    {
                        if (source[i] == '\n')
                        {
                            int previous = i - 1;
                            if (previous >= start && source[previous] == '\r') previous--;
                            if (previous < start || source[previous] != '\\')
                            {
                                i++;
                                break;
                            }
                        }
                        i++;
                    }
                    protectedSpans.Add(new LexicalSpan(LexicalSpanKind.Preprocessor, start, i));
                    continue;
                }

                if (current == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    int start = i;
                    i += 2;
                    while (i < source.Length && source[i] != '\n') i++;
                    protectedSpans.Add(new LexicalSpan(LexicalSpanKind.LineComment, start, i));
                    continue;
                }

                if (current == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    int start = i;
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                    i = Math.Min(source.Length, i + 2);
                    protectedSpans.Add(new LexicalSpan(LexicalSpanKind.BlockComment, start, i));
                    continue;
                }

                if (TryScanLiteral(source, i, out int literalEnd, out CodeTokenKind literalKind))
                {
                    tokens.Add(new CodeToken(source, i, literalEnd, literalKind));
                    protectedSpans.Add(new LexicalSpan(
                        literalKind == CodeTokenKind.CharacterLiteral ? LexicalSpanKind.CharacterLiteral : LexicalSpanKind.StringLiteral,
                        i,
                        literalEnd));
                    i = literalEnd;
                    continue;
                }

                if (char.IsLetter(current) || current == '_')
                {
                    int start = i++;
                    while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                    tokens.Add(new CodeToken(source, start, i, CodeTokenKind.Identifier));
                    continue;
                }

                if (char.IsDigit(current) || (current == '.' && i + 1 < source.Length && char.IsDigit(source[i + 1])))
                {
                    int start = i++;
                    while (i < source.Length)
                    {
                        char value = source[i];
                        if (char.IsLetterOrDigit(value) || value == '.' || value == '\'' || value == '_')
                        {
                            i++;
                            continue;
                        }
                        if ((value == '+' || value == '-') && i > start &&
                            (source[i - 1] == 'e' || source[i - 1] == 'E' || source[i - 1] == 'p' || source[i - 1] == 'P'))
                        {
                            i++;
                            continue;
                        }
                        break;
                    }
                    tokens.Add(new CodeToken(source, start, i, CodeTokenKind.Number));
                    continue;
                }

                int operatorLength = OperatorLength(source, i);
                if (operatorLength > 0)
                {
                    tokens.Add(new CodeToken(
                        source,
                        i,
                        i + operatorLength,
                        CodeTokenKind.Operator,
                        KnownTokenText(source, i, operatorLength)));
                    i += operatorLength;
                    continue;
                }

                tokens.Add(new CodeToken(source, i, i + 1, CodeTokenKind.Punctuation, KnownTokenText(source, i, 1)));
                i++;
            }
            return new TokenizationResult(tokens, protectedSpans);
        }

        private static bool TryScanLiteral(string source, int start, out int end, out CodeTokenKind kind)
        {
            end = start;
            kind = CodeTokenKind.StringLiteral;
            int quote = start;
            bool raw = false;

            if (source[start] == 'R' && start + 1 < source.Length && source[start + 1] == '"')
            {
                raw = true;
                quote = start + 1;
            }
            else if (source[start] == 'u')
            {
                int next = start + 1;
                if (next < source.Length && source[next] == '8') next++;
                if (next < source.Length && source[next] == 'R' && next + 1 < source.Length && source[next + 1] == '"')
                {
                    raw = true;
                    quote = next + 1;
                }
                else if (next < source.Length && (source[next] == '"' || source[next] == '\''))
                {
                    quote = next;
                }
                else
                {
                    return false;
                }
            }
            else if ((source[start] == 'U' || source[start] == 'L') && start + 1 < source.Length)
            {
                if (source[start + 1] == 'R' && start + 2 < source.Length && source[start + 2] == '"')
                {
                    raw = true;
                    quote = start + 2;
                }
                else if (source[start + 1] == '"' || source[start + 1] == '\'')
                {
                    quote = start + 1;
                }
                else
                {
                    return false;
                }
            }
            else if (source[start] != '"' && source[start] != '\'')
            {
                return false;
            }

            char quoteCharacter = source[quote];
            kind = quoteCharacter == '\'' ? CodeTokenKind.CharacterLiteral : CodeTokenKind.StringLiteral;
            if (raw)
            {
                int delimiterStart = quote + 1;
                int openParen = source.IndexOf('(', delimiterStart);
                if (openParen < 0 || openParen - delimiterStart > 16) return false;
                string delimiter = source.Substring(delimiterStart, openParen - delimiterStart);
                string terminator = ")" + delimiter + "\"";
                int close = source.IndexOf(terminator, openParen + 1, StringComparison.Ordinal);
                end = close < 0 ? source.Length : close + terminator.Length;
                return true;
            }

            bool escaped = false;
            int cursor = quote + 1;
            while (cursor < source.Length)
            {
                char value = source[cursor++];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (value == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (value == quoteCharacter) break;
            }
            end = cursor;
            return true;
        }

        private static int OperatorLength(string source, int start)
        {
            if (start + 3 <= source.Length)
            {
                string three = source.Substring(start, 3);
                if (three == "<<=" || three == ">>=" || three == "->*" || three == "..." || three == "<=>") return 3;
            }
            if (start + 2 <= source.Length)
            {
                string two = source.Substring(start, 2);
                if (two == "&&" || two == "||" || two == "->" || two == "::" || two == "==" || two == "!=" ||
                    two == "<=" || two == ">=" || two == "++" || two == "--" || two == "<<" || two == ">>" ||
                    two == "+=" || two == "-=" || two == "*=" || two == "/=" || two == "%=" || two == "&=" ||
                    two == "|=" || two == "^=" || two == ".*" || two == "##") return 2;
            }
            return "+-*/%=!<>&|^~?:.,;()[]{}".IndexOf(source[start], StringComparison.Ordinal) >= 0 ? 1 : 0;
        }

        private static string KnownTokenText(string source, int start, int length)
        {
            if (length != 1) return source.Substring(start, length);
            return source[start] switch
            {
                '(' => "(", ')' => ")", '[' => "[", ']' => "]", '{' => "{", '}' => "}",
                ';' => ";", ',' => ",", ':' => ":", '?' => "?", '*' => "*", '/' => "/",
                '+' => "+", '-' => "-", '=' => "=", '!' => "!", '<' => "<", '>' => ">",
                '&' => "&", '|' => "|", '^' => "^", '%' => "%", '.' => ".", '~' => "~",
                _ => source.Substring(start, 1),
            };
        }

        private static bool IsLinePrefixWhitespace(string source, int position)
        {
            for (int i = position - 1; i >= 0 && source[i] != '\n'; i--)
                if (!char.IsWhiteSpace(source[i])) return false;
            return true;
        }
    }
}
