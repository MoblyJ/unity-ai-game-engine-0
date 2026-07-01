// AgentBridgeWindow — the human-visible control panel. Shows listening state, port, connected
// clients, and a live scrolling log of every op Claude executes, so you watch the world get built.
// Open via  Window > Agent Bridge.
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    public class AgentBridgeWindow : EditorWindow
    {
        Vector2 _scroll;

        [MenuItem("Window/Agent Bridge")]
        public static void Open()
        {
            var window = GetWindow<AgentBridgeWindow>("Agent Bridge");
            window.minSize = new Vector2(320, 300);
            window.Show();
        }

        [MenuItem("Window/Agent Bridge/Start Server")]
        public static void StartMenu() => AgentBridgeServer.Start();

        [MenuItem("Window/Agent Bridge/Stop Server")]
        public static void StopMenu() => AgentBridgeServer.Stop();

        void OnEnable() => EditorApplication.update += Repaint; // keep status/log live
        void OnDisable() => EditorApplication.update -= Repaint;

        void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Unity ⇄ MCP Agent Bridge", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                var running = AgentBridgeServer.IsRunning;
                var color = GUI.color;
                GUI.color = running ? Color.green : Color.gray;
                EditorGUILayout.LabelField(running ? "● LISTENING" : "○ stopped", GUILayout.Width(90));
                GUI.color = color;
                EditorGUILayout.LabelField(
                    running ? $"127.0.0.1:{AgentBridgeServer.Port}  ·  clients: {AgentBridgeServer.ClientCount}" : "not started");
            }

            using (new EditorGUI.DisabledScope(AgentBridgeServer.IsRunning))
            {
                var port = EditorGUILayout.IntField("Port", AgentBridgeServer.Port);
                if (port != AgentBridgeServer.Port && port > 0 && port < 65536) AgentBridgeServer.Port = port;
            }
            AgentBridgeServer.AutoStart = EditorGUILayout.Toggle(
                new GUIContent("Auto-start on load", "Start the bridge automatically when Unity opens / after recompiles."),
                AgentBridgeServer.AutoStart);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (AgentBridgeServer.IsRunning)
                {
                    if (GUILayout.Button("Stop")) AgentBridgeServer.Stop();
                }
                else if (GUILayout.Button("Start")) AgentBridgeServer.Start();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Activity", EditorStyles.boldLabel);
            using (var scope = new EditorGUILayout.ScrollViewScope(_scroll, EditorStyles.helpBox))
            {
                _scroll = scope.scrollPosition;
                var log = AgentBridgeServer.GetActivityLog();
                for (int i = log.Length - 1; i >= 0; i--)
                    EditorGUILayout.LabelField(log[i], EditorStyles.miniLabel);
                if (log.Length == 0)
                    EditorGUILayout.LabelField("(no activity yet)", EditorStyles.miniLabel);
            }
        }
    }
}
