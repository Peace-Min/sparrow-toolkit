using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SparrowCFamily.Core
{
    /// <summary>한 C/C++ 파일의 텍스트, 인코딩과 지연 생성 토큰을 한 실행 동안 공유합니다.</summary>
    internal sealed class CFamilySourceDocument
    {
        private readonly Encoding _encoding;
        private readonly byte[] _preamble;
        private readonly List<CFamilyTransformStageResult> _stages = new List<CFamilyTransformStageResult>();
        private TokenizationResult? _tokenization;

        private CFamilySourceDocument(string path, string text, Encoding encoding, byte[] preamble)
        {
            Path = path;
            OriginalText = text;
            Text = text;
            _encoding = encoding;
            _preamble = preamble;
            PreferredNewLine = DetectPreferredNewLine(text);
        }

        internal string Path { get; }
        internal string OriginalText { get; }
        internal string Text { get; private set; }
        internal string PreferredNewLine { get; }
        internal int TokenizationPasses { get; private set; }
        internal int AppliedEditCount { get; private set; }
        internal IReadOnlyList<CFamilyTransformStageResult> Stages => _stages;
        internal bool IsChanged => !string.Equals(OriginalText, Text, StringComparison.Ordinal);

        internal IReadOnlyList<CodeToken> Tokens => Tokenization.Tokens;
        internal IReadOnlyList<LexicalSpan> ProtectedSpans => Tokenization.ProtectedSpans;

        private TokenizationResult Tokenization
        {
            get
            {
                if (_tokenization == null)
                {
                    _tokenization = CFamilyTokenizer.Tokenize(Text);
                    TokenizationPasses++;
                }
                return _tokenization;
            }
        }

        internal static CFamilySourceDocument Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            DetectEncoding(bytes, out Encoding encoding, out byte[] preamble, out int offset);
            string text = encoding.GetString(bytes, offset, bytes.Length - offset);
            return new CFamilySourceDocument(path, text, encoding, preamble);
        }

        internal bool Apply(EditCollector edits)
        {
            string rewritten = edits.Apply(Text);
            if (!UpdateText(rewritten, appliedEdits: 0)) return false;
            AppliedEditCount += edits.Count;
            return true;
        }

        internal bool UpdateText(string rewritten, int appliedEdits = 1)
        {
            if (string.Equals(Text, rewritten, StringComparison.Ordinal)) return false;
            Text = rewritten;
            _tokenization = null;
            AppliedEditCount += appliedEdits;
            return true;
        }

        internal void MeasureStage(string name, Action action)
        {
            int tokenizationsBefore = TokenizationPasses;
            int editsBefore = AppliedEditCount;
            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            _stages.Add(new CFamilyTransformStageResult(
                name,
                TokenizationPasses - tokenizationsBefore,
                AppliedEditCount - editsBefore,
                stopwatch.ElapsedMilliseconds));
        }

        internal void Save()
        {
            byte[] body = _encoding.GetBytes(Text);
            string temp = Path + ".sparrow-tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    options: FileOptions.SequentialScan))
                {
                    if (_preamble.Length > 0) stream.Write(_preamble, 0, _preamble.Length);
                    stream.Write(body, 0, body.Length);
                    stream.Flush(flushToDisk: true);
                }

                try
                {
                    File.Replace(temp, Path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temp, Path, overwrite: true);
                }
                catch (IOException)
                {
                    File.Move(temp, Path, overwrite: true);
                }
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); } catch { }
                }
            }
        }

        private static void DetectEncoding(
            byte[] bytes,
            out Encoding encoding,
            out byte[] preamble,
            out int offset)
        {
            if (StartsWith(bytes, 0x00, 0x00, 0xFE, 0xFF))
            {
                encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true);
                preamble = new byte[] { 0x00, 0x00, 0xFE, 0xFF };
                offset = 4;
                return;
            }
            if (StartsWith(bytes, 0xFF, 0xFE, 0x00, 0x00))
            {
                encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true);
                preamble = new byte[] { 0xFF, 0xFE, 0x00, 0x00 };
                offset = 4;
                return;
            }
            if (StartsWith(bytes, 0xEF, 0xBB, 0xBF))
            {
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                preamble = new byte[] { 0xEF, 0xBB, 0xBF };
                offset = 3;
                return;
            }
            if (StartsWith(bytes, 0xFE, 0xFF))
            {
                encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
                preamble = new byte[] { 0xFE, 0xFF };
                offset = 2;
                return;
            }
            if (StartsWith(bytes, 0xFF, 0xFE))
            {
                encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
                preamble = new byte[] { 0xFF, 0xFE };
                offset = 2;
                return;
            }

            preamble = Array.Empty<byte>();
            offset = 0;
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            try
            {
                utf8.GetCharCount(bytes);
                encoding = utf8;
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                encoding = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
        }

        private static bool StartsWith(byte[] bytes, params byte[] signature)
        {
            if (bytes.Length < signature.Length) return false;
            for (int i = 0; i < signature.Length; i++)
                if (bytes[i] != signature[i]) return false;
            return true;
        }

        private static string DetectPreferredNewLine(string text)
        {
            int lf = text.IndexOf('\n');
            if (lf < 0) return Environment.NewLine;
            return lf > 0 && text[lf - 1] == '\r' ? "\r\n" : "\n";
        }
    }
}
