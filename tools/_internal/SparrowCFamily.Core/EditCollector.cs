using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SparrowCFamily.Core
{
    internal readonly struct TextEdit
    {
        internal TextEdit(int start, int length, string replacement, string rule = "")
        {
            Start = start;
            Length = length;
            Replacement = replacement;
            Rule = rule;
        }

        internal int Start { get; }
        internal int Length { get; }
        internal string Replacement { get; }
        internal string Rule { get; }
    }

    /// <summary>규칙에서 생성한 편집을 검증하고 원본을 한 번 순회해 적용합니다.</summary>
    internal sealed class EditCollector
    {
        private readonly List<(TextEdit Edit, int Sequence)> _edits = new List<(TextEdit, int)>();
        private int _sequence;

        internal int Count => _edits.Count;

        internal void Add(int start, int length, string replacement, string rule = "") =>
            Add(new TextEdit(start, length, replacement, rule));

        internal void Add(TextEdit edit) => _edits.Add((edit, _sequence++));

        internal string Apply(string source)
        {
            if (_edits.Count == 0) return source;
            var ordered = _edits
                .OrderBy(item => item.Edit.Start)
                .ThenBy(item => item.Edit.Length == 0 ? 0 : 1)
                .ThenBy(item => item.Sequence)
                .ToArray();

            int cursor = 0;
            long targetLength = source.Length;
            foreach ((TextEdit edit, _) in ordered)
            {
                if (edit.Start < 0 || edit.Length < 0 || edit.Start + edit.Length > source.Length)
                    throw new InvalidOperationException("편집 범위가 소스 범위를 벗어났습니다: " + edit.Rule);
                if (edit.Start < cursor)
                    throw new InvalidOperationException("서로 겹치는 C/C++ 자동수정 편집이 감지되었습니다: " + edit.Rule);
                targetLength += edit.Replacement.Length - edit.Length;
            }

            int capacity = targetLength > int.MaxValue ? source.Length : Math.Max(0, (int)targetLength);
            var builder = new StringBuilder(capacity);
            foreach ((TextEdit edit, _) in ordered)
            {
                if (edit.Start > cursor)
                {
                    builder.Append(source, cursor, edit.Start - cursor);
                    cursor = edit.Start;
                }
                builder.Append(edit.Replacement);
                if (edit.Length > 0) cursor = edit.Start + edit.Length;
            }
            if (cursor < source.Length) builder.Append(source, cursor, source.Length - cursor);
            return builder.ToString();
        }
    }
}
