// AuthoringOps — the "game developer" verbs. Each maps a symbolic op to real UnityEditor calls,
// wrapped in Undo so the user can Ctrl-Z anything Claude does, and marking the scene dirty so
// changes show + persist. All handlers run on the main thread. Bad input -> BridgeException.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AgentBridge
{
    public static class AuthoringOps
    {
        const string PendingAttachKey = "AgentBridge.PendingAttach";

        // ---- Objects ---------------------------------------------------------

        public static Dictionary<string, object> CreateObject(Dictionary<string, object> args)
        {
            var primitive = (BridgeUtil.GetString(args, "primitive", "empty") ?? "empty").ToLowerInvariant();
            GameObject go = primitive == "empty"
                ? new GameObject()
                : GameObject.CreatePrimitive(ToPrimitive(primitive));

            go.name = BridgeUtil.GetString(args, "name") ?? (primitive == "empty" ? "GameObject" : go.name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + go.name);

            if (BridgeUtil.Has(args, "parent"))
            {
                var parent = BridgeUtil.ResolveObject(BridgeUtil.GetString(args, "parent"));
                if (parent != null) go.transform.SetParent(parent.transform, true);
            }
            if (BridgeUtil.TryGetVector3(args, "position", out var pos)) go.transform.position = pos;
            if (BridgeUtil.TryGetVector3(args, "rotation", out var rot)) go.transform.eulerAngles = rot;
            if (BridgeUtil.TryGetVector3(args, "scale", out var scl)) go.transform.localScale = scl;

            if (BridgeUtil.TryGetVector3(args, "color", out var color))
            {
                var renderer = go.GetComponent<Renderer>();
                if (renderer != null)
                {
                    var mat = new Material(DefaultLitShader());
                    SetColor(mat, new Color(color.x, color.y, color.z));
                    renderer.sharedMaterial = mat;
                }
            }

            MarkDirty(go);
            return ObjectRef(go);
        }

        public static Dictionary<string, object> ModifyObject(Dictionary<string, object> args)
        {
            var go = ResolveTargetOrThrow(args);
            Undo.RecordObject(go, "Modify " + go.name);
            Undo.RecordObject(go.transform, "Modify Transform");
            var applied = new List<object>();

            var newName = BridgeUtil.GetString(args, "name");
            if (newName != null) { go.name = newName; applied.Add("name"); }
            if (BridgeUtil.TryGetVector3(args, "position", out var pos)) { go.transform.position = pos; applied.Add("position"); }
            if (BridgeUtil.TryGetVector3(args, "rotation", out var rot)) { go.transform.eulerAngles = rot; applied.Add("rotation"); }
            if (BridgeUtil.TryGetVector3(args, "scale", out var scl)) { go.transform.localScale = scl; applied.Add("scale"); }
            if (BridgeUtil.Has(args, "parent"))
            {
                var p = BridgeUtil.GetString(args, "parent");
                var parent = string.IsNullOrEmpty(p) ? null : BridgeUtil.ResolveObject(p);
                go.transform.SetParent(parent != null ? parent.transform : null, true);
                applied.Add("parent");
            }
            var tag = BridgeUtil.GetString(args, "tag");
            if (tag != null) { try { go.tag = tag; applied.Add("tag"); } catch { throw new BridgeException($"Tag '{tag}' does not exist in the project (add it in Tags & Layers)."); } }
            if (BridgeUtil.Has(args, "layer")) { go.layer = Mathf.Clamp(BridgeUtil.GetInt(args, "layer"), 0, 31); applied.Add("layer"); }
            if (BridgeUtil.Has(args, "active")) { go.SetActive(BridgeUtil.GetBool(args, "active")); applied.Add("active"); }

            MarkDirty(go);
            var result = ObjectRef(go);
            result["applied"] = applied;
            return result;
        }

        public static Dictionary<string, object> DeleteObject(Dictionary<string, object> args)
        {
            var go = ResolveTargetOrThrow(args);
            var name = go.name;
            var scene = go.scene;
            Undo.DestroyObjectImmediate(go);
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
            return new Dictionary<string, object> { { "deleted", name } };
        }

        // ---- Components ------------------------------------------------------

        public static Dictionary<string, object> AddComponent(Dictionary<string, object> args)
        {
            var go = ResolveTargetOrThrow(args);
            var typeName = RequireString(args, "component");
            var type = BridgeUtil.FindComponentType(typeName)
                ?? throw new BridgeException($"No component type named '{typeName}' found (built-in or user script).");

            var comp = Undo.AddComponent(go, type);
            var warnings = ApplyProperties(comp, BridgeUtil.GetDict(args, "properties"));
            MarkDirty(go);
            var result = ObjectRef(go);
            result["component"] = type.Name;
            if (warnings.Count > 0) result["warnings"] = warnings;
            return result;
        }

        public static Dictionary<string, object> SetComponent(Dictionary<string, object> args)
        {
            var go = ResolveTargetOrThrow(args);
            var typeName = RequireString(args, "component");
            var type = BridgeUtil.FindComponentType(typeName)
                ?? throw new BridgeException($"No component type named '{typeName}' found.");
            var comp = go.GetComponent(type)
                ?? throw new BridgeException($"'{go.name}' has no {type.Name} component to edit.");

            Undo.RecordObject(comp, "Set " + type.Name);
            var props = BridgeUtil.GetDict(args, "properties")
                ?? throw new BridgeException("'properties' is required and must be an object.");
            var warnings = ApplyProperties(comp, props);
            EditorUtility.SetDirty(comp);
            MarkDirty(go);
            var result = new Dictionary<string, object> { { "component", type.Name }, { "applied", props.Keys.ToList() } };
            if (warnings.Count > 0) result["warnings"] = warnings;
            return result;
        }

        static List<object> ApplyProperties(Component comp, Dictionary<string, object> props)
        {
            var warnings = new List<object>();
            if (props == null) return warnings;
            foreach (var kv in props)
            {
                var err = BridgeUtil.SetMember(comp, kv.Key, kv.Value);
                if (err != null) warnings.Add($"{kv.Key}: {err}");
            }
            return warnings;
        }

        // ---- Materials -------------------------------------------------------

        public static Dictionary<string, object> CreateMaterial(Dictionary<string, object> args)
        {
            var name = RequireString(args, "name");
            var shaderName = BridgeUtil.GetString(args, "shader", "Universal Render Pipeline/Lit");
            var shader = Shader.Find(shaderName) ?? DefaultLitShader();
            var mat = new Material(shader);

            if (BridgeUtil.TryGetVector3(args, "color", out var c)) SetColor(mat, new Color(c.x, c.y, c.z));
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", BridgeUtil.GetFloat(args, "metallic", 0f));
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", BridgeUtil.GetFloat(args, "smoothness", 0.5f));
            else if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", BridgeUtil.GetFloat(args, "smoothness", 0.5f));

            var folder = BridgeUtil.GetString(args, "save_path", "Assets/Materials");
            EnsureFolder(folder);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}.mat");
            AssetDatabase.CreateAsset(mat, path);
            AssetDatabase.SaveAssets();

            var result = new Dictionary<string, object> { { "path", path } };
            if (BridgeUtil.Has(args, "assign_to"))
            {
                var go = BridgeUtil.ResolveObject(BridgeUtil.GetString(args, "assign_to"));
                var renderer = go != null ? go.GetComponent<Renderer>() : null;
                if (renderer != null)
                {
                    Undo.RecordObject(renderer, "Assign Material");
                    renderer.sharedMaterial = mat;
                    MarkDirty(go);
                    result["assigned_to"] = BridgeUtil.GetPath(go);
                }
                else result["assign_warning"] = "target has no Renderer";
            }
            return result;
        }

        // ---- Prefabs ---------------------------------------------------------

        public static Dictionary<string, object> CreatePrefab(Dictionary<string, object> args)
        {
            var go = ResolveTargetOrThrow(args);
            var folder = BridgeUtil.GetString(args, "save_path", "Assets/Prefabs");
            EnsureFolder(folder);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{go.name}.prefab");
            bool connect = BridgeUtil.GetBool(args, "connect", true);
            if (connect) PrefabUtility.SaveAsPrefabAssetAndConnect(go, path, InteractionMode.UserAction);
            else PrefabUtility.SaveAsPrefabAsset(go, path);
            return new Dictionary<string, object> { { "path", path } };
        }

        public static Dictionary<string, object> InstantiatePrefab(Dictionary<string, object> args)
        {
            var prefabPath = RequireString(args, "prefab_path");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath)
                ?? throw new BridgeException($"No prefab at '{prefabPath}'.");
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(go, "Instantiate " + go.name);

            if (BridgeUtil.Has(args, "parent"))
            {
                var parent = BridgeUtil.ResolveObject(BridgeUtil.GetString(args, "parent"));
                if (parent != null) go.transform.SetParent(parent.transform, true);
            }
            if (BridgeUtil.TryGetVector3(args, "position", out var pos)) go.transform.position = pos;
            if (BridgeUtil.TryGetVector3(args, "rotation", out var rot)) go.transform.eulerAngles = rot;
            MarkDirty(go);
            return ObjectRef(go);
        }

        // ---- Scripts (async compile; optional post-compile attach) -----------

        public static Dictionary<string, object> CreateScript(Dictionary<string, object> args)
        {
            var name = RequireString(args, "name");
            var content = RequireString(args, "content");
            var folder = BridgeUtil.GetString(args, "save_path", "Assets/Scripts");
            EnsureFolder(folder);
            var path = $"{folder}/{name}.cs";
            File.WriteAllText(path, content);
            AssetDatabase.ImportAsset(path);

            var result = new Dictionary<string, object>
            {
                { "path", path },
                { "note", "Script written; Unity will compile. Call unity_console_logs to confirm a clean compile." },
            };

            if (BridgeUtil.Has(args, "attach_to"))
            {
                var go = BridgeUtil.ResolveObject(BridgeUtil.GetString(args, "attach_to"));
                if (go == null) { result["attach_warning"] = "attach_to target not found"; return result; }
                // Persist across the domain reload the compile triggers; processed by OnScriptsReloaded.
                var pending = LoadPendingAttach();
                pending.Add(new Dictionary<string, object> { { "path", BridgeUtil.GetPath(go) }, { "type", name } });
                SessionState.SetString(PendingAttachKey, Json.Serialize(pending));
                result["attach"] = $"{name} will be added to {BridgeUtil.GetPath(go)} after compilation.";
            }
            AssetDatabase.Refresh();
            return result;
        }

        static List<object> LoadPendingAttach()
        {
            var raw = SessionState.GetString(PendingAttachKey, "");
            if (string.IsNullOrEmpty(raw)) return new List<object>();
            return Json.Deserialize(raw) as List<object> ?? new List<object>();
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        static void OnScriptsReloaded()
        {
            var pending = LoadPendingAttach();
            if (pending.Count == 0) return;
            SessionState.EraseString(PendingAttachKey);
            foreach (var item in pending.OfType<Dictionary<string, object>>())
            {
                var go = BridgeUtil.ResolveObject(Convert.ToString(item["path"]));
                var type = BridgeUtil.FindComponentType(Convert.ToString(item["type"]));
                if (go != null && type != null)
                {
                    Undo.AddComponent(go, type);
                    MarkDirty(go);
                    AgentBridgeServer.LogActivity($"attached {type.Name} to {go.name}");
                }
            }
        }

        // ---- Lighting / environment -----------------------------------------

        public static Dictionary<string, object> SetLighting(Dictionary<string, object> args)
        {
            var applied = new List<object>();
            if (BridgeUtil.TryGetVector3(args, "ambient_color", out var amb))
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(amb.x, amb.y, amb.z);
                applied.Add("ambient_color");
            }
            var skybox = BridgeUtil.GetString(args, "skybox_material");
            if (skybox != null)
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(skybox);
                if (mat == null) throw new BridgeException($"No skybox material at '{skybox}'.");
                RenderSettings.skybox = mat; applied.Add("skybox_material");
            }
            if (BridgeUtil.Has(args, "fog")) { RenderSettings.fog = BridgeUtil.GetBool(args, "fog"); applied.Add("fog"); }
            if (BridgeUtil.TryGetVector3(args, "fog_color", out var fc)) { RenderSettings.fogColor = new Color(fc.x, fc.y, fc.z); applied.Add("fog_color"); }
            if (BridgeUtil.Has(args, "fog_density")) { RenderSettings.fogDensity = BridgeUtil.GetFloat(args, "fog_density"); applied.Add("fog_density"); }

            var sun = RenderSettings.sun ?? Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .FirstOrDefault(l => l.type == LightType.Directional);
            if (sun != null)
            {
                Undo.RecordObject(sun, "Set Lighting");
                Undo.RecordObject(sun.transform, "Set Sun Angle");
                if (BridgeUtil.TryGetVector3(args, "sun_rotation", out var sr)) { sun.transform.eulerAngles = sr; applied.Add("sun_rotation"); }
                if (BridgeUtil.Has(args, "sun_intensity")) { sun.intensity = BridgeUtil.GetFloat(args, "sun_intensity", 1f); applied.Add("sun_intensity"); }
            }
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return new Dictionary<string, object> { { "applied", applied } };
        }

        // ---- Scenes ----------------------------------------------------------

        public static Dictionary<string, object> CreateScene(Dictionary<string, object> args)
        {
            var path = RequireString(args, "path");
            bool additive = BridgeUtil.GetBool(args, "additive", false);
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects,
                additive ? NewSceneMode.Additive : NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, path);
            return new Dictionary<string, object> { { "scene", scene.name }, { "path", path } };
        }

        public static Dictionary<string, object> OpenScene(Dictionary<string, object> args)
        {
            var path = RequireString(args, "path");
            var scene = EditorSceneManager.OpenScene(path);
            return new Dictionary<string, object> { { "scene", scene.name }, { "path", path } };
        }

        public static Dictionary<string, object> SaveScene(Dictionary<string, object> args)
        {
            var scene = SceneManager.GetActiveScene();
            var path = BridgeUtil.GetString(args, "path");
            bool ok = string.IsNullOrEmpty(path)
                ? EditorSceneManager.SaveScene(scene)
                : EditorSceneManager.SaveScene(scene, path);
            if (!ok) throw new BridgeException("Save failed (scene may be untitled — pass a 'path').");
            return new Dictionary<string, object> { { "scene", scene.name }, { "path", string.IsNullOrEmpty(path) ? scene.path : path } };
        }

        // ---- Editor niceties -------------------------------------------------

        public static Dictionary<string, object> Select(Dictionary<string, object> args)
        {
            var go = ResolveTargetOrThrow(args);
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            return ObjectRef(go);
        }

        public static Dictionary<string, object> Focus(Dictionary<string, object> args)
        {
            var go = ResolveTargetOrThrow(args);
            Selection.activeGameObject = go;
            if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
            return ObjectRef(go);
        }

        public static Dictionary<string, object> ExecuteMenu(Dictionary<string, object> args)
        {
            var menu = RequireString(args, "menu_path");
            bool executed = EditorApplication.ExecuteMenuItem(menu);
            return new Dictionary<string, object> { { "menu_path", menu }, { "executed", executed } };
        }

        public static Dictionary<string, object> UndoOp()
        {
            UnityEditor.Undo.PerformUndo();
            return new Dictionary<string, object> { { "status", "undone" } };
        }

        public static Dictionary<string, object> RedoOp()
        {
            UnityEditor.Undo.PerformRedo();
            return new Dictionary<string, object> { { "status", "redone" } };
        }

        public static Dictionary<string, object> Play()
        {
            EditorApplication.isPlaying = true;
            return new Dictionary<string, object> { { "status", "entering play mode" } };
        }

        public static Dictionary<string, object> Stop()
        {
            EditorApplication.isPlaying = false;
            return new Dictionary<string, object> { { "status", "exiting play mode" } };
        }

        // ---- Phase 1: richer authoring ---------------------------------------

        static NavMeshDataInstance _navInstance;

        public static Dictionary<string, object> BakeNavMesh(Dictionary<string, object> args)
        {
            var settings = NavMesh.CreateSettings();
            settings.agentRadius = BridgeUtil.GetFloat(args, "agent_radius", 0.5f);
            settings.agentHeight = BridgeUtil.GetFloat(args, "agent_height", 2f);
            settings.agentSlope = BridgeUtil.GetFloat(args, "agent_slope", 45f);
            settings.agentClimb = BridgeUtil.GetFloat(args, "agent_climb", 0.4f);

            float extent = BridgeUtil.GetFloat(args, "size", 500f);
            var bounds = new Bounds(Vector3.zero, new Vector3(extent, extent, extent));
            var markups = new List<NavMeshBuildMarkup>();
            var sources = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(bounds, ~0, NavMeshCollectGeometry.RenderMeshes, 0, markups, sources);
            var data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);

            if (_navInstance.valid) NavMesh.RemoveNavMeshData(_navInstance);
            _navInstance = NavMesh.AddNavMeshData(data);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            InternalEditorUtility.RepaintAllViews();
            return new Dictionary<string, object>
            {
                { "sources_collected", sources.Count },
                { "note", "NavMesh baked from render meshes. Add NavMeshAgent components to move agents on it." },
            };
        }

        public static Dictionary<string, object> CreateTerrain(Dictionary<string, object> args)
        {
            int res = BridgeUtil.GetInt(args, "resolution", 513); // must be 2^n + 1
            float width = BridgeUtil.GetFloat(args, "width", 100f);
            float length = BridgeUtil.GetFloat(args, "length", 100f);
            float maxHeight = BridgeUtil.GetFloat(args, "height", 30f);

            var td = new TerrainData { heightmapResolution = res, size = new Vector3(width, maxHeight, length) };
            EnsureFolder("Assets/Terrains");
            var dataPath = AssetDatabase.GenerateUniqueAssetPath("Assets/Terrains/TerrainData.asset");
            AssetDatabase.CreateAsset(td, dataPath);

            var go = Terrain.CreateTerrainGameObject(td);
            go.name = BridgeUtil.GetString(args, "name") ?? "Terrain";
            Undo.RegisterCreatedObjectUndo(go, "Create Terrain");
            if (BridgeUtil.TryGetVector3(args, "position", out var pos)) go.transform.position = pos;
            AssetDatabase.SaveAssets();
            MarkDirty(go);
            var result = ObjectRef(go);
            result["terrain_data"] = dataPath;
            return result;
        }

        public static Dictionary<string, object> CreateUI(Dictionary<string, object> args)
        {
            var element = (BridgeUtil.GetString(args, "element", "text") ?? "text").ToLowerInvariant();
            var menus = UiMenuPaths(element)
                ?? throw new BridgeException($"Unknown UI element '{element}'. Use text/button/image/raw_image/slider/panel/canvas.");
            // ExecuteMenuItem lets Unity wire up Canvas + EventSystem with no UGUI compile dependency.
            // Menu paths differ by version (Unity 6.5 uses "UI (Canvas)"), so try known variants.
            string used = null;
            foreach (var m in menus)
                if (EditorApplication.ExecuteMenuItem(m)) { used = m; break; }
            if (used == null)
                throw new BridgeException($"Could not create '{element}' (tried: {string.Join(", ", menus)}). Is com.unity.ugui installed?");
            var go = Selection.activeGameObject
                ?? throw new BridgeException("UI element was created but Unity did not return it as the active selection.");

            var name = BridgeUtil.GetString(args, "name");
            if (name != null) go.name = name;
            var text = BridgeUtil.GetString(args, "text");
            if (text != null) SetUiText(go, text);
            if (BridgeUtil.TryGetVector3(args, "position", out var p))
            {
                var rt = go.GetComponent<RectTransform>();
                if (rt != null) rt.anchoredPosition = new Vector2(p.x, p.y);
            }
            MarkDirty(go);
            var result = ObjectRef(go);
            result["element"] = element;
            return result;
        }

        static string[] UiMenuPaths(string element)
        {
            // First entry = Unity 6.5 (uGUI 2.5) "UI (Canvas)" submenu; rest = older-version fallbacks.
            switch (element)
            {
                case "text": return new[] { "GameObject/UI (Canvas)/Legacy/Text", "GameObject/UI/Legacy/Text", "GameObject/UI/Text" };
                case "button": return new[] { "GameObject/UI (Canvas)/Legacy/Button", "GameObject/UI/Legacy/Button", "GameObject/UI/Button" };
                case "image": return new[] { "GameObject/UI (Canvas)/Image", "GameObject/UI/Image" };
                case "raw_image": return new[] { "GameObject/UI (Canvas)/Raw Image", "GameObject/UI/Raw Image" };
                case "slider": return new[] { "GameObject/UI (Canvas)/Slider", "GameObject/UI/Slider" };
                case "panel": return new[] { "GameObject/UI (Canvas)/Panel", "GameObject/UI/Panel" };
                case "canvas": return new[] { "GameObject/UI (Canvas)/Canvas", "GameObject/UI/Canvas" };
                default: return null;
            }
        }

        static void SetUiText(GameObject go, string text)
        {
            // Set the label reflectively so we don't need a compile-time UGUI/TMP reference.
            foreach (var comp in go.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                var n = comp.GetType().Name;
                if (n == "Text" || n == "TextMeshProUGUI" || n == "TMP_Text")
                {
                    BridgeUtil.SetMember(comp, "text", text);
                    return;
                }
            }
        }

        public static Dictionary<string, object> AddAudio(Dictionary<string, object> args)
        {
            var go = ResolveTargetOrThrow(args);
            var src = go.GetComponent<AudioSource>();
            if (src == null) src = Undo.AddComponent(go, typeof(AudioSource)) as AudioSource;
            if (src == null) throw new BridgeException("Could not add an AudioSource to " + go.name + ".");
            Undo.RecordObject(src, "Configure Audio");

            var clipPath = BridgeUtil.GetString(args, "clip_path");
            if (clipPath != null)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath)
                    ?? throw new BridgeException($"No AudioClip at '{clipPath}'.");
                src.clip = clip;
            }
            if (BridgeUtil.Has(args, "play_on_awake")) src.playOnAwake = BridgeUtil.GetBool(args, "play_on_awake");
            if (BridgeUtil.Has(args, "loop")) src.loop = BridgeUtil.GetBool(args, "loop");
            if (BridgeUtil.Has(args, "spatial_blend")) src.spatialBlend = BridgeUtil.GetFloat(args, "spatial_blend");
            if (BridgeUtil.Has(args, "volume")) src.volume = BridgeUtil.GetFloat(args, "volume", 1f);
            MarkDirty(go);
            var result = ObjectRef(go);
            result["has_clip"] = src.clip != null;
            return result;
        }

        public static Dictionary<string, object> CreatePhysicsMaterial(Dictionary<string, object> args)
        {
            var name = RequireString(args, "name");
            // Unity 6 renamed PhysicMaterial -> PhysicsMaterial (old name is obsolete-as-error).
            // Resolve by name via reflection so this compiles on both.
            var type = BridgeUtil.FindType("UnityEngine.PhysicsMaterial") ?? BridgeUtil.FindType("PhysicsMaterial")
                       ?? BridgeUtil.FindType("UnityEngine.PhysicMaterial") ?? BridgeUtil.FindType("PhysicMaterial")
                       ?? throw new BridgeException("No PhysicsMaterial/PhysicMaterial type available.");
            var mat = Activator.CreateInstance(type);
            ((Object)mat).name = name;
            SetFloatProp(mat, "dynamicFriction", BridgeUtil.GetFloat(args, "dynamic_friction", 0.6f));
            SetFloatProp(mat, "staticFriction", BridgeUtil.GetFloat(args, "static_friction", 0.6f));
            SetFloatProp(mat, "bounciness", BridgeUtil.GetFloat(args, "bounciness", 0f));

            EnsureFolder("Assets/PhysicsMaterials");
            var path = AssetDatabase.GenerateUniqueAssetPath($"Assets/PhysicsMaterials/{name}.physicMaterial");
            AssetDatabase.CreateAsset((Object)mat, path);
            AssetDatabase.SaveAssets();

            var result = new Dictionary<string, object> { { "path", path } };
            if (BridgeUtil.Has(args, "assign_to"))
            {
                var go = BridgeUtil.ResolveObject(BridgeUtil.GetString(args, "assign_to"));
                var col = go != null ? go.GetComponent<Collider>() : null;
                if (col != null)
                {
                    Undo.RecordObject(col, "Assign PhysicsMaterial");
                    typeof(Collider).GetProperty("sharedMaterial")?.SetValue(col, mat);
                    MarkDirty(go);
                    result["assigned_to"] = BridgeUtil.GetPath(go);
                }
                else result["assign_warning"] = "target has no Collider";
            }
            return result;
        }

        static void SetFloatProp(object target, string name, float value)
        {
            var p = target.GetType().GetProperty(name);
            if (p != null && p.CanWrite) p.SetValue(target, value);
        }

        // ---- Helpers ---------------------------------------------------------

        static GameObject ResolveTargetOrThrow(Dictionary<string, object> args)
        {
            var target = RequireString(args, "target");
            return BridgeUtil.ResolveObject(target)
                ?? throw new BridgeException($"No object matches '{target}' (try an instance id or 'Parent/Child' path from unity_find).");
        }

        static string RequireString(Dictionary<string, object> args, string key)
        {
            var v = BridgeUtil.GetString(args, key);
            if (string.IsNullOrEmpty(v)) throw new BridgeException($"'{key}' is required.");
            return v;
        }

        static Dictionary<string, object> ObjectRef(GameObject go)
        {
            return new Dictionary<string, object>
            {
                { "id", BridgeUtil.Iid(go) },
                { "name", go.name },
                { "path", BridgeUtil.GetPath(go) },
            };
        }

        static void MarkDirty(GameObject go)
        {
            if (go != null && go.scene.IsValid()) EditorSceneManager.MarkSceneDirty(go.scene);
            InternalEditorUtility.RepaintAllViews(); // make the change show in the Scene view immediately
        }

        static PrimitiveType ToPrimitive(string name)
        {
            switch (name)
            {
                case "cube": return PrimitiveType.Cube;
                case "sphere": return PrimitiveType.Sphere;
                case "capsule": return PrimitiveType.Capsule;
                case "cylinder": return PrimitiveType.Cylinder;
                case "plane": return PrimitiveType.Plane;
                case "quad": return PrimitiveType.Quad;
                default: throw new BridgeException($"Unknown primitive '{name}'.");
            }
        }

        static Shader DefaultLitShader()
        {
            return Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? Shader.Find("Sprites/Default");
        }

        static void SetColor(Material mat, Color c)
        {
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
            else mat.color = c;
        }

        static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            if (parts.Length == 0 || parts[0] != "Assets")
                throw new BridgeException($"Folder must be under 'Assets': got '{folder}'.");
            var current = "Assets";
            for (int i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
