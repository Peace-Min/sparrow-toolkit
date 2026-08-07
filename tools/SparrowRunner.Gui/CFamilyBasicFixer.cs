using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
            public bool LogicalParentheses { get; init; }
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
                string newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                bool finalNewline = original.EndsWith("\n", StringComparison.Ordinal);
                string[] lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
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
                log("C/C++ 기본 규칙 적용: " + file);
            }
            return changed;
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
            if (condition.StartsWith("(", StringComparison.Ordinal) && condition.EndsWith(")", StringComparison.Ordinal)) return line;
            return line.Substring(0, open + 1) + "(" + line.Substring(open + 1, close - open - 1) + ")" + line.Substring(close);
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
