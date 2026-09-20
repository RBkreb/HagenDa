using System.Linq;
using HagenDa.Animation.Rigging;   // HandGripConstraint / HandGripAnalyzer
using HagenDa.Networking;          // NetworkHitbox / HitboxPart
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace HagenDa.Animation.RigDriver.EditorTools
{
    /// <summary>
    /// PHASE14 **Natlan** rig 烘焙（幂等，从 0 重写）。
    ///
    /// 目标：Assets/Game/Characters/natlan/Natlan Soldier FBX.prefab
    /// 与 <see cref="SoldierRigBaker"/>（Fatui）同构，差别只在：
    ///   · 不同预制体（Natlan 的模型根就是本预制体的根）；
    ///   · 头骨骼解析走 **humanoid Head**（Natlan = DEF-spine.006；Fatui = .005 是脖子，
    ///     按名查 .005 会静默选错 —— 见 MODEL-PORT-CHECKLIST §0）；
    ///   · Hint / GunCylinder / SprintAim 按 **Natlan 自身骨骼几何**重新摆放
    ///     （骨架缩放 100 vs Fatui 70，直接抄数字会错）；
    ///   · 命中箱尺寸按 Natlan 世界尺寸（米）反算到骨骼本地，不再沿用 Fatui 的比例补偿。
    ///
    /// 产物结构（与 Fatui 一致）：
    ///   Natlan Soldier FBX              [Animator + RigBuilder + SoldierRigDriver]
    ///     ├─ Natlan Soldier Rig.001/    （骨架）
    ///     │    └─ …/DEF-spine.006/      （= humanoid Head）
    ///     │         ├─ AimSource        ← §8 规范瞄准节点（空物体）
    ///     │         ├─ headcollider     ← §9 SphereCollider(trigger) + NetworkHitbox(Head)
    ///     │         └─ …
    ///     │    （命中箱：bodycapsule @ DEF-spine.002，left/rightarmcapsule @ DEF-forearm.*，
    ///     │      left/rightlegcapsule @ DEF-shin.*，全部 trigger + NetworkHitbox）
    ///     ├─ Rig_Drivers/               Driver_AimSource / Driver_AimTarget /
    ///     │                             Driver_FootTarget_L/R / Driver_FootHint_L/R
    ///     ├─ Rig_LowerBody/             FootIK_L/R（TwoBoneIK，仅钉位置 rotW=0）
    ///     ├─ Rig_UpperBody/             UpperAim（UpperBodyAimConstraint，反扭≤60°）
    ///     ├─ Rig_Hands/                 ArmIK_L/R（TwoBoneIK）+ LeftGrip/RightGrip（卷指）
    ///     ├─ Hint/                      RtKnee / LtKnee / RtElbow / LtElbow（静态 pole）
    ///     ├─ GunCylinder/               CapsuleCollider（腰射枪托轨道半径）
    ///     └─ SprintAim/                 冲刺收枪目标点
    ///
    /// 手部握持分析复用 Assets/Game/Characters/Guns/M4_8.prefab 的 RearGripCap/BarrelGripCap
    /// （分析常量全是"手骨/胶囊局部"量，与枪的世界位姿无关），与 FatuiRigBuilder 同法；
    /// 运行时 BindWeapon 再换上真实枪的胶囊。已 analyzed 的 Grip 节点会被**保留**
    /// （§2：bind 常量在组件上，重烘焙不重建）。
    ///
    /// 前置：先运行 HagenDa/SoldierAnim/Bake Soldier Controller 生成 SoldierLoco（缺失仅告警）。
    /// </summary>
    public static class NatlanRigBaker
    {
        const string NatlanPath = "Assets/Game/Characters/natlan/Natlan Soldier FBX.prefab";
        const string GunPath = "Assets/Game/Characters/Guns/M4_8.prefab";
        const string ControllerPath = "Assets/Game/Animation/SoldierLoco.controller";

        const string ArmatureRoot = "Natlan Soldier Rig.001";
        const string ChestBone = "DEF-spine.002";
        const string HeadBoneFallback = "DEF-spine.006";

        // ---- 命中箱世界尺寸（米）---- 骨骼 lossyScale=100，故本地量 = 世界量/100。
        const float HeadWorldRadius = 0.11f;    // 眼部离头心距离（相机球面基准）
        const float TorsoWorldRadius = 0.20f;
        const float TorsoWorldLength = 0.85f;
        const float ArmWorldRadius = 0.055f;
        const float ArmWorldLength = 0.72f;
        const float LegWorldRadius = 0.075f;
        const float LegWorldLength = 0.95f;

        // ---- GunCylinder（腰射轨道柱面）----
        const float GunCylinderRadius = 0.11f;  // 世界半径，须落在 0.09–0.14
        const float GunCylinderLength = 0.30f;

        // ---- 踝骨到脚底的高度（脚 IK 贴地基准）----
        // 驱动 footHeight 的语义是"rest 姿态下踝骨离地高度"，**按模型取值**：
        // Fatui 实测 0.209（与它的 0.21 相符），Natlan 实测 0.116
        // （DEF-foot.L y=0.1158，DEF-toe.L y≈0 → 脚掌正好落在 y=0 平面）。
        // 沿用 Fatui 的 0.21 会把 Natlan 的脚抬起约 9cm。
        const float FootHeight = 0.116f;

        // ---- Hint 静态 pole 摆距（沿角色前/后，米）----
        const float HintKneeForward = 0.30f;
        const float HintElbowBack = 0.30f;
        const float HintKneeOut = 0.02f;
        const float HintElbowOut = 0.06f;

        // =================================================================
        // 菜单入口
        // =================================================================

        [MenuItem("HagenDa/SoldierAnim/Bake Natlan Rig")]
        public static void Bake()
        {
            var contents = PrefabUtility.LoadPrefabContents(NatlanPath);
            if (contents == null)
            {
                Debug.LogError($"[NatlanBake] 找不到预制体 {NatlanPath}");
                return;
            }

            try
            {
                BuildInternal(contents);
                PrefabUtility.SaveAsPrefabAsset(contents, NatlanPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[NatlanBake] rig 烘焙完成 → {NatlanPath}");
        }

        [MenuItem("HagenDa/SoldierAnim/Verify Natlan Rig")]
        public static void Verify()
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(NatlanPath);
            if (go == null) { Debug.LogError($"[NatlanBake] 找不到 {NatlanPath}"); return; }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[NatlanBake] === {NatlanPath} ===");
            var anim = go.GetComponent<Animator>();
            sb.AppendLine($"Animator={(anim != null)} ctrl={(anim != null && anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "null")} human={(anim != null && anim.isHuman)} rootMotion={(anim != null && anim.applyRootMotion)} culling={(anim != null ? anim.cullingMode.ToString() : "-")}");
            if (anim != null && anim.isHuman)
            {
                var head = anim.GetBoneTransform(HumanBodyBones.Head);
                sb.AppendLine($"humanoid Head={N(head)}");
            }
            sb.AppendLine($"SoldierRigDriver={(go.GetComponent<SoldierRigDriver>() != null)}");
            var rb = go.GetComponent<RigBuilder>();
            sb.AppendLine($"RigBuilder layers={(rb != null && rb.layers != null ? rb.layers.Count : -1)}");
            if (rb != null && rb.layers != null)
                foreach (var l in rb.layers) sb.AppendLine($"   layer '{(l.rig != null ? l.rig.name : "null")}'");

            foreach (var t in new[] { "AimSource", "headcollider", "bodycapsule", "leftarmcapsule", "rightarmcapsule", "leftlegcapsule", "rightlegcapsule", "Hint", "GunCylinder", "SprintAim", "Rig_Drivers", "Rig_Hands", "Rig_LowerBody", "Rig_UpperBody" })
                sb.AppendLine($"  node {t}: {(FindDeep(go.transform, t) != null ? "OK" : "MISSING")}");

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
                        sb.AppendLine($"  HandGrip {c.gameObject.name}: side={hg.data.side} analyzed={hg.data.analyzed} grip={N(hg.data.grip != null ? hg.data.grip.transform : null)} armIK={hg.data.armIK} reachBase={hg.data.reachBase} anchorLen={hg.data.anchorLen} w={hg.weight}");
                        break;
                    case NetworkHitbox hb:
                        var hbc = hb.GetComponent<Collider>();
                        sb.AppendLine($"  Hitbox {c.gameObject.name}: part={hb.part} trigger={(hbc != null ? hbc.isTrigger.ToString() : "NO-COL")}");
                        break;
                }
            }

            foreach (var br in go.GetComponentsInChildren<BoneRenderer>(true))
                sb.AppendLine($"  BoneRenderer @ {br.gameObject.name} enabled={br.enabled}");
            var drv = go.GetComponent<SoldierRigDriver>();
            if (drv != null)
                sb.AppendLine($"  driver: bodyPivotHeight={drv.bodyPivotHeight} belly={drv.proneBellyTarget} restFoot={drv.useRestFootPlacement} footProne={drv.footHeightProne}");

            Debug.Log(sb.ToString());
        }

        private static string N(Object o) => o != null ? o.name : "null";

        // =================================================================
        // 构建
        // =================================================================

        private static void BuildInternal(GameObject root)
        {
            var anim = root.GetComponent<Animator>();
            if (anim == null) anim = root.AddComponent<Animator>();

            var armature = root.transform.Find(ArmatureRoot);
            if (armature == null)
            {
                Debug.LogError($"[NatlanBake] 找不到骨架根 '{ArmatureRoot}'，无法建 rig。");
                return;
            }

            // ---- 0) 清理旧分支残留（§11）----
            StripLegacy(root);

            // ---- 头部骨骼：humanoid 优先，名字兜底（§0）----
            Transform head = anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.Head) : null;
            if (head == null) head = FindDeep(root.transform, HeadBoneFallback);
            if (head == null) { Debug.LogError("[NatlanBake] 头骨骼解析失败（humanoid Head / DEF-spine.006 都没有）"); return; }
            var chest = FindDeep(root.transform, ChestBone);
            if (chest == null) { Debug.LogError($"[NatlanBake] 找不到胸骨骼 '{ChestBone}'"); return; }

            var fwd = ModelForward(root.transform);
            var right = Vector3.Cross(Vector3.up, fwd).normalized;

            // ---- 1) Rig_Drivers（清空重建）----
            var oldDrivers = root.transform.Find("Rig_Drivers");
            if (oldDrivers != null) Object.DestroyImmediate(oldDrivers.gameObject, true);
            var drivers = NewChild(root.transform, "Rig_Drivers");
            NewChild(drivers, "Driver_AimSource");
            NewChild(drivers, "Driver_AimTarget");
            var footTargetL = NewChild(drivers, "Driver_FootTarget_L");
            var footTargetR = NewChild(drivers, "Driver_FootTarget_R");
            var footHintL = NewChild(drivers, "Driver_FootHint_L");
            var footHintR = NewChild(drivers, "Driver_FootHint_R");

            // ---- 2) 规范节点：头骨骼下 AimSource（§8）----
            if (head.Find("AimSource") == null)
                NewChild(head, "AimSource");

            // ---- 3) Rig_LowerBody：脚 IK（腿链，仅钉位置）----
            var oldLower = root.transform.Find("Rig_LowerBody");
            if (oldLower != null) Object.DestroyImmediate(oldLower.gameObject, true);
            var lower = NewChild(root.transform, "Rig_LowerBody");
            lower.gameObject.AddComponent<Rig>().weight = 1f;
            BuildTwoBoneIK(lower, "FootIK_L", "DEF-thigh.L", "DEF-shin.L", "DEF-foot.L",
                footTargetL, footHintL, positionWeight: 1f, rotationWeight: 0f, hintWeight: 1f);
            BuildTwoBoneIK(lower, "FootIK_R", "DEF-thigh.R", "DEF-shin.R", "DEF-foot.R",
                footTargetR, footHintR, positionWeight: 1f, rotationWeight: 0f, hintWeight: 1f);

            // ---- 4) Rig_UpperBody：脊柱反扭瞄准 ----
            var oldUpper = root.transform.Find("Rig_UpperBody");
            if (oldUpper != null) Object.DestroyImmediate(oldUpper.gameObject, true);
            var upper = NewChild(root.transform, "Rig_UpperBody");
            upper.gameObject.AddComponent<Rig>().weight = 1f;
            var upperAim = NewChild(upper, "UpperAim");
            var ua = upperAim.gameObject.AddComponent<UpperBodyAimConstraint>();
            var ud = ua.data;
            ud.root = root.transform;
            ud.lowerBody = root.transform;          // 模型根 = 滞后后的下半身朝向
            ud.chest = chest;
            ud.head = head;
            ud.aimSource = drivers.Find("Driver_AimSource");
            ud.maxTwist = 60f;
            ud.chestPitchShare = 0.3f;
            ud.headPitchShare = 0.6f;
            ud.facingAxis = new Vector3(0f, 0f, 1f);
            ua.data = ud;

            // ---- 5) Hint：4 个静态 pole（§5，按 Natlan 骨骼几何摆放）----
            BuildHint(root.transform, fwd, right);

            // ---- 6) Rig_Hands：臂 IK + 卷指（Grip 节点保留复用）----
            var hands = root.transform.Find("Rig_Hands");
            if (hands == null) hands = NewChild(root.transform, "Rig_Hands");
            foreach (var childName in new[] { "ArmIK_L", "ArmIK_R" })
            {
                var c = hands.Find(childName);
                if (c != null) Object.DestroyImmediate(c.gameObject, true);
            }
            var handsRig = hands.GetComponent<Rig>();
            if (handsRig == null) handsRig = hands.gameObject.AddComponent<Rig>();
            handsRig.weight = 1f;

            var elbowHintR = EnsureHintPoint(root.transform, "Hint/RtElbow");
            var elbowHintL = EnsureHintPoint(root.transform, "Hint/LtElbow");
            BuildTwoBoneIK(hands, "ArmIK_R", "DEF-upper_arm.R", "DEF-forearm.R", "DEF-hand.R",
                null, elbowHintR, positionWeight: 1f, rotationWeight: 1f, hintWeight: 1f);
            BuildTwoBoneIK(hands, "ArmIK_L", "DEF-upper_arm.L", "DEF-forearm.L", "DEF-hand.L",
                null, elbowHintL, positionWeight: 1f, rotationWeight: 1f, hintWeight: 1f);

            var gripR = EnsureGrip(hands, "RightGrip", HandGripSide.Right, root);
            var gripL = EnsureGrip(hands, "LeftGrip", HandGripSide.Left, root);
            if (gripR != null) { var d = gripR.data; d.armIK = false; gripR.data = d; }
            if (gripL != null) { var d = gripL.data; d.armIK = false; gripL.data = d; }

            // 求值顺序 = 层级顺序（手 IK 必须先于 HandGrip：gripNear 用腕位算）
            ReorderChild(hands, "ArmIK_R", 0);
            ReorderChild(hands, "ArmIK_L", 1);
            ReorderChild(hands, "LeftGrip", 2);
            ReorderChild(hands, "RightGrip", 3);

            // ---- 7) GunCylinder（§6）/ SprintAim（§7）----
            BuildGunCylinder(root.transform, "DEF-upper_arm.R");
            if (root.transform.Find("SprintAim") == null)
            {
                var sprint = NewChild(root.transform, "SprintAim");
                sprint.localPosition = new Vector3(-0.50f, 1.15f, 0.35f);   // 左前下方（收枪抱身前）
                sprint.localRotation = Quaternion.Euler(28f, 0f, 0f);
            }

            // ---- 8) 命中箱套件（§9）----
            BuildHitboxes(root, anim, head, chest);

            // ---- 9) RigBuilder 层序：LowerBody → UpperBody → Hands ----
            var rigBuilder = root.GetComponent<RigBuilder>();
            if (rigBuilder == null) rigBuilder = root.AddComponent<RigBuilder>();
            rigBuilder.layers.Clear();
            rigBuilder.layers.Add(new RigLayer(lower.GetComponent<Rig>()));
            rigBuilder.layers.Add(new RigLayer(upper.GetComponent<Rig>()));
            rigBuilder.layers.Add(new RigLayer(handsRig));

            // ---- 10) 运行时驱动 ----
            var driver = root.GetComponent<SoldierRigDriver>();
            if (driver == null) driver = root.AddComponent<SoldierRigDriver>();
            driver.bodyPivotHeight = 0f;                        // 0 = 自动取腹部/骨盆高度
            driver.proneBellyTarget = new Vector3(0f, 0.28f, 0f);
            driver.useRestFootPlacement = true;
            driver.footHeight = FootHeight;                     // 模型相关（Fatui 0.21 / Natlan 0.116）
            driver.footHeightProne = 0.03f;
            EditorUtility.SetDirty(driver);

            // ---- 11) Animator：控制器 + 关根运动（§10）----
            var loco = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            if (loco == null)
                Debug.LogWarning($"[NatlanBake] 找不到 {ControllerPath} —— 先运行 HagenDa/SoldierAnim/Bake Soldier Controller；Animator 控制器暂不变更。");
            else if (anim.runtimeAnimatorController != loco)
                anim.runtimeAnimatorController = loco;
            if (loco != null)
            {
                anim.applyRootMotion = false;                   // Humanoid 剪辑会重写模型根 localPosition
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                anim.updateMode = AnimatorUpdateMode.Normal;
            }

            EditorUtility.SetDirty(root);
        }

        // =================================================================
        // Hint（§5）
        // =================================================================

        /// <summary>
        /// 4 个静态 pole：膝点放膝**正前方**（决定膝朝前弯），肘点放肘**后方偏外侧**
        /// （决定肘朝后下弯）。按 Natlan 自身骨骼几何摆放 —— 骨架缩放 100、比例与 Fatui
        /// 不同，不能沿用 Fatui 的本地数字。
        /// </summary>
        private static void BuildHint(Transform root, Vector3 fwd, Vector3 right)
        {
            var old = root.Find("Hint");
            if (old != null) Object.DestroyImmediate(old.gameObject, true);
            var hint = NewChild(root, "Hint");

            var kneeL = FindDeep(root, "DEF-shin.L");
            var kneeR = FindDeep(root, "DEF-shin.R");
            var elbowL = FindDeep(root, "DEF-forearm.L");
            var elbowR = FindDeep(root, "DEF-forearm.R");

            PlaceHintPoint(hint, "RtKnee", kneeR, kneeR != null ? kneeR.position + fwd * HintKneeForward + right * HintKneeOut : root.position + fwd * 0.3f);
            PlaceHintPoint(hint, "LtKnee", kneeL, kneeL != null ? kneeL.position + fwd * HintKneeForward - right * HintKneeOut : root.position + fwd * 0.3f);
            PlaceHintPoint(hint, "RtElbow", elbowR, elbowR != null ? elbowR.position - fwd * HintElbowBack + right * HintElbowOut : root.position - fwd * 0.3f);
            PlaceHintPoint(hint, "LtElbow", elbowL, elbowL != null ? elbowL.position - fwd * HintElbowBack - right * HintElbowOut : root.position - fwd * 0.3f);
        }

        private static void PlaceHintPoint(Transform parent, string name, Transform debugBone, Vector3 worldPos)
        {
            var t = NewChild(parent, name);
            t.position = worldPos;                 // 世界⇒本地换算交由 Unity
            t.rotation = Quaternion.identity;
            if (debugBone == null)
                Debug.LogWarning($"[NatlanBake] Hint/{name}: 参考骨骼缺失，用的是退化占位位置。");
        }

        // =================================================================
        // GunCylinder（§6）
        // =================================================================

        /// <summary>
        /// 右肩柱体：腰射时枪托(Stock)绕它滑动。驱动 <c>CylinderWorldRadius</c> 只取
        /// Collider.bounds 的中位边 → 世界半径 = radius × 轴垂直缩放。这里放纯点物体
        /// （localScale=1），故世界半径 = radius，直接落 0.11（目标区间 0.09–0.14）。
        /// isTrigger=true：只用于量半径，避免给角色多加一层实体碰撞。
        /// </summary>
        private static void BuildGunCylinder(Transform root, string shoulderBone)
        {
            var old = root.Find("GunCylinder");
            if (old != null) Object.DestroyImmediate(old.gameObject, true);

            var shoulder = FindDeep(root, shoulderBone);
            var cyl = NewChild(root, "GunCylinder");
            cyl.localPosition = shoulder != null ? root.InverseTransformPoint(shoulder.position) : new Vector3(0.16f, 1.47f, 0f);
            cyl.localRotation = Quaternion.identity;
            cyl.localScale = Vector3.one;

            var cap = cyl.gameObject.AddComponent<CapsuleCollider>();
            cap.direction = 1;                      // Y：沿手臂/竖直
            cap.radius = GunCylinderRadius;
            cap.height = GunCylinderLength;
            cap.center = Vector3.zero;
            cap.isTrigger = true;
        }

        // =================================================================
        // 命中箱（§9）
        // =================================================================

        /// <summary>
        /// 全套命中部位（头/躯干/双臂/双腿）。**要建就建全**：只要有任意一个 NetworkHitbox，
        /// 子弹射线就会忽略移动胶囊（NetworkPlayerHealth.HasHitboxes），留下空洞就会"打不到"。
        /// 全部 trigger + NetworkHitbox，绑在骨骼下随动画变形。
        /// 尺寸按世界（米）给定，再除以骨骼 lossyScale 落到本地。
        /// </summary>
        private static void BuildHitboxes(GameObject root, Animator anim, Transform head, Transform chest)
        {
            var spine = FindDeep(root.transform, "DEF-spine");
            var neck = FindDeep(root.transform, "DEF-spine.004");
            var thighL = FindDeep(root.transform, "DEF-thigh.L");
            var thighR = FindDeep(root.transform, "DEF-thigh.R");
            var footL = FindDeep(root.transform, "DEF-foot.L");
            var footR = FindDeep(root.transform, "DEF-foot.R");
            var shinL = FindDeep(root.transform, "DEF-shin.L");
            var shinR = FindDeep(root.transform, "DEF-shin.R");
            var upArmL = FindDeep(root.transform, "DEF-upper_arm.L");
            var upArmR = FindDeep(root.transform, "DEF-upper_arm.R");
            var foreL = FindDeep(root.transform, "DEF-forearm.L");
            var foreR = FindDeep(root.transform, "DEF-forearm.R");
            var handL = FindDeep(root.transform, "DEF-hand.L");
            var handR = FindDeep(root.transform, "DEF-hand.R");

            // 头：球，中心=头骨原点
            MakeHitbox(head, "headcollider", HitboxPart.Head, head.position, Vector3.up, HeadWorldRadius, 0f);

            // 躯干：骨盆→颈 的中点为心，轴沿该方向
            if (spine != null && neck != null)
            {
                var mid = (spine.position + neck.position) * 0.5f;
                MakeHitbox(chest, "bodycapsule", HitboxPart.Body, mid, (neck.position - spine.position).normalized, TorsoWorldRadius, TorsoWorldLength);
            }

            // 臂：肩→手；腿：胯→踝
            if (upArmL != null && handL != null) MakeHitbox(foreL, "leftarmcapsule", HitboxPart.Limb, (upArmL.position + handL.position) * 0.5f, (handL.position - upArmL.position).normalized, ArmWorldRadius, ArmWorldLength);
            if (upArmR != null && handR != null) MakeHitbox(foreR, "rightarmcapsule", HitboxPart.Limb, (upArmR.position + handR.position) * 0.5f, (handR.position - upArmR.position).normalized, ArmWorldRadius, ArmWorldLength);
            if (thighL != null && footL != null) MakeHitbox(shinL, "leftlegcapsule", HitboxPart.Limb, (thighL.position + footL.position) * 0.5f, (footL.position - thighL.position).normalized, LegWorldRadius, LegWorldLength);
            if (thighR != null && footR != null) MakeHitbox(shinR, "rightlegcapsule", HitboxPart.Limb, (thighR.position + footR.position) * 0.5f, (footR.position - thighR.position).normalized, LegWorldRadius, LegWorldLength);
        }

        /// <summary>
        /// 在 <paramref name="bone"/> 下建一个命中箱。<paramref name="length"/> = 0 表示球。
        /// 位置/朝向按**世界**给定后交由 Unity 换算到骨骼本地，胶囊轴对齐 <paramref name="axisWorld"/>。
        /// </summary>
        private static void MakeHitbox(Transform bone, string name, HitboxPart part,
            Vector3 centerWorld, Vector3 axisWorld, float worldRadius, float worldLength)
        {
            if (bone == null) { Debug.LogWarning($"[NatlanBake] 命中箱 {name}: 宿主骨骼缺失，跳过。"); return; }

            // 幂等：清掉同名旧件
            var existing = bone.Find(name);
            if (existing != null) Object.DestroyImmediate(existing.gameObject, true);

            var go = new GameObject(name);
            go.transform.SetParent(bone, false);
            go.transform.position = centerWorld;
            if (worldLength > 0f && axisWorld.sqrMagnitude > 1e-8f)
                go.transform.rotation = Quaternion.FromToRotation(Vector3.up, axisWorld.normalized);
            else
                go.transform.rotation = bone.rotation;
            go.transform.localScale = Vector3.one;

            // 骨骼 lossyScale 常为 100（Natlan）；世界⇒本地。
            var ls = go.transform.lossyScale;
            float k = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
            if (k < 1e-6f) k = 1f;

            var nh = go.AddComponent<NetworkHitbox>();
            nh.part = part;

            if (worldLength > 0f)
            {
                var cap = go.AddComponent<CapsuleCollider>();
                cap.direction = 1;                  // Y（已用 rotation 对齐到 limb 轴）
                cap.center = Vector3.zero;
                cap.radius = worldRadius / k;
                cap.height = Mathf.Max(worldLength / k, 2f * cap.radius + 1e-4f);
                cap.isTrigger = true;
            }
            else
            {
                var sph = go.AddComponent<SphereCollider>();
                sph.center = Vector3.zero;
                sph.radius = worldRadius / k;
                sph.isTrigger = true;
            }
        }

        // =================================================================
        // 旧分支残留清理（§11）
        // =================================================================

        /// <summary>
        /// 清掉旧分支残留与本工具不认识的旧碰撞体：
        ///   · 旧 rig 节点（TwoHandRig / Rig_Pose / LeftHint / RightHint / WeaponAnchor / GripCapR/L）；
        ///   · 模型内的枪（M4_8，枪必须挂实体层 WeaponBasis，不能留在模型里）；
        ///   · 早期编辑器脚本留下的无名碰撞体（Sphere @ 头骨 / Capsule @ 骨盆）——
        ///     非 trigger、无 NetworkHitbox，会与地形实体碰撞；本工具会用规范的
        ///     headcollider/bodycapsule 取代。
        /// </summary>
        private static void StripLegacy(GameObject root)
        {
            foreach (var name in new[] { "TwoHandRig", "Rig_Pose", "LeftHint", "RightHint", "WeaponAnchor", "GripCapR", "GripCapL" })
            {
                var t = root.transform.Find(name);
                if (t != null) { Object.DestroyImmediate(t.gameObject, true); Debug.Log($"[NatlanBake] 清理旧节点 {name}"); }
            }

            // 模型内的枪（含其下的 AimSource）
            foreach (var t in root.GetComponentsInChildren<Transform>(true).ToArray())
            {
                if (t == null || t == root.transform) continue;
                if (t.name == "M4_8" || t.name.StartsWith("M4 ") || t.name == "AK74")
                {
                    string gunName = t.name;          // 先取名字：Destroy 后再读会抛 MissingReference
                    Object.DestroyImmediate(t.gameObject, true);
                    Debug.Log($"[NatlanBake] 从模型内移除残留枪 {gunName}（枪应由实体层 WeaponBasis 装配）");
                }
            }

            // 无名旧碰撞体：恰好 {MeshFilter + Sphere/CapsuleCollider} 且无 NetworkHitbox
            var armature = root.transform.Find(ArmatureRoot);
            if (armature != null)
            {
                foreach (var col in armature.GetComponentsInChildren<Collider>(true))
                {
                    if (col == null) continue;
                    var go = col.gameObject;
                    if (go.GetComponent<NetworkHitbox>() != null) continue;
                    bool isPlainSphere = col is SphereCollider && go.name == "Sphere";
                    bool isPlainCapsule = col is CapsuleCollider && go.name == "Capsule";
                    if (isPlainSphere || isPlainCapsule)
                    {
                        string colName = go.name;     // 先取名字：Destroy 后再读会抛 MissingReference
                        Object.DestroyImmediate(go, true);
                        Debug.Log($"[NatlanBake] 清理旧无名碰撞体 {colName} @ {armature.name}");
                    }
                }
            }
        }

        // =================================================================
        // 约束/节点工具
        // =================================================================

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
            d.targetRotationWeight = rotationWeight;   // 脚 IK 必须为 0（否则鞋底朝上）
            d.hintWeight = hintWeight;
            ik.data = d;
            if (positionWeight == 0f && rotationWeight == 0f && hintWeight == 0f)
                Debug.LogWarning($"[NatlanBake] {name}: 权重全 0，约束不会生效。");
        }

        /// <summary>
        /// 取/建卷指约束。<b>已 analyzed 的既有节点原样保留</b>（§2：bind 分析常量存在
        /// 组件上，重建会丢失标定）。未分析时用 M4_8 的握把胶囊做一次 bind 姿态分析。
        /// </summary>
        private static HandGripConstraint EnsureGrip(Transform hands, string name, HandGripSide side, GameObject root)
        {
            var t = hands.Find(name);
            HandGripConstraint g;
            if (t != null)
            {
                g = t.GetComponent<HandGripConstraint>();
                if (g == null) g = t.gameObject.AddComponent<HandGripConstraint>();
            }
            else
            {
                t = NewChild(hands, name);
                g = t.gameObject.AddComponent<HandGripConstraint>();
            }
            g.weight = 1f;

            var d = g.data;
            d.side = side;
            d.thumbXBias = 0.8f;
            g.data = d;

            if (!g.data.analyzed) AnalyzeGrip(g, root);
            return g;
        }

        /// <summary>
        /// 用枪预制体的握把胶囊做 bind 姿态分析。分析常量全是"手骨/胶囊局部"量，
        /// 与枪的世界位姿无关 → 临时实例化（放在模型下以便解析手骨）测完即销毁，
        /// 不把枪留在模型里、也不保留胶囊引用（运行时 BindWeapon 绑外部基准的枪）。
        /// </summary>
        private static void AnalyzeGrip(HandGripConstraint grip, GameObject root)
        {
            var gunPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GunPath);
            if (gunPrefab == null)
            {
                Debug.LogWarning($"[NatlanBake] 找不到枪预制体 {GunPath}，跳过 {grip.name} 握持分析（指节将不会弯曲）。");
                return;
            }

            var gun = Object.Instantiate(gunPrefab);
            gun.name = "__TempGripAnalysis";
            try
            {
                gun.transform.SetParent(root.transform, false);
                gun.transform.localPosition = Vector3.zero;
                gun.transform.localRotation = Quaternion.identity;
                gun.transform.localScale = Vector3.one;

                string capName = grip.data.side == HandGripSide.Left ? "BarrelGripCap" : "RearGripCap";
                var capT = FindDeep(gun.transform, capName);
                var cap = capT != null ? capT.GetComponent<CapsuleCollider>() : null;
                if (cap == null)
                {
                    Debug.LogWarning($"[NatlanBake] {grip.name}: 枪上找不到 {capName} 胶囊，跳过分析。");
                    return;
                }

                var anim = root.GetComponent<Animator>();
                ref var d = ref grip.data;
                d.grip = cap;
                d.analyzed = false;

                if (!HandGripAnalyzer.ResolveSkeleton(ref d, anim))
                {
                    Debug.LogWarning($"[NatlanBake] {grip.name} 骨架解析失败，请检查 DEF-hand/DEF-f_* 命名。");
                    d.grip = null;
                    return;
                }
                HandGripAnalyzer.AnalyzePose(ref d);

                d.grip = null;          // 引用不保留：运行时绑定外部基准下的枪
                d.analyzed = true;
                EditorUtility.SetDirty(grip);
            }
            finally
            {
                Object.DestroyImmediate(gun);
            }
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
            if (t == null) Debug.LogWarning($"[NatlanBake] 静态基准点缺失: {path}（IK pole 将退化，可能翻转）");
            return t;
        }

        /// <summary>模型根局部空间的前方：用 脚→脚尖 的水平方向推导（不受骨架内部翻转影响）。</summary>
        private static Vector3 ModelForward(Transform root)
        {
            var foot = FindDeep(root, "DEF-foot.L");
            var toe = FindDeep(root, "DEF-toe.L");
            if (foot != null && toe != null)
            {
                Vector3 f = root.InverseTransformPoint(toe.position) - root.InverseTransformPoint(foot.position);
                f.y = 0f;
                if (f.sqrMagnitude > 1e-8f) return root.TransformDirection(f.normalized);
            }
            return Vector3.forward;
        }

        private static Transform NewChild(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
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
