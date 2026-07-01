// SceneQuery — read-only perception of the Editor: scene hierarchy, object detail, asset search,
// and recent console/compiler messages. Feeds Claude's "what exists / did it compile" checks.
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AgentBridge
{
    public static class SceneQuery
    {
        public static Dictionary<string, object> EditorVersion(Dictionary<string, object> args)
        {
            var pipeline = GraphicsSettings.currentRenderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            string rp = pipeline != null ? pipeline.GetType().Name : "Built-in";
            string family =
                rp.Contains("Universal") ? "URP" :
                rp.Contains("HDRenderPipeline") || rp.Contains("HDRP") ? "HDRP" :
                pipeline == null ? "Built-in" : "SRP";

            // Minimum this bridge requires (FindObjectsByType overloads are 2022.2+).
            const int minMajor = 2022, minMinor = 2;
            bool supported = TryParseVersion(Application.unityVersion, out int major, out int minor)
                && (major > minMajor || (major == minMajor && minor >= minMinor));

            return new Dictionary<string, object>
            {
                { "unity_version", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "render_pipeline", rp },
                { "render_pipeline_family", family },
                { "supported_by_bridge", supported },
                { "min_supported", "2022.2 (Unity 6 fully supported)" },
            };
        }

        static bool TryParseVersion(string version, out int major, out int minor)
        {
            major = minor = 0;
            if (string.IsNullOrEmpty(version)) return false;
            // Handles both "2022.3.10f1" and Unity 6's "6000.0.23f1".
            var parts = version.Split('.');
            if (parts.Length < 2) return false;
            int.TryParse(parts[0], out major);
            int.TryParse(parts[1], out minor);
            if (major >= 6000) { major = 2022; minor = 2; } // Unity 6 numbering >= our floor
            return major > 0;
        }

        public static Dictionary<string, object> SceneInfo(Dictionary<string, object> args)
        {
            var scene = SceneManager.GetActiveScene();
            var data = new Dictionary<string, object>
            {
                { "scene", new Dictionary<string, object>
                    {
                        { "name", scene.name },
                        { "path", scene.path },
                        { "dirty", scene.isDirty },
                        { "root_count", scene.rootCount },
                        { "is_playing", EditorApplication.isPlaying },
                    }
                },
            };

            if (BridgeUtil.GetBool(args, "include_hierarchy", true))
            {
                int cap = BridgeUtil.GetInt(args, "max_objects", 200);
                int count = 0;
                var roots = new List<object>();
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (count >= cap) break;
                    roots.Add(BuildNode(root, cap, ref count));
                }
                data["hierarchy"] = roots;
                data["truncated"] = count >= cap;
            }
            return data;
        }

        static Dictionary<string, object> BuildNode(GameObject go, int cap, ref int count)
        {
            count++;
            var node = new Dictionary<string, object>
            {
                { "id", BridgeUtil.Iid(go) },
                { "name", go.name },
                { "path", BridgeUtil.GetPath(go) },
                { "active", go.activeSelf },
            };
            var children = new List<object>();
            foreach (Transform child in go.transform)
            {
                if (count >= cap) break;
                children.Add(BuildNode(child.gameObject, cap, ref count));
            }
            if (children.Count > 0) node["children"] = children;
            return node;
        }

        public static Dictionary<string, object> Find(Dictionary<string, object> args)
        {
            var name = BridgeUtil.GetString(args, "name");
            var tag = BridgeUtil.GetString(args, "tag");
            var component = BridgeUtil.GetString(args, "component");
            int max = BridgeUtil.GetInt(args, "max_results", 50);

            System.Type compType = null;
            if (!string.IsNullOrEmpty(component))
                compType = BridgeUtil.FindComponentType(component)
                    ?? throw new BridgeException($"No component type named '{component}'.");

            var matches = new List<object>();
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (matches.Count >= max) break;
                if (!string.IsNullOrEmpty(name) && go.name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!string.IsNullOrEmpty(tag) && !go.CompareTag(tag)) continue;
                if (compType != null && go.GetComponent(compType) == null) continue;
                matches.Add(new Dictionary<string, object>
                {
                    { "id", BridgeUtil.Iid(go) },
                    { "name", go.name },
                    { "path", BridgeUtil.GetPath(go) },
                    { "tag", go.tag },
                });
            }
            return new Dictionary<string, object> { { "count", matches.Count }, { "matches", matches } };
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> args)
        {
            var target = BridgeUtil.GetString(args, "target");
            var go = BridgeUtil.ResolveObject(target)
                ?? throw new BridgeException($"No object matches '{target}'.");

            var t = go.transform;
            var data = new Dictionary<string, object>
            {
                { "id", BridgeUtil.Iid(go) },
                { "name", go.name },
                { "path", BridgeUtil.GetPath(go) },
                { "active", go.activeSelf },
                { "tag", go.tag },
                { "layer", go.layer },
                { "transform", new Dictionary<string, object>
                    {
                        { "position", Vec(t.position) },
                        { "rotation", Vec(t.eulerAngles) },
                        { "scale", Vec(t.localScale) },
                    }
                },
            };

            if (BridgeUtil.GetBool(args, "include_components", true))
            {
                var comps = new List<object>();
                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    comps.Add(DumpComponent(comp));
                }
                data["components"] = comps;
            }
            return data;
        }

        static Dictionary<string, object> DumpComponent(Component comp)
        {
            var result = new Dictionary<string, object> { { "type", comp.GetType().Name } };
            var props = new Dictionary<string, object>();
            try
            {
                var so = new SerializedObject(comp);
                var it = so.GetIterator();
                bool enter = true;
                int emitted = 0;
                while (it.NextVisible(enter) && emitted < 40)
                {
                    enter = false;
                    if (it.name == "m_Script") continue;
                    var val = PropValue(it);
                    if (val != null) { props[it.name] = val; emitted++; }
                }
            }
            catch { /* some components resist SerializedObject; skip props */ }
            if (props.Count > 0) result["properties"] = props;
            return result;
        }

        static object PropValue(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.intValue;
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.Float: return p.floatValue;
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Enum:
                    return p.enumDisplayNames != null && p.enumValueIndex >= 0 && p.enumValueIndex < p.enumDisplayNames.Length
                        ? p.enumDisplayNames[p.enumValueIndex] : (object)p.enumValueIndex;
                case SerializedPropertyType.Vector3: return Vec(p.vector3Value);
                case SerializedPropertyType.Vector2: return new List<object> { p.vector2Value.x, p.vector2Value.y };
                case SerializedPropertyType.Color:
                    var c = p.colorValue; return new List<object> { c.r, c.g, c.b, c.a };
                case SerializedPropertyType.ObjectReference:
                    return p.objectReferenceValue != null ? p.objectReferenceValue.name : null;
                default: return null; // skip complex/nested types to keep the payload bounded
            }
        }

        public static Dictionary<string, object> QueryAssets(Dictionary<string, object> args)
        {
            var filter = BridgeUtil.GetString(args, "filter", "");
            int max = BridgeUtil.GetInt(args, "max_results", 100);
            var guids = AssetDatabase.FindAssets(filter);
            var assets = new List<object>();
            foreach (var guid in guids.Take(max))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                assets.Add(new Dictionary<string, object>
                {
                    { "path", path },
                    { "type", AssetDatabase.GetMainAssetTypeAtPath(path)?.Name },
                    { "guid", guid },
                });
            }
            return new Dictionary<string, object> { { "count", assets.Count }, { "total", guids.Length }, { "assets", assets } };
        }

        public static Dictionary<string, object> ConsoleLogs(Dictionary<string, object> args)
        {
            int max = BridgeUtil.GetInt(args, "max_entries", 50);
            var min = BridgeUtil.GetString(args, "min_severity", "log");
            int minRank = Rank(min);

            var entries = AgentBridgeServer.GetConsoleEntries()
                .Where(e => Rank(e.severity) >= minRank)
                .Reverse().Take(max).Reverse()
                .Select(e =>
                {
                    var d = new Dictionary<string, object> { { "severity", e.severity }, { "message", e.message } };
                    if (!string.IsNullOrEmpty(e.stack)) d["stack"] = e.stack;
                    return (object)d;
                })
                .ToList();
            return new Dictionary<string, object> { { "count", entries.Count }, { "logs", entries } };
        }

        static int Rank(string severity)
        {
            switch (severity) { case "error": return 2; case "warning": return 1; default: return 0; }
        }

        static List<object> Vec(Vector3 v) => new List<object> { v.x, v.y, v.z };
    }
}
