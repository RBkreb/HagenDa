using System.Collections.Generic;
using HagenDa.Animation.Rigging;   // HandGripConstraint / HandGripAnalyzer
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEngine.SceneManagement;

namespace HagenDa.Animation.RigDriver.EditorTools
{
    /// <summary>
    /// **握持调参场景**构建器（幂等，可反复重建）。
    ///
    /// 产出一个最小舞台：地平面 + 平行光 + 相机 + Natlan 角色 + 一排可切换道具，
    /// 挂 <see cref="GripTuningDriver"/> 直接喂 <see cref="SoldierRigDriver.SetFrameState"/>，
    /// 于是**双手 TwoBoneIK 与手指卷握在无网络、无对局系统的情况下即可工作**。
    ///
    /// 结构：
    ///   GripTuningRoot                [GripTuningDriver]
    ///     Natlan Soldier FBX          角色模型（Animator + RigBuilder + SoldierRigDriver）
    ///     WeaponBasis/                枪械基准（与模型同级 —— BindWeapon 要求）
    ///       M4_8 / AK74 / …           可切换道具（数字键 1..9/0）
    ///     Ground                      地平面（脚 IK 射线的落点）
    ///
    /// <b>关于锚点</b>：<c>SoldierRigDriver.BindWeapon</c> 要求道具自带
    /// <c>RearGrip</c>/<c>BarrelGrip</c>（缺任一直接 return false，双手 IK 不绑），
    /// 手指卷握还要求 <c>RearGripCap</c>/<c>BarrelGripCap</c> 带 CapsuleCollider。
    /// 本工具只对**缺少锚点**的道具补齐**占位锚点**（位置按道具包围盒粗放推导），
    /// 正是留给用户手动拖拽的起手位；已自带锚点的道具（如 M4_8）原样不动。
    ///
    /// 锚点是加在**场景实例**上的覆写，不改动 ThirdParty 预制体资产本身。
    /// </summary>
    public static class GripTuningSceneBuilder
    {
        const string ScenePath = "Assets/Game/Scenes/GripTuning.scene";
        const string NatlanPath = "Assets/Game/Characters/natlan/Natlan Soldier FBX.prefab";
        const string RootName = "GripTuningRoot";
        const string BasisName = "WeaponBasis";

        /// <summary>放进场景的道具。想加/减直接改这个列表（顺序 = HUD 与数字键顺序）。</summary>
        static readonly string[] PropPaths =
        {
            "Assets/Game/Characters/Guns/M4_8.prefab",
            "Assets/ThirdParty/Low Poly Weapons VOL.1/Prefabs/AK74.prefab",
            "Assets/ThirdParty/Low Poly Weapons VOL.1/Prefabs/M1911.prefab",
            "Assets/ThirdParty/Low Poly Weapons VOL.1/Prefabs/Uzi.prefab",
            "Assets/ThirdParty/Low Poly Weapons VOL.1/Prefabs/M249.prefab",
            "Assets/ThirdParty/Low Poly Weapons VOL.1/Prefabs/Bennelli_M4.prefab",
            "Assets/ThirdParty/Low Poly Weapons VOL.1/Prefabs/M107.prefab",
            "Assets/ThirdParty/Low Poly Weapons VOL.1/Prefabs/RPG7.prefab",
            "Assets/ThirdParty/Low Poly Weapons VOL.1/Prefabs/RGD-5.prefab",
            "Assets/Game/Prefabs/SignalCharge.prefab",
        };

        [MenuItem("HagenDa/SoldierAnim/Build Grip Tuning Scene")]
        public static void Build()
        {
            var modelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(NatlanPath);
            if (modelPrefab == null) { Debug.LogError($"[GripScene] 找不到 {NatlanPath}"); return; }
            var rig = modelPrefab.GetComponent<SoldierRigDriver>();
            if (rig == null)
            {
                Debug.LogError("[GripScene] Natlan 预制体缺 SoldierRigDriver —— 先运行 HagenDa/SoldierAnim/Bake Natlan Rig");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ---- 光照 ----
            NetworkSetupEdLighting();

            // ---- 地面（脚 IK 射线落点）----
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Ground";
            floor.transform.position = Vector3.zero;
            floor.transform.localScale = new Vector3(4f, 1f, 4f);   // 40 x 40 m

            // ---- 实体根 ----
            var root = new GameObject(RootName);
            root.transform.position = Vector3.zero;

            // 角色模型（实体根的直接子级 → 驱动 entityRoot 即该根）
            var model = (GameObject)PrefabUtility.InstantiatePrefab(modelPrefab);
            model.name = "Natlan Soldier FBX";
            model.transform.SetParent(root.transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;

            // ---- 枪械基准 + 道具 ----
            var basis = new GameObject(BasisName);
            basis.transform.SetParent(root.transform, false);

            int added = 0;
            foreach (var p in PropPaths)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (prefab == null) { Debug.LogWarning($"[GripScene] 道具缺失，跳过: {p}"); continue; }

                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                inst.name = System.IO.Path.GetFileNameWithoutExtension(p);
                inst.transform.SetParent(basis.transform, false);
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = Quaternion.identity;

                added += EnsurePropAnchors(inst);
            }
            // 默认只亮第一个（BindWeapon 一次只绑一个；其余由数字键切换）
            for (int i = 0; i < basis.transform.childCount; i++)
                basis.transform.GetChild(i).gameObject.SetActive(i == 0);

            // ---- 驱动（挂在实体根，喂 rig 帧状态）----
            var driver = root.AddComponent<GripTuningDriver>();
            driver.model = model;
            driver.weaponBasis = basis.transform;
            driver.aimAmount = 0f;
            // 显式写入道具名单（Inspector 可见、HUD 与数字键顺序确定；
            // 运行时 ResolveRefs 只在为空时才自动收集）
            driver.propNames = new string[basis.transform.childCount];
            for (int i = 0; i < basis.transform.childCount; i++)
                driver.propNames[i] = basis.transform.GetChild(i).name;

            // ---- 相机：看向上半身，方便观察双手 ----
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 45f;
            cam.nearClipPlane = 0.05f;
            camGo.AddComponent<AudioListener>();
            camGo.transform.position = new Vector3(1.35f, 1.62f, -1.75f);
            camGo.transform.rotation = Quaternion.LookRotation(
                (new Vector3(0f, 1.30f, 0.10f) - camGo.transform.position).normalized, Vector3.up);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            Debug.Log($"[GripScene] 已生成 {ScenePath}\n" +
                      $"  道具 {basis.transform.childCount} 个，其中 {added} 个补了占位锚点（请手动拖拽 RearGrip/BarrelGrip 及握把胶囊）。\n" +
                      $"  进 Play：右键拖动转视角 / Q 腰射↔ADS / 数字键 1..9、0 切换道具。");
        }

        // =================================================================
        // 占位锚点
        // =================================================================

        /// <summary>
        /// 补齐 BindWeapon / 手指卷握所需的锚点。**已存在的一律不动**。
        /// 返回新建的锚点数量。
        /// </summary>
        static int EnsurePropAnchors(GameObject prop)
        {
            // 局部包围盒（用于推导合理的起手位；不含锚点自身，故先算）
            Bounds local = LocalBounds(prop);
            float len = local.size.z;
            float halfLen = len * 0.5f;
            // 枪管指向 = 局部 -Z（M4_8 实测：SightAim − RearAim 为 -Z），后握把在 +Z 侧。
            // 数量级按包围盒缩放，粗放即可（用户会手调）。
            float zRear = halfLen * 0.62f;
            float zFore = -halfLen * 0.30f;
            float yLow = local.min.y + local.size.y * 0.22f;
            float yTop = local.max.y + 0.01f;

            int made = 0;

            // 枪机（仅视觉往复，缺失只是没有枪机动画）
            made += EnsureEmpty(prop, "Bolt", new Vector3(local.center.x, yTop, zRear * 0.7f));

            // 双手 IK 目标
            made += EnsureEmpty(prop, "RearGrip", new Vector3(local.center.x, yLow, zRear));
            made += EnsureEmpty(prop, "BarrelGrip", new Vector3(local.center.x, yLow, zFore));

            // 手指卷握胶囊（CapsuleCollider，供 HandGripAnalyzer 分析）
            made += EnsureGripCap(prop, "RearGripCap", new Vector3(local.center.x, yLow, zRear), Vector3.up, 0.022f, 0.10f);
            made += EnsureGripCap(prop, "BarrelGripCap", new Vector3(local.center.x, yLow, zFore), Vector3.forward, 0.026f, 0.22f);

            // 瞄准轴两点（轴 = SightAim − RearAim）
            made += EnsureEmpty(prop, "RearAim", new Vector3(local.center.x, yTop, zRear * 0.55f));
            made += EnsureEmpty(prop, "SightAim", new Vector3(local.center.x, yTop, -halfLen * 0.85f));

            // 腰射枪托锚点（落在右肩 GunCylinder 柱面上）
            made += EnsureEmpty(prop, "Stock", new Vector3(local.center.x, local.center.y, halfLen * 0.92f));

            return made;
        }

        static int EnsureEmpty(GameObject prop, string name, Vector3 localPos)
        {
            if (FindDeep(prop.transform, name) != null) return 0;
            var go = new GameObject(name);
            go.transform.SetParent(prop.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return 1;
        }

        static int EnsureGripCap(GameObject prop, string name, Vector3 localPos, Vector3 axis, float radius, float height)
        {
            if (FindDeep(prop.transform, name) != null) return 0;
            var go = new GameObject(name);
            go.transform.SetParent(prop.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            var cap = go.AddComponent<CapsuleCollider>();
            cap.direction = axis == Vector3.right ? 0 : (axis == Vector3.forward ? 2 : 1);
            cap.radius = radius;
            cap.height = height;
            cap.center = Vector3.zero;
            cap.isTrigger = true;
            return 1;
        }

        /// <summary>道具自身网格的**局部空间**包围盒（忽略锚点空物体：它们没有 Renderer）。</summary>
        static Bounds LocalBounds(GameObject prop)
        {
            var rends = prop.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) return new Bounds(Vector3.zero, new Vector3(0.08f, 0.2f, 0.7f));

            var t = prop.transform;
            bool has = false;
            Bounds b = default;
            foreach (var r in rends)
            {
                var mb = r.bounds;                 // 世界
                var corners = new Vector3[8];
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3(
                        (i & 1) == 0 ? mb.min.x : mb.max.x,
                        (i & 2) == 0 ? mb.min.y : mb.max.y,
                        (i & 4) == 0 ? mb.min.z : mb.max.z);
                    corners[i] = t.InverseTransformPoint(c);
                }
                for (int i = 0; i < 8; i++)
                {
                    if (!has) { b = new Bounds(corners[i], Vector3.zero); has = true; }
                    else b.Encapsulate(corners[i]);
                }
            }
            return b;
        }

        // =================================================================
        // 移动锚点后重跑 bind 姿态分析
        // =================================================================

        /// <summary>
        /// 移动道具的握把胶囊后，重新做一次 bind 姿态分析（写弯曲轴/满握角/胶囊局部常量）。
        ///
        /// 为什么必须重跑：<c>capPerpScale</c> 是**分析时该胶囊的垂直轴缩放**，而运行时
        /// 半径 = <c>胶囊radius × capPerpScale</c>。换一把握把尺寸不同的道具后沿用旧常量，
        /// 半径会成比例偏差 → 手指不握或握穿。
        ///
        /// 目标道具：优先取**层级里选中的** WeaponBasis 子物体；没选中则用当前激活的那个。
        /// 必须在**编辑模式**下运行（分析取的是 rest/bind 姿态；Play 中骨骼在动画位）。
        /// </summary>
        [MenuItem("HagenDa/SoldierAnim/Re-analyze Scene Grips")]
        public static void ReanalyzeSceneGrips()
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[GripScene] 请在**编辑模式**下运行（bind 姿态分析需要骨架处于 rest 位）");
                return;
            }

            var root = GameObject.Find(RootName);
            if (root == null)
            {
                Debug.LogError($"[GripScene] 当前场景找不到 {RootName}（请先 Build Grip Tuning Scene 并打开该场景）");
                return;
            }

            var model = root.transform.Find("Natlan Soldier FBX");
            var basis = root.transform.Find(BasisName);
            if (model == null || basis == null) { Debug.LogError("[GripScene] 场景结构不完整"); return; }

            var anim = model.GetComponent<Animator>();
            var grips = model.GetComponentsInChildren<HandGripConstraint>(true);
            if (grips.Length == 0) { Debug.LogError("[GripScene] 模型里没有 HandGripConstraint"); return; }

            // 目标道具：选中优先，否则激活的
            Transform target = null;
            var sel = Selection.activeTransform;
            if (sel != null && sel != basis && sel.IsChildOf(basis)) target = NearestProp(basis, sel);
            if (target == null)
                for (int i = 0; i < basis.childCount; i++)
                    if (basis.GetChild(i).gameObject.activeSelf) { target = basis.GetChild(i); break; }
            if (target == null) { Debug.LogError("[GripScene] 没找到要分析的道具"); return; }

            int ok = 0;
            var missing = new List<string>();
            foreach (var g in grips)
            {
                string capName = g.data.side == HandGripSide.Left ? "BarrelGripCap" : "RearGripCap";
                var capT = FindDeep(target, capName);
                var cap = capT != null ? capT.GetComponent<CapsuleCollider>() : null;
                if (cap == null) { missing.Add(capName); continue; }

                ref var d = ref g.data;
                d.grip = cap;
                d.thumbXBias = 0.8f;
                d.analyzed = false;
                if (!HandGripAnalyzer.ResolveSkeleton(ref d, anim))
                {
                    Debug.LogWarning($"[GripScene] {g.name} 骨架解析失败（DEF-hand/DEF-f_* 命名？）");
                    continue;
                }
                HandGripAnalyzer.AnalyzePose(ref d);
                d.grip = null;          // 运行时由 BindWeapon 换真实胶囊
                d.analyzed = true;
                EditorUtility.SetDirty(g);
                ok++;
            }

            EditorUtility.SetDirty(model.gameObject);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            string msg = $"[GripScene] 已按道具 '{target.name}' 重分析 {ok}/{grips.Length} 个握持约束。";
            if (missing.Count > 0) msg += $" 缺锚点: {string.Join(",", missing)}（该手不会卷指）。";
            msg += " 记得保存场景（Ctrl+S）。";
            Debug.Log(msg);
        }

        /// <summary>从任意后代找到 WeaponBasis 下的直接子物体（道具根）。</summary>
        static Transform NearestProp(Transform basis, Transform t)
        {
            while (t != null && t.parent != basis) t = t.parent;
            return t;
        }

        // =================================================================
        // 工具
        // =================================================================

        static void NetworkSetupEdLighting()
        {
            if (Object.FindObjectOfType<Light>() != null) return;
            var go = new GameObject("Directional Light");
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;
            go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindDeep(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }
    }
}
