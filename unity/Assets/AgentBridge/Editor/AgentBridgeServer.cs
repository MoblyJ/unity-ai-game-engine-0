// AgentBridgeServer — TCP bridge that lets the Python MCP server drive the Unity Editor live.
//
// A background thread accepts clients and reads newline-delimited JSON requests. Because the
// UnityEditor API is main-thread only, each request is queued and executed inside
// EditorApplication.update; the background thread blocks until the main thread produces a
// response, then writes it back on the socket. Envelope matches the MCP side:
//   request : {"id","op","args"}
//   response: {"id","ok":true,"data":{...}}  or  {"id","ok":false,"error":"..."}
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    [InitializeOnLoad]
    public static class AgentBridgeServer
    {
        const string PortPref = "AgentBridge.Port";
        const string AutoStartPref = "AgentBridge.AutoStart";
        const int DefaultPort = 8765;
        const int MainThreadTimeoutMs = 30000;

        static TcpListener _listener;
        static Thread _acceptThread;
        static volatile bool _running;
        static volatile int _clients;

        static readonly ConcurrentQueue<PendingCommand> Queue = new ConcurrentQueue<PendingCommand>();

        // Rolling logs surfaced in the Agent Bridge window.
        static readonly List<string> _activityLog = new List<string>();
        // Ring buffer of Unity console/compiler messages for the console_logs tool.
        static readonly List<ConsoleEntry> _consoleLog = new List<ConsoleEntry>();
        static readonly object _logLock = new object();

        public static bool IsRunning => _running;
        public static int ClientCount => _clients;

        public static int Port
        {
            get => EditorPrefs.GetInt(PortPref, DefaultPort);
            set => EditorPrefs.SetInt(PortPref, value);
        }

        public static bool AutoStart
        {
            get => EditorPrefs.GetBool(AutoStartPref, false);
            set => EditorPrefs.SetBool(AutoStartPref, value);
        }

        static AgentBridgeServer()
        {
            Application.logMessageReceivedThreaded += OnLogMessage;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            // Auto-start if the user toggled it, OR the setup exe dropped an autostart marker at the
            // project root (agentbridge.autostart) — this is how the one-click installer opens comms.
            if (AutoStart || MarkerAutoStart())
                EditorApplication.delayCall += () => Start();
        }

        static bool MarkerAutoStart()
        {
            try
            {
                var marker = System.IO.Path.Combine(Application.dataPath, "..", "agentbridge.autostart");
                return System.IO.File.Exists(marker);
            }
            catch { return false; }
        }

        // ---- Lifecycle -------------------------------------------------------

        public static void Start()
        {
            if (_running) return;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                _running = true;
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "AgentBridgeAccept" };
                _acceptThread.Start();
                EditorApplication.update += Drain;
                LogActivity($"Listening on 127.0.0.1:{Port}");
            }
            catch (Exception e)
            {
                _running = false;
                LogActivity($"Failed to start on port {Port}: {e.Message}");
                Debug.LogError($"[AgentBridge] Failed to start: {e}");
            }
        }

        public static void Stop()
        {
            if (!_running && _listener == null) return;
            _running = false;
            EditorApplication.update -= Drain;
            try { _listener?.Stop(); } catch { /* ignore */ }
            _listener = null;
            _clients = 0;
            // Release any request still waiting on the main thread.
            while (Queue.TryDequeue(out var cmd))
            {
                cmd.ResponseJson = Envelope.Error(cmd.Id, "Bridge stopped");
                cmd.Done.Set();
            }
            LogActivity("Stopped");
        }

        // ---- Networking (background threads) ---------------------------------

        static void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    var t = new Thread(() => ClientLoop(client)) { IsBackground = true, Name = "AgentBridgeClient" };
                    t.Start();
                }
                catch (SocketException) { break; } // listener stopped
                catch (ObjectDisposedException) { break; }
                catch (Exception e) { if (_running) Debug.LogWarning($"[AgentBridge] accept: {e.Message}"); }
            }
        }

        static void ClientLoop(TcpClient client)
        {
            Interlocked.Increment(ref _clients);
            LogActivity("Client connected");
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
                {
                    string line;
                    while (_running && (line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        var cmd = new PendingCommand(line);
                        Queue.Enqueue(cmd);
                        if (!cmd.Done.Wait(MainThreadTimeoutMs))
                            cmd.ResponseJson = Envelope.Error(cmd.Id, "Main-thread execution timed out");
                        writer.WriteLine(cmd.ResponseJson);
                    }
                }
            }
            catch (Exception e) { if (_running) LogActivity($"Client error: {e.Message}"); }
            finally
            {
                Interlocked.Decrement(ref _clients);
                LogActivity("Client disconnected");
            }
        }

        // ---- Main-thread execution ------------------------------------------

        static void Drain()
        {
            int budget = 32; // bound work per editor frame
            while (budget-- > 0 && Queue.TryDequeue(out var cmd))
            {
                try
                {
                    cmd.ResponseJson = CommandDispatcher.Handle(cmd.RequestJson);
                }
                catch (Exception e)
                {
                    cmd.ResponseJson = Envelope.Error(cmd.Id, $"Unhandled: {e.Message}");
                }
                finally
                {
                    cmd.Done.Set();
                }
            }
        }

        // ---- Logging ---------------------------------------------------------

        public static void LogActivity(string message)
        {
            lock (_logLock)
            {
                _activityLog.Add($"{DateTime.Now:HH:mm:ss}  {message}");
                if (_activityLog.Count > 200) _activityLog.RemoveRange(0, _activityLog.Count - 200);
            }
        }

        public static string[] GetActivityLog()
        {
            lock (_logLock) return _activityLog.ToArray();
        }

        static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            lock (_logLock)
            {
                _consoleLog.Add(new ConsoleEntry
                {
                    severity = type == LogType.Error || type == LogType.Exception || type == LogType.Assert
                        ? "error"
                        : type == LogType.Warning ? "warning" : "log",
                    message = condition,
                    stack = type == LogType.Error || type == LogType.Exception ? stackTrace : null,
                });
                if (_consoleLog.Count > 500) _consoleLog.RemoveRange(0, _consoleLog.Count - 500);
            }
        }

        public static List<ConsoleEntry> GetConsoleEntries()
        {
            lock (_logLock) return new List<ConsoleEntry>(_consoleLog);
        }

        public struct ConsoleEntry
        {
            public string severity;
            public string message;
            public string stack;
        }

        sealed class PendingCommand
        {
            public readonly string RequestJson;
            public readonly string Id;
            public string ResponseJson;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);

            public PendingCommand(string requestJson)
            {
                RequestJson = requestJson;
                // Best-effort id extraction so error envelopes echo it even if dispatch never runs.
                Id = TryExtractId(requestJson);
            }

            static string TryExtractId(string json)
            {
                try
                {
                    if (Json.Deserialize(json) is Dictionary<string, object> d && d.TryGetValue("id", out var v))
                        return Convert.ToString(v);
                }
                catch { /* ignore */ }
                return "";
            }
        }
    }
}
