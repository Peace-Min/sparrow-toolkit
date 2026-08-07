// SessionLog: the GUI's on-disk transcript. The screen LogBox evaporates when the app closes, which makes
// after-the-fact diagnosis ("어떤 상황에서 제대로 안 되었나?") impossible — so every AppendLog line is ALSO
// appended to a per-session file with an HH:mm:ss.fff stamp.
//
// Placement: %LOCALAPPDATA%\SparrowRunner\logs\session-<yyyyMMdd-HHmmss>.log. Deliberately NOT next to the exe —
// the tool is expected to run from Program Files / a read-only 폐쇄망 share where the install dir is unwritable.
// --log-dir <DIR> overrides it (tests point it at a temp folder).
//
// HARD RULE: logging is best-effort and must NEVER affect the app. Every filesystem call is swallowed; once a
// write fails the file sink switches itself off and the screen log carries on untouched.
//
// Rotation: only the newest 20 session-*.log survive (and the newest 20 xlssplit-*.json run reports with their .log
// companions), so an interactive tool cannot slowly fill the profile.
// Encoding: UTF-8 WITHOUT BOM, CRLF (opened in Notepad by operators on Windows).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SparrowRunner.Gui
{
    /// <summary>Per-session file transcript of the GUI log. Never throws; failures silently degrade to no file.</summary>
    internal sealed class SessionLog
    {
        /// <summary>Session transcripts kept in the log folder (older ones are deleted at startup).</summary>
        private const int KeepSessions = 20;

        /// <summary>[XLS 분리] run reports (json + .log pairs) kept in the log folder.</summary>
        private const int KeepReports = 20;

        private readonly object _gate = new object();
        private readonly UTF8Encoding _utf8NoBom = new UTF8Encoding(false);
        private bool _sinkAlive;

        private SessionLog(string logDirectory, string? filePath)
        {
            LogDirectory = logDirectory;
            FilePath = filePath;
            _sinkAlive = filePath != null;
        }

        /// <summary>Folder holding session transcripts and [XLS 분리] run reports (exists when <see cref="FilePath"/> is non-null).</summary>
        public string LogDirectory { get; }

        /// <summary>This session's transcript path, or null when the folder could not be created/written.</summary>
        public string? FilePath { get; }

        /// <summary>Default log folder: %LOCALAPPDATA%\SparrowRunner\logs (falls back to TEMP, then the cwd).</summary>
        public static string DefaultDirectory()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(local)) return Path.Combine(local, "SparrowRunner", "logs");
            }
            catch { /* fall through */ }

            try { return Path.Combine(Path.GetTempPath(), "SparrowRunner", "logs"); }
            catch { return Path.Combine(Environment.CurrentDirectory, "logs"); }
        }

        /// <summary>
        /// Open a session transcript. <paramref name="overrideDir"/> (from --log-dir) wins over the default folder.
        /// Creates the folder, opens session-&lt;stamp&gt;.log and rotates old logs. Any failure yields a live object
        /// with <see cref="FilePath"/> = null (screen-only logging) rather than an exception.
        /// </summary>
        public static SessionLog Create(string? overrideDir)
        {
            string dir;
            try
            {
                dir = !string.IsNullOrWhiteSpace(overrideDir)
                    ? Path.GetFullPath(overrideDir!.Trim().Trim('"'))
                    : DefaultDirectory();
            }
            catch { dir = DefaultDirectory(); }

            string? path = null;
            try
            {
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
                // A second launch inside the same second must not clobber the first session's transcript.
                if (File.Exists(path))
                {
                    for (int n = 2; n <= 99 && File.Exists(path); n++)
                    {
                        path = Path.Combine(dir, "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                                                 + "-" + n.ToString(CultureInfo.InvariantCulture) + ".log");
                    }
                }
                // Create with an explicit UTF-8 BOM, then append BOM-less. The BOM is deliberate: this file is read
                // by operators (메모장) and by PowerShell 5.1 `Get-Content`, which decodes BOM-less UTF-8 as ANSI and
                // turns every 한글 line into mojibake. Writing 3 bytes here also proves the folder is writable.
                File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF });
            }
            catch
            {
                path = null;
            }

            var log = new SessionLog(dir, path);
            log.Rotate("session-*.log", KeepSessions, deleteCompanionLog: false);
            log.Rotate("xlssplit-*.json", KeepReports, deleteCompanionLog: true);
            return log;
        }

        /// <summary>Append one line with an HH:mm:ss.fff stamp. Best-effort: a failed write disables the file sink
        /// permanently for this session and is otherwise ignored.</summary>
        public void Append(string line)
        {
            if (!_sinkAlive || FilePath == null) return;
            try
            {
                lock (_gate)
                {
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + (line ?? "") + "\r\n",
                        _utf8NoBom);
                }
            }
            catch
            {
                _sinkAlive = false;   // read-only/locked/full disk: stop trying, keep the app alive
            }
        }

        /// <summary>Write the session header: what build ran, with which arguments, against which folders, on what
        /// runtime. This is the block that makes a later "왜 이렇게 동작했나" answerable without asking the user.</summary>
        public void WriteHeader(string skillRoot, string guidesDir, IEnumerable<string> startupArgs)
        {
            if (!_sinkAlive || FilePath == null) return;

            string args;
            try { args = string.Join(" ", (startupArgs ?? Array.Empty<string>()).Skip(1)); }
            catch { args = "(알 수 없음)"; }

            var lines = new List<string>
            {
                "=== Sparrow Helper 세션 로그 ===",
                "세션 시작   : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                                 + " (UTC " + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + ")",
                "앱 버전     : " + ResolveAppVersion(),
                "실행 파일   : " + SafeProcessPath(),
                "시작 인자   : " + (args.Length > 0 ? args : "(없음)"),
                "스킬 루트   : " + skillRoot,
                "guides 폴더 : " + guidesDir,
                "로그 폴더   : " + LogDirectory,
                "OS          : " + SafeOs(),
                ".NET        : " + Environment.Version + " / " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                "프로세스    : PID " + SafePid() + " · " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture,
                "작업 폴더   : " + SafeCwd(),
                new string('=', 72),
            };

            try
            {
                lock (_gate)
                {
                    File.AppendAllText(FilePath, string.Join("\r\n", lines) + "\r\n", _utf8NoBom);
                }
            }
            catch { _sinkAlive = false; }
        }

        /// <summary>Path for this run's [XLS 분리] report json inside the log folder (never the export output folder).
        /// Returns null when there is no writable log folder.</summary>
        public string? NewXlsSplitReportPath()
        {
            if (FilePath == null) return null;
            try
            {
                return Path.Combine(LogDirectory,
                    "xlssplit-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
            }
            catch { return null; }
        }

        // Keep only the newest `keep` files matching `pattern` (names carry a sortable timestamp, so an ordinal
        // descending sort is chronological). deleteCompanionLog also removes "<stem>.log" next to a deleted report.
        private void Rotate(string pattern, int keep, bool deleteCompanionLog)
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) return;
                List<string> files = Directory.GetFiles(LogDirectory, pattern, SearchOption.TopDirectoryOnly)
                                              .OrderByDescending(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                                              .ToList();
                for (int i = keep; i < files.Count; i++)
                {
                    try { File.Delete(files[i]); } catch { /* locked file: leave it */ }
                    if (!deleteCompanionLog) continue;
                    try
                    {
                        string companion = Path.Combine(LogDirectory, Path.GetFileNameWithoutExtension(files[i]) + ".log");
                        if (File.Exists(companion)) File.Delete(companion);
                    }
                    catch { /* best-effort */ }
                }
            }
            catch { /* rotation is housekeeping only */ }
        }

        private static string ResolveAppVersion()
        {
            try
            {
                Assembly asm = typeof(SessionLog).Assembly;
                string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                string name = asm.GetName().Name ?? "SparrowRunner.Gui";
                string ver = !string.IsNullOrWhiteSpace(info) ? info! : (asm.GetName().Version?.ToString() ?? "0.0.0.0");
                return name + " " + ver;
            }
            catch { return "unknown"; }
        }

        private static string SafeProcessPath()
        {
            try { return Environment.ProcessPath ?? AppContext.BaseDirectory; }
            catch { return "(알 수 없음)"; }
        }

        private static string SafeOs()
        {
            try { return Environment.OSVersion.VersionString + " (" + System.Runtime.InteropServices.RuntimeInformation.OSDescription + ")"; }
            catch { return "(알 수 없음)"; }
        }

        private static string SafePid()
        {
            try { return Environment.ProcessId.ToString(CultureInfo.InvariantCulture); }
            catch { return "?"; }
        }

        private static string SafeCwd()
        {
            try { return Environment.CurrentDirectory; }
            catch { return "(알 수 없음)"; }
        }
    }
}
