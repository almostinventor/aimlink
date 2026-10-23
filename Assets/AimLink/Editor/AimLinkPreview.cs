using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AimLink.EditorTools
{
    /// <summary>
    /// Renders the HUD, reticle and a beacon to a PNG without a headset, for checking layout and shaders.
    /// Batch: -executeMethod AimLink.EditorTools.AimLinkPreview.Render -previewOut &lt;file.png&gt;
    /// </summary>
    public static class AimLinkPreview
    {
        [MenuItem("AimLink/Render HUD Preview")]
        public static void Render()
        {
            string outPath = "Builds/hud_preview.png";
            string[] args = System.Environment.GetCommandLineArgs();
            float visorProgress = -1f;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-previewOut") outPath = args[i + 1];
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-visorProgress") float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out visorProgress);

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

            var camGo = new GameObject("PreviewCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            // stand-in for a dim room seen through passthrough
            cam.backgroundColor = new Color(0.32f, 0.3f, 0.28f, 1f);
            cam.fieldOfView = 78f;
            cam.nearClipPlane = 0.05f;
            cam.allowHDR = false;

            // a bright "wall" patch behind the reticle, to check it does not wash out
            var wall = GameObject.CreatePrimitive(PrimitiveType.Quad);
            wall.transform.position = new Vector3(0.25f, 0f, 3.2f);
            wall.transform.localScale = new Vector3(1.2f, 1.6f, 1f);
            var wallMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            wallMat.SetColor("_BaseColor", new Color(0.92f, 0.9f, 0.86f, 1f));
            wall.GetComponent<MeshRenderer>().sharedMaterial = wallMat;

            // ---- HUD ----
            var hudGo = new GameObject("HUD");
            var hud = hudGo.AddComponent<HudController>();
            var so = new SerializedObject(hud);
            so.FindProperty("head").objectReferenceValue = camGo.transform;
            so.ApplyModifiedPropertiesWithoutUndo();
            const BindingFlags priv = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(HudController).GetMethod("BuildCanvas", priv).Invoke(hud, null);
            typeof(HudController).GetMethod("LateUpdate", priv).Invoke(hud, null);
            SetText(hud, "_rangeValue", "4.27<size=45%> M</size>");
            SetText(hud, "_rangeTarget", "WALL");
            hud.Notify("MARK 01 PLACED");
            typeof(HudController).GetMethod("LateUpdate", priv).Invoke(hud, null);

            // ---- reticle + beacon parts ----
            Mesh quad = Quad();
            Material reticle = AssetDatabase.LoadAssetAtPath<Material>("Assets/AimLink/Materials/Reticle.mat");
            Material beacon = AssetDatabase.LoadAssetAtPath<Material>("Assets/AimLink/Materials/Beacon.mat");
            AddQuad("Reticle", quad, new Material(reticle), new Vector3(0f, 0f, 3f), new Vector3(0.157f, 0.157f, 1f));
            var pillarMat = new Material(beacon);
            pillarMat.SetFloat("_Shape", 0f);
            AddQuad("Pillar", quad, pillarMat, new Vector3(-0.55f, 0.35f, 3f), new Vector3(0.03f, 1.3f, 1f));
            var markerMat = new Material(beacon);
            markerMat.SetFloat("_Shape", 1f);
            AddQuad("Marker", quad, markerMat, new Vector3(-0.55f, -0.1f, 3f), new Vector3(0.09f, 0.09f, 1f));

            // optional: the boot block reveal part-way through (-visorProgress 0..1)
            if (visorProgress >= 0f)
            {
                var visorMat = new Material(AssetDatabase.LoadAssetAtPath<Material>("Assets/AimLink/Materials/VisorMask.mat"));
                visorMat.SetFloat("_Progress", visorProgress);
                Mesh visorQuad = Quad();
                visorQuad.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);
                AddQuad("VisorMask", visorQuad, visorMat, Vector3.zero, Vector3.one);
            }

            Canvas.ForceUpdateCanvases();
            foreach (var t in hudGo.GetComponentsInChildren<TMPro.TMP_Text>(true)) t.ForceMeshUpdate();
            Canvas.ForceUpdateCanvases();

            var rt = new RenderTexture(2820, 1950, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Debug.Log("[AimLink] Preview written: " + outPath);
        }

        static void SetText(HudController hud, string field, string value)
        {
            var f = typeof(HudController).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f?.GetValue(hud) is TMPro.TMP_Text t) t.text = value;
        }

        static void AddQuad(string name, Mesh mesh, Material mat, Vector3 pos, Vector3 scale)
        {
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            go.transform.position = pos;
            go.transform.localScale = scale;
        }

        static Mesh Quad()
        {
            var mesh = new Mesh();
            mesh.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f)
            });
            mesh.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) });
            mesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
