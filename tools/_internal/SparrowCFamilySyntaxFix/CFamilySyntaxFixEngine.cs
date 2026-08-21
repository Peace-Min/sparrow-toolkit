using System;
using System.Collections.Generic;
using System.Threading;
using SparrowCFamily.Core;

namespace SparrowCFamilySyntaxFix
{
    /// <summary>C 및 C++ 코드 규칙만 실행하는 전용 엔진입니다.</summary>
    public static class CFamilySyntaxFixEngine
    {
        public sealed class Options
        {
            public bool CompoundStatements { get; init; }
            public bool MissingElse { get; init; }
            public bool SwitchDefault { get; init; }
            public bool LogicalParentheses { get; init; }
            public bool UnsignedSuffix { get; init; }
            public bool SizeOfPointee { get; init; }
            public bool FixedWidthTypes { get; init; }
            public bool ConstantOnLeft { get; init; }
            public bool VariableInitialization { get; init; }
            public bool FileNoFollow { get; init; }
        }

        public static int Apply(IEnumerable<string> files, Options options, CancellationToken token, Action<string> log)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            return CFamilyTransformCore.Apply(files, new CFamilyTransformCore.Options
            {
                CompoundStatements = options.CompoundStatements,
                MissingElse = options.MissingElse,
                SwitchDefault = options.SwitchDefault,
                LogicalParentheses = options.LogicalParentheses,
                UnsignedSuffix = options.UnsignedSuffix,
                SizeOfPointee = options.SizeOfPointee,
                FixedWidthTypes = options.FixedWidthTypes,
                ConstantOnLeft = options.ConstantOnLeft,
                VariableInitialization = options.VariableInitialization,
                FileNoFollow = options.FileNoFollow,
            }, token, log);
        }
    }
}
