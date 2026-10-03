using System.IO;

using MaxQ.Game.Map;

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MaxQ.Game.Editor;

/// <summary>Rebuilds the render pipeline, materials and map scene from source. Safe to re-run.</summary>
public static class ProjectSetup {

    private const string Rendering = "Assets/Game/Settings/Rendering";
    private const string Materials = "Assets/Game/Settings/Materials";
    private const string Art = "Assets/Game/Art";
    private const string ScenePath = "Assets/Game/Scenes/Map.unity";
    private const string Sky = "Assets/Game/Planet/Sky";
    private const string Water = "Assets/Game/Planet/Water";
    private const string Vessels = "Assets/Game/Vessels";

    // Slice order of the ground material arrays; GroundMaterials.hlsl names the same slices, Regolith.shader the last.
    private static readonly string[] GroundMaterials = { "grass", "forest", "soil", "sand", "rock", "snow", "regolith" };

    [MenuItem("Max-Q/Rebuild Project Setup")]
    public static void Run() {

        foreach (string folder in new[] { Rendering, Materials, Path.GetDirectoryName(ScenePath) }) {

            Directory.CreateDirectory(folder);

        }

        ConfigurePipeline();
        ConfigureTextures();
        PlayerSettings.enableFrameTimingStats = true;

        // The sky, the ground and the exposure are lit in physical units, which only display right when encoded to sRGB.
        PlayerSettings.colorSpace = ColorSpace.Linear;

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
        treeTemplate.SetTexture("_Foliage", FoliageAtlas.Bake($"{Materials}/Foliage.asset"));
        treeTemplate.SetTexture("_GroundAlbedo", groundTemplate.GetTexture("_GroundAlbedo"));
        Material tree = SaveMaterial(treeTemplate, "Tree");
        Material water = SaveMaterial(new Material(Shader.Find("MaxQ/Water")), "Water");
        Material regolithTemplate = new Material(Shader.Find("MaxQ/Regolith"));
        regolithTemplate.SetTexture("_GroundAlbedo", groundTemplate.GetTexture("_GroundAlbedo"));
        regolithTemplate.SetTexture("_GroundNormal", groundTemplate.GetTexture("_GroundNormal"));
        Material regolith = SaveMaterial(regolithTemplate, "Regolith");
        Material boulderTemplate = new Material(Shader.Find("MaxQ/Boulder")) { enableInstancing = true };
        boulderTemplate.SetTexture("_GroundAlbedo", groundTemplate.GetTexture("_GroundAlbedo"));
        boulderTemplate.SetTexture("_GroundNormal", groundTemplate.GetTexture("_GroundNormal"));
        Material boulder = SaveMaterial(boulderTemplate, "Boulder");

        (Texture2D detailAlbedo, Texture2D detailNormal) = HullTextures.Bake(Materials);
        Material[] finishes = {

            SaveMaterial(Hull(new Color(0.8f, 0.8f, 0.78f), 0.0f, 0.45f, detailAlbedo, detailNormal), "Hull Paint"),
            SaveMaterial(Hull(new Color(0.9f, 0.9f, 0.91f), 1.0f, 0.62f, detailAlbedo, detailNormal), "Hull Metal"),
            SaveMaterial(Hull(new Color(0.045f, 0.045f, 0.05f), 0.0f, 0.35f, detailAlbedo, detailNormal), "Hull Dark"),

        };

        // PICA-style ablator, charcoal and matte; window panes, near black and glassy.
        Material shield = SaveMaterial(Hull(new Color(0.055f, 0.048f, 0.042f), 0.0f, 0.12f, detailAlbedo, detailNormal), "Heat Shield");
        Material glass = SaveMaterial(Hull(new Color(0.02f, 0.022f, 0.026f), 0.0f, 0.95f, null, null), "Window");

        // Every jet layer shares one material, and every engine's exhaust another; their looks come from the catalogue.
        Material plume = SaveMaterial(new Material(Shader.Find("MaxQ/Plume")), "Plume");
        Material exhaust = SaveMaterial(new Material(Shader.Find("MaxQ/Exhaust")), "Exhaust");
        Material nozzleGlow = SaveMaterial(new Material(Shader.Find("MaxQ/NozzleGlow")), "Nozzle Glow");

        // The pad: Poly Haven's concrete on a 2 m square, and grey-painted steel.
        Material concrete = SaveMaterial(Concrete(), "Concrete");
        Material padSteel = SaveMaterial(Hull(new Color(0.3f, 0.31f, 0.32f), 0.4f, 0.35f, detailAlbedo, detailNormal), "Pad Steel");

        // Stars dimmed so only the brightest show at daylight exposure.
        Material sky = new Material(Shader.Find("Skybox/Panoramic"));
        sky.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>($"{Art}/Sky/stars.png"));
        sky.SetFloat("_Mapping", 1.0f);
        sky.SetFloat("_Exposure", 0.05f);
        sky = SaveMaterial(sky, "Sky");

        BuildScene(sky, ground, water, rock, grass, tree, regolith, boulder, finishes, shield, glass, plume, exhaust, nozzleGlow, concrete, padSteel);

        AssetDatabase.SaveAssets();
        Debug.Log("Max-Q setup complete");

    }

    private static void ConfigurePipeline() {

        UniversalRendererData renderer = LoadOrCreate($"{Rendering}/Renderer.asset", () => ScriptableObject.CreateInstance<UniversalRendererData>());
        UniversalRenderPipelineAsset pipeline = LoadOrCreate($"{Rendering}/Pipeline.asset", () => UniversalRenderPipelineAsset.Create(renderer));

        // A renderer made from script has no post-processing resources, and without them the volume's effects never run.
        renderer.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
        EditorUtility.SetDirty(renderer);

        // Anti-aliasing is SMAA on the camera: the atmosphere composites over a resolved, single-sample target.
        pipeline.msaaSampleCount = 1;
        pipeline.supportsHDR = true;
        // The sun's cascades; Sun refits their distances to the camera's altitude every frame.
        pipeline.shadowDistance = 100.0f;
        pipeline.shadowCascadeCount = 4;
        pipeline.mainLightShadowmapResolution = 4096;
        pipeline.shadowDepthBias = 1.0f;
        pipeline.shadowNormalBias = 1.0f;
        // An engine lights its stage with its bell, its exhaust and its glowing extension: seven lights on one part.
        pipeline.maxAdditionalLightsCount = 8;

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

    private static void BuildScene(Material sky, Material ground, Material water, Material rock, Material grass, Material tree, Material regolith, Material boulder,
        Material[] finishes, Material shield, Material glass, Material plume, Material exhaust, Material nozzleGlow, Material concrete, Material padSteel) {

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        GameObject cameraObject = new GameObject("Map Camera") { tag = "MainCamera" };
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.fieldOfView = 50.0f;

        UniversalAdditionalCameraData cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
        cameraData.renderPostProcessing = true;
        // Temporal: distant ridges and crater rims are thinner than a pixel, which only accumulating jittered frames resolves.
        cameraData.antialiasing = AntialiasingMode.TemporalAntiAliasing;

        // Sunlit ground, a daylit sky and the sun's disk span a wide range; neutral tonemapping keeps hues.
        VolumeProfile profile = LoadOrCreate($"{Rendering}/Map Volume.asset", () => ScriptableObject.CreateInstance<VolumeProfile>());

        Override<Tonemapping>(profile).mode.Override(TonemappingMode.Neutral);

        // The sun flares in the lens wherever it shows: bloom gathers the light of everything brighter than the threshold
        // (the sun's disk, its glint off water, a plume's core), and the lens flare mirrors it into ghosts across the
        // frame. Both read the image, so whatever hides or reddens the sun (the limb, clouds, the air, the vessel) dims
        // its flare to match. The clamp keeps the disk, tens of thousands of times a lit cloud, from washing out the frame.
        Bloom bloom = Override<Bloom>(profile);
        bloom.threshold.Override(4.0f);
        bloom.intensity.Override(0.06f);
        bloom.scatter.Override(0.75f);
        bloom.clamp.Override(4000.0f);
        bloom.highQualityFiltering.Override(true);

        ScreenSpaceLensFlare flare = Override<ScreenSpaceLensFlare>(profile);
        flare.intensity.Override(0.4f);
        flare.firstFlareIntensity.Override(1.0f);
        flare.secondaryFlareIntensity.Override(0.6f);
        flare.warpedFlareIntensity.Override(0.4f);
        flare.samples.Override(2);
        flare.chromaticAbberationIntensity.Override(0.6f);
        flare.vignetteEffect.Override(0.8f);
        EditorUtility.SetDirty(profile);

        Volume volume = new GameObject("Post Processing").AddComponent<Volume>();
        volume.isGlobal = true;
        volume.sharedProfile = profile;

        SerializedObject view = new SerializedObject(new GameObject("Map").AddComponent<MapView>());
        view.FindProperty("_groundMaterial").objectReferenceValue = ground;
        view.FindProperty("_waterMaterial").objectReferenceValue = water;
        view.FindProperty("_rockMaterial").objectReferenceValue = rock;
        view.FindProperty("_grassMaterial").objectReferenceValue = grass;
        view.FindProperty("_treeMaterial").objectReferenceValue = tree;
        view.FindProperty("_regolithMaterial").objectReferenceValue = regolith;
        view.FindProperty("_boulderMaterial").objectReferenceValue = boulder;
        view.FindProperty("_atmosphereTables").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Shader>($"{Sky}/AtmosphereLuts.shader");
        view.FindProperty("_atmosphereSky").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Shader>($"{Sky}/AtmosphereSky.shader");
        view.FindProperty("_exposure").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>($"{Sky}/Exposure.compute");
        view.FindProperty("_cloudShader").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Shader>($"{Sky}/Clouds/Clouds.shader");
        view.FindProperty("_cloudNoise").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>($"{Sky}/Clouds/CloudNoise.compute");
        view.FindProperty("_waves").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>($"{Water}/Waves/Waves.compute");
        view.FindProperty("_waterCopy").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Shader>($"{Water}/Surface/WaterCopy.shader");
        view.FindProperty("_skyMaterial").objectReferenceValue = sky;
        view.FindProperty("_cameraMotion").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Game/Map/CameraMotion.shader");
        view.FindProperty("_concreteMaterial").objectReferenceValue = concrete;
        view.FindProperty("_padSteelMaterial").objectReferenceValue = padSteel;

        SerializedProperty art = view.FindProperty("_vesselArt");
        art.FindPropertyRelative("Catalogue").objectReferenceValue = AssetDatabase.LoadAssetAtPath<TextAsset>($"{Vessels}/Craft/Catalogue.json");
        art.FindPropertyRelative("Craft").objectReferenceValue = AssetDatabase.LoadAssetAtPath<TextAsset>($"{Vessels}/Craft/Stack.json");
        art.FindPropertyRelative("Shield").objectReferenceValue = shield;
        art.FindPropertyRelative("Glass").objectReferenceValue = glass;
        art.FindPropertyRelative("Plume").objectReferenceValue = plume;
        art.FindPropertyRelative("Exhaust").objectReferenceValue = exhaust;
        art.FindPropertyRelative("NozzleGlow").objectReferenceValue = nozzleGlow;
        Fill(art.FindPropertyRelative("Finishes"), finishes);
        Fill(art.FindPropertyRelative("Models"), VesselModels());

        view.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

    }

    // URP's Lit, white-based, with the hull's tiling detail on a 2 m square.
    private static Material Hull(Color colour, float metallic, float smoothness, Texture2D detailAlbedo, Texture2D detailNormal) {

        Material material = new Material(Shader.Find("Universal Render Pipeline/Lit"));

        material.SetColor("_BaseColor", colour);
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Smoothness", smoothness);
        material.SetTexture("_DetailAlbedoMap", detailAlbedo);
        material.SetTexture("_DetailNormalMap", detailNormal);
        material.SetFloat("_DetailAlbedoMapScale", 1.0f);
        material.SetFloat("_DetailNormalMapScale", 1.0f);
        material.SetTextureScale("_DetailAlbedoMap", new Vector2(0.5f, 0.5f));
        if (detailAlbedo != null) {

            material.EnableKeyword("_DETAIL_MULX2");

        }

        return material;

    }

    // URP's Lit with the concrete maps from Art/Site: albedo, OpenGL normals, and smoothness in the mask's alpha.
    private static Material Concrete() {

        Material material = new Material(Shader.Find("Universal Render Pipeline/Lit"));

        material.SetColor("_BaseColor", Color.white);
        material.SetTexture("_BaseMap", SiteTexture("concrete_albedo", TextureImporterType.Default, true));
        material.SetTexture("_BumpMap", SiteTexture("concrete_normal", TextureImporterType.NormalMap, false));
        material.SetTexture("_MetallicGlossMap", SiteTexture("concrete_mask", TextureImporterType.Default, false));
        material.SetFloat("_Smoothness", 1.0f);
        material.SetTextureScale("_BaseMap", new Vector2(0.5f, 0.5f));
        material.EnableKeyword("_NORMALMAP");
        material.EnableKeyword("_METALLICSPECGLOSSMAP");

        return material;

    }

    private static Texture2D SiteTexture(string name, TextureImporterType type, bool colour) {

        string path = $"{Art}/Site/{name}.png";
        TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);

        importer.textureType = type;
        importer.sRGBTexture = colour;
        importer.mipmapEnabled = true;
        importer.maxTextureSize = 2048;
        importer.anisoLevel = 8;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.textureCompression = TextureImporterCompression.CompressedHQ;
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);

    }

    // Every model imported under the vessel art, found by the catalogue by file name.
    private static Object[] VesselModels() {

        string[] guids = AssetDatabase.FindAssets("t:GameObject", new[] { $"{Art}/Vessel" });

        return System.Array.ConvertAll(guids, guid => (Object)AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid)));

    }

    private static void Fill(SerializedProperty array, Object[] values) {

        array.arraySize = values.Length;

        for (int i = 0; i < values.Length; i++) {

            array.GetArrayElementAtIndex(i).objectReferenceValue = values[i];

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

    private static T Override<T>(VolumeProfile profile) where T : VolumeComponent {

        if (profile.TryGet(out T component)) {

            return component;

        }

        component = profile.Add<T>();
        AssetDatabase.AddObjectToAsset(component, profile);

        return component;

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
