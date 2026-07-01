// CommandDispatcher — routes a parsed request envelope to the right op handler and formats the
// response. Every handler runs on the main thread (guaranteed by AgentBridgeServer.Drain).
// Op names here match 1:1 with the Python tool relay in unity_editor_mcp.py.
using System;
using System.Collections.Generic;

namespace AgentBridge
{
    /// <summary>Actionable, non-fatal error the agent can read and retry.</summary>
    public sealed class BridgeException : Exception
    {
        public BridgeException(string message) : base(message) { }
    }

    public static class Envelope
    {
        public static string Ok(string id, Dictionary<string, object> data)
        {
            return Json.Serialize(new Dictionary<string, object>
            {
                { "id", id }, { "ok", true }, { "data", data ?? new Dictionary<string, object>() },
            });
        }

        public static string Error(string id, string message)
        {
            return Json.Serialize(new Dictionary<string, object>
            {
                { "id", id }, { "ok", false }, { "error", message },
            });
        }
    }

    public static class CommandDispatcher
    {
        public static string Handle(string requestJson)
        {
            string id = "";
            try
            {
                if (!(Json.Deserialize(requestJson) is Dictionary<string, object> env))
                    return Envelope.Error(id, "Request was not a JSON object");

                id = env.TryGetValue("id", out var idv) ? Convert.ToString(idv) : "";
                var op = env.TryGetValue("op", out var opv) ? Convert.ToString(opv) : null;
                var args = env.TryGetValue("args", out var av) && av is Dictionary<string, object> d
                    ? d : new Dictionary<string, object>();

                if (string.IsNullOrEmpty(op))
                    return Envelope.Error(id, "Missing 'op'");

                var data = Route(op, args);
                AgentBridgeServer.LogActivity($"{op} ✓");
                return Envelope.Ok(id, data);
            }
            catch (BridgeException be)
            {
                AgentBridgeServer.LogActivity($"error: {be.Message}");
                return Envelope.Error(id, be.Message);
            }
            catch (Exception e)
            {
                AgentBridgeServer.LogActivity($"error: {e.Message}");
                return Envelope.Error(id, $"{e.GetType().Name}: {e.Message}");
            }
        }

        static Dictionary<string, object> Route(string op, Dictionary<string, object> args)
        {
            switch (op)
            {
                // Query / read-only
                case "editor_version": return SceneQuery.EditorVersion(args);
                case "scene_info": return SceneQuery.SceneInfo(args);
                case "find": return SceneQuery.Find(args);
                case "get_object": return SceneQuery.GetObject(args);
                case "query_assets": return SceneQuery.QueryAssets(args);
                case "console_logs": return SceneQuery.ConsoleLogs(args);
                case "screenshot": return Screenshot.Capture(args);

                // Authoring
                case "create_object": return AuthoringOps.CreateObject(args);
                case "modify_object": return AuthoringOps.ModifyObject(args);
                case "delete_object": return AuthoringOps.DeleteObject(args);
                case "add_component": return AuthoringOps.AddComponent(args);
                case "set_component": return AuthoringOps.SetComponent(args);
                case "create_material": return AuthoringOps.CreateMaterial(args);
                case "create_prefab": return AuthoringOps.CreatePrefab(args);
                case "instantiate_prefab": return AuthoringOps.InstantiatePrefab(args);
                case "create_script": return AuthoringOps.CreateScript(args);
                case "set_lighting": return AuthoringOps.SetLighting(args);
                case "bake_navmesh": return AuthoringOps.BakeNavMesh(args);
                case "create_terrain": return AuthoringOps.CreateTerrain(args);
                case "create_ui": return AuthoringOps.CreateUI(args);
                case "add_audio": return AuthoringOps.AddAudio(args);
                case "create_physics_material": return AuthoringOps.CreatePhysicsMaterial(args);
                case "create_scene": return AuthoringOps.CreateScene(args);
                case "open_scene": return AuthoringOps.OpenScene(args);
                case "save_scene": return AuthoringOps.SaveScene(args);
                case "select": return AuthoringOps.Select(args);
                case "focus": return AuthoringOps.Focus(args);
                case "execute_menu": return AuthoringOps.ExecuteMenu(args);
                case "undo": return AuthoringOps.UndoOp();
                case "redo": return AuthoringOps.RedoOp();
                case "play": return AuthoringOps.Play();
                case "stop": return AuthoringOps.Stop();

                default:
                    throw new BridgeException(
                        $"Unknown op '{op}'. See the unity_* tool catalog for valid operations.");
            }
        }
    }
}
