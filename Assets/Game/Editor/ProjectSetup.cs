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
    private const string Sky = "Assets/Game/Planet/Sky";

    // Slice order of the ground material arrays; GroundMaterials.hlsl names the same slices.
    private static readonly string[] GroundMaterials = { "grass", "forest", "soil", "sand", "rock", "snow" };

    [MenuItem("Max-Q/Rebuild Project Setup")]
    public static void Run() {

        foreach (string folder in new[] { Rendering, Materials, Interface, Path.GetDirectoryName(ScenePath) }) {

            Directory.CreateDirectory(folder);

        }

        ConfigurePipeline();
        ConfigureTextures();
        PlayerSettings.enableFrameTimingStats = true;

        // The sky, the ground and the exposure are lit in physical units, which only display right when encoded to sRGB.
        PlayerSettings.colorSpace = ColorSpace.Linear;

        Material surface = SaveMaterial(new Material(Shader.Find("Universal Render Pipeline/Lit")), "Surface");
        Material line = SaveMaterial(new Material(Shader.Find("MaxQ/MapLine")), "MapLine");
        Material groundTemplate = new Material(Shader.Find("MaxQ/Ground"));
        groundTemplate.SetTexture("_GroundAlbedo", GroundArray("albedo_height", false, "Ground Albedo"));
        groundTemplate.SetTexture("_GroundNormal", GroundArray("normal", true, "Ground Normals"));
        Material ground = SaveMaterial(groundTemplate, "Ground");
        Material rockTemplate = new Material(Shader.Find("MaxQ/Rock")) { enableInstancing = true };
        rockTemplate.SetTexture("_GroundAlbedo", groundTemplate.GetTexture("_GroundAlbedo"));
        rockTemplate.SetTexture("_GroundNormal", groundTemplate.GetTexture("_GroundNormal"));
        Material rock = SaveMaterial(rockTemplate, "Rock");
        Material grass = SaveMaterial(new Material(Shader.Find("MaxQ/Grass")), "Grass");
        Material treeTemplate = new Material(Shader.Find("MaxQ/Tree"));
        treeTemplate.SetTexture("_GroundAlbedo", groundTemplate.GetTexture("_GroundAlbedo"));
        treeTemplate.SetTexture("_GroundNormal", groundTemplate.GetTexture("_GroundNormal"));
        Material tree = SaveMaterial(treeTemplate, "Tree");
        Material water = SaveMaterial(new Material(Shader.Find("MaxQ/Water")), "Water");

        Material sky = new Material(Shader.Find("Skybox/Panoramic"));
        sky.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>($"{Art}/Sky/stars.png"));
        sky.SetFloat("_Mapping", 1.0f);
        sky.SetFloat("_Exposure", 1.0f);
        sky = SaveMaterial(sky, "Sky");

        BuildScene(surface, line, sky, ground, water, rock, grass, tree);

        AssetDatabase.SaveAssets();
        Debug.Log("Max-Q setup complete");

    }

    private static void ConfigurePipeline() {

        UniversalRendererData renderer = LoadOrCreate($"{Rendering}/Renderer.asset", () => ScriptableObject.CreateInstance<UniversalRendererData>());
        UniversalRenderPipelineAsset pipeline = LoadOrCreate($"{Rendering}/Pipeline.asset", () => UniversalRenderPipelineAsset.Create(renderer));

        // Anti-aliasing is SMAA on the camera: the atmosphere composites over a resolved, single-sample target.
        pipeline.msaaSampleCount = 1;
        pipeline.supportsHDR = true;
        // The sun's cascades; Sun refits their distances to the camera's altitude every frame.
        pipeline.shadowDistance = 100.0f;
        pipeline.shadowCascadeCount = 4;
        pipeline.mainLightShadowmapResolution = 4096;
        pipeline.shadowDepthBias = 1.0f;
        pipeline.shadowNormalBias = 1.0f;

        SerializedObject settings = new SerializedObject(pipeline);
        settings.FindProperty("m_MainLightShadowsSupported").boolValue = true;
        settings.FindProperty("m_SoftShadowsSupported").boolValue = true;
        settings.FindProperty("m_SoftShadowQuality").intValue = (int)SoftShadowQuality.High;
        settings.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(pipeline);

        GraphicsSettings.defaultRenderPipeline = pipeline;

        for (int i = 0; i < QualitySettings.names.Length; i++) {

            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = pipeline;

        }

    }

    private static void ConfigureTextures() {

        TextureImporter stars = (TextureImporter)AssetImporter.GetAtPath($"{Art}/Sky/stars.png");
        stars.maxTextureSize = 8192;
        stars.mipmapEnabled = false;
        stars.wrapModeU = TextureWrapMode.Repeat;
        stars.wrapModeV = TextureWrapMode.Clamp;
        stars.textureCompression = TextureImporterCompression.CompressedHQ;
        stars.SaveAndReimport();

    }

    // Packs one map of every ground material into a mipmapped array, copying the compressed imports block for block.
    private static Texture2DArray GroundArray(string map, bool normal, string name) {

        Texture2D[] sources = new Texture2D[GroundMaterials.Length];

        for (int i = 0; i < sources.Length; i++) {

            string path = $"{Art}/Ground/{GroundMaterials[i]}_{map}.png";
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);

            importer.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = !normal;
            importer.isReadable = true;
            importer.mipmapEnabled = true;
            importer.maxTextureSize = 1024;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();

            sources[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

        }

        Texture2D first = sources[0];
        Texture2DArray array = new Texture2DArray(first.width, first.height, sources.Length, first.format, true, normal) {

            name = name,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 8,

        };

        for (int slice = 0; slice < sources.Length; slice++) {

            for (int mip = 0; mip < first.mipmapCount; mip++) {

                array.SetPixelData(sources[slice].GetPixelData<byte>(mip), mip, slice);

            }

        }

        array.Apply(false, false);

        string target = $"{Materials}/{name}.asset";
        Texture2DArray existing = AssetDatabase.LoadAssetAtPath<Texture2DArray>(target);

        if (existing == null) {

            AssetDatabase.CreateAsset(array, target);

            return array;

        }

        EditorUtility.CopySerialized(array, existing);
        EditorUtility.SetDirty(existing);

        return existing;

    }

    private static void BuildScene(Material surface, Material line, Material sky, Material ground, Material water, Material rock, Material grass, Material tree) {

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        GameObject cameraObject = new GameObject("Map Camera") { tag = "MainCamera" };
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.fieldOfView = 50.0f;

        UniversalAdditionalCameraData cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
        cameraData.renderPostProcessing = true;
        cameraData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        cameraData.antialiasingQuality = AntialiasingQuality.High;

        // Sunlit ground, a daylit sky and the sun's disk span a wide range; neutral tonemapping keeps hues.
        VolumeProfile profile = LoadOrCreate($"{Rendering}/Map Volume.asset", () => ScriptableObject.CreateInstance<VolumeProfile>());

        if (!profile.TryGet(out Tonemapping tonemapping)) {

            tonemapping = profile.Add<Tonemapping>();
            AssetDatabase.AddObjectToAsset(tonemapping, profile);

        }

        tonemapping.mode.Override(TonemappingMode.Neutral);
        EditorUtility.SetDirty(profile);

        Volume volume = new GameObject("Post Processing").AddComponent<Volume>();
        volume.isGlobal = true;
        volume.sharedProfile = profile;

        PanelSettings panel = LoadOrCreate($"{Interface}/Hud Panel.asset", () => ScriptableObject.CreateInstance<PanelSettings>());
        panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        panel.referenceResolution = new Vector2Int(1920, 1080);
        panel.match = 0.5f;
        panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>($"{Interface}/Runtime.tss");
        EditorUtility.SetDirty(panel);

        GameObject map = new GameObject("Map");
        map.AddComponent<UIDocument>().panelSettings = panel;

        SerializedObject view = new SerializedObject(map.AddComponent<MapView>());
        SetArray(view.FindProperty("_seleneFaces"), Faces("Selene"));
        view.FindProperty("_surfaceMaterial").objectReferenceValue = surface;
        view.FindProperty("_groundMaterial").objectReferenceValue = ground;
        view.FindProperty("_waterMaterial").objectReferenceValue = water;
        view.FindProperty("_rockMaterial").objectReferenceValue = rock;
        view.FindProperty("_grassMaterial").objectReferenceValue = grass;
        view.FindProperty("_treeMaterial").objectReferenceValue = tree;
        view.FindProperty("_vegetation").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Game/Planet/Ground/Vegetation.compute");
        view.FindProperty("_atmosphereTables").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Shader>($"{Sky}/AtmosphereLuts.shader");
        view.FindProperty("_atmosphereSky").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Shader>($"{Sky}/AtmosphereSky.shader");
        view.FindProperty("_exposure").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>($"{Sky}/Exposure.compute");
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
        existing.enableInstancing = material.enableInstancing;
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
