using HagenDa.Networking;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace HagenDa.Soldier.EditorTools
{
    /// <summary>
    /// PHASE14 NetworkPlayer 预制体装配（幂等）：
    ///  - 模型：只挂 Fatui 单模型（natlan 分支内容从装配移除，双模型切换逻辑取消）
    ///  - 移除旧 NetworkSoldierAnimator，挂 SoldierAnimatorDriver
    ///  - 模型 Animator 指向 SoldierLoco.controller，applyRootMotion=false
    ///  - WeaponBasis/M4_8 保障（枪械基准与模型同级，PHASE14 角色结构）
    ///  - 胶囊 Body 渲染关闭（PHASE14：主胶囊体去除 render 显示）
    ///  - eyeAnchor = 模型 headcollider（相机球面法线路径使用）
    /// 前置：先运行 HagenDa/SoldierAnim/Bake Fatui Rig 与 Bake Soldier Controller。
    /// </summary>
    public static class SoldierAssembly
    {
        const string PlayerPrefabPath = "Assets/Game/Prefabs/NetworkPlayer.prefab";
        const string AIPrefabPath = "Assets/Game/Prefabs/AIEntity.prefab";
        const string FatuiPath = "Assets/Game/Characters/Fatui/Fatui with Collider.prefab";
        const string GunPath = "Assets/Game/Characters/Guns/M4_8.prefab";
        const string ControllerPath = "Assets/Game/Animation/SoldierLoco.controller";

        [MenuItem("HagenDa/SoldierAnim/Assemble NetworkPlayer Prefab")]
        public static void AssemblePlayer() => Assemble(PlayerPrefabPath);

        [MenuItem("HagenDa/SoldierAnim/Assemble AIEntity Prefab")]
        public static void AssembleAI() => Assemble(AIPrefabPath);

        static void Assemble(string prefabPath)
        {
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            if (controller == null)
            {
                Debug.LogError("[SoldierAssembly] 缺少 SoldierLoco.controller —— 先运行 Bake Soldier Controller");
                return;
            }
            var fatui = AssetDatabase.LoadAssetAtPath<GameObject>(FatuiPath);
            if (fatui == null) { Debug.LogError($"[SoldierAssembly] 缺少 {FatuiPath}"); return; }
            var fatuiRig = fatui.GetComponent<SoldierRigDriver>();
            if (fatuiRig == null)
            {
                Debug.LogError("[SoldierAssembly] Fatui 预制体缺 SoldierRigDriver —— 先运行 Bake Fatui Rig");
                return;
            }

            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            if (contents == null) { Debug.LogError($"[SoldierAssembly] 找不到 {prefabPath}"); return; }

            try
            {
                BuildInternal(contents, fatui, controller);
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[SoldierAssembly] 装配完成 → {prefabPath}");
        }

        static void BuildInternal(GameObject root, GameObject fatuiPrefab,
            RuntimeAnimatorController controller)
        {
            // ---- 1) 移除旧动画驱动 ----
            foreach (var old in root.GetComponents<NetworkSoldierAnimator>())
                Object.DestroyImmediate(old, true);

            // ---- 2) 单 Fatui 模型 ----
            var red = root.transform.Find("RedSoldierModel");
            if (red != null) Object.DestroyImmediate(red.gameObject, true);

            Transform soldier = root.transform.Find("SoldierModel");
            if (soldier == null)
            {
                var blue = root.transform.Find("BlueSoldierModel");
                if (blue != null)
                {
                    blue.name = "SoldierModel";   // 复用既有实例（保留其覆盖）
                    soldier = blue;
                }
            }
            if (soldier == null)
            {
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(fatuiPrefab);
                inst.name = "SoldierModel";
                inst.transform.SetParent(root.transform, false);
                soldier = inst.transform;
            }
            soldier.localPosition = Vector3.zero;
            soldier.localRotation = Quaternion.identity;
            soldier.localScale = Vector3.one;
            soldier.gameObject.SetActive(true);

            // ---- 3) 模型 Animator：控制器 + 根运动关 ----
            var modelAnim = soldier.GetComponentInChildren<Animator>(true);
            if (modelAnim != null)
            {
                modelAnim.runtimeAnimatorController = controller;
                modelAnim.applyRootMotion = false;
            }
            else
            {
                Debug.LogError("[SoldierAssembly] Fatui 模型内无 Animator");
            }

            // ---- 4) 新动画驱动 ----
            var driver = root.GetComponent<SoldierAnimatorDriver>();
            if (driver == null) driver = root.AddComponent<SoldierAnimatorDriver>();
            driver.model = soldier.gameObject;

            // ---- 5) WeaponBasis + M4_8（与模型同级，PHASE14 角色结构）----
            var basis = root.transform.Find("WeaponBasis");
            if (basis == null)
            {
                var go = new GameObject("WeaponBasis");
                go.transform.SetParent(root.transform, false);
                basis = go.transform;
            }
            basis.localPosition = Vector3.zero;
            basis.localRotation = Quaternion.identity;
            if (basis.childCount == 0)
            {
                var gunPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GunPath);
                if (gunPrefab != null)
                {
                    var gun = (GameObject)PrefabUtility.InstantiatePrefab(gunPrefab);
                    gun.name = "M4_8";
                    gun.transform.SetParent(basis, false);
                }
            }

            // ---- 6) 胶囊 Body 渲染关闭 ----
            var body = root.transform.Find("Body");
            if (body != null)
            {
                var r = body.GetComponent<Renderer>();
                if (r != null) r.enabled = false;
            }

            // ---- 7) 编辑器骨骼 gizmo 关闭 ----
            foreach (var br in root.GetComponentsInChildren<BoneRenderer>(true))
                br.enabled = false;

            // ---- 8) eyeAnchor = headcollider ----
            var ctrl = root.GetComponent<NetworkPlayerController>();
            if (ctrl != null)
            {
                foreach (var t in soldier.GetComponentsInChildren<Transform>(true))
                    if (t.name.ToLowerInvariant().Contains("headcollider"))
                    {
                        ctrl.eyeAnchor = t;
                        break;
                    }
            }

            EditorUtility.SetDirty(root);
        }
    }
}
