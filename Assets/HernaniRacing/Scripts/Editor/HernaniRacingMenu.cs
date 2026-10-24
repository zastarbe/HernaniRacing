using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HernaniRacing.EditorTools
{
    public static class HernaniRacingMenu
    {
        const string ScenePath = "Assets/HernaniRacing/Scenes/Hernani.unity";

        [MenuItem("Hernani Racing/1. Crear escena de juego", false, 1)]
        public static void CreateScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var go = new GameObject("Hernani Racing");
            go.AddComponent<GameBootstrap>();
            EditorSceneManager.SaveScene(scene, ScenePath);

            var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!list.Exists(s => s.path == ScenePath))
            {
                list.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
                EditorBuildSettings.scenes = list.ToArray();
            }
            IncludeShaders();
            Debug.Log("[Hernani Racing] Escena creada en " + ScenePath + ". Pulsa Play.");
        }

        [MenuItem("Hernani Racing/2. Incluir shaders para builds", false, 2)]
        public static void IncludeShaders()
        {
            // Los materiales se crean en código, así que los shaders deben ir siempre en el build.
            // (también los del terreno: se crea en tiempo de ejecución y sin ellos el suelo sale invisible en el .exe)
            var names = new[]
            {
                "Standard", "Unlit/Color", "Universal Render Pipeline/Lit", "Universal Render Pipeline/Unlit",
                "Universal Render Pipeline/Simple Lit", "Universal Render Pipeline/Terrain/Lit",
                "Hidden/Universal Render Pipeline/Terrain/Lit (Add Pass)",
                "Hidden/Universal Render Pipeline/Terrain/Lit (Base Pass)",
                "Hidden/Universal Render Pipeline/Terrain/Lit (Basemap Gen)",
                "Nature/Terrain/Standard", "Nature/Terrain/Diffuse",
                "Hidden/TerrainEngine/Splatmap/Standard-AddPass", "Hidden/TerrainEngine/Splatmap/Standard-Base",
                "Hidden/TerrainEngine/Splatmap/Standard-BaseGen",
                "Hidden/TerrainEngine/Splatmap/Diffuse-AddPass", "Hidden/TerrainEngine/Splatmap/Diffuse-Base",
                "Hidden/TerrainEngine/Splatmap/Diffuse-BaseGen",
                "Hidden/TerrainEngine/CrossBlendNeighbors", "Hidden/TerrainEngine/HeightBlitCopy",
                "Hidden/TerrainEngine/GenerateNormalmap", "Hidden/TerrainEngine/TerrainLayerUtils",
                "Hidden/TerrainEngine/PaintHeight", "Hidden/TerrainEngine/BrushPreview"
            };
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (assets == null || assets.Length == 0) { Debug.LogWarning("No se encontró ProjectSettings/GraphicsSettings.asset"); return; }
            var so = new SerializedObject(assets[0]);
            var arr = so.FindProperty("m_AlwaysIncludedShaders");
            if (arr == null) { Debug.LogWarning("No se pudo acceder a Always Included Shaders"); return; }
            int added = 0;
            foreach (var n in names)
            {
                var sh = Shader.Find(n);
                if (sh == null) continue;
                bool exists = false;
                for (int i = 0; i < arr.arraySize; i++)
                    if (arr.GetArrayElementAtIndex(i).objectReferenceValue == sh) { exists = true; break; }
                if (exists) continue;
                arr.InsertArrayElementAtIndex(arr.arraySize);
                arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = sh;
                added++;
            }
            if (added > 0)
            {
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
            }
            Debug.Log("[Hernani Racing] Shaders añadidos a 'Always Included': " + added);
        }

        /// <summary>
        /// Ajustes recomendados para la versión web (navegador): Gzip (Brotli sólo va por https), 4 GB de memoria máxima,
        /// stripping mínimo (el terreno se crea por código), caché de datos y la escena en el build.
        /// </summary>
        [MenuItem("Hernani Racing/3. Preparar ajustes para el build web", false, 3)]
        public static void PrepareWeb()
        {
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = false;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.memoryGrowthMode = WebGLMemoryGrowthMode.Geometric;
            PlayerSettings.WebGL.maximumMemorySize = 4096;
            PlayerSettings.SetManagedStrippingLevel(UnityEditor.Build.NamedBuildTarget.WebGL, ManagedStrippingLevel.Minimal);
            PlayerSettings.runInBackground = true;
            if (File.Exists(ScenePath))
            {
                var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
                if (!list.Exists(s => s.path == ScenePath))
                {
                    list.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
                    EditorBuildSettings.scenes = list.ToArray();
                }
            }
            IncludeShaders();
            AssetDatabase.SaveAssets();
            Debug.Log("[Hernani Racing] Ajustes web listos: Gzip, sin fallback, caché de datos, memoria máx. 4096 MB, stripping Minimal.");
            EditorUtility.DisplayDialog("Hernani Racing", "Ajustes del build web preparados:\n\n" +
                "• Compression Format: Gzip\n• Decompression Fallback: no\n• Data Caching: sí\n" +
                "• Maximum Memory Size: 4096 MB\n• Managed Stripping Level: Minimal\n\n" +
                "Ahora: File → Build Profiles → Web → Switch Platform → Build.", "OK");
        }

        [MenuItem("Hernani Racing/4. Regenerar datos geográficos (Python)", false, 20)]
        public static void RegenerateData()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string script = Path.Combine(root, "DataPipeline", "build_hernani_data.py");
            if (!File.Exists(script))
            {
                EditorUtility.DisplayDialog("Hernani Racing", "No se encuentra " + script +
                    "\nCopia la carpeta DataPipeline junto a Assets.", "OK");
                return;
            }
            string py = Application.platform == RuntimePlatform.WindowsEditor ? "py" : "python3";
            var psi = new System.Diagnostics.ProcessStartInfo(py, "\"" + script + "\"")
            {
                WorkingDirectory = Path.GetDirectoryName(script),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            try
            {
                EditorUtility.DisplayProgressBar("Hernani Racing", "Descargando b5m / geoEuskadi y procesando (puede tardar varios minutos)...", 0.5f);
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    var errTask = p.StandardError.ReadToEndAsync();   // evita bloqueos si stderr se llena
                    string output = p.StandardOutput.ReadToEnd();
                    string err = errTask.Result;
                    p.WaitForExit();
                    Debug.Log(output);
                    if (p.ExitCode != 0) Debug.LogError(err);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError("No se pudo ejecutar Python (" + py + "): " + e.Message +
                               "\nEjecuta a mano: python DataPipeline/build_hernani_data.py");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }
        }

        [MenuItem("Hernani Racing/Abrir carpeta de datos", false, 40)]
        public static void OpenData()
        {
            EditorUtility.RevealInFinder(Path.Combine(Application.dataPath, "HernaniRacing", "Resources", "HernaniData", "meta.json"));
        }
    }

    /// <summary>
    /// Antes de cada build (exe, WebGL, Android...) mete los shaders necesarios en "Always Included Shaders":
    /// así nunca falta el suelo aunque no se haya usado el menú 2.
    /// </summary>
    public class HernaniRacingBuildPrep : UnityEditor.Build.IPreprocessBuildWithReport
    {
        public int callbackOrder { get { return 0; } }

        public void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
        {
            HernaniRacingMenu.IncludeShaders();
            if (report.summary.platform == BuildTarget.WebGL)
            {
                if (PlayerSettings.WebGL.compressionFormat == WebGLCompressionFormat.Brotli)
                    Debug.LogWarning("[Hernani Racing] Web: Brotli sólo funciona por https. Para tu servidor (http) usa Gzip: menú Hernani Racing > 3. Preparar ajustes para el build web.");
                if (PlayerSettings.WebGL.maximumMemorySize < 4096)
                    Debug.LogWarning("[Hernani Racing] Web: Maximum Memory Size = " + PlayerSettings.WebGL.maximumMemorySize + " MB; se recomienda 4096 (menú Hernani Racing > 3).");
            }
        }
    }
}
