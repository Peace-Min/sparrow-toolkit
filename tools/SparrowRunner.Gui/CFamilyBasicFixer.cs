using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SparrowRunner.Gui
{
    /// <summary>
    /// C/C++ 헤더용 보수적 기본 규칙. C#의 var 규칙은 C에서 문법적으로 성립하지 않으므로 적용하지 않고,
    /// 코드 규칙에서는 의미를 바꾸지 않는 조건식 외곽 괄호만, 주석 규칙에서는 기본 4종만 처리한다.
    /// </summary>
    internal static class CFamilyBasicFixer
    {
        internal sealed class Options
        {
            public bool CompoundStatements { get; init; }
            public bool FinalElse { get; init; }
            public bool MissingElse { get; init; }
            public bool SwitchDefault { get; init; }
            public bool LogicalParentheses { get; init; }
            public bool UnsignedSuffix { get; init; }
            public bool IgnoredReturn { get; init; }
            public bool SizeOfPointee { get; init; }
            public bool FixedWidthTypes { get; init; }
            public bool TrailingComment { get; init; }
            public bool CommentSpace { get; init; }
            public bool CommentPeriod { get; init; }
            public bool CommentCapitalize { get; init; }
        }

        public static int Apply(IEnumerable<string> files, Options options, CancellationToken token, Action<string> log)
        {
            int changed = 0;
            foreach (string file in files)
            {
                token.ThrowIfCancellationRequested();
                string original = ReadText(file, out Encoding encoding, out bool bom);
                string codeRewritten = original;
                if (options.CompoundStatements) codeRewritten = AddCompoundStatements(codeRewritten);
                if (options.FinalElse) codeRewritten = AddMissingElse(codeRewritten, elseIfOnly: true);
                if (options.MissingElse) codeRewritten = AddMissingElse(codeRewritten, elseIfOnly: false);
                if (options.SwitchDefault) codeRewritten = AddMissingSwitchDefault(codeRewritten);
                if (options.UnsignedSuffix) codeRewritten = AddUnsignedIntegerSuffixes(codeRewritten);
                if (options.IgnoredReturn) codeRewritten = MarkIgnoredReturnValues(codeRewritten);
                if (options.SizeOfPointee) codeRewritten = FixSizeOfPointers(codeRewritten);
                if (options.FixedWidthTypes) codeRewritten = UseFixedWidthIntegerTypes(codeRewritten);
                string newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                bool finalNewline = codeRewritten.EndsWith("\n", StringComparison.Ordinal);
                string[] lines = codeRewritten.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                if (finalNewline && lines.Length > 0) lines = lines.Take(lines.Length - 1).ToArray();

                var output = new List<string>(lines.Length + 8);
                foreach (string sourceLine in lines)
                {
                    string line = options.LogicalParentheses ? AddOuterConditionParentheses(sourceLine) : sourceLine;
                    RewriteComment(line, options, output);
                }

                string rewritten = string.Join(newline, output) + (finalNewline ? newline : "");
                if (string.Equals(original, rewritten, StringComparison.Ordinal)) continue;
                WriteText(file, rewritten, encoding, bom);
                changed++;
                log("C/C++ 선택 규칙 적용: " + file);
            }
            return changed;
        }

        private readonly struct CodeToken
        {
            public CodeToken(string text, int start, int end)
            {
                Text = text;
                Start = start;
                End = end;
            }

            public string Text { get; }
            public int Start { get; }
            public int End { get; }
            public bool IsIdentifier => Text.Length > 0 && (char.IsLetter(Text[0]) || Text[0] == '_');
        }

        private readonly struct TextEdit
        {
            public TextEdit(int start, int length, string replacement)
            {
                Start = start;
                Length = length;
                Replacement = replacement;
            }

            public int Start { get; }
            public int Length { get; }
            public string Replacement { get; }
        }

        private static string AddCompoundStatements(string source)
        {
            for (int pass = 0; pass < 2048; pass++)
            {
                List<CodeToken> tokens = Tokenize(source);
                bool changed = false;
                for (int i = 0; i < tokens.Count; i++)
                {
                    string keyword = tokens[i].Text;
                    int bodyStart;
                    if (keyword == "if" || keyword == "for" || keyword == "while")
                    {
                        int open = i + 1 < tokens.Count && tokens[i + 1].Text == "(" ? i + 1 : -1;
                        int close = open >= 0 ? FindMatchingToken(tokens, open, "(", ")") : -1;
                        if (close < 0 || close + 1 >= tokens.Count || tokens[close + 1].Text == ";") continue;
                        bodyStart = close + 1;
                    }
                    else if (keyword == "else")
                    {
                        if (i + 1 >= tokens.Count || tokens[i + 1].Text == "if") continue;
                        bodyStart = i + 1;
                    }
                    else if (keyword == "do")
                    {
                        if (i + 1 >= tokens.Count) continue;
                        bodyStart = i + 1;
                    }
                    else
                    {
                        continue;
                    }

                    if (tokens[bodyStart].Text == "{") continue;
                    int bodyEnd = FindStatementEnd(tokens, bodyStart);
                    if (bodyEnd < bodyStart) continue;
                    source = WrapStatementBody(source, tokens[i], tokens[bodyStart], tokens[bodyEnd]);
                    changed = true;
                    break;
                }
                if (!changed) return source;
            }
            return source;
        }

        private static string AddMissingElse(string source, bool elseIfOnly)
        {
            for (int pass = 0; pass < 2048; pass++)
            {
                List<CodeToken> tokens = Tokenize(source);
                int missingIf = -1;
                int bodyEnd = -1;
                for (int i = tokens.Count - 1; i >= 0; i--)
                {
                    if (tokens[i].Text != "if" || i + 1 >= tokens.Count || tokens[i + 1].Text != "(") continue;
                    bool isElseIf = i > 0 && tokens[i - 1].Text == "else";
                    if (isElseIf != elseIfOnly) continue;
                    int close = FindMatchingToken(tokens, i + 1, "(", ")");
                    if (close < 0 || close + 1 >= tokens.Count) continue;
                    int candidateEnd = FindStatementEnd(tokens, close + 1);
                    if (candidateEnd < 0) continue;
                    if (candidateEnd + 1 < tokens.Count && tokens[candidateEnd + 1].Text == "else") continue;
                    missingIf = i;
                    bodyEnd = candidateEnd;
                    break;
                }
                if (missingIf < 0) return source;

                string newline = DetectNewline(source);
                string indent = GetLineIndent(source, tokens[missingIf].Start);
                int insertion = StatementInsertionPoint(source, tokens[bodyEnd].End);
                string addition = newline + indent + "else" + newline + indent + "{" + newline
                    + indent + "    asm(\"nop\");" + newline + indent + "}";
                source = ApplyEdits(source, new[] { new TextEdit(insertion, 0, addition) });
            }
            return source;
        }

        private static string AddMissingSwitchDefault(string source)
        {
            List<CodeToken> tokens = Tokenize(source);
            var edits = new List<TextEdit>();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Text != "switch" || i + 1 >= tokens.Count || tokens[i + 1].Text != "(") continue;
                int closeParen = FindMatchingToken(tokens, i + 1, "(", ")");
                if (closeParen < 0 || closeParen + 1 >= tokens.Count || tokens[closeParen + 1].Text != "{") continue;
                int openBrace = closeParen + 1;
                int closeBrace = FindMatchingToken(tokens, openBrace, "{", "}");
                if (closeBrace < 0) continue;

                int depth = 0;
                bool hasDefault = false;
                for (int j = openBrace + 1; j < closeBrace; j++)
                {
                    if (tokens[j].Text == "{") depth++;
                    else if (tokens[j].Text == "}") depth--;
                    else if (depth == 0 && tokens[j].Text == "default" && j + 1 < closeBrace && tokens[j + 1].Text == ":")
                    {
                        hasDefault = true;
                        break;
                    }
                }
                if (hasDefault) continue;

                int lineStart = FindLineStart(source, tokens[closeBrace].Start);
                string indent = GetLineIndent(source, tokens[closeBrace].Start);
                string newline = DetectNewline(source);
                string addition = indent + "default:" + newline + indent + "    /* Unexpected state. */" + newline
                    + indent + "    asm(\"nop\");" + newline + indent + "    break;" + newline;
                edits.Add(new TextEdit(lineStart, 0, addition));
            }
            return ApplyEdits(source, edits);
        }

        private static string AddUnsignedIntegerSuffixes(string source)
        {
            List<CodeToken> tokens = Tokenize(source);
            var unsignedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < tokens.Count; i++)
            {
                if (!IsUnsignedTypeToken(tokens[i].Text)) continue;
                for (int j = i + 1; j < tokens.Count && j <= i + 8; j++)
                {
                    string text = tokens[j].Text;
                    if (text == ";" || text == "=" || text == ")") break;
                    if (tokens[j].IsIdentifier && !IsTypeWord(text))
                    {
                        unsignedNames.Add(text);
                        break;
                    }
                }
            }

            var edits = new List<TextEdit>();
            for (int i = 0; i < tokens.Count; i++)
            {
                Match match = Regex.Match(tokens[i].Text, @"^(0[xX][0-9A-Fa-f]+|0[bB][01]+|[0-9]+)([uUlL]*)$");
                if (!match.Success || match.Groups[2].Value.IndexOf('u', StringComparison.OrdinalIgnoreCase) >= 0) continue;
                int start = FindStatementTokenStart(tokens, i);
                int end = FindStatementTokenEnd(tokens, i);
                bool unsignedContext = false;
                for (int j = start; j <= end; j++)
                {
                    if (IsUnsignedTypeToken(tokens[j].Text) || unsignedNames.Contains(tokens[j].Text))
                    {
                        unsignedContext = true;
                        break;
                    }
                }
                if (!unsignedContext) continue;
                string replacement = match.Groups[1].Value + "U" + match.Groups[2].Value;
                edits.Add(new TextEdit(tokens[i].Start, tokens[i].End - tokens[i].Start, replacement));
            }
            return ApplyEdits(source, edits);
        }

        private static string MarkIgnoredReturnValues(string source)
        {
            List<CodeToken> tokens = Tokenize(source);
            var edits = new List<TextEdit>();
            var excluded = new HashSet<string>(StringComparer.Ordinal)
            {
                "if", "for", "while", "switch", "return", "sizeof", "alignof", "static_assert",
                "asm", "__asm", "__asm__"
            };
            for (int i = 0; i < tokens.Count; i++)
            {
                if (!tokens[i].IsIdentifier || excluded.Contains(tokens[i].Text)) continue;
                if (i > 0 && tokens[i - 1].Text != ";" && tokens[i - 1].Text != "{" &&
                    tokens[i - 1].Text != "}" && tokens[i - 1].Text != ":") continue;

                int open = i + 1;
                while (open + 1 < tokens.Count && (tokens[open].Text == "." || tokens[open].Text == "->" || tokens[open].Text == "::") && tokens[open + 1].IsIdentifier)
                    open += 2;
                if (open >= tokens.Count || tokens[open].Text != "(") continue;
                int close = FindMatchingToken(tokens, open, "(", ")");
                if (close < 0 || close + 1 >= tokens.Count || tokens[close + 1].Text != ";") continue;
                edits.Add(new TextEdit(tokens[i].Start, 0, "(void)"));
            }
            return ApplyEdits(source, edits);
        }

        private static string FixSizeOfPointers(string source)
        {
            List<CodeToken> tokens = Tokenize(source);
            var pointers = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < tokens.Count; i++)
            {
                if (tokens[i].Text == "*" && tokens[i + 1].IsIdentifier && LooksLikePointerDeclaration(tokens, i))
                    pointers.Add(tokens[i + 1].Text);
                if (tokens[i].IsIdentifier && i + 3 < tokens.Count && tokens[i + 1].Text == "=" &&
                    (tokens[i + 2].Text == "malloc" || tokens[i + 2].Text == "calloc" || tokens[i + 2].Text == "realloc") &&
                    tokens[i + 3].Text == "(")
                    pointers.Add(tokens[i].Text);
            }

            var edits = new List<TextEdit>();
            for (int i = 0; i + 3 < tokens.Count; i++)
            {
                if (tokens[i].Text != "sizeof" || tokens[i + 1].Text != "(" || !tokens[i + 2].IsIdentifier || tokens[i + 3].Text != ")") continue;
                if (pointers.Contains(tokens[i + 2].Text)) edits.Add(new TextEdit(tokens[i + 2].Start, 0, "*"));
            }
            return ApplyEdits(source, edits);
        }

        private static string UseFixedWidthIntegerTypes(string source)
        {
            List<CodeToken> tokens = Tokenize(source);
            var edits = new List<TextEdit>();
            for (int i = 0; i < tokens.Count; i++)
            {
                int end = i;
                string? replacement = null;
                string text = tokens[i].Text;
                if (text == "unsigned" || text == "signed")
                {
                    bool unsigned = text == "unsigned";
                    if (i + 1 < tokens.Count && tokens[i + 1].Text == "char") { end = i + 1; replacement = unsigned ? "uint8_t" : "int8_t"; }
                    else if (i + 1 < tokens.Count && tokens[i + 1].Text == "short")
                    {
                        end = i + 1;
                        if (end + 1 < tokens.Count && tokens[end + 1].Text == "int") end++;
                        replacement = unsigned ? "uint16_t" : "int16_t";
                    }
                    else if (i + 2 < tokens.Count && tokens[i + 1].Text == "long" && tokens[i + 2].Text == "long")
                    {
                        end = i + 2;
                        if (end + 1 < tokens.Count && tokens[end + 1].Text == "int") end++;
                        replacement = unsigned ? "uint64_t" : "int64_t";
                    }
                    else if (i + 1 < tokens.Count && tokens[i + 1].Text == "int") { end = i + 1; replacement = unsigned ? "uint32_t" : "int32_t"; }
                    else { replacement = unsigned ? "uint32_t" : "int32_t"; }
                }
                else if (text == "short")
                {
                    if (i > 0 && (tokens[i - 1].Text == "unsigned" || tokens[i - 1].Text == "signed")) continue;
                    end = i + 1 < tokens.Count && tokens[i + 1].Text == "int" ? i + 1 : i;
                    replacement = "int16_t";
                }
                else if (text == "long" && i + 1 < tokens.Count && tokens[i + 1].Text == "long")
                {
                    if (i > 0 && (tokens[i - 1].Text == "unsigned" || tokens[i - 1].Text == "signed")) continue;
                    end = i + 1;
                    if (end + 1 < tokens.Count && tokens[end + 1].Text == "int") end++;
                    replacement = "int64_t";
                }
                else if (text == "int")
                {
                    if (i > 0 && (tokens[i - 1].Text == "unsigned" || tokens[i - 1].Text == "signed" || tokens[i - 1].Text == "short" || tokens[i - 1].Text == "long")) continue;
                    if (i + 2 < tokens.Count && tokens[i + 1].Text == "main" && tokens[i + 2].Text == "(") continue;
                    replacement = "int32_t";
                }

                if (replacement == null) continue;
                edits.Add(new TextEdit(tokens[i].Start, tokens[end].End - tokens[i].Start, replacement));
                i = end;
            }

            if (edits.Count == 0) return source;
            string rewritten = ApplyEdits(source, edits);
            if (Regex.IsMatch(rewritten, "(?m)^\\s*#\\s*include\\s*[<\"]stdint\\.h[>\"]")) return rewritten;
            return InsertStdIntInclude(rewritten);
        }

        private static string WrapStatementBody(string source, CodeToken control, CodeToken bodyStart, CodeToken bodyEnd)
        {
            string newline = DetectNewline(source);
            int controlLine = FindLineStart(source, control.Start);
            int bodyLine = FindLineStart(source, bodyStart.Start);
            string indent = GetLineIndent(source, control.Start);
            if (controlLine != bodyLine)
            {
                int insertion = StatementInsertionPoint(source, bodyEnd.End);
                return ApplyEdits(source, new[]
                {
                    new TextEdit(bodyLine, 0, indent + "{" + newline),
                    new TextEdit(insertion, 0, newline + indent + "}")
                });
            }
            return ApplyEdits(source, new[]
            {
                new TextEdit(bodyStart.Start, 0, "{ "),
                new TextEdit(bodyEnd.End, 0, " }")
            });
        }

        private static int FindStatementEnd(IReadOnlyList<CodeToken> tokens, int start)
        {
            if (start < 0 || start >= tokens.Count) return -1;
            if (tokens[start].Text == "{") return FindMatchingToken(tokens, start, "{", "}");
            if (tokens[start].Text == "if" && start + 1 < tokens.Count && tokens[start + 1].Text == "(")
            {
                int close = FindMatchingToken(tokens, start + 1, "(", ")");
                if (close < 0 || close + 1 >= tokens.Count) return -1;
                int thenEnd = FindStatementEnd(tokens, close + 1);
                if (thenEnd >= 0 && thenEnd + 1 < tokens.Count && tokens[thenEnd + 1].Text == "else")
                    return FindStatementEnd(tokens, thenEnd + 2);
                return thenEnd;
            }
            if ((tokens[start].Text == "for" || tokens[start].Text == "while" || tokens[start].Text == "switch") &&
                start + 1 < tokens.Count && tokens[start + 1].Text == "(")
            {
                int close = FindMatchingToken(tokens, start + 1, "(", ")");
                return close >= 0 && close + 1 < tokens.Count ? FindStatementEnd(tokens, close + 1) : -1;
            }
            if (tokens[start].Text == "do" && start + 1 < tokens.Count)
            {
                int bodyEnd = FindStatementEnd(tokens, start + 1);
                if (bodyEnd < 0) return -1;
                int next = bodyEnd + 1;
                if (next < tokens.Count && tokens[next].Text == "while" && next + 1 < tokens.Count && tokens[next + 1].Text == "(")
                {
                    int close = FindMatchingToken(tokens, next + 1, "(", ")");
                    if (close >= 0 && close + 1 < tokens.Count && tokens[close + 1].Text == ";") return close + 1;
                }
                return bodyEnd;
            }

            int parens = 0;
            int brackets = 0;
            for (int i = start; i < tokens.Count; i++)
            {
                if (tokens[i].Text == "(") parens++;
                else if (tokens[i].Text == ")") parens--;
                else if (tokens[i].Text == "[") brackets++;
                else if (tokens[i].Text == "]") brackets--;
                else if (tokens[i].Text == "{" && parens == 0 && brackets == 0) return FindMatchingToken(tokens, i, "{", "}");
                else if (tokens[i].Text == ";" && parens == 0 && brackets == 0) return i;
            }
            return -1;
        }

        private static int FindMatchingToken(IReadOnlyList<CodeToken> tokens, int open, string opener, string closer)
        {
            int depth = 0;
            for (int i = open; i < tokens.Count; i++)
            {
                if (tokens[i].Text == opener) depth++;
                else if (tokens[i].Text == closer && --depth == 0) return i;
            }
            return -1;
        }

        private static List<CodeToken> Tokenize(string source)
        {
            var tokens = new List<CodeToken>();
            for (int i = 0; i < source.Length;)
            {
                char c = source[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '#' && IsLinePrefixWhitespace(source, i))
                {
                    i++;
                    while (i < source.Length)
                    {
                        if (source[i] == '\n')
                        {
                            int previous = i - 1;
                            if (previous >= 0 && source[previous] == '\r') previous--;
                            if (previous < 0 || source[previous] != '\\') { i++; break; }
                        }
                        i++;
                    }
                    continue;
                }
                if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    i += 2;
                    while (i < source.Length && source[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                    i = Math.Min(source.Length, i + 2);
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    int start = i++;
                    char quote = c;
                    bool escaped = false;
                    while (i < source.Length)
                    {
                        char value = source[i++];
                        if (escaped) { escaped = false; continue; }
                        if (value == '\\') { escaped = true; continue; }
                        if (value == quote) break;
                    }
                    tokens.Add(new CodeToken(source.Substring(start, i - start), start, i));
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i++;
                    while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                    tokens.Add(new CodeToken(source.Substring(start, i - start), start, i));
                    continue;
                }
                if (char.IsDigit(c))
                {
                    int start = i++;
                    while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '.' || source[i] == '\'')) i++;
                    tokens.Add(new CodeToken(source.Substring(start, i - start), start, i));
                    continue;
                }
                int length = i + 1 < source.Length && IsTwoCharacterOperator(source.Substring(i, 2)) ? 2 : 1;
                tokens.Add(new CodeToken(source.Substring(i, length), i, i + length));
                i += length;
            }
            return tokens;
        }

        private static bool IsTwoCharacterOperator(string text)
            => text == "&&" || text == "||" || text == "->" || text == "::" || text == "==" || text == "!=" ||
               text == "<=" || text == ">=" || text == "++" || text == "--" || text == "<<" || text == ">>" ||
               text == "+=" || text == "-=" || text == "*=" || text == "/=" || text == "%=" || text == "&=" ||
               text == "|=" || text == "^=";

        private static bool IsLinePrefixWhitespace(string source, int position)
        {
            for (int i = position - 1; i >= 0 && source[i] != '\n'; i--)
                if (!char.IsWhiteSpace(source[i])) return false;
            return true;
        }

        private static int FindLineStart(string source, int position)
        {
            int line = source.LastIndexOf('\n', Math.Max(0, position - 1));
            return line < 0 ? 0 : line + 1;
        }

        private static string GetLineIndent(string source, int position)
        {
            int line = FindLineStart(source, position);
            int end = line;
            while (end < source.Length && (source[end] == ' ' || source[end] == '\t')) end++;
            return source.Substring(line, end - line);
        }

        private static int StatementInsertionPoint(string source, int tokenEnd)
        {
            int lineEnd = source.IndexOf('\n', tokenEnd);
            if (lineEnd < 0) lineEnd = source.Length;
            string suffix = source.Substring(tokenEnd, lineEnd - tokenEnd);
            if (suffix.Contains("//", StringComparison.Ordinal) || suffix.Contains("/*", StringComparison.Ordinal)) return lineEnd;
            return tokenEnd;
        }

        private static string DetectNewline(string source)
            => source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        private static string ApplyEdits(string source, IEnumerable<TextEdit> edits)
        {
            var builder = new StringBuilder(source);
            foreach (TextEdit edit in edits.OrderByDescending(edit => edit.Start).ThenByDescending(edit => edit.Length))
            {
                builder.Remove(edit.Start, edit.Length);
                builder.Insert(edit.Start, edit.Replacement);
            }
            return builder.ToString();
        }

        private static int FindStatementTokenStart(IReadOnlyList<CodeToken> tokens, int index)
        {
            int start = index;
            while (start > 0 && tokens[start - 1].Text != ";" && tokens[start - 1].Text != "{" && tokens[start - 1].Text != "}") start--;
            return start;
        }

        private static int FindStatementTokenEnd(IReadOnlyList<CodeToken> tokens, int index)
        {
            int end = index;
            while (end + 1 < tokens.Count && tokens[end + 1].Text != ";" && tokens[end + 1].Text != "{" && tokens[end + 1].Text != "}") end++;
            return end;
        }

        private static bool IsUnsignedTypeToken(string text)
            => text == "unsigned" || text == "size_t" || Regex.IsMatch(text, @"^uint(8|16|32|64)_t$");

        private static bool IsTypeWord(string text)
            => text == "const" || text == "volatile" || text == "static" || text == "extern" || text == "register" ||
               text == "signed" || text == "unsigned" || text == "char" || text == "short" || text == "int" ||
               text == "long" || text == "float" || text == "double" || text == "void" || text == "struct" ||
               text == "union" || text == "enum" || text == "size_t" || Regex.IsMatch(text, @"^[u]?int(8|16|32|64)_t$");

        private static bool LooksLikePointerDeclaration(IReadOnlyList<CodeToken> tokens, int star)
        {
            if (star <= 0) return false;
            for (int i = star - 1, checkedCount = 0; i >= 0 && checkedCount < 5; i--, checkedCount++)
            {
                string text = tokens[i].Text;
                if (text == ";" || text == "{" || text == "}" || text == "=") return false;
                if (IsTypeWord(text) || text.EndsWith("_t", StringComparison.Ordinal) || text == "struct" || text == "union") return true;
                if (tokens[i].IsIdentifier && text.Length > 0 && char.IsUpper(text[0])) return true;
            }
            return false;
        }

        private static string InsertStdIntInclude(string source)
        {
            string newline = DetectNewline(source);
            MatchCollection includes = Regex.Matches(source, @"(?m)^\s*#\s*include[^\r\n]*(?:\r?\n|$)");
            if (includes.Count > 0)
            {
                Match last = includes[includes.Count - 1];
                return source.Insert(last.Index + last.Length, "#include <stdint.h>" + newline);
            }
            Match pragma = Regex.Match(source, @"(?m)^\s*#\s*pragma\s+once[^\r\n]*(?:\r?\n|$)");
            if (pragma.Success) return source.Insert(pragma.Index + pragma.Length, "#include <stdint.h>" + newline);
            return "#include <stdint.h>" + newline + source;
        }

        private static void RewriteComment(string line, Options options, List<string> output)
        {
            int lineCommentAt = FindLineComment(line);
            int blockCommentAt = FindBlockComment(line);
            if (blockCommentAt >= 0 && (lineCommentAt < 0 || blockCommentAt < lineCommentAt))
            {
                RewriteBlockComment(line, blockCommentAt, options, output);
                return;
            }
            if (lineCommentAt < 0)
            {
                output.Add(line);
                return;
            }

            string code = line.Substring(0, lineCommentAt);
            string comment = line.Substring(lineCommentAt + 2);
            if (options.CommentSpace) comment = comment.TrimStart();
            if (options.CommentCapitalize) comment = CapitalizeFirstAscii(comment);
            if (options.CommentPeriod) comment = AddPeriod(comment);
            string marker = options.CommentSpace ? "// " : "//";
            string rebuiltComment = marker + comment;

            if (options.TrailingComment && code.Trim().Length > 0)
            {
                string indent = code.Substring(0, code.Length - code.TrimStart().Length);
                output.Add(indent + rebuiltComment);
                output.Add(code.TrimEnd());
            }
            else
            {
                output.Add(code + rebuiltComment);
            }
        }

        private static void RewriteBlockComment(string line, int commentAt, Options options, List<string> output)
        {
            int close = line.IndexOf("*/", commentAt + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                // 여러 줄 블록은 구조를 깨지 않도록 그대로 둔다. 단일 줄 C 블록 주석은 아래에서 기본 규칙을 적용한다.
                output.Add(line);
                return;
            }

            bool doxygen = commentAt + 2 < line.Length && (line[commentAt + 2] == '*' || line[commentAt + 2] == '!');
            int bodyStart = commentAt + 2 + (doxygen ? 1 : 0);
            string code = line.Substring(0, commentAt);
            string suffix = line.Substring(close + 2);
            string comment = line.Substring(bodyStart, close - bodyStart);
            if (options.CommentSpace) comment = comment.Trim();
            if (options.CommentCapitalize) comment = CapitalizeFirstAscii(comment);
            if (options.CommentPeriod) comment = AddPeriod(comment);

            string opener = doxygen ? line.Substring(commentAt, bodyStart - commentAt) : "/*";
            string rebuilt = options.CommentSpace
                ? opener + " " + comment.Trim() + " */"
                : opener + comment + "*/";

            if (options.TrailingComment && code.Trim().Length > 0 && suffix.Trim().Length == 0)
            {
                string indent = code.Substring(0, code.Length - code.TrimStart().Length);
                output.Add(indent + rebuilt);
                output.Add(code.TrimEnd());
            }
            else
            {
                output.Add(code + rebuilt + suffix);
            }
        }

        private static int FindLineComment(string line)
        {
            bool inString = false;
            bool inChar = false;
            bool escaped = false;
            for (int i = 0; i + 1 < line.Length; i++)
            {
                char c = line[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\' && (inString || inChar)) { escaped = true; continue; }
                if (c == '"' && !inChar) { inString = !inString; continue; }
                if (c == '\'' && !inString) { inChar = !inChar; continue; }
                if (!inString && !inChar && c == '/' && line[i + 1] == '/') return i;
            }
            return -1;
        }

        private static int FindBlockComment(string line)
        {
            bool inString = false;
            bool inChar = false;
            bool escaped = false;
            for (int i = 0; i + 1 < line.Length; i++)
            {
                char c = line[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\' && (inString || inChar)) { escaped = true; continue; }
                if (c == '"' && !inChar) { inString = !inString; continue; }
                if (c == '\'' && !inString) { inChar = !inChar; continue; }
                if (!inString && !inChar && c == '/' && line[i + 1] == '*') return i;
            }
            return -1;
        }

        private static string AddOuterConditionParentheses(string line)
        {
            int keyword = IndexOfKeyword(line, "if");
            if (keyword < 0) keyword = IndexOfKeyword(line, "while");
            if (keyword < 0) return line;
            int open = line.IndexOf('(', keyword);
            if (open < 0) return line;
            int close = FindMatchingParen(line, open);
            if (close < 0) return line;
            string condition = line.Substring(open + 1, close - open - 1).Trim();
            if (!(condition.Contains("&&", StringComparison.Ordinal) || condition.Contains("||", StringComparison.Ordinal))) return line;
            string clarified = ClarifyLogicalExpression(condition);
            if (string.Equals(condition, clarified, StringComparison.Ordinal)) return line;
            string originalCondition = line.Substring(open + 1, close - open - 1);
            string leading = originalCondition.Substring(0, originalCondition.Length - originalCondition.TrimStart().Length);
            string trailing = originalCondition.Substring(originalCondition.TrimEnd().Length);
            return line.Substring(0, open + 1) + leading + clarified + trailing + line.Substring(close);
        }

        private static string ClarifyLogicalExpression(string condition)
        {
            List<int> orPositions = FindTopLevelOperators(condition, "||");
            List<int> andPositions = FindTopLevelOperators(condition, "&&");
            if (orPositions.Count == 0)
            {
                if (andPositions.Count > 0 && condition.Contains("||", StringComparison.Ordinal)) return condition;
                return HasSingleOuterParentheses(condition) ? condition : "(" + condition + ")";
            }

            var pieces = new List<string>();
            int start = 0;
            foreach (int position in orPositions.Concat(new[] { condition.Length }))
            {
                string piece = condition.Substring(start, position - start).Trim();
                if (FindTopLevelOperators(piece, "&&").Count > 0 && !HasSingleOuterParentheses(piece)) piece = "(" + piece + ")";
                pieces.Add(piece);
                start = position + 2;
            }
            return string.Join(" || ", pieces);
        }

        private static List<int> FindTopLevelOperators(string expression, string op)
        {
            var positions = new List<int>();
            int depth = 0;
            bool inString = false;
            bool inChar = false;
            bool escaped = false;
            for (int i = 0; i + 1 < expression.Length; i++)
            {
                char c = expression[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\' && (inString || inChar)) { escaped = true; continue; }
                if (c == '"' && !inChar) { inString = !inString; continue; }
                if (c == '\'' && !inString) { inChar = !inChar; continue; }
                if (inString || inChar) continue;
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (depth == 0 && expression.AsSpan(i).StartsWith(op, StringComparison.Ordinal))
                {
                    positions.Add(i);
                    i++;
                }
            }
            return positions;
        }

        private static bool HasSingleOuterParentheses(string expression)
        {
            if (expression.Length < 2 || expression[0] != '(' || expression[expression.Length - 1] != ')') return false;
            int depth = 0;
            for (int i = 0; i < expression.Length; i++)
            {
                if (expression[i] == '(') depth++;
                else if (expression[i] == ')' && --depth == 0) return i == expression.Length - 1;
            }
            return false;
        }

        private static int IndexOfKeyword(string line, string keyword)
        {
            int index = line.IndexOf(keyword, StringComparison.Ordinal);
            while (index >= 0)
            {
                bool left = index == 0 || !char.IsLetterOrDigit(line[index - 1]) && line[index - 1] != '_';
                int end = index + keyword.Length;
                bool right = end >= line.Length || !char.IsLetterOrDigit(line[end]) && line[end] != '_';
                if (left && right) return index;
                index = line.IndexOf(keyword, end, StringComparison.Ordinal);
            }
            return -1;
        }

        private static int FindMatchingParen(string line, int open)
        {
            int depth = 0;
            for (int i = open; i < line.Length; i++)
            {
                if (line[i] == '(') depth++;
                else if (line[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }

        private static string CapitalizeFirstAscii(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] >= 'a' && text[i] <= 'z') return text.Substring(0, i) + char.ToUpperInvariant(text[i]) + text.Substring(i + 1);
                if (char.IsLetterOrDigit(text[i])) break;
            }
            return text;
        }

        private static string AddPeriod(string text)
        {
            string trimmed = text.TrimEnd();
            if (trimmed.Length == 0 || ".!?;:)}]".Contains(trimmed[trimmed.Length - 1])) return text;
            return trimmed + "." + text.Substring(trimmed.Length);
        }

        private static string ReadText(string path, out Encoding encoding, out bool bom)
        {
            byte[] bytes = File.ReadAllBytes(path);
            bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            int offset = bom ? 3 : 0;
            try
            {
                encoding = new UTF8Encoding(false, true);
                return encoding.GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                encoding = Encoding.GetEncoding(949);
                return encoding.GetString(bytes);
            }
        }

        private static void WriteText(string path, string text, Encoding encoding, bool bom)
        {
            byte[] body = encoding.GetBytes(text);
            byte[] preamble = bom ? new UTF8Encoding(true).GetPreamble() : Array.Empty<byte>();
            string temp = path + ".sparrow-tmp-" + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (preamble.Length > 0) stream.Write(preamble, 0, preamble.Length);
                stream.Write(body, 0, body.Length);
            }
            File.Move(temp, path, true);
        }
    }
}
