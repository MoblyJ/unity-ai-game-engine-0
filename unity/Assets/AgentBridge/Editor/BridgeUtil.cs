// Helpers shared by the dispatcher/ops: typed arg reading, object/type resolution, value coercion.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AgentBridge
{
    public static class BridgeUtil
    {
        // ---- Instance-id helpers, called via reflection ------------------------
        // Unity 6.5 tech-stream marks Object.GetInstanceID()/EditorUtility.InstanceIDToObject as
        // obsolete-AS-ERROR (CS0619, "use GetEntityId/EntityIdToObject"). Reflecting bypasses the
        // compile-time obsolete check while still calling the real (still-present) methods, so this
        // compiles and behaves identically on 2022.3 LTS, 6.0 LTS, and 6.5 alike.
        static MethodInfo _getId;
        static MethodInfo _idToObj;

        public static int Iid(Object o)
        {
            if (o == null) return 0;
            if (_getId == null) _getId = typeof(Object).GetMethod("GetInstanceID", Type.EmptyTypes);
            return (int)_getId.Invoke(o, null);
        }

        public static Object ObjectFromIid(int id)
        {
            if (_idToObj == null)
                _idToObj = typeof(EditorUtility).GetMethod("InstanceIDToObject", new[] { typeof(int) });
            return (Object)_idToObj.Invoke(null, new object[] { id });
        }

        // ---- Typed arg reading (args is a Dictionary<string,object> from MiniJSON) ----

        public static bool Has(Dictionary<string, object> args, string key)
            => args != null && args.ContainsKey(key) && args[key] != null;

        public static string GetString(Dictionary<string, object> args, string key, string def = null)
            => Has(args, key) ? Convert.ToString(args[key], CultureInfo.InvariantCulture) : def;

        public static float GetFloat(Dictionary<string, object> args, string key, float def = 0f)
            => Has(args, key) ? ToFloat(args[key], def) : def;

        public static int GetInt(Dictionary<string, object> args, string key, int def = 0)
            => Has(args, key) ? (int)Math.Round(ToFloat(args[key], def)) : def;

        public static bool GetBool(Dictionary<string, object> args, string key, bool def = false)
        {
            if (!Has(args, key)) return def;
            var v = args[key];
            if (v is bool b) return b;
            if (v is string s) return s == "true" || s == "1";
            return ToFloat(v, def ? 1 : 0) != 0f;
        }

        public static Dictionary<string, object> GetDict(Dictionary<string, object> args, string key)
            => Has(args, key) && args[key] is Dictionary<string, object> d ? d : null;

        public static bool TryGetVector3(Dictionary<string, object> args, string key, out Vector3 v)
        {
            v = Vector3.zero;
            if (!Has(args, key) || !(args[key] is IList list) || list.Count < 3) return false;
            v = new Vector3(ToFloat(list[0], 0), ToFloat(list[1], 0), ToFloat(list[2], 0));
            return true;
        }

        public static float ToFloat(object o, float def = 0f)
        {
            switch (o)
            {
                case null: return def;
                case double d: return (float)d;
                case long l: return l;
                case int i: return i;
                case float f: return f;
                case bool b: return b ? 1f : 0f;
                case string s:
                    return float.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : def;
                default: return def;
            }
        }

        // ---- GameObject resolution: numeric instance id OR "Parent/Child" hierarchy path ----

        public static GameObject ResolveObject(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return null;

            if (int.TryParse(identity, out var id))
            {
                var obj = ObjectFromIid(id);
                if (obj is GameObject go) return go;
                if (obj is Component comp) return comp.gameObject;
            }

            // Hierarchy path lookup across all loaded scenes.
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var found = FindByPath(root, identity);
                    if (found != null) return found;
                }
            }
            // Last resort: unique name match.
            var byName = Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(g => g.name == identity);
            return byName;
        }

        static GameObject FindByPath(GameObject root, string path)
        {
            if (GetPath(root) == path) return root;
            foreach (Transform child in root.transform)
            {
                var res = FindByPath(child.gameObject, path);
                if (res != null) return res;
            }
            return null;
        }

        public static string GetPath(GameObject go)
        {
            if (go == null) return null;
            var path = go.name;
            var t = go.transform.parent;
            while (t != null) { path = t.name + "/" + path; t = t.parent; }
            return path;
        }

        // ---- Type resolution across all loaded assemblies ----

        /// <summary>Find any type by simple or full name (used for version-renamed types like
        /// PhysicsMaterial vs the obsolete PhysicMaterial).</summary>
        public static Type FindType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in types)
                    if (t != null && (t.Name == typeName || t.FullName == typeName)) return t;
            }
            return null;
        }

        public static Type FindComponentType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            var candidates = new List<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in types)
                {
                    if (t == null || !typeof(Component).IsAssignableFrom(t)) continue;
                    if (t.Name == typeName || t.FullName == typeName) candidates.Add(t);
                }
            }
            if (candidates.Count == 0) return null;
            // Prefer UnityEngine built-ins, then the first user type.
            return candidates.FirstOrDefault(t => t.Namespace != null && t.Namespace.StartsWith("UnityEngine"))
                   ?? candidates[0];
        }

        // ---- Set a public field/property by name, coercing the JSON value to its type ----

        public static string SetMember(Component comp, string name, object value)
        {
            var type = comp.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field != null)
            {
                if (!TryCoerce(value, field.FieldType, out var coerced, out var err)) return err;
                field.SetValue(comp, coerced);
                return null;
            }
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop != null && prop.CanWrite)
            {
                if (!TryCoerce(value, prop.PropertyType, out var coerced, out var err)) return err;
                prop.SetValue(comp, coerced);
                return null;
            }
            return $"'{name}' is not a public field/property of {type.Name}";
        }

        public static bool TryCoerce(object value, Type target, out object result, out string error)
        {
            error = null; result = null;
            try
            {
                if (target == typeof(float)) { result = ToFloat(value); return true; }
                if (target == typeof(double)) { result = (double)ToFloat(value); return true; }
                if (target == typeof(int)) { result = (int)Math.Round(ToFloat(value)); return true; }
                if (target == typeof(bool))
                {
                    result = value is bool b ? b : ToFloat(value) != 0f; return true;
                }
                if (target == typeof(string)) { result = Convert.ToString(value, CultureInfo.InvariantCulture); return true; }
                if (target.IsEnum)
                {
                    if (value is string es) { result = Enum.Parse(target, es, true); return true; }
                    result = Enum.ToObject(target, (int)Math.Round(ToFloat(value))); return true;
                }
                if (target == typeof(Vector3) && value is IList v3 && v3.Count >= 3)
                { result = new Vector3(ToFloat(v3[0]), ToFloat(v3[1]), ToFloat(v3[2])); return true; }
                if (target == typeof(Vector2) && value is IList v2 && v2.Count >= 2)
                { result = new Vector2(ToFloat(v2[0]), ToFloat(v2[1])); return true; }
                if (target == typeof(Color) && value is IList col && col.Count >= 3)
                {
                    result = new Color(ToFloat(col[0]), ToFloat(col[1]), ToFloat(col[2]),
                        col.Count >= 4 ? ToFloat(col[3]) : 1f);
                    return true;
                }
                error = $"cannot coerce value to {target.Name}";
                return false;
            }
            catch (Exception e) { error = e.Message; return false; }
        }
    }
}
