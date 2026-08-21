using System;
using System.Collections.Generic;
using System.Threading;
using SparrowCFamily.Core;

namespace SparrowCFamilyCommentFix
{
    /// <summary>C 및 C++ 주석·레이아웃 규칙만 실행하는 전용 엔진입니다.</summary>
    public static class CFamilyCommentFixEngine
    {
        public sealed class Options
        {
            public bool TrailingComment { get; init; }
            public bool CommentSpace { get; init; }
            public bool CommentPeriod { get; init; }
            public bool CommentCapitalize { get; init; }
            public bool SingleLineDelimiter { get; init; }
            public string SingleLineDelimiterText { get; init; } = "///<";
            public bool MultiLineDelimiter { get; init; }
            public string MultiLineDelimiterText { get; init; } = "/**";
            public bool ParagraphDelimiter { get; init; }
            public string ParagraphDelimiterText { get; init; } = "//!";
        }

        public static int Apply(IEnumerable<string> files, Options options, CancellationToken token, Action<string> log)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.SingleLineDelimiter) ValidateLineDelimiter(options.SingleLineDelimiterText, nameof(options.SingleLineDelimiterText));
            if (options.MultiLineDelimiter) ValidateBlockDelimiter(options.MultiLineDelimiterText, nameof(options.MultiLineDelimiterText));
            if (options.ParagraphDelimiter) ValidateLineDelimiter(options.ParagraphDelimiterText, nameof(options.ParagraphDelimiterText));
            return CFamilyTransformCore.Apply(files, new CFamilyTransformCore.Options
            {
                TrailingComment = options.TrailingComment,
                CommentSpace = options.CommentSpace,
                CommentPeriod = options.CommentPeriod,
                CommentCapitalize = options.CommentCapitalize,
                SingleLineDelimiter = options.SingleLineDelimiter,
                SingleLineDelimiterText = options.SingleLineDelimiterText,
                MultiLineDelimiter = options.MultiLineDelimiter,
                MultiLineDelimiterText = options.MultiLineDelimiterText,
                ParagraphDelimiter = options.ParagraphDelimiter,
                ParagraphDelimiterText = options.ParagraphDelimiterText,
            }, token, log);
        }

        private static void ValidateLineDelimiter(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("//", StringComparison.Ordinal) ||
                value.Length > 16 || ContainsWhitespace(value))
                throw new ArgumentException("한 줄 주석 구분자는 공백 없이 //로 시작해야 합니다.", parameterName);
        }

        private static void ValidateBlockDelimiter(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("/*", StringComparison.Ordinal) ||
                value.Contains("*/", StringComparison.Ordinal) || value.Length > 16 || ContainsWhitespace(value))
                throw new ArgumentException("블록 주석 구분자는 공백 없이 /*로 시작해야 하며 */를 포함할 수 없습니다.", parameterName);
        }

        private static bool ContainsWhitespace(string value)
        {
            foreach (char character in value)
                if (char.IsWhiteSpace(character)) return true;
            return false;
        }
    }
}
