using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Meta.XR;
using Meta.XR.MRUtilityKit;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TextCore.LowLevel;

namespace AimLink.EditorTools
{
    /// <summary>
    /// One-click project setup. Everything here is safe to re-run.
    /// Menu: AimLink/...   Batch: -executeMethod AimLink.EditorTools.AimLinkSetup.&lt;Method&gt;
    /// </summary>
    public static class AimLinkSetup
    {
        const string Root = "Assets/AimLink";
        const string ScenePath = Root + "/Scenes/AimLink.unity";
        const string ApkPath = "Builds/AimLink.apk";
        const string TmpPackage = "Packages/com.unity.ugui/Package Resources/TMP Essential Resources.unitypackage";
        const string CameraRigPrefab = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";
        const string MrukPrefab = "Packages/com.meta.xr.mrutilitykit/Core/Tools/MRUK.prefab";

        // ---------- 1. TextMeshPro resources ----------

        [MenuItem("AimLink/1. Import TMP Essentials")]
        public static void ImportTmpEssentials()
        {
            if (File.Exists("Assets/TextMesh Pro/Resources/TMP Settings.asset"))
            {
                Debug.Log("[AimLink] TMP essentials already present.");
                return;
            }
            AssetDatabase.ImportPackage(TmpPackage, false);
            AssetDatabase.Refresh();
            Debug.Log("[AimLink] TMP essentials import requested.");
        }

        // ---------- 2. assets + scene ----------

        [MenuItem("AimLink/2. Build Assets And Scene")]
        public static void BuildScene()
        {
            foreach (string dir in new[] { "Materials", "Config", "Scenes", "Settings", "Resources/AimLink" })
                Directory.CreateDirectory(Root + "/" + dir);
            AssetDatabase.Refresh();

            SetupRenderPipeline();
            CreateFontAsset("ShareTechMono-Regular.ttf", "AimLinkMono");
            CreateFontAsset("Rajdhani-SemiBold.ttf", "AimLinkLabel");
            Material scanMat = GetOrCreateMaterial("RoomScan", "AimLink/RoomScan");
            Material reticleMat = GetOrCreateMaterial("Reticle", "AimLink/Reticle");
            Material visorMat = GetOrCreateMaterial("VisorMask", "AimLink/VisorMask");
            Material beaconMat = GetOrCreateMaterial("Beacon", "AimLink/Beacon");
            SfxBank bank = GetOrCreateSfxBank();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // --- camera rig ---
            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CameraRigPrefab);
            if (rigPrefab == null) throw new Exception("OVRCameraRig prefab not found at " + CameraRigPrefab);
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab);
            rig.name = "OVRCameraRig";

            var manager = rig.GetComponent<OVRManager>();
            manager.isInsightPassthroughEnabled = true;
            manager.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
            EditorUtility.SetDirty(manager);

            var passthroughLayer = rig.GetComponent<OVRPassthroughLayer>();
            if (passthroughLayer == null) passthroughLayer = rig.AddComponent<OVRPassthroughLayer>();

            Transform centerEye = FindDeep(rig.transform, "CenterEyeAnchor");
            Transform rightController = FindDeep(rig.transform, "RightControllerAnchor");
            Transform leftController = FindDeep(rig.transform, "LeftControllerAnchor");
            if (centerEye == null || rightController == null || leftController == null)
                throw new Exception("OVRCameraRig anchors not found (CenterEyeAnchor / Left+RightControllerAnchor).");

            foreach (Camera cam in rig.GetComponentsInChildren<Camera>(true))
            {
                // transparent black so the passthrough underlay shows through
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.nearClipPlane = 0.05f;
                cam.allowHDR = false;
                EditorUtility.SetDirty(cam);
            }

            // --- muzzle ---
            // aim line runs from the handle controller (right) to the barrel controller (left)
            var muzzleGo = new GameObject("MuzzleTransform");
            var muzzle = muzzleGo.AddComponent<MuzzleTrim>();
            Set(muzzle, "handleController", rightController);
            Set(muzzle, "barrelController", leftController);

            // --- MRUK ---
            var mrukPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(MrukPrefab);
            GameObject mrukGo = mrukPrefab != null ? (GameObject)PrefabUtility.InstantiatePrefab(mrukPrefab) : new GameObject("MRUK");
            var mruk = mrukGo.GetComponent<MRUK>();
            if (mruk == null) mruk = mrukGo.AddComponent<MRUK>();
            if (mruk.SceneSettings == null) mruk.SceneSettings = new MRUK.MRUKSettings();
            mruk.SceneSettings.DataSource = MRUK.SceneDataSource.Device;
            mruk.SceneSettings.LoadSceneOnStartup = true;
            EditorUtility.SetDirty(mruk);
            if (PrefabUtility.IsPartOfPrefabInstance(mruk)) PrefabUtility.RecordPrefabInstancePropertyModifications(mruk);

            var depthGo = new GameObject("EnvironmentRaycast");
            var depth = depthGo.AddComponent<EnvironmentRaycastManager>();

            // --- AimLink ---
            var root = new GameObject("AimLink");
            var fsm = root.AddComponent<AppStateMachine>();
            var input = root.AddComponent<InputRouter>();
            var sfx = root.AddComponent<SfxPlayer>();
            var passthrough = root.AddComponent<PassthroughController>();
            var room = root.AddComponent<RoomScanController>();
            var aim = root.AddComponent<AimRaycaster>();
            var boot = root.AddComponent<BootSequence>();
            var beacons = root.AddComponent<BeaconManager>();
            var scope = root.AddComponent<ScopeController>();

            // forward RGB camera for the scope inset; ScopeController switches it on and off
            var cameraGo = new GameObject("PassthroughCamera");
            var cameraAccess = cameraGo.AddComponent<PassthroughCameraAccess>();
            cameraAccess.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
            cameraAccess.RequestedResolution = new Vector2Int(1280, 960);
            var cameraSo = new SerializedObject(cameraAccess);
            cameraSo.FindProperty("_maxFramerate").intValue = 30;
            cameraSo.ApplyModifiedPropertiesWithoutUndo();

            var reticleGo = new GameObject("Reticle");
            reticleGo.transform.SetParent(root.transform, false);
            var reticle = reticleGo.AddComponent<ReticleController>();

            var hudGo = new GameObject("HUD");
            var hud = hudGo.AddComponent<HudController>();

            Set(fsm, "input", input);
            Set(input, "sfx", sfx);
            Set(sfx, "bank", bank);
            Set(muzzle, "input", input);
            Set(muzzle, "sfx", sfx);

            Set(passthrough, "passthroughLayer", passthroughLayer);
            Set(passthrough, "visorMaskMaterial", visorMat);
            Set(passthrough, "stateMachine", fsm);

            Set(room, "scanMaterial", scanMat);
            Set(room, "head", centerEye);
            Set(room, "stateMachine", fsm);
            Set(room, "input", input);
            Set(room, "sfx", sfx);

            Set(aim, "muzzle", muzzle);
            Set(aim, "room", room);
            Set(aim, "depthRaycaster", depth);

            Set(reticle, "aim", aim);
            Set(reticle, "muzzle", muzzle);
            Set(reticle, "input", input);
            Set(reticle, "head", centerEye);
            Set(reticle, "stateMachine", fsm);
            Set(reticle, "reticleMaterial", reticleMat);

            Set(hud, "head", centerEye);
            Set(hud, "stateMachine", fsm);
            Set(hud, "input", input);
            Set(hud, "muzzle", muzzle);
            Set(hud, "room", room);
            Set(hud, "beacons", beacons);

            Set(scope, "cameraAccess", cameraAccess);
            Set(scope, "aim", aim);
            Set(scope, "hud", hud);
            Set(scope, "input", input);
            Set(scope, "stateMachine", fsm);
            Set(scope, "sfx", sfx);

            Set(beacons, "aim", aim);
            Set(beacons, "head", centerEye);
            Set(beacons, "input", input);
            Set(beacons, "stateMachine", fsm);
            Set(beacons, "sfx", sfx);
            Set(beacons, "beaconMaterial", beaconMat);
            Set(beacons, "ringMaterial", reticleMat);

            Set(boot, "stateMachine", fsm);
            Set(boot, "passthrough", passthrough);
            Set(boot, "room", room);
            Set(boot, "hud", hud);
            Set(boot, "reticle", reticle);
            Set(boot, "muzzle", muzzle);
            Set(boot, "input", input);
            Set(boot, "sfx", sfx);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[AimLink] Scene built: " + ScenePath);
        }

        static void SetupRenderPipeline()
        {
            const string urpPath = Root + "/Settings/AimLinkURP.asset";
            const string rendererPath = Root + "/Settings/AimLinkURP_Renderer.asset";

            var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(urpPath);
            if (urp == null)
            {
                var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(rendererData, rendererPath);
                ResourceReloader.TryReloadAllNullIn(rendererData, UniversalRenderPipelineAsset.packagePath);
                EditorUtility.SetDirty(rendererData);

                urp = UniversalRenderPipelineAsset.Create(rendererData);
                AssetDatabase.CreateAsset(urp, urpPath);
            }
            // HDR and post-processing break the alpha channel passthrough relies on
            urp.supportsHDR = false;
            urp.msaaSampleCount = 4;
            urp.supportsCameraDepthTexture = false;
            urp.supportsCameraOpaqueTexture = false;
            urp.shadowDistance = 0f;
            EditorUtility.SetDirty(urp);

            GraphicsSettings.defaultRenderPipeline = urp;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = urp;
            }
            QualitySettings.SetQualityLevel(current, false);
        }

        static Material GetOrCreateMaterial(string name, string shaderName)
        {
            string path = Root + "/Materials/" + name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            Shader shader = Shader.Find(shaderName);
            if (shader == null) throw new Exception("Shader not found: " + shaderName);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else
            {
                // reset to the shader's current defaults; per-instance values are set from the scripts
                var fresh = new Material(shader);
                mat.shader = shader;
                mat.CopyPropertiesFromMaterial(fresh);
                UnityEngine.Object.DestroyImmediate(fresh);
                EditorUtility.SetDirty(mat);
            }
            return mat;
        }

        // Dynamic SDF font asset in Resources/AimLink, where AimLinkFonts loads it from at runtime.
        static void CreateFontAsset(string ttfFile, string assetName)
        {
            string path = Root + "/Resources/AimLink/" + assetName + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
            if (existing != null)
            {
                StyleFontMaterial(existing.material);
                return;
            }
            var font = AssetDatabase.LoadAssetAtPath<Font>(Root + "/Fonts/" + ttfFile);
            if (font == null) throw new Exception("Font not found: " + ttfFile);

            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            asset.name = assetName;
            AssetDatabase.CreateAsset(asset, path);
            asset.material.name = assetName + " Material";
            asset.atlasTexture.name = assetName + " Atlas";
            AssetDatabase.AddObjectToAsset(asset.material, asset);
            AssetDatabase.AddObjectToAsset(asset.atlasTexture, asset);
            StyleFontMaterial(asset.material);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            Debug.Log("[AimLink] Font asset created: " + path);
        }

        // Soft dark underlay behind every glyph, so HUD text stays readable over a bright wall in passthrough.
        static void StyleFontMaterial(Material mat)
        {
            if (mat == null) return;
            mat.EnableKeyword("UNDERLAY_ON");
            mat.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.7f));
            mat.SetFloat("_UnderlayOffsetX", 0f);
            mat.SetFloat("_UnderlayOffsetY", 0f);
            mat.SetFloat("_UnderlayDilate", 0.6f);
            mat.SetFloat("_UnderlaySoftness", 0.5f);
            EditorUtility.SetDirty(mat);
        }

        static SfxBank GetOrCreateSfxBank()
        {
            const string path = Root + "/Config/SfxBank.asset";
            var bank = AssetDatabase.LoadAssetAtPath<SfxBank>(path);
            if (bank == null)
            {
                bank = ScriptableObject.CreateInstance<SfxBank>();
                AssetDatabase.CreateAsset(bank, path);
            }
            // only fill empty slots, so hand-assigned clips survive a re-run
            bank.bootPowerup = bank.bootPowerup != null ? bank.bootPowerup : Clip("boot_powerup_riser.mp3");
            bank.bootHumLoop = bank.bootHumLoop != null ? bank.bootHumLoop : Clip("boot_hum_loop.wav");
            bank.postTick = bank.postTick != null ? bank.postTick : Clip("post_tick.wav");
            bank.linkConfirm = bank.linkConfirm != null ? bank.linkConfirm : Clip("link_confirm.wav");
            bank.visorOpen = bank.visorOpen != null ? bank.visorOpen : Clip("visor_open.wav");
            bank.scanSweep = bank.scanSweep != null ? bank.scanSweep : Clip("scan_sweep.wav");
            bank.labelPop = bank.labelPop != null ? bank.labelPop : Clip("label_pop.wav");
            bank.hudElementIn = bank.hudElementIn != null ? bank.hudElementIn : Clip("hud_element_in.wav");
            bank.reticleLock = bank.reticleLock != null ? bank.reticleLock : Clip("reticle_lock.wav");
            bank.trimTick = bank.trimTick != null ? bank.trimTick : Clip("trim_tick.wav");
            bank.trimReset = bank.trimReset != null ? bank.trimReset : Clip("trim_reset.wav");
            bank.yPress = bank.yPress != null ? bank.yPress : Clip("y_press.wav");
            bank.beaconPlace = bank.beaconPlace != null ? bank.beaconPlace : Clip("beacon_place.wav");
            bank.toggle = bank.toggle != null ? bank.toggle : Clip("toggle.wav");
            EditorUtility.SetDirty(bank);
            return bank;
        }

        static AudioClip Clip(string file)
        {
            return AssetDatabase.LoadAssetAtPath<AudioClip>(Root + "/Audio/" + file);
        }

        static void Set(UnityEngine.Object target, string field, UnityEngine.Object value)
        {
            var so = new SerializedObject(target);
            SerializedProperty prop = so.FindProperty(field);
            if (prop == null) throw new Exception(target.GetType().Name + " has no serialized field '" + field + "'");
            prop.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static Transform FindDeep(Transform parent, string name)
        {
            foreach (Transform t in parent.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        // ---------- 3. Quest project settings ----------

        [MenuItem("AimLink/3. Configure Project For Quest 3")]
        public static void ConfigureProject()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
                throw new Exception("Android Build Support is not installed for this Unity version. Add it in Unity Hub > Installs > Add modules.");

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

            PlayerSettings.companyName = "AimLink";
            PlayerSettings.productName = "AIMLINK";
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.aimlink.reticle");
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            // no network use and no Unity hardware statistics from the player
            PlayerSettings.Android.forceInternetPermission = false;
            // "submitAnalytics" (send hardware statistics) has no public API in this Unity version
            var playerSettings = new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings"));
            SerializedProperty submitAnalytics = playerSettings.FindProperty("submitAnalytics");
            if (submitAnalytics != null)
            {
                submitAnalytics.boolValue = false;
                playerSettings.ApplyModifiedPropertiesWithoutUndo();
            }

            OVRProjectConfig config = OVRProjectConfig.CachedProjectConfig;
            config.targetDeviceTypes = new List<OVRProjectConfig.DeviceType>
            {
                OVRProjectConfig.DeviceType.Quest3, OVRProjectConfig.DeviceType.Quest3S
            };
            config.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersOnly;
            config.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required;
            config.sceneSupport = OVRProjectConfig.FeatureSupport.Required;
            // headset camera permission in the manifest, for the scope inset
            config.isPassthroughCameraAccessEnabled = true;
            OVRProjectConfig.CommitProjectConfig(config);

            ApplyMetaProjectSetupFixes();
            EnableOpenXRFeatures();
            ApplyMetaProjectSetupFixes();
            // the fix-all only regenerates the manifest when it notices a mismatch; force it so new permissions land
            OVRManifestPreprocessor.GenerateOrUpdateAndroidManifest(true);
            AssetDatabase.SaveAssets();
            Debug.Log("[AimLink] Project configured for Quest 3.");
        }

        // The Meta SDK only enables its OpenXR feature set through an interactive dialog, which never
        // appears in batch mode. Without MetaXRFeature, OVRPlugin (input, passthrough, scene) is dead.
        static void EnableOpenXRFeatures()
        {
            var wanted = new HashSet<string>
            {
                "MetaXRFeature", "MetaQuestFeature", "OculusTouchControllerProfile", "MetaQuestTouchPlusControllerProfile"
            };
            UnityEditor.XR.OpenXR.Features.FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
            var settings = UnityEngine.XR.OpenXR.OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (settings == null) throw new Exception("No OpenXR settings for Android. Is the OpenXR loader assigned?");

            var found = new HashSet<string>();
            foreach (var feature in settings.GetFeatures())
            {
                if (feature == null || !wanted.Contains(feature.GetType().Name)) continue;
                feature.enabled = true;
                if (feature.GetType().Name == "MetaQuestFeature")
                {
                    // the app needs no network access: strip android.permission.INTERNET from the manifest,
                    // so nothing (including SDK telemetry) can leave the headset
                    var so = new SerializedObject(feature);
                    SerializedProperty removeInternet = so.FindProperty("forceRemoveInternetPermission");
                    if (removeInternet != null)
                    {
                        removeInternet.boolValue = true;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                    else Debug.LogWarning("[AimLink] MetaQuestFeature.forceRemoveInternetPermission not found in this OpenXR version.");
                }
                EditorUtility.SetDirty(feature);
                found.Add(feature.GetType().Name);
            }
            EditorUtility.SetDirty(settings);
            foreach (string name in wanted)
                if (!found.Contains(name)) Debug.LogWarning("[AimLink] OpenXR feature not found: " + name);
            Debug.Log("[AimLink] OpenXR features enabled: " + string.Join(", ", found));
        }

        // The Meta Project Setup Tool's "Fix All", run synchronously. The blocking entry point is internal.
        static void ApplyMetaProjectSetupFixes()
        {
            MethodInfo fix = typeof(OVRProjectSetup).GetMethod("FixTasks", BindingFlags.NonPublic | BindingFlags.Static);
            if (fix == null)
            {
                Debug.LogWarning("[AimLink] OVRProjectSetup.FixTasks not found in this SDK version. Run Meta > Tools > Project Setup Tool > Fix All manually.");
                return;
            }
            ParameterInfo[] ps = fix.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType == typeof(BuildTargetGroup)) args[i] = BuildTargetGroup.Android;
                else if (ps[i].Name == "blocking") args[i] = true;
                else if (ps[i].ParameterType.IsEnum) args[i] = Enum.ToObject(ps[i].ParameterType, 1); // LogMessages.Summary
                else args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
            }
            // a few passes: some fixes only become applicable after others have run
            for (int pass = 0; pass < 3; pass++) fix.Invoke(null, args);
        }

        // ---------- 4. build ----------

        [MenuItem("AimLink/4. Build APK")]
        public static void BuildApk()
        {
            ApplyVersionFromArgs();
            ApplySigningFromEnvironment();
            Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None
            };
            UnityEditor.Build.Reporting.BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                // don't leave the keystore path (it contains a local user folder) in ProjectSettings
                PlayerSettings.Android.useCustomKeystore = false;
                PlayerSettings.Android.keystoreName = "";
                PlayerSettings.Android.keyaliasName = "";
                PlayerSettings.Android.keystorePass = "";
                PlayerSettings.Android.keyaliasPass = "";
            }
            var summary = report.summary;
            Debug.Log("[AimLink] Build " + summary.result + ": " + summary.outputPath + " (" + summary.totalErrors + " errors)");
            if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                throw new Exception("Build failed with " + summary.totalErrors + " errors.");
        }

        // Batch args: -versionName 1.2.0 -versionCode 3. Every release needs a higher versionCode than the last.
        static void ApplyVersionFromArgs()
        {
            string name = GetArg("-versionName");
            string code = GetArg("-versionCode");
            if (!string.IsNullOrEmpty(name)) PlayerSettings.bundleVersion = name;
            if (!string.IsNullOrEmpty(code))
            {
                if (!int.TryParse(code, out int versionCode) || versionCode < 1)
                    throw new Exception("-versionCode must be a positive whole number, got '" + code + "'.");
                PlayerSettings.Android.bundleVersionCode = versionCode;
            }
            Debug.Log("[AimLink] Version " + PlayerSettings.bundleVersion + " (code " + PlayerSettings.Android.bundleVersionCode + ")");
        }

        // Release signing comes from environment variables so no secret is ever stored in the project:
        //   AIMLINK_KEYSTORE        path to the .keystore file (keep it outside the repository)
        //   AIMLINK_KEYSTORE_PASS   keystore password
        //   AIMLINK_KEY_ALIAS       key alias inside the keystore
        //   AIMLINK_KEY_PASS        key password
        // Without them the APK is signed with Unity's debug key, which is fine for testing but not for releases:
        // an update only installs over a previous version signed with the same key.
        static void ApplySigningFromEnvironment()
        {
            string keystore = Environment.GetEnvironmentVariable("AIMLINK_KEYSTORE");
            if (string.IsNullOrEmpty(keystore))
            {
                PlayerSettings.Android.useCustomKeystore = false;
                Debug.LogWarning("[AimLink] AIMLINK_KEYSTORE not set: signing with the debug key (not for release).");
                return;
            }
            if (!File.Exists(keystore)) throw new Exception("Keystore not found: " + keystore);
            string storePass = Environment.GetEnvironmentVariable("AIMLINK_KEYSTORE_PASS");
            string alias = Environment.GetEnvironmentVariable("AIMLINK_KEY_ALIAS");
            string keyPass = Environment.GetEnvironmentVariable("AIMLINK_KEY_PASS");
            if (string.IsNullOrEmpty(storePass) || string.IsNullOrEmpty(alias) || string.IsNullOrEmpty(keyPass))
                throw new Exception("AIMLINK_KEYSTORE is set, but AIMLINK_KEYSTORE_PASS, AIMLINK_KEY_ALIAS and AIMLINK_KEY_PASS must be set too.");

            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = keystore;
            PlayerSettings.Android.keystorePass = storePass;
            PlayerSettings.Android.keyaliasName = alias;
            PlayerSettings.Android.keyaliasPass = keyPass;
            Debug.Log("[AimLink] Signing with release key '" + alias + "'.");
        }

        static string GetArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
