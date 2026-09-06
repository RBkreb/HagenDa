using System.IO;
using Mirror;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HagenDa.Networking.EditorTools
{
    /// <summary>
    /// PHASE11 场景装配：HagenDa/Setup Commander System
    ///
    /// 在当前场景（预期 FSMBattle.scene）注入：
    ///   LLMCommander (root)
    ///     ├─ HexGrid : CommanderHexGrid（六边形空间编码，红蓝共享）
    ///     ├─ Red     : CommanderOrchestrator(team=0) + Weapons
    ///     ├─ Blue    : 同上 team=1
    ///     └─ Gate    : CommanderGateController
    ///   NetworkCommanderState (独立 NetworkIdentity 对象，matchStarted 门控/目标同步)
    ///   LLMCommanderHud       (客户端遮罩/目标标记)
    ///
    /// 同时：创建 Assets/Settings/CommanderConfig.asset、把地图 AABB 写入
    /// HexGrid（可由 Match 构建器/NetworkSetup 传入精确边界覆盖）。
    /// </summary>
    public static class CommanderSetup
    {
        private const string ConfigAssetPath = "Assets/Settings/CommanderConfig.asset";
        private const string RootName = "LLMCommander";

        [MenuItem("HagenDa/Setup Commander System")]
        public static void Setup() => Setup(null);

        /// <summary>
        /// 装配指挥官 rig。mapBoundsOverride：Match 场景构建器 / NetworkSetup 传入
        /// 的精确地图 AABB（FBX 图的 "*wall*" 名字推导会失真，优先用调用方边界）；
        /// 空 = 用 ComputeMapBounds（Wall/Floor 名字推导）。
        /// </summary>
        public static void Setup(Bounds? mapBoundsOverride)
        {
            var cfg = EnsureConfig();

            // 幂等：清旧装新。
            var old = GameObject.Find(RootName);
            if (old != null) Object.DestroyImmediate(old);
            var oldState = GameObject.Find("NetworkCommanderState");
            if (oldState != null) Object.DestroyImmediate(oldState);
            var oldHud = GameObject.Find("LLMCommanderHud");
            if (oldHud != null) Object.DestroyImmediate(oldHud);

            // ---- 地图边界 ----
            Bounds mapBounds = mapBoundsOverride ?? ComputeMapBounds();
            Debug.Log($"[CmdSetup] 地图边界 X[{mapBounds.min.x:F0},{mapBounds.max.x:F0}] " +
                      $"Z[{mapBounds.min.z:F0},{mapBounds.max.z:F0}]");

            // ---- NetworkCommanderState ----
            var stateGo = new GameObject("NetworkCommanderState",
                typeof(NetworkIdentity), typeof(NetworkCommanderState));

            // ---- 共享六边形空间编码（红蓝共用一个实例）----
            var root = new GameObject(RootName);
            var hexGo = new GameObject("HexGrid");
            hexGo.transform.SetParent(root.transform, false);
            var hex = hexGo.AddComponent<CommanderHexGrid>();
            hex.targetCells = Mathf.Max(16, cfg.hexTargetCells);
            hex.boundsOverride = mapBounds;

            // ---- 红蓝双份 Rig ----
            BuildRig(root, "Red", (int)MatchTeam.Red, cfg, hex,
                     stateGo.GetComponent<NetworkCommanderState>());
            BuildRig(root, "Blue", (int)MatchTeam.Blue, cfg, hex,
                     stateGo.GetComponent<NetworkCommanderState>());

            // ---- 门控 ----
            root.AddComponent<CommanderGateController>();

            // ---- HUD ----
            new GameObject("LLMCommanderHud").AddComponent<CommanderHud>();

            MarkAndSave();
            Debug.Log("[CmdSetup] 完成：共享 HexGrid + 红/蓝指挥官 + 门控 + 状态对象 + HUD 已注入。" +
                      " Play 即自动 Host 并进入开局部署门控。");
        }

        private static CommanderOrchestrator BuildRig(GameObject parent, string label,
            int team, CommanderConfig cfg, CommanderHexGrid hex, NetworkCommanderState state)
        {
            var rigGo = new GameObject(label);
            rigGo.transform.SetParent(parent.transform, false);

            var weaponsGo = new GameObject($"Weapons_{label}");
            weaponsGo.transform.SetParent(rigGo.transform, false);
            var weapons = weaponsGo.AddComponent<CommanderWeaponSystem>();

            var orch = rigGo.AddComponent<CommanderOrchestrator>();
            orch.Init(team, cfg, hex, weapons, state);
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
            // 优先：名字含 "wall" 的物体组合包围盒（兼容 Wall_N / wall_loop 等命名）。
            Bounds? combined = null;
            foreach (var go in Object.FindObjectsOfType<GameObject>())
            {
                if (!go.name.ToLowerInvariant().Contains("wall")) continue;
                var r = go.GetComponent<Renderer>();
                if (r == null) continue;
                combined = combined.HasValue
                    ? Encapsulate(combined.Value, r.bounds)
                    : r.bounds;
            }
            if (combined.HasValue) return combined.Value;

            var floor = GameObject.Find("Floor");
            if (floor != null && floor.GetComponent<Renderer>() is Renderer fr)
                return fr.bounds;

            // HGTR (Blender map): floor union mesh 兜底。
            var hgtrFloor = GameObject.Find("hgtr_floor_union");
            if (hgtrFloor != null && hgtrFloor.GetComponent<Renderer>() is Renderer hf)
                return hf.bounds;

            Debug.LogWarning("[CmdSetup] 未找到 Wall/Floor，使用默认边界 ±60/±110");
            return new Bounds(Vector3.zero, new Vector3(120f, 20f, 220f));
        }

        private static Bounds Encapsulate(Bounds a, Bounds b)
        {
            var min = Vector3.Min(a.min, b.min);
            var max = Vector3.Max(a.max, b.max);
            return new Bounds((min + max) * 0.5f, max - min);
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
