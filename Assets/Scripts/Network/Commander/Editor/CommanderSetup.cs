using System.Collections.Generic;
using System.IO;
using Mirror;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HagenDa.Networking.EditorTools
{
    /// <summary>
    /// PHASE10 场景装配：HagenDa/Setup Commander System
    ///
    /// 在当前场景（预期 FSMBattle.scene）注入：
    ///   LLMCommander (root)
    ///     ├─ Red   : CommanderOrchestrator(team=0) + Overlay + Camera + Weapons
    ///     ├─ Blue  : 同上 team=1
    ///     └─ Gate  : CommanderGateController
    ///   NetworkCommanderState (独立 NetworkIdentity 对象，matchStarted 门控/目标同步)
    ///   LLMCommanderHud       (客户端遮罩/目标标记)
    ///
    /// 同时：确保 CommanderMap layer、创建 Assets/Settings/CommanderConfig.asset、
    /// 从场景 Wall 推导地图边界写入两个 Overlay。
    /// </summary>
    public static class CommanderSetup
    {
        private const string ConfigAssetPath = "Assets/Settings/CommanderConfig.asset";
        private const string RootName = "LLMCommander";

        [MenuItem("HagenDa/Setup Commander System")]
        public static void Setup()
        {
            EnsureLayer("CommanderMap");
            var cfg = EnsureConfig();

            // 幂等：清旧装新。
            var old = GameObject.Find(RootName);
            if (old != null) Object.DestroyImmediate(old);
            var oldState = GameObject.Find("NetworkCommanderState");
            if (oldState != null) Object.DestroyImmediate(oldState);
            var oldHud = GameObject.Find("LLMCommanderHud");
            if (oldHud != null) Object.DestroyImmediate(oldHud);

            // ---- 地图边界（Wall 组合包围盒；兜底 Floor）----
            Bounds mapBounds = ComputeMapBounds();
            Debug.Log($"[CmdSetup] 地图边界 X[{mapBounds.min.x:F0},{mapBounds.max.x:F0}] " +
                      $"Z[{mapBounds.min.z:F0},{mapBounds.max.z:F0}]");

            // ---- NetworkCommanderState ----
            var stateGo = new GameObject("NetworkCommanderState",
                typeof(NetworkIdentity), typeof(NetworkCommanderState));

            // ---- 红蓝双份 Rig ----
            var root = new GameObject(RootName);

            var redOrch = BuildRig(root, "Red", (int)MatchTeam.Red, cfg,
                                   mapBounds, stateGo.GetComponent<NetworkCommanderState>());
            var blueOrch = BuildRig(root, "Blue", (int)MatchTeam.Blue, cfg,
                                    mapBounds, stateGo.GetComponent<NetworkCommanderState>());

            // ---- 门控 ----
            root.AddComponent<CommanderGateController>();

            // ---- HUD ----
            new GameObject("LLMCommanderHud").AddComponent<CommanderHud>();

            MarkAndSave();
            Debug.Log("[CmdSetup] 完成：红/蓝指挥官 + 门控 + 状态对象 + HUD 已注入。" +
                      " Play 即自动 Host 并进入开局部署门控。");
        }

        private static CommanderOrchestrator BuildRig(GameObject parent, string label,
            int team, CommanderConfig cfg, Bounds mapBounds, NetworkCommanderState state)
        {
            var rigGo = new GameObject(label);
            rigGo.transform.SetParent(parent.transform, false);

            var overlayGo = new GameObject($"Overlay_{label}");
            overlayGo.transform.SetParent(rigGo.transform, false);
            var overlay = overlayGo.AddComponent<CommanderMapOverlay>();
            overlay.mapMinWorld = new Vector2(mapBounds.min.x, mapBounds.min.z);
            overlay.mapMaxWorld = new Vector2(mapBounds.max.x, mapBounds.max.z);
            overlay.gridSize = cfg.gridSizeMeters;
            overlay.markerY = MapLayers.IndicatorWorldY + 1f;   // 12m

            var camGo = new GameObject($"Cam_{label}");
            camGo.transform.SetParent(rigGo.transform, false);
            var cam = camGo.AddComponent<CommanderMapCamera>();
            cam.overlay = overlay;
            cam.longSidePixels = cfg.snapshotLongSide;

            var weaponsGo = new GameObject($"Weapons_{label}");
            weaponsGo.transform.SetParent(rigGo.transform, false);
            var weapons = weaponsGo.AddComponent<CommanderWeaponSystem>();

            var orch = rigGo.AddComponent<CommanderOrchestrator>();
            orch.Init(team, cfg, overlay, cam, weapons, state);
            return orch;
        }

        // ---------------------------------------------------------------
        // 资产 / 边界 / layer 工具
        // ---------------------------------------------------------------

        private static CommanderConfig EnsureConfig()
        {
            var loaded = AssetDatabase.LoadAssetAtPath<CommanderConfig>(ConfigAssetPath);
            if (loaded != null) return loaded;

            EnsureFolder("Assets", "Settings");
            var cfg = ScriptableObject.CreateInstance<CommanderConfig>();
            cfg.squadsPerTeam = 6;
            AssetDatabase.CreateAsset(cfg, ConfigAssetPath);
            Debug.Log($"[CmdSetup] 已创建 {ConfigAssetPath}");
            return cfg;
        }

        private static Bounds ComputeMapBounds()
        {
            Bounds? combined = null;
            foreach (var go in Object.FindObjectsOfType<GameObject>())
            {
                if (go.name != "Wall") continue;
                var r = go.GetComponent<Renderer>();
                if (r == null) continue;
                combined = combined.HasValue
                    ? new Bounds { min = Vector3.Min(combined.Value.min, r.bounds.min),
                                   max = Vector3.Max(combined.Value.max, r.bounds.max) }
                    : r.bounds;
            }
            if (combined.HasValue) return combined.Value;

            var floor = GameObject.Find("Floor");
            if (floor != null && floor.GetComponent<Renderer>() is Renderer fr)
                return fr.bounds;

            Debug.LogWarning("[CmdSetup] 未找到 Wall/Floor，使用默认边界 ±60/±110");
            return new Bounds(Vector3.zero, new Vector3(120f, 20f, 220f));
        }

        private static void EnsureLayer(string name)
        {
            var tagManager = new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layersProp = tagManager.FindProperty("layers");
            for (int i = 8; i < layersProp.arraySize; i++)
            {
                var slot = layersProp.GetArrayElementAtIndex(i);
                if (slot.stringValue == name) return;
                if (string.IsNullOrEmpty(slot.stringValue))
                {
                    slot.stringValue = name;
                    tagManager.ApplyModifiedProperties();
                    Debug.Log($"[CmdSetup] 已注册 layer '{name}' (slot {i})");
                    return;
                }
            }
            Debug.LogWarning("[CmdSetup] TagManager 无空闲 layer 槽位！");
        }

        private static void EnsureFolder(string parent, string folder)
        {
            string full = parent + "/" + folder;
            if (!AssetDatabase.IsValidFolder(full))
                AssetDatabase.CreateFolder(parent, folder);
        }

        private static void MarkAndSave()
        {
            var scene = SceneManager.GetActiveScene();
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            bool ok = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[CmdSetup] 场景保存 {(ok ? "成功" : "失败")}: {scene.path}");
        }
    }
}
