using System.Collections.Generic;
using HagenDa.Animation;          // LocalSoldierDriver（剥离目标）
using HagenDa.Animation.Rigging;  // 旧约束组件（剥离目标）+ HandGripConstraint（保留件）
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace HagenDa.Soldier.EditorTools
{
    /// <summary>
    /// PHASE14 Fatui rig 烘焙（幂等，从0重写；仅 HandGripConstraint 复用旧实例的
    /// 分析常量——analyzed 常量烘焙在组件上，重挂会丢失 bind 姿态标定）。
    ///
    /// 目标预制体：Assets/Model/Fatui/Fatui with Collider.prefab
    /// 剥离（两条旧分支实现残留）：SoldierRigSetup、LocalSoldierDriver、
    /// SpineAimConstraint、HipsPoseConstraint、SkeletonPoseConstraint、
    /// MultiPositionConstraint、旧 TwoBoneIK 实例、旧 RigBuilder 层、旧驱动节点。
    /// 重建（PHASE14 规范结构）：
    ///   Fatui with Collider                [Animator + RigBuilder + SoldierRigDriver]
    ///     ├─ DEF-spine.005/AimSource       （头部骨骼子对象，规范节点）
    ///     ├─ Rig_Drivers/                  Driver_AimSource / Driver_AimTarget /
    ///     │                                Driver_FootTarget_L/R / Driver_FootHint_L/R
    ///     ├─ Rig_LowerBody/                FootIK_L/R（TwoBoneIK，腿链，仅钉位置）
    ///     ├─ Rig_UpperBody/                UpperAim（UpperBodyAimConstraint，反扭≤60°）
    ///     └─ Rig_Hands/                    ArmIK_L/R（TwoBoneIK，臂链）+ LeftGrip/RightGrip
    ///                                      （HandGripConstraint 复用，armIK=false）
    /// </summary>
    public static class SoldierRigBaker
    {
        const string FatuiPath = "Assets/Model/Fatui/Fatui with Collider.prefab";
        const string DriversRoot = "Rig_Drivers";

        // 骨骼（Blender DEF-* 命名，实测层级见 prefab）
        const string ChestBone = "DEF-spine.002";
        const string HeadBone = "DEF-spine.005";

        [MenuItem("HagenDa/SoldierAnim/Bake Fatui Rig")]
        public static void Bake()
        {
            var contents = PrefabUtility.LoadPrefabContents(FatuiPath);
            if (contents == null)
            {
                Debug.LogError($"[SoldierBake] 找不到预制体 {FatuiPath}");
                return;
            }

            try
            {
                BuildInternal(contents);
                PrefabUtility.SaveAsPrefabAsset(contents, FatuiPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[SoldierBake] rig 烘焙完成 → {FatuiPath}");
        }

        [MenuItem("HagenDa/SoldierAnim/Verify Fatui Rig")]
        public static void Verify()
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(FatuiPath);
            if (go == null) { Debug.LogError($"[SoldierBake] 找不到 {FatuiPath}"); return; }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[SoldierBake] === {FatuiPath} ===");
            var anim = go.GetComponent<Animator>();
            sb.AppendLine($"Animator={(anim != null)} ctrl={(anim != null && anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "null")} human={(anim != null && anim.isHuman)}");
            sb.AppendLine($"SoldierRigDriver={(go.GetComponent<SoldierRigDriver>() != null)}");
            var rb2 = go.GetComponent<RigBuilder>();
            sb.AppendLine($"RigBuilder layers={(rb2 != null && rb2.layers != null ? rb2.layers.Count : -1)}");
            if (rb2 != null && rb2.layers != null)
                foreach (var l in rb2.layers)
                    sb.AppendLine($"   layer '{(l.rig != null ? l.rig.name : "null")}'");

            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform) continue;
                switch (c)
                {
                    case TwoBoneIKConstraint two:
                        var d = two.data;
                        sb.AppendLine($"  TwoBoneIK {c.gameObject.name}: root={N(d.root)} mid={N(d.mid)} tip={N(d.tip)} target={N(d.target)} hint={N(d.hint)} pW={d.targetPositionWeight} rW={d.targetRotationWeight} w={two.weight}");
                        break;
                    case UpperBodyAimConstraint ua:
                        var ud = ua.data;
                        sb.AppendLine($"  UpperAim {c.gameObject.name}: lower={N(ud.lowerBody)} chest={N(ud.chest)} head={N(ud.head)} aim={N(ud.aimSource)} maxTwist={ud.maxTwist} w={ua.weight}");
                        break;
                    case HandGripConstraint hg:
                        sb.AppendLine($"  HandGrip {c.gameObject.name}: side={hg.data.side} analyzed={hg.data.analyzed} grip={N(hg.data.grip != null ? hg.data.grip.transform : null)} armIK={hg.data.armIK} w={hg.weight}");
                        break;
                    case SoldierRigSetup:
                    case LocalSoldierDriver:
                    case SpineAimConstraint:
                    case HipsPoseConstraint:
                    case SkeletonPoseConstraint:
                        sb.AppendLine($"  !! 旧分支组件残留: {c.GetType().Name} on {c.gameObject.name}");
                        break;
                }
            }
            Debug.Log(sb.ToString());
        }

        private static string N(Object o) => o != null ? o.name : "null";

        private static void BuildInternal(GameObject root)
        {
            // ---- 1) 剥离旧分支组件与旧 rig 节点（HandGrip 实例保留）----
            foreach (var c in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c == null) continue;
                if (c is SoldierRigSetup || c is LocalSoldierDriver
                    || c is SpineAimConstraint || c is HipsPoseConstraint
                    || c is SkeletonPoseConstraint || c is MultiPositionConstraint)
                    Object.DestroyImmediate(c, true);
            }
            foreach (var name in new[] { "LeftHint", "RightHint", "TwoHandRig", "WeaponAnchor" })
            {
                var t = FindDeep(root.transform, name);
                if (t != null) Object.DestroyImmediate(t.gameObject, true);
            }

            // 旧 Rig 层节点：Rig_LowerBody / Rig_UpperBody 整体删除重建；
            // Rig_Hands 保留（LeftGrip/RightGrip 的 HandGrip 分析常量在组件上），
            // 仅删旧 ArmIK 子节点；Rig_Drivers 清空重建。
            var oldLower = root.transform.Find("Rig_LowerBody");
            if (oldLower != null) Object.DestroyImmediate(oldLower.gameObject, true);
            var oldUpper = root.transform.Find("Rig_UpperBody");
            if (oldUpper != null) Object.DestroyImmediate(oldUpper.gameObject, true);

            Transform hands = root.transform.Find("Rig_Hands");
            if (hands != null)
            {
                foreach (var childName in new[] { "ArmIK_L", "ArmIK_R" })
                {
                    var c = hands.Find(childName);
                    if (c != null) Object.DestroyImmediate(c.gameObject, true);
                }
                foreach (var hb in hands.GetComponentsInChildren<MonoBehaviour>(true))
                    if (hb is LocalSoldierDriver) Object.DestroyImmediate(hb, true);
            }
            else
            {
                hands = NewChild(root.transform, "Rig_Hands");
            }
            var handsRig = hands.GetComponent<Rig>();
            if (handsRig == null) handsRig = hands.gameObject.AddComponent<Rig>();
            handsRig.weight = 1f;

            var drivers = root.transform.Find(DriversRoot);
            if (drivers != null) Object.DestroyImmediate(drivers.gameObject, true);
            drivers = NewChild(root.transform, DriversRoot);
            var aimSource = NewChild(drivers, "Driver_AimSource");
            var aimTarget = NewChild(drivers, "Driver_AimTarget");
            var footTargetL = NewChild(drivers, "Driver_FootTarget_L");
            var footTargetR = NewChild(drivers, "Driver_FootTarget_R");
            var footHintL = NewChild(drivers, "Driver_FootHint_L");
            var footHintR = NewChild(drivers, "Driver_FootHint_R");

            // ---- 2) 规范节点：头部骨骼子对象 AimSource ----
            var head = FindDeep(root.transform, HeadBone);
            if (head == null) { Debug.LogError($"[SoldierBake] {HeadBone} 不存在"); return; }
            if (head.Find("AimSource") == null)
            {
                var aim = NewChild(head, "AimSource");
                aim.localPosition = Vector3.zero;
            }

            // ---- 3) Rig_LowerBody：脚 IK（腿链，仅钉位置）----
            var lower = NewChild(root.transform, "Rig_LowerBody");
            lower.gameObject.AddComponent<Rig>().weight = 1f;
            BuildTwoBoneIK(lower, "FootIK_L", "DEF-thigh.L", "DEF-shin.L", "DEF-foot.L",
                footTargetL, footHintL, positionWeight: 1f, rotationWeight: 0f, hintWeight: 1f);
            BuildTwoBoneIK(lower, "FootIK_R", "DEF-thigh.R", "DEF-shin.R", "DEF-foot.R",
                footTargetR, footHintR, positionWeight: 1f, rotationWeight: 0f, hintWeight: 1f);

            // ---- 4) Rig_UpperBody：脊柱反扭 ----
            var upper = NewChild(root.transform, "Rig_UpperBody");
            upper.gameObject.AddComponent<Rig>().weight = 1f;
            var upperAimGo = NewChild(upper, "UpperAim");
            var ua = upperAimGo.gameObject.AddComponent<UpperBodyAimConstraint>();
            var ud = ua.data;
            ud.root = root.transform;
            ud.lowerBody = root.transform;          // 模型根 = 滞后后的下半身朝向
            ud.chest = FindDeep(root.transform, ChestBone);
            ud.head = head;
            ud.aimSource = aimSource;
            ud.maxTwist = 60f;
            ud.chestPitchShare = 0.3f;
            ud.headPitchShare = 0.6f;
            ud.facingAxis = new Vector3(0f, 0f, 1f);
            ua.data = ud;

            // ---- 5) Rig_Hands：臂 IK（HandGrip 实例已保留）----
            // 肘 pole（hint）= 预制体 Hint 对象里的 RtElbow/LtElbow **静态基准点**。
            // hint 决定 TwoBoneIK 的弯曲平面：用相对角色静态的点，位置恒可预测；
            // 早期版本由驱动每帧按关节位置重算，会在部分姿势把 hint 推到角色身后，
            // 使肘绕错误平面弯曲（已废弃）。
            var elbowHintR = EnsureHintPoint(root.transform, "Hint/RtElbow");
            var elbowHintL = EnsureHintPoint(root.transform, "Hint/LtElbow");
            BuildTwoBoneIK(hands, "ArmIK_R", "DEF-upper_arm.R", "DEF-forearm.R", "DEF-hand.R",
                null, elbowHintR, positionWeight: 1f, rotationWeight: 1f, hintWeight: 1f);
            BuildTwoBoneIK(hands, "ArmIK_L", "DEF-upper_arm.L", "DEF-forearm.L", "DEF-hand.L",
                null, elbowHintL, positionWeight: 1f, rotationWeight: 1f, hintWeight: 1f);
            // 清理早期版本的驱动侧动态 hint 空物体（已不再使用）。
            foreach (var stale in new[] { "ElbowHint_R", "ElbowHint_L" })
            {
                var t = hands.Find(stale);
                if (t != null) Object.DestroyImmediate(t.gameObject, true);
            }
            foreach (var hg in hands.GetComponentsInChildren<HandGripConstraint>(true))
            {
                var d = hg.data;
                d.armIK = false;   // PHASE14：HandGrip 仅卷指，手臂由 TwoBoneIK 驱动
                hg.data = d;
            }

            // ---- 5b) 约束求值顺序（= 层级顺序，同层内按子物体顺序解算）----
            // 手 IK **必须**先于 HandGrip：HandGrip 的 gripNear（握持贴近度）用
            // **腕位**到握把胶囊轴的距离计算；顺序反了会拿未 IK 的腕位算出
            // gripNear≈0 → 手指不握紧（实测踩坑）。
            ReorderChild(hands, "ArmIK_R", 0);
            ReorderChild(hands, "ArmIK_L", 1);
            ReorderChild(hands, "LeftGrip", 2);
            ReorderChild(hands, "RightGrip", 3);

            // ---- 6) RigBuilder 层序：LowerBody → UpperBody → Hands ----
            var rigBuilder = root.GetComponent<RigBuilder>();
            if (rigBuilder == null) rigBuilder = root.AddComponent<RigBuilder>();
            rigBuilder.layers.Clear();
            rigBuilder.layers.Add(new RigLayer(lower.GetComponent<Rig>()));
            rigBuilder.layers.Add(new RigLayer(upper.GetComponent<Rig>()));
            rigBuilder.layers.Add(new RigLayer(handsRig));

            // ---- 7) 运行时驱动 ----
            var driver = root.GetComponent<SoldierRigDriver>();
            if (driver == null) driver = root.AddComponent<SoldierRigDriver>();
            // 趴姿绕**腹部**旋转（bodyPivotHeight<=0 = 自动取骨盆高度），腹部落在实体竖轴上；
            // 肘/膝 hint 距离（hint 目标由 ElbowHint_* 空物体 + 本驱动每帧摆放提供）。
            driver.bodyPivotHeight = 0f;
            driver.proneBellyTarget = new Vector3(0f, 0.28f, 0f);
            driver.useRestFootPlacement = true;
            driver.footHeightProne = 0.03f;
            EditorUtility.SetDirty(driver);

            // ---- 8) 预制体 Animator 指向 SoldierLoco（若已烘焙）----
            // 保证从预制体直接实例化也拿到"只用八向剪辑"的新控制器，
            // 而非含 Shoot/Reload/Death 剪辑的参考控制器 NetworkSoldierLayers。
            var anim = root.GetComponent<Animator>();
            var loco = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                "Assets/Scripts/Soldier/SoldierLoco.controller");
            if (anim != null && loco != null && anim.runtimeAnimatorController != loco)
                anim.runtimeAnimatorController = loco;

            EditorUtility.SetDirty(root);
        }

        private static void BuildTwoBoneIK(Transform parent, string name,
            string rootBone, string midBone, string tipBone,
            Transform target, Transform hint,
            float positionWeight, float rotationWeight, float hintWeight)
        {
            var go = NewChild(parent, name);
            var ik = go.gameObject.AddComponent<TwoBoneIKConstraint>();
            var d = ik.data;
            d.root = FindDeep(parent.root, rootBone);
            d.mid = FindDeep(parent.root, midBone);
            d.tip = FindDeep(parent.root, tipBone);
            d.target = target;
            d.hint = hint;
            d.targetPositionWeight = positionWeight;
            d.targetRotationWeight = rotationWeight;
            d.hintWeight = hintWeight;
            ik.data = d;
        }

        private static void ReorderChild(Transform parent, string name, int index)
        {
            var c = parent.Find(name);
            if (c != null) c.SetSiblingIndex(index);
        }

        /// <summary>按 "A/B" 路径解析静态基准点（如 Hint/RtElbow）；缺失仅告警不中断。</summary>
        private static Transform EnsureHintPoint(Transform root, string path)
        {
            var t = root.Find(path);
            if (t == null) Debug.LogWarning($"[SoldierBake] 静态基准点缺失: {path}（IK pole 将退化，可能翻转）");
            return t;
        }

        private static Transform NewChild(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        /// <summary>确保同名子物体只保留一个（复用现有者，删掉多余副本）。</summary>
        private static Transform EnsureChild(Transform parent, string name)
        {
            Transform first = null;
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var c = parent.GetChild(i);
                if (c.name != name) continue;
                if (first == null) first = c;
                else Object.DestroyImmediate(c.gameObject, true);   // 删掉旧副本
            }
            if (first != null) return first;
            return NewChild(parent, name);
        }

        /// <summary>删除 parent 下所有名为 name 的子物体（用于幂等重建前清理）。</summary>
        private static void RemoveDuplicates(Transform parent, string name)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var c = parent.GetChild(i);
                if (c.name == name) Object.DestroyImmediate(c.gameObject, true);
            }
        }

        private static Transform FindDeep(Transform root, string name)
        {
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
