using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace JustReadTheInstructions
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class JRTIStreamServer : MonoBehaviour
    {
        public static JRTIStreamServer Instance { get; private set; }

        private HttpListener _listener;
        private Thread _listenerThread;
        private Thread _watchdogThread;
        private volatile bool _running;

        private static readonly TimeSpan RecordingIdleTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);

        private readonly ConcurrentDictionary<int, CameraStreamState> _states
            = new ConcurrentDictionary<int, CameraStreamState>();

        private readonly ConcurrentDictionary<int, float> _lastCaptureTimes
            = new ConcurrentDictionary<int, float>();

        private readonly ConcurrentDictionary<int, bool> _captureInFlight
            = new ConcurrentDictionary<int, bool>();

        private readonly ConcurrentDictionary<string, RecordingSession> _recordings
            = new ConcurrentDictionary<string, RecordingSession>();

        private readonly ConcurrentDictionary<string, byte> _finalizedSessions
            = new ConcurrentDictionary<string, byte>();

        private float MinCapturePeriod => 1f / Mathf.Max(1, JRTISettings.StreamMaxFps);

        private static readonly string WebRoot =
            KSPUtil.ApplicationRootPath + "GameData/JustReadTheInstructions/Web/";

        private static readonly string RecordingsRoot = Path.Combine(WebRoot, "recordings");

        private static readonly string DefaultLosPath = Path.Combine(WebRoot, "images", "los.png");
        private static readonly string CustomLosPath = Path.Combine(WebRoot, "images", "customlos.png");

        void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this;
            EnsureRecordingsDirectory();
        }

        void Start() => StartServer();

        void OnDestroy()
        {
            StopServer();
            FinalizeAllRecordings();
            if (Instance == this) Instance = null;
        }

        private static void EnsureRecordingsDirectory()
        {
            try
            {
                Directory.CreateDirectory(RecordingsRoot);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[JRTI-Stream]: Could not create recordings directory: {ex.Message}");
            }
        }

        public void RegisterCamera(int cameraId)
            => _states.GetOrAdd(cameraId, _ => new CameraStreamState());

        public void UnregisterCamera(int cameraId)
        {
            if (_states.TryRemove(cameraId, out var state))
                state.Dispose();
            _lastCaptureTimes.TryRemove(cameraId, out _);
            _captureInFlight.TryRemove(cameraId, out _);
        }

        public bool IsStreaming(int cameraId)
            => _states.TryGetValue(cameraId, out var s) && s.MjpegClientCount > 0;

        public void TryCaptureFrame(int cameraId, RenderTexture renderTexture)
        {
            if (!_states.TryGetValue(cameraId, out var state) || !state.HasActiveClients)
                return;

            float now = Time.unscaledTime;
            _lastCaptureTimes.TryGetValue(cameraId, out float last);
            if (now - last < MinCapturePeriod)
                return;

            _captureInFlight.TryGetValue(cameraId, out bool inFlight);
            if (inFlight)
                return;

            _lastCaptureTimes[cameraId] = now;
            _captureInFlight[cameraId] = true;

            int rtWidth = renderTexture.width;
            int rtHeight = renderTexture.height;
            int quality = JRTISettings.StreamJpegQuality;

            AsyncGPUReadback.Request(renderTexture, 0, TextureFormat.RGB24, (request) =>
            {
                _captureInFlight[cameraId] = false;

                if (request.hasError)
                    return;

                if (!_states.TryGetValue(cameraId, out var s))
                    return;

                var raw = request.GetData<byte>().ToArray();

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    var jpeg = ImageConversion.EncodeArrayToJPG(
                        raw,
                        GraphicsFormat.R8G8B8_UNorm,
                        (uint)rtWidth,
                        (uint)rtHeight,
                        0,
                        quality);

                    if (jpeg != null && _states.TryGetValue(cameraId, out var s2))
                        s2.PushFrame(jpeg);
                });
            });
        }

        private void StartServer()
        {
            if (!HttpListener.IsSupported)
            {
                Debug.LogWarning("[JRTI-Stream]: HttpListener not supported on this platform");
                return;
            }

            _listener = new HttpListener();

            bool started = TryBind($"http://*:{JRTISettings.StreamPort}/")
                        || TryBind($"http://localhost:{JRTISettings.StreamPort}/");

            if (!started)
            {
                Debug.LogError("[JRTI-Stream]: Could not bind to any address. Streaming disabled.");
                return;
            }

            _running = true;
            _listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "JRTI-StreamServer"
            };
            _listenerThread.Start();

            _watchdogThread = new Thread(WatchdogLoop)
            {
                IsBackground = true,
                Name = "JRTI-RecordingWatchdog"
            };
            _watchdogThread.Start();

            Debug.Log($"[JRTI-Stream]: Web UI at http://localhost:{JRTISettings.StreamPort}/");
        }

        private bool TryBind(string prefix)
        {
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(prefix);
                _listener.Start();
                Debug.Log($"[JRTI-Stream]: Listening on {prefix}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[JRTI-Stream]: Could not bind {prefix}: {ex.Message}");
                return false;
            }
        }

        private void StopServer()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listenerThread?.Join(2000);
            _watchdogThread?.Join(2000);

            foreach (var state in _states.Values)
                state.Dispose();
            _states.Clear();
        }

        private void WatchdogLoop()
        {
            while (_running)
            {
                try
                {
                    Thread.Sleep(WatchdogInterval);
                    if (!_running) break;

                    var cutoff = DateTime.UtcNow - RecordingIdleTimeout;
                    foreach (var kv in _recordings)
                    {
                        if (kv.Value.LastActivityUtc < cutoff)
                        {
                            _finalizedSessions.TryAdd(kv.Key, 0);
                            if (_recordings.TryRemove(kv.Key, out var session))
                            {
                                try
                                {
                                    session.Dispose();
                                    Debug.Log($"[JRTI-Stream]: Recording auto-finalized (idle): {session.DisplayPath} ({session.BytesWritten} bytes)");
                                }
                                catch (Exception ex)
                                {
                                    Debug.LogError($"[JRTI-Stream]: Watchdog finalize error: {ex.Message}");
                                }
                            }
                        }
                    }
                }
                catch (ThreadInterruptedException) { break; }
                catch (Exception ex)
                {
                    if (_running)
                        Debug.LogError($"[JRTI-Stream]: Watchdog error: {ex.Message}");
                }
            }
        }

        private void FinalizeAllRecordings()
        {
            foreach (var kv in _recordings)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _recordings.Clear();
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
                }
                catch (HttpListenerException) when (!_running) { break; }
                catch (Exception ex)
                {
                    if (_running)
                        Debug.LogError($"[JRTI-Stream]: Accept error: {ex.Message}");
                }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url.AbsolutePath;
                var trimmed = path == "/" ? "" : path.TrimEnd('/');

                if (trimmed == "" || trimmed == "/index.html")
                {
                    ServeStaticFile(ctx, "index.html");
                    return;
                }

                if (trimmed == "/cameras")
                {
                    ServeCameraList(ctx);
                    return;
                }

                if (trimmed.StartsWith("/recordings/"))
                {
                    HandleRecordingEndpoint(ctx, trimmed);
                    return;
                }

                if (trimmed.StartsWith("/camera/"))
                {
                    ServeCameraEndpoint(ctx, trimmed);
                    return;
                }

                var relative = trimmed.TrimStart('/');
                if (!string.IsNullOrEmpty(relative))
                {
                    ServeStaticFile(ctx, relative);
                    return;
                }

                ServeError(ctx, 404, "Not found");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[JRTI-Stream]: Request handler error: {ex.Message}");
                try { ctx.Response.Close(); } catch { }
            }
        }

        private void ServeStaticFile(HttpListenerContext ctx, string relativePath)
        {
            var webRootFull = Path.GetFullPath(WebRoot);
            var candidate = Path.GetFullPath(Path.Combine(WebRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            if (!candidate.StartsWith(webRootFull, StringComparison.Ordinal))
            {
                ServeError(ctx, 403, "Forbidden");
                return;
            }

            if (PathsEqual(candidate, DefaultLosPath) && File.Exists(CustomLosPath))
            {
                candidate = CustomLosPath;
            }

            if (!File.Exists(candidate))
            {
                ServeError(ctx, 404, "Not found");
                return;
            }

            try
            {
                var bytes = File.ReadAllBytes(candidate);
                ctx.Response.ContentType = GetContentType(candidate);
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.Headers.Add("Cache-Control", "no-cache");
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                ServeError(ctx, 500, $"Read failed: {ex.Message}");
            }
        }

        private static readonly StringComparison PathComparison =
            Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        private static bool PathsEqual(string a, string b)
            => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), PathComparison);

        private static string GetContentType(string fullPath)
        {
            var ext = Path.GetExtension(fullPath).ToLowerInvariant();
            switch (ext)
            {
                case ".html":
                case ".htm": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js":
                case ".mjs": return "application/javascript; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".svg": return "image/svg+xml";
                case ".ico": return "image/x-icon";
                case ".webm": return "video/webm";
                case ".mp4": return "video/mp4";
                case ".mkv": return "video/x-matroska";
                case ".txt": return "text/plain; charset=utf-8";
                default: return "application/octet-stream";
            }
        }

        private void ServeCameraList(HttpListenerContext ctx)
        {
            var sb = new StringBuilder("[");
            bool first = true;

            foreach (var kv in _states)
            {
                if (HullCameraManager.Instance != null && !HullCameraManager.Instance.HasCamera(kv.Key))
                    continue;

                if (!first) sb.Append(',');
                int id = kv.Key;
                string name = HullCameraManager.Instance?.GetCameraDisplayName(id) ?? id.ToString();
                sb.Append($"{{\"id\":{id},");
                sb.Append($"\"name\":\"{EscapeJson(name)}\",");
                sb.Append($"\"streaming\":true,");
                sb.Append($"\"snapshotUrl\":\"/camera/{id}/snapshot\",");
                sb.Append($"\"streamUrl\":\"/viewer.html?id={id}\"}}");
                first = false;
            }

            sb.Append(']');
            ServeText(ctx, sb.ToString(), "application/json");
        }

        private void ServeCameraEndpoint(HttpListenerContext ctx, string path)
        {
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out int cameraId))
            {
                ServeError(ctx, 400, "Invalid camera ID");
                return;
            }

            if (parts.Length == 2)
            {
                ctx.Response.Redirect($"/viewer.html?id={cameraId}");
                ctx.Response.Close();
                return;
            }

            if (!_states.TryGetValue(cameraId, out var state))
            {
                ServeError(ctx, 404, "Camera not found");
                return;
            }

            switch (parts[2])
            {
                case "snapshot": ServeSnapshot(ctx, state); break;
                case "stream": ServeMjpeg(ctx, state); break;
                case "status": ServeText(ctx, "ok", "text/plain"); break;
                default: ServeError(ctx, 404, "Unknown action"); break;
            }
        }

        private void HandleRecordingEndpoint(HttpListenerContext ctx, string path)
        {
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
            {
                ServeError(ctx, 400, "Expected /recordings/<sessionId>/<action>");
                return;
            }

            var sessionId = parts[1];
            var action = parts[2];

            if (!IsSafeId(sessionId))
            {
                ServeError(ctx, 400, "Invalid session id");
                return;
            }

            var name = ctx.Request.QueryString["name"];
            if (string.IsNullOrEmpty(name))
            {
                ServeError(ctx, 400, "Missing name parameter");
                return;
            }

            var safeName = SanitizeRecordingFilename(name);
            if (string.IsNullOrEmpty(safeName))
            {
                ServeError(ctx, 400, "Invalid filename");
                return;
            }

            switch (action)
            {
                case "append":
                    AppendRecordingChunk(ctx, sessionId, safeName);
                    break;
                case "finalize":
                    FinalizeRecordingSession(ctx, sessionId);
                    break;
                case "abort":
                    AbortRecordingSession(ctx, sessionId);
                    break;
                default:
                    ServeError(ctx, 404, "Unknown recording action");
                    break;
            }
        }

        private void AppendRecordingChunk(HttpListenerContext ctx, string sessionId, string safeName)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                ServeError(ctx, 405, "POST required");
                return;
            }

            if (_finalizedSessions.ContainsKey(sessionId))
            {
                ServeError(ctx, 410, "Session closed");
                return;
            }

            RecordingSession session;
            try
            {
                session = _recordings.GetOrAdd(sessionId, id =>
                    RecordingSession.Create(id, Path.Combine(RecordingsRoot, safeName)));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[JRTI-Stream]: Could not create session {sessionId}: {ex.Message}");
                ServeError(ctx, 500, "Session create failed");
                return;
            }

            if (_finalizedSessions.ContainsKey(sessionId))
            {
                if (_recordings.TryRemove(sessionId, out var zombie) && ReferenceEquals(zombie, session))
                {
                    try { zombie.DisposeAndDelete(); } catch { }
                }
                ServeError(ctx, 410, "Session closed");
                return;
            }

            try
            {
                session.AppendFromStream(ctx.Request.InputStream);
                ServeText(ctx, "ok", "text/plain");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[JRTI-Stream]: Append failed for {sessionId}: {ex.Message}");
                ServeError(ctx, 500, "Append failed");
            }
        }

        private void FinalizeRecordingSession(HttpListenerContext ctx, string sessionId)
        {
            _finalizedSessions.TryAdd(sessionId, 0);
            if (_recordings.TryRemove(sessionId, out var session))
            {
                try
                {
                    session.Dispose();
                    Debug.Log($"[JRTI-Stream]: Recording saved: {session.DisplayPath} ({session.BytesWritten} bytes)");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[JRTI-Stream]: Finalize error: {ex.Message}");
                }
            }
            ServeText(ctx, "ok", "text/plain");
        }

        private void AbortRecordingSession(HttpListenerContext ctx, string sessionId)
        {
            _finalizedSessions.TryAdd(sessionId, 0);
            if (_recordings.TryRemove(sessionId, out var session))
            {
                try
                {
                    session.DisposeAndDelete();
                    Debug.Log($"[JRTI-Stream]: Recording aborted: {session.DisplayPath}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[JRTI-Stream]: Abort error: {ex.Message}");
                }
            }
            ServeText(ctx, "ok", "text/plain");
        }

        private static bool IsSafeId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128) return false;
            foreach (var c in id)
            {
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_')) return false;
            }
            return true;
        }

        private static string SanitizeRecordingFilename(string requested)
        {
            if (string.IsNullOrEmpty(requested)) return null;

            var name = Path.GetFileName(requested);
            if (string.IsNullOrEmpty(name)) return null;

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (Array.IndexOf(invalid, c) >= 0 || c < 32) sb.Append('_');
                else sb.Append(c);
            }

            var result = sb.ToString();
            if (result.Length > 200) result = result.Substring(0, 200);

            var ext = Path.GetExtension(result).ToLowerInvariant();
            if (ext != ".webm" && ext != ".mp4" && ext != ".mkv")
            {
                result += ".webm";
            }
            return result;
        }

        private static void ServeSnapshot(HttpListenerContext ctx, CameraStreamState state)
        {
            state.MarkSnapshotInterest();

            byte[] jpeg;
            lock (state.JpegLock)
                jpeg = state.LatestJpeg;

            if (jpeg == null)
            {
                ServeError(ctx, 503, "No frame available yet");
                return;
            }

            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.ContentLength64 = jpeg.Length;
            ctx.Response.Headers.Add("Cache-Control", "no-cache");
            ctx.Response.OutputStream.Write(jpeg, 0, jpeg.Length);
            ctx.Response.Close();
        }

        private static void ServeMjpeg(HttpListenerContext ctx, CameraStreamState state)
        {
            const string boundary = "jrtiboundary";
            ctx.Response.ContentType = $"multipart/x-mixed-replace; boundary={boundary}";
            ctx.Response.SendChunked = true;

            var clientId = Guid.NewGuid();
            var slot = new LatestFrameSlot();
            state.MjpegClients[clientId] = slot;

            try
            {
                var outStream = ctx.Response.OutputStream;
                var boundaryBytes = Encoding.ASCII.GetBytes($"--{boundary}\r\n");
                var crlf = Encoding.ASCII.GetBytes("\r\n");
                var headerPrefix = Encoding.ASCII.GetBytes("Content-Type: image/jpeg\r\nContent-Length: ");
                var headerSuffix = Encoding.ASCII.GetBytes("\r\n\r\n");

                while (true)
                {
                    var jpeg = slot.Take(30_000);
                    if (jpeg == null)
                        break;

                    var lengthBytes = Encoding.ASCII.GetBytes(jpeg.Length.ToString());

                    outStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                    outStream.Write(headerPrefix, 0, headerPrefix.Length);
                    outStream.Write(lengthBytes, 0, lengthBytes.Length);
                    outStream.Write(headerSuffix, 0, headerSuffix.Length);
                    outStream.Write(jpeg, 0, jpeg.Length);
                    outStream.Write(crlf, 0, crlf.Length);
                    outStream.Flush();
                }
            }
            catch { }
            finally
            {
                state.MjpegClients.TryRemove(clientId, out _);
                slot.Dispose();
                try { ctx.Response.Close(); } catch { }
            }
        }

        private static void ServeText(HttpListenerContext ctx, string text, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            ctx.Response.ContentType = contentType + (contentType.Contains("charset") ? "" : "; charset=utf-8");
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        private static void ServeError(HttpListenerContext ctx, int code, string message)
        {
            ctx.Response.StatusCode = code;
            ServeText(ctx, message, "text/plain");
        }

        private static string EscapeJson(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")
                .Replace("\r", "\\r").Replace("\t", "\\t");

        internal sealed class LatestFrameSlot : IDisposable
        {
            private byte[] _frame;
            private readonly ManualResetEventSlim _signal = new ManualResetEventSlim(false);
            private volatile bool _disposed;

            public void Push(byte[] jpeg)
            {
                if (_disposed) return;
                Interlocked.Exchange(ref _frame, jpeg);
                _signal.Set();
            }

            public byte[] Take(int timeoutMs)
            {
                if (_disposed) return null;
                if (!_signal.Wait(timeoutMs)) return null;
                _signal.Reset();
                return Interlocked.Exchange(ref _frame, null);
            }

            public void Dispose()
            {
                _disposed = true;
                _signal.Set();
            }
        }

        internal sealed class CameraStreamState : IDisposable
        {
            private const float SnapshotInterestDuration = 5f;

            public byte[] LatestJpeg;
            public readonly object JpegLock = new object();

            private volatile float _lastSnapshotInterest;

            public readonly ConcurrentDictionary<Guid, LatestFrameSlot> MjpegClients
                = new ConcurrentDictionary<Guid, LatestFrameSlot>();

            public int MjpegClientCount => MjpegClients.Count;

            public bool HasActiveClients =>
                MjpegClients.Count > 0
                || (Time.unscaledTime - _lastSnapshotInterest < SnapshotInterestDuration);

            public void MarkSnapshotInterest()
            {
                _lastSnapshotInterest = Time.unscaledTime;
            }

            public void PushFrame(byte[] jpeg)
            {
                lock (JpegLock)
                    LatestJpeg = jpeg;

                foreach (var kv in MjpegClients)
                    kv.Value.Push(jpeg);
            }

            public void Dispose()
            {
                foreach (var kv in MjpegClients)
                    kv.Value.Dispose();
            }
        }

        internal sealed class RecordingSession : IDisposable
        {
            public string SessionId { get; }
            public string DisplayPath { get; }
            public long BytesWritten { get; private set; }
            public DateTime LastActivityUtc { get; private set; }

            private readonly FileStream _stream;
            private readonly object _writeLock = new object();
            private bool _disposed;

            private RecordingSession(string sessionId, string path, FileStream stream)
            {
                SessionId = sessionId;
                DisplayPath = path;
                _stream = stream;
                LastActivityUtc = DateTime.UtcNow;
            }

            public static RecordingSession Create(string sessionId, string path)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var finalPath = ResolveUniquePath(path);
                var stream = new FileStream(finalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
                return new RecordingSession(sessionId, finalPath, stream);
            }

            private static string ResolveUniquePath(string requested)
            {
                if (!File.Exists(requested)) return requested;

                var directory = Path.GetDirectoryName(requested);
                var baseName = Path.GetFileNameWithoutExtension(requested);
                var ext = Path.GetExtension(requested);

                for (int i = 1; i < 10000; i++)
                {
                    var candidate = Path.Combine(directory, $"{baseName}_{i}{ext}");
                    if (!File.Exists(candidate)) return candidate;
                }

                return Path.Combine(directory, $"{baseName}_{Guid.NewGuid():N}{ext}");
            }

            public void AppendFromStream(Stream input)
            {
                var buffer = new byte[16 * 1024];
                lock (_writeLock)
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(RecordingSession));
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        _stream.Write(buffer, 0, read);
                        BytesWritten += read;
                    }
                    _stream.Flush();
                    LastActivityUtc = DateTime.UtcNow;
                }
            }

            public void Dispose()
            {
                lock (_writeLock)
                {
                    if (_disposed) return;
                    _disposed = true;
                    try { _stream.Flush(); } catch { }
                    try { _stream.Dispose(); } catch { }
                }
            }

            public void DisposeAndDelete()
            {
                Dispose();
                try
                {
                    if (File.Exists(DisplayPath)) File.Delete(DisplayPath);
                }
                catch { }
            }
        }
    }
}