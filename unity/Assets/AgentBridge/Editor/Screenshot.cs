// Screenshot — render the Scene or Game view camera to a PNG so Claude can visually verify what it
// built ("see live what happens") and self-correct. Returned as base64 in the response data.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AgentBridge
{
    public static class Screenshot
    {
        public static Dictionary<string, object> Capture(Dictionary<string, object> args)
        {
            var view = (BridgeUtil.GetString(args, "view", "scene") ?? "scene").ToLowerInvariant();
            int width = Mathf.Clamp(BridgeUtil.GetInt(args, "width", 1280), 64, 4096);
            int height = Mathf.Clamp(BridgeUtil.GetInt(args, "height", 720), 64, 4096);

            Camera cam = view == "game" ? GameCamera() : SceneCamera();
            if (cam == null)
                throw new BridgeException(view == "game"
                    ? "No Game camera found (add a Camera to the scene)."
                    : "No active Scene view to capture (open a Scene view window).");

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                var png = tex.EncodeToPNG();
                return new Dictionary<string, object>
                {
                    { "png_base64", Convert.ToBase64String(png) },
                    { "width", width },
                    { "height", height },
                    { "view", view },
                };
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                if (tex != null) Object.DestroyImmediate(tex);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        static Camera SceneCamera()
        {
            var sv = SceneView.lastActiveSceneView;
            return sv != null ? sv.camera : null;
        }

        static Camera GameCamera()
        {
            if (Camera.main != null) return Camera.main;
            return Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault(c => c.enabled);
        }
    }
}
