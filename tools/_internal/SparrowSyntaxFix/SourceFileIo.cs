// Safety / encoding layer. Reads a .cs file preserving its UTF-8 BOM presence and (implicitly, via
// Roslyn's full-fidelity trivia) its exact newlines. If the file does not round-trip cleanly as UTF-8,
// TryRead returns null so the caller SKIPS it (never corrupts a non-UTF-8/UTF-16 source). Writes are
// atomic: a temp file in the same directory is written then moved over the target, so a crash mid-write
// can never truncate source.

using System;
using System.IO;
using System.Text;

namespace SparrowSyntaxFix
{
    internal sealed class SourceFile
    {
        public SourceFile(string text, bool hasBom, string newline)
        {
            Text = text;
            HasBom = hasBom;
            Newline = newline;
        }

        public string Text { get; }
        public bool HasBom { get; }

        // Predominant newline, for reporting only. We do NOT normalize: Roslyn preserves every existing
        // newline in unchanged trivia and we insert no newlines, so the written text keeps the file's
        // exact (even mixed) line endings.
        public string Newline { get; }
    }

    internal static class SourceFileIo
    {
        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

        // Returns null when the file is not clean UTF-8 (caller must skip + warn, never corrupt).
        public static SourceFile? TryRead(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            bool hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            int start = hasBom ? 3 : 0;

            string text;
            try
            {
                var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                text = strict.GetString(raw, start, raw.Length - start);
            }
            catch (ArgumentException)   // DecoderFallbackException derives from ArgumentException
            {
                return null;
            }

            // Explicit round-trip guard: re-encoding must reproduce the original body byte-for-byte.
            byte[] reencoded = new UTF8Encoding(false).GetBytes(text);
            if (!raw.AsSpan(start).SequenceEqual(reencoded)) return null;

            return new SourceFile(text, hasBom, DetectNewline(text));
        }

        // Atomic write: temp file in the same directory, then replace. Preserves the BOM; writes the exact
        // text bytes (Roslyn already preserved every existing newline in the unchanged trivia).
        public static void WriteAtomic(string path, string content, bool hasBom)
        {
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full) ?? ".";
            byte[] body = new UTF8Encoding(false).GetBytes(content);

            byte[] outBytes;
            if (hasBom)
            {
                outBytes = new byte[Utf8Bom.Length + body.Length];
                Buffer.BlockCopy(Utf8Bom, 0, outBytes, 0, Utf8Bom.Length);
                Buffer.BlockCopy(body, 0, outBytes, Utf8Bom.Length, body.Length);
            }
            else
            {
                outBytes = body;
            }

            string tmp = Path.Combine(dir, "." + Path.GetFileName(full) + ".syntaxfix-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(tmp, outBytes);
                File.Move(tmp, full, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    try { File.Delete(tmp); } catch { /* best effort cleanup */ }
                }
            }
        }

        private static string DetectNewline(string text)
        {
            int crlf = 0, lf = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    if (i > 0 && text[i - 1] == '\r') crlf++;
                    else lf++;
                }
            }
            return crlf >= lf ? "\r\n" : "\n";
        }
    }
}
