using System.IO;
using System.Linq;

using MaxQ.Game.Map;

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

namespace MaxQ.Game.Editor;

/// <summary>Rebuilds the render pipeline, materials and map scene from source. Safe to re-run.</summary>
public static class ProjectSetup {

    private const string Rendering = "Assets/Game/Settings/Rendering";
    private const string Materials = "Assets/Game/Settings/Materials";
    private const string Interface = "Assets/Game/Settings/UI";
    private const string Art = "Assets/Game/Art";
    private const string ScenePath = "Assets/Game/Scenes/Map.unity";

    [MenuItem("Max-Q/Rebuild Project Setup")]
    public static void Run() {

        foreach (string folder in new[] { Rendering, Materials, Interface, Path.GetDirectoryName(ScenePath) }) {

            Directory.CreateDirectory(folder);

        }

        ConfigurePipeline();
        ConfigureTextures();

        Material surface = SaveMaterial(new Material(Shader.Find("Universal Render Pipeline/Lit")), "Surface");
        Material line = SaveMaterial(new Material(Shader.Find("MaxQ/MapLine")), "MapLine");

        Material sky = new Material(Shader.Find("Skybox/Panoramic"));
        sky.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>($"{Art}/Sky/stars.png"));
        sky.SetFloat("_Mapping", 1.0f);
        sky.SetFloat("_Exposure", 1.0f);
        sky = SaveMaterial(sky, "Sky");

        BuildScene(surface, line, sky);

        AssetDatabase.SaveAssets();
        Debug.Log("Max-Q setup complete");

    }

    private static void ConfigurePipeline() {

        UniversalRendererData renderer = LoadOrCreate($"{Rendering}/Renderer.asset", () => ScriptableObject.CreateInstance<UniversalRendererData>());
        UniversalRenderPipelineAsset pipeline = LoadOrCreate($"{Rendering}/Pipeline.asset", () => UniversalRenderPipelineAsset.Create(renderer));

        pipeline.msaaSampleCount = 4;
        pipeline.supportsHDR = true;
        pipeline.shadowDistance = 0.0f;
        EditorUtility.SetDirty(pipeline);

        GraphicsSettings.defaultRenderPipeline = pipeline;

        for (int i = 0; i < QualitySettings.names.Length; i++) {

            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = pipeline;

        }

    }

    private static void ConfigureTextures() {

        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { $"{Art}/Terra", $"{Art}/Selene" })) {

            string path = AssetDatabase.GUIDToAssetPath(guid);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);

            importer.maxTextureSize = 4096;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.anisoLevel = 8;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();

        }

        TextureImporter stars = (TextureImporter)AssetImporter.GetAtPath($"{Art}/Sky/stars.png");
        stars.maxTextureSize = 8192;
        stars.mipmapEnabled = false;
        stars.wrapModeU = TextureWrapMode.Repeat;
        stars.wrapModeV = TextureWrapMode.Clamp;
        stars.textureCompression = TextureImporterCompression.CompressedHQ;
        stars.SaveAndReimport();

    }

    private static void BuildScene(Material surface, Material line, Material sky) {

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        GameObject cameraObject = new GameObject("Map Camera") { tag = "MainCamera" };
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.fieldOfView = 50.0f;
        cameraObject.AddComponent<UniversalAdditionalCameraData>();

        PanelSettings panel = LoadOrCreate($"{Interface}/Hud Panel.asset", () => ScriptableObject.CreateInstance<PanelSettings>());
        panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        panel.referenceResolution = new Vector2Int(1920, 1080);
        panel.match = 0.5f;
        panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>($"{Interface}/Runtime.tss");
        EditorUtility.SetDirty(panel);

        GameObject map = new GameObject("Map");
        map.AddComponent<UIDocument>().panelSettings = panel;

        SerializedObject view = new SerializedObject(map.AddComponent<MapView>());
        SetArray(view.FindProperty("_terraFaces"), Faces("Terra"));
        SetArray(view.FindProperty("_seleneFaces"), Faces("Selene"));
        view.FindProperty("_surfaceMaterial").objectReferenceValue = surface;
        view.FindProperty("_lineMaterial").objectReferenceValue = line;
        view.FindProperty("_skyMaterial").objectReferenceValue = sky;
        view.FindProperty("_hudStyle").objectReferenceValue = AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/Game/Map/Overlay/Hud.uss");
        view.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

    }

    private static Texture2D[] Faces(string body) => Enumerable.Range(0, 6).Select(i => AssetDatabase.LoadAssetAtPath<Texture2D>($"{Art}/{body}/surface_{i}.jpg")).ToArray();

    private static void SetArray(SerializedProperty property, Object[] values) {

        property.arraySize = values.Length;

        for (int i = 0; i < values.Length; i++) {

            property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];

        }

    }

    private static Material SaveMaterial(Material material, string name) {

        string path = $"{Materials}/{name}.mat";
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);

        if (existing == null) {

            AssetDatabase.CreateAsset(material, path);

            return material;

        }

        existing.shader = material.shader;
        existing.CopyPropertiesFromMaterial(material);
        EditorUtility.SetDirty(existing);

        return existing;

    }

    private static T LoadOrCreate<T>(string path, System.Func<T> create) where T : Object {

        T asset = AssetDatabase.LoadAssetAtPath<T>(path);

        if (asset != null) {

            return asset;

        }

        asset = create();
        AssetDatabase.CreateAsset(asset, path);

        return asset;

    }

}
