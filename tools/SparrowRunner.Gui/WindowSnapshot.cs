// WindowSnapshot: the app renders ITS OWN windows to PNG so a reviewer (or an AI) can actually SEE this UI.
//
// WHY: this GUI is a custom, non-installed exe, so it cannot be added to an OS automation allow-list — nobody can
// take a screenshot of it from the outside. Until now the only evidence of layout was the UIA tree dump (Rect
// numbers), from which 잘림/겹침 had to be *inferred*. A window that renders itself with RenderTargetBitmap closes
// that gap: `tests\_logs\uia-<stamp>\shots\*.png` is a real image of the real window.
//
// Two triggers (both OFF unless --screenshot-dir <DIR> is given — no arg = zero behaviour change):
//   1. 자동 지점  — 메인창 로드 완료 / 관리창 오픈 직후 / [XLS 분리] 실행 완료 후 (MainWindow calls Capture/CaptureWhenIdle)
//   2. 요청 기반  — a `capture.request` file appearing in that folder captures the ACTIVE window at any moment
//                   (so a harness can photograph transient states); its content, if any, becomes the name suffix.
//
// HARD RULES (same contract as SessionLog): every capture is best-effort. Failures never throw out of here, never
// touch app behaviour, and are reported through `error` / the session log only.
//
// DPI: the bitmap is sized in PHYSICAL pixels using VisualTreeHelper.GetDpi(window) — never a fixed 96 — otherwise
// a 125%/150% desktop would render a shrunken, blurry image that no longer matches the on-screen window rect
// (the UIA harness asserts PNG px ≈ 창 Rect exactly to catch that regression).
// 투명 방지: 흰색(+창 Background) 사각형을 먼저 깔고 그 위에 창을 그린다 — 투명 PNG 는 판독이 불가능하다.

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SparrowRunner.Gui
{
    /// <summary>Renders a live WPF <see cref="Window"/> to a PNG file. Never throws; returns false with a reason.</summary>
    internal static class WindowSnapshot
    {
        /// <summary>Sanity ceiling for a rendered side (px). Guards against an absurd RenderTargetBitmap allocation.</summary>
        private const int MaxPixelSide = 20000;

        /// <summary>
        /// Render <paramref name="w"/> into a PNG at <paramref name="path"/>. MUST be called on the window's UI
        /// thread. Returns false (with <paramref name="error"/> set) when the window has no layout yet, when the
        /// thread is wrong, or when encoding/writing fails — it never throws.
        /// </summary>
        public static bool TryCapture(Window w, string path, out string? error)
        {
            int pw, ph;
            return TryCapture(w, path, out error, out pw, out ph);
        }

        /// <summary>
        /// Same as <see cref="TryCapture(Window, string, out string?)"/> but also reports the rendered pixel size
        /// (0x0 on failure), which the session log prints so a later reader can compare it to the window rect.
        /// </summary>
        public static bool TryCapture(Window w, string path, out string? error, out int pixelWidth, out int pixelHeight)
        {
            error = null;
            pixelWidth = 0;
            pixelHeight = 0;

            if (w == null) { error = "창 참조가 없습니다"; return false; }
            if (string.IsNullOrWhiteSpace(path)) { error = "저장 경로가 비어 있습니다"; return false; }

            string tempPath = "";
            try
            {
                // RenderTargetBitmap 은 UI 스레드 전용이다. 잘못된 스레드면 예외 대신 실패로 돌려준다.
                if (!w.Dispatcher.CheckAccess()) { error = "UI 스레드가 아닙니다"; return false; }

                // 레이아웃 전(Show 전/최소화 직후)에는 찍을 것이 없다.
                try { w.UpdateLayout(); } catch { /* 레이아웃 강제는 best-effort */ }
                double dipW = w.ActualWidth;
                double dipH = w.ActualHeight;
                if (dipW <= 0 || dipH <= 0)
                {
                    error = "창 레이아웃 전(ActualWidth/Height <= 0: "
                            + dipW.ToString("0.#", CultureInfo.InvariantCulture) + "x"
                            + dipH.ToString("0.#", CultureInfo.InvariantCulture) + ")";
                    return false;
                }

                // 실제 DPI 배율로 렌더한다(96 고정 금지). PixelsPerInch = 96 * DpiScale.
                DpiScale dpi = VisualTreeHelper.GetDpi(w);
                double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
                double dpiX = dpi.PixelsPerInchX > 0 ? dpi.PixelsPerInchX : 96.0;
                double dpiY = dpi.PixelsPerInchY > 0 ? dpi.PixelsPerInchY : 96.0;

                int px = (int)Math.Ceiling(dipW * scaleX);
                int py = (int)Math.Ceiling(dipH * scaleY);
                if (px <= 0 || py <= 0 || px > MaxPixelSide || py > MaxPixelSide)
                {
                    error = "렌더 픽셀 크기 비정상 (" + px.ToString(CultureInfo.InvariantCulture) + "x"
                            + py.ToString(CultureInfo.InvariantCulture) + ")";
                    return false;
                }

                var rtb = new RenderTargetBitmap(px, py, dpiX, dpiY, PixelFormats.Pbgra32);

                // 1) 불투명 바탕을 먼저 깐다: 흰색 + (있으면) 창 Background. 투명 PNG 로 나오면 판독이 불가능하다.
                var backdrop = new DrawingVisual();
                using (DrawingContext dc = backdrop.RenderOpen())
                {
                    var full = new Rect(0, 0, dipW, dipH);
                    dc.DrawRectangle(Brushes.White, null, full);
                    Brush? bg = w.Background;
                    if (bg != null)
                    {
                        try { dc.DrawRectangle(bg, null, full); } catch { /* 이상한 브러시는 흰 바탕으로 대체 */ }
                    }
                }
                rtb.Render(backdrop);

                // 2) 그 위에 창 Visual. (창 테두리/제목줄은 OS 가 그리는 영역이라 이미지에 포함되지 않고,
                //    그 두께만큼 우/하단에 바탕색 여백이 남는다 — 크기는 창 전체(ActualWidth/Height) 기준.)
                rtb.Render(w);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));

                string fullPath = Path.GetFullPath(path);
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

                // .tmp 로 쓰고 rename 한다: 폴더를 감시하는 하네스가 절대 반쯤 쓰인 PNG 를 보지 않도록.
                tempPath = fullPath + ".tmp";
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    encoder.Save(fs);
                }
                if (File.Exists(fullPath)) File.Delete(fullPath);
                File.Move(tempPath, fullPath);
                tempPath = "";

                pixelWidth = px;
                pixelHeight = py;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                if (tempPath.Length > 0)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 정리는 best-effort */ }
                }
            }
        }
    }

    /// <summary>
    /// One snapshot session = one output folder (--screenshot-dir). Numbers the files, watches the folder for
    /// <c>capture.request</c>, and writes one session-log line per capture. Disabled (null) when no folder was given.
    /// </summary>
    internal sealed class SnapshotRecorder : IDisposable
    {
        /// <summary>Drop a file with this name into the snapshot folder to capture the active window right now.</summary>
        public const string RequestFileName = "capture.request";

        private readonly string _folder;
        private readonly Dispatcher _dispatcher;
        private readonly Action<string> _log;
        private readonly Window _fallbackWindow;

        private FileSystemWatcher? _watcher;
        private int _sequence;
        private int _requestBusy;   // 0/1 — 겹친 요청은 조용히 무시한다(경합 방지)
        private volatile bool _disposed;

        private SnapshotRecorder(string folder, Window fallbackWindow, Action<string> log)
        {
            _folder = folder;
            _fallbackWindow = fallbackWindow;
            _dispatcher = fallbackWindow.Dispatcher;
            _log = log;
        }

        /// <summary>Snapshot output folder.</summary>
        public string Folder => _folder;

        /// <summary>
        /// Create a recorder for <paramref name="dir"/> (--screenshot-dir). Returns null when no folder was given
        /// (feature off) or when the folder cannot be created — in both cases the app behaves exactly as before.
        /// </summary>
        public static SnapshotRecorder? Create(string? dir, Window fallbackWindow, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(dir) || fallbackWindow == null || log == null) return null;

            string full;
            try
            {
                full = Path.GetFullPath(dir!.Trim().Trim('"'));
                Directory.CreateDirectory(full);
            }
            catch (Exception ex)
            {
                try { log("snapshot 실패: 스냅샷 폴더를 만들 수 없습니다 (" + ex.Message + ")"); } catch { }
                return null;
            }

            var recorder = new SnapshotRecorder(full, fallbackWindow, log);
            recorder.Log("창 스냅샷 폴더: " + full + "  (" + RequestFileName + " 파일로 임의 시점 캡처)");
            recorder.StartWatcher();
            return recorder;
        }

        /// <summary>Capture <paramref name="w"/> now (caller must already be on the UI thread).</summary>
        public void Capture(Window? w, string stage)
        {
            if (_disposed || w == null) return;

            int n = Interlocked.Increment(ref _sequence);
            string name = n.ToString("00", CultureInfo.InvariantCulture) + "-" + Sanitize(stage) + "-"
                          + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".png";

            string? error;
            int px = 0, py = 0;
            bool ok;
            try { ok = WindowSnapshot.TryCapture(w, Path.Combine(_folder, name), out error, out px, out py); }
            catch (Exception ex) { ok = false; error = ex.Message; }

            if (ok)
            {
                Log("snapshot: " + name + " (" + px.ToString(CultureInfo.InvariantCulture) + "x"
                    + py.ToString(CultureInfo.InvariantCulture) + "px)");
            }
            else
            {
                Log("snapshot 실패: " + name + " — " + (string.IsNullOrEmpty(error) ? "알 수 없는 사유" : error));
            }
        }

        /// <summary>
        /// Queue a capture for when the UI thread goes idle (ContextIdle runs AFTER Loaded/Render/DataBind), so a
        /// window that was just shown is fully laid out and its item rows are generated before we photograph it.
        /// </summary>
        public void CaptureWhenIdle(Window? w, string stage)
        {
            if (_disposed || w == null) return;
            try { _dispatcher.InvokeAsync(() => Capture(w, stage), DispatcherPriority.ContextIdle); }
            catch { /* 디스패처 종료 중일 수 있다 */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                FileSystemWatcher? w = _watcher;
                _watcher = null;
                if (w != null)
                {
                    w.EnableRaisingEvents = false;
                    w.Created -= OnRequestFile;
                    w.Changed -= OnRequestFile;
                    w.Dispose();
                }
            }
            catch { /* 정리는 best-effort */ }
        }

        // ---- 요청 기반 캡처 ----------------------------------------------------

        private void StartWatcher()
        {
            try
            {
                var w = new FileSystemWatcher(_folder, RequestFileName)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                w.Created += OnRequestFile;
                w.Changed += OnRequestFile;
                w.EnableRaisingEvents = true;
                _watcher = w;
            }
            catch (Exception ex)
            {
                Log("snapshot 실패: 요청 감시(" + RequestFileName + ")를 시작할 수 없습니다 (" + ex.Message + ")");
            }
        }

        // Watcher thread. WriteAllText raises Created+Changed, so re-entry must be harmless: the first handler wins
        // and the rest see either the busy flag or an already-deleted request file.
        private void OnRequestFile(object? sender, FileSystemEventArgs e)
        {
            if (_disposed) return;
            if (Interlocked.CompareExchange(ref _requestBusy, 1, 0) != 0) return;   // 겹친 요청 무시

            bool handedToUi = false;
            try
            {
                string requestPath = Path.Combine(_folder, RequestFileName);
                if (!File.Exists(requestPath)) return;                              // 이미 처리됨

                string suffix = ReadRequestSuffix(requestPath);                      // 내용이 있으면 파일명 접미사
                _dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        Capture(ResolveActiveWindow(), suffix.Length > 0 ? suffix : "request");
                        TryDelete(requestPath);
                    }
                    catch { /* 요청 처리 실패는 앱을 건드리지 않는다 */ }
                    finally { Interlocked.Exchange(ref _requestBusy, 0); }
                });
                handedToUi = true;
            }
            catch { /* 감시 콜백은 절대 예외를 밖으로 내지 않는다 */ }
            finally
            {
                if (!handedToUi) Interlocked.Exchange(ref _requestBusy, 0);
            }
        }

        // 활성(포커스) 창 → 없으면 가장 최근에 보이는 창(= 소유 창이 열려 있으면 그것) → 없으면 메인 창.
        private Window? ResolveActiveWindow()
        {
            try
            {
                Application? app = Application.Current;
                if (app != null)
                {
                    Window? active = null;
                    Window? lastVisible = null;
                    foreach (Window w in app.Windows)
                    {
                        if (w == null || !w.IsLoaded) continue;
                        if (w.IsActive) active = w;
                        if (w.IsVisible) lastVisible = w;
                    }
                    if (active != null) return active;
                    if (lastVisible != null) return lastVisible;
                }
            }
            catch { /* 아래 fallback */ }
            return _fallbackWindow;
        }

        // 요청 파일 내용(접미사)을 읽는다. 방금 만들어진 파일은 잠깐 잠겨 있을 수 있어 짧게 재시도한다(감시 스레드).
        //
        // 빈 내용도 재시도한다: WriteAllText 는 "파일 생성"과 "내용 쓰기"가 별개 단계라 감시자가 생성 알림을 내용이
        // 들어가기 전에 받을 수 있다. 그때 곧장 ""를 돌려주면 접미사가 사라져 요청한 이름 대신 'request' 로 저장되고,
        // 그 이름을 기다리던 하네스는 타임아웃한다(부하가 걸린 머신에서 실제로 재현됨). 정말로 내용 없는 요청은
        // 이 재시도를 다 쓴 뒤 예전처럼 접미사 없이 처리된다(최대 지연 0.4초, 캡처는 원래 비동기 best-effort).
        private static string ReadRequestSuffix(string path)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs, new UTF8Encoding(false), true))
                    {
                        string text = (reader.ReadToEnd() ?? "").Trim();
                        if (text.Length > 0) return text;
                    }
                }
                catch (FileNotFoundException) { return ""; }
                catch (DirectoryNotFoundException) { return ""; }
                catch { /* 잠김 등 — 아래에서 잠깐 쉬고 재시도 */ }
                Thread.Sleep(40);
            }
            return "";
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }

        // 파일명 안전화: ASCII 영숫자 + - _ . 만 남기고 나머지는 '-' 로. 빈 값이면 "shot".
        private static string Sanitize(string? stage)
        {
            if (string.IsNullOrWhiteSpace(stage)) return "shot";
            var sb = new StringBuilder();
            foreach (char c in stage!.Trim())
            {
                if (c < 128 && char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (c == '-' || c == '_' || c == '.') sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
                if (sb.Length >= 48) break;
            }
            string s = sb.ToString().Trim('-', '_', '.');
            return s.Length > 0 ? s : "shot";
        }

        // 로그는 화면 LogBox 도 건드리므로 반드시 UI 스레드에서 호출한다(감시 스레드에서 오면 마셜링).
        private void Log(string line)
        {
            try
            {
                if (_dispatcher.CheckAccess()) _log(line);
                else _dispatcher.InvokeAsync(() => { try { _log(line); } catch { } });
            }
            catch { /* 로깅 실패가 캡처/앱을 막지 않는다 */ }
        }
    }
}
