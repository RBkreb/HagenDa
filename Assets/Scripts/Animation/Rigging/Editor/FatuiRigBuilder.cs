using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;
using HagenDa.Animation;
using HagenDa.Animation.Rigging;

namespace HagenDa.Animation.Rigging.EditorTools
{
    /// <summary>
    /// PHASE14 Fatui 角色动画 rig 烘焙（幂等，可反复运行）。
    ///
    /// 目标预制体：Assets/Model/Fatui/Fatui with Collider.prefab
    /// 结构（与已有的 Natlan rig 同构，但 Fatui 从 0 建，不与 Natlan 交叉）：
    ///   Fatui with Collider                     [Animator + RigBuilder + SoldierRigSetup]
    ///     Fatui bodyguard rig.001               (armature；Humanoid Hips = 该节点)
    ///     Rig_Drivers/                          (驱动空物体，模型根局部空间)
    ///       Driver_Yaw / Driver_Offset / Driver_AimSource / Driver_AimTarget
    ///       Driver_FootTarget_L/R / Driver_FootHint_L/R / WeaponAnchor
    ///     Rig_LowerBody/  HipsPose + HipsPos + FootIK_L/R
    ///     Rig_UpperBody/  SpineAim
    ///     Rig_Hands/      ArmIK_R + ArmIK_L + RightGrip + LeftGrip
    ///
    /// 手部 grip 的 bind 姿态分析在本工具内用新枪预制体的握把胶囊完成
    /// （Assets/Model/Guns/M4_8.prefab 的 RearGripCap/BarrelGripCap）：
    /// 分析常量全部是“手骨局部 / 胶囊局部”量，与枪的世界位姿无关，故用临时
    /// 实例测完即可销毁；运行时由 SoldierRigSetup.BindWeapon 换上真实胶囊
    /// （analyzed=true → Binder 跳过重分析，沿用烘焙常量）。
    ///
    /// 旧的手工 TwoHandRig（RightHandRig/LeftHandRig/LeftGrip/RightGrip）会被
    /// 移除并迁移到 Rig_Hands，避免同链双解算器互写。
    /// </summary>
    public static class FatuiRigBuilder
    {
        const string FatuiPath = "Assets/Model/Fatui/Fatui with Collider.prefab";
        const string GunPath = "Assets/Model/Guns/M4_8.prefab";

        const string ModelRootName = "Fatui with Collider";
        const string ArmatureName = "Fatui bodyguard rig.001";
        const string StaleRigName = "TwoHandRig";
        const string DriversRoot = "Rig_Drivers";

        // 新枪握把锚点（PHASE14 枪械专项）
        const string RightGripAnchor = "RearGrip";       // 后握把 -> 右手
        const string LeftGripAnchor = "BarrelGrip";      // 枪管/护木 -> 左手
        const string RightGripCapsule = "RearGripCap";
        const string LeftGripCapsule = "BarrelGripCap";

        static readonly Vector3 GunHoldLocalPos = new Vector3(0.082f, 1.42f, 0.40f);

        [MenuItem("HagenDa/Rigging/Build Fatui Rig")]
        public static void Build()
        {
            var contents = PrefabUtility.LoadPrefabContents(FatuiPath);
            if (contents == null)
            {
                Debug.LogError($"[FatuiRig] 找不到预制体 {FatuiPath}");
                return;
            }

            int made;
            try
            {
                made = BuildInternal(contents);
                PrefabUtility.SaveAsPrefabAsset(contents, FatuiPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[FatuiRig] 完成：{made} 个约束写入 {FatuiPath}。" +
                      "枪由实体层装配在 WeaponBasis 下(模型内不留枪),运行时 BindWeapon 绑定。");
        }

        [MenuItem("HagenDa/Rigging/Verify Fatui Rig")]
        public static void Verify()
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(FatuiPath);
            if (go == null) { Debug.LogError($"[FatuiRig] 找不到 {FatuiPath}"); return; }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[FatuiRig] === {FatuiPath} ===");
            var anim = go.GetComponent<Animator>();
            sb.AppendLine($"Animator={(anim != null)} ctrl={(anim != null && anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "null")} human={(anim != null && anim.isHuman)}");
            var srs = go.GetComponent<SoldierRigSetup>();
            sb.AppendLine($"SoldierRigSetup={(srs != null)}");
            var rb = go.GetComponent<RigBuilder>();
            sb.AppendLine($"RigBuilder layers={(rb != null && rb.layers != null ? rb.layers.Count : -1)}");
            if (rb != null && rb.layers != null)
                foreach (var L in rb.layers)
                    sb.AppendLine($"   layer '{(L.rig != null ? L.rig.name : "null")}' active={L.active}");

            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform) continue;
                var two = c as TwoBoneIKConstraint;
                if (two != null)
                {
                    var d = two.data;
                    sb.AppendLine($"  TwoBoneIK {c.gameObject.name}: root={N(d.root)} mid={N(d.mid)} tip={N(d.tip)} target={N(d.target)} hint={N(d.hint)} pW={d.targetPositionWeight} rW={d.targetRotationWeight} w={two.weight}");
                    continue;
                }
                var g = c as HandGripConstraint;
                if (g != null)
                {
                    sb.AppendLine($"  HandGrip {c.gameObject.name}: side={g.data.side} analyzed={g.data.analyzed} grip={N(g.data.grip ? g.data.grip.transform : null)} armIK={g.data.armIK} thumbXBias={g.data.thumbXBias} flexThumb=({ThumbFlex(g)}) w={g.weight}");
                    continue;
                }
                var hp = c as HipsPoseConstraint;
                if (hp != null) { sb.AppendLine($"  HipsPose {c.gameObject.name}: hips={N(hp.data.hips)} yaw={N(hp.data.yawDriver)} facing={hp.data.facingAxis} w={hp.weight}"); continue; }
                var mp = c as MultiPositionConstraint;
                if (mp != null) { var d = mp.data; sb.AppendLine($"  MultiPosition {c.gameObject.name}: obj={N(d.constrainedObject)} srcs={d.sourceObjects.Count} src0={N(d.sourceObjects.Count > 0 ? d.sourceObjects.GetTransform(0) : null)} w={mp.weight}"); continue; }
                var sa = c as SpineAimConstraint;
                if (sa != null) { sb.AppendLine($"  SpineAim {c.gameObject.name}: chest={N(sa.data.chest)} head={N(sa.data.head)} aim={N(sa.data.aimSource)} facing={sa.data.facingAxis} maxTwist={sa.data.maxTwist} w={sa.weight}"); continue; }
            }
            Debug.Log(sb.ToString());
        }

        static string ThumbFlex(HandGripConstraint g)
        {
            if (g.data.fingers == null || g.data.fingers.Length < 3) return "?";
            var f = g.data.fingers[2];
            return $"A={f.flexA} B={f.flexB} axisB={f.axisB}";
        }

        static string N(Transform t) => t != null ? t.name : "null";

        // =================================================================
        // 构建
        // =================================================================

        static int BuildInternal(GameObject root)
        {
            root.name = ModelRootName;

            var anim = root.GetComponent<Animator>();
            if (anim == null) anim = root.AddComponent<Animator>();
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            var armature = Find(root.transform, ArmatureName);
            if (armature == null)
            {
                Debug.LogWarning($"[FatuiRig] 找不到骨架根 '{ArmatureName}'，无法建 rig。");
                return 0;
            }

            // ---- 迁移/清理 ----
            // 1) 旧的手工 TwoHandRig(约束重建到 Rig_Hands 后删除)
            var stale = root.transform.Find(StaleRigName);
            if (stale != null) Object.DestroyImmediate(stale.gameObject);

            // 2) PHASE14:枪械基准必须在模型**之外**(与模型同级挂在胶囊根下),
            //    模型内不得残留枪 —— 否则动画层级里会多出一把“自装配”的枪,
            //    且其 IK 目标会经 AnimationStream 读到过期值。清掉模型内所有 M4。
            int stripped = StripGunsInsideModel(root);
            if (stripped > 0)
                Debug.Log($"[FatuiRig] 已从模型内移除 {stripped} 把残留枪(枪应由实体层装配在 WeaponBasis 下)。");

            // ---- 驱动空物体 ----
            var driverRoot = EnsureChild(root.transform, DriversRoot);
            Vector3 fwd = ModelForward(root.transform);
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            Vector3 facingAxisHips = armature.InverseTransformDirection(fwd).normalized;

            // 瞄准锚必须挂**动画骨架之外**(Rig_Drivers 下):骨架内的 Transform 由
            // AnimationStream 持有副本,rig 作业读不到驱动侧写入的相机位姿 → SpineAim
            // 算不出反扭(上半身不跟随)。骨架外者读实时场景值(与 IK 目标同理)。
            var headBone = Find(root.transform, "DEF-spine.005");
            var chestBone = Find(root.transform, "DEF-spine.001");
            var footL = Find(root.transform, "DEF-foot.L");
            var footR = Find(root.transform, "DEF-foot.R");

            var driverYaw = EnsureChild(driverRoot, "Driver_Yaw");
            var offsetDriver = EnsureChild(driverRoot, "Driver_Offset");
            var aimTarget = EnsureChild(driverRoot, "Driver_AimTarget");
            var footTargetL = EnsureChild(driverRoot, "Driver_FootTarget_L");
            var footTargetR = EnsureChild(driverRoot, "Driver_FootTarget_R");
            var footHintL = EnsureChild(driverRoot, "Driver_FootHint_L");
            var footHintR = EnsureChild(driverRoot, "Driver_FootHint_R");
            var weaponAnchor = EnsureChild(driverRoot, "WeaponAnchor");

            driverYaw.localPosition = Vector3.zero;
            driverYaw.localRotation = Quaternion.identity;
            offsetDriver.localPosition = Vector3.zero;
            offsetDriver.localRotation = Quaternion.identity;

            float footHeight = 0.08f;
            float eyeLift = 0.06f;

            // 瞄准锚:骨架之外的驱动空物体(运行时由 SoldierRigSetup 写入相机眼位与朝向)。
            var aimSource = EnsureChild(driverRoot, "Driver_AimSource");
            if (headBone != null)
            {
                Vector3 headL = root.transform.InverseTransformPoint(headBone.position);
                aimSource.localPosition = headL + Vector3.up * eyeLift;
            }
            else aimSource.localPosition = new Vector3(0f, 1.65f, 0.10f);
            aimSource.localRotation = Quaternion.LookRotation(fwd, Vector3.up);
            // 清掉早期版本误建在头骨下的同名锚,避免与驱动器锚混淆。
            var strayAim = Find(headBone, "AimSource");
            if (strayAim != null) Object.DestroyImmediate(strayAim.gameObject);
            aimTarget.localPosition = aimSource.localPosition + fwd * 30f;
            aimTarget.localRotation = Quaternion.identity;

            SetFootDrivers(footTargetL, footHintL, footL, root.transform, fwd, footHeight);
            SetFootDrivers(footTargetR, footHintR, footR, root.transform, fwd, footHeight);

            if (chestBone != null)
            {
                Vector3 chestL = root.transform.InverseTransformPoint(chestBone.position);
                // PHASE14：枪基准建议放右前胸；运行时会被 SetWeaponAnchorPosition 覆盖，
                // 这里给一个合理默认并允许用户手调。
                weaponAnchor.localPosition = chestL + right * 0.09f + fwd * 0.28f;
            }
            else weaponAnchor.localPosition = GunHoldLocalPos;
            weaponAnchor.localRotation = Quaternion.LookRotation(fwd, Vector3.up);

            // ---- Rig 层 ----
            var rigLower = EnsureRig(root.transform, "Rig_LowerBody");
            var rigUpper = EnsureRig(root.transform, "Rig_UpperBody");
            var rigHands = EnsureRig(root.transform, "Rig_Hands");

            int made = 0;

            // --- LowerBody: 髋朝向滞后 + hips 位置(蹲/趴下移) + 双脚贴地 IK ---
            var hipsPose = EnsureConstraint<HipsPoseConstraint>(rigLower.transform, "HipsPose");
            {
                var d = hipsPose.data;
                d.hips = armature;
                d.yawDriver = driverYaw;
                d.facingAxis = facingAxisHips;
                hipsPose.data = d;
                hipsPose.weight = 1f;
                made++;
            }

            var hipsPos = EnsureConstraint<MultiPositionConstraint>(rigLower.transform, "HipsPos");
            {
                var d = hipsPos.data;
                d.constrainedObject = armature;
                var so = default(WeightedTransformArray);
                so.Add(new WeightedTransform(offsetDriver, 1f));
                d.sourceObjects = so;
                d.maintainOffset = true;
                d.offset = Vector3.zero;
                d.constrainedXAxis = true;
                d.constrainedYAxis = true;
                d.constrainedZAxis = true;
                hipsPos.data = d;
                hipsPos.weight = 0f;   // 站立时关闭，蹲/趴由驱动器开
                made++;
            }

            made += BuildFootIK(rigLower.transform, root.transform, "FootIK_L", footL, footTargetL, footHintL);
            made += BuildFootIK(rigLower.transform, root.transform, "FootIK_R", footR, footTargetR, footHintR);

            // --- UpperBody: 脊柱瞄准反扭（上半身立即跟随水平瞄准）---
            var spineAim = EnsureConstraint<SpineAimConstraint>(rigUpper.transform, "SpineAim");
            {
                var d = spineAim.data;
                d.root = root.transform;
                d.hips = armature;
                d.chest = chestBone;
                d.head = headBone;
                d.aimSource = aimSource;
                d.maxTwist = 60f;
                d.chestPitchShare = 0.3f;
                d.headPitchShare = 0.6f;
                d.facingAxis = facingAxisHips;
                spineAim.data = d;
                spineAim.weight = 1f;
                made++;
            }

            // --- Hands: 手臂 Two Bone IK + 手指握持 ---
            made += BuildArmIK(rigHands.transform, root.transform, "ArmIK_R", "R");
            made += BuildArmIK(rigHands.transform, root.transform, "ArmIK_L", "L");

            var gripR = EnsureConstraint<HandGripConstraint>(rigHands.transform, "RightGrip");
            var gripL = EnsureConstraint<HandGripConstraint>(rigHands.transform, "LeftGrip");
            {
                var d = gripR.data;
                d.side = HandGripSide.Right;
                d.armIK = false;          // 手位由 ArmIK 负责，本约束只收指
                gripR.data = d;
                gripR.weight = 1f;

                var d2 = gripL.data;
                d2.side = HandGripSide.Left;
                d2.armIK = false;
                gripL.data = d2;
                gripL.weight = 1f;
                made += 2;
            }

            // --- 枪:PHASE14 结构要求枪械基准是**胶囊的子对象、与模型同级**,
            //     故不烘进模型预制体(见 SetupLocalTest / NetworkSetup 在实体层装配)。
            //     这里只把约束目标引用清空,等运行时 BindWeapon 绑定外部基准下的枪。 ---
            var ikR = rigHands.transform.Find("ArmIK_R")?.GetComponent<TwoBoneIKConstraint>();
            var ikL = rigHands.transform.Find("ArmIK_L")?.GetComponent<TwoBoneIKConstraint>();
            if (ikR != null) { var d = ikR.data; d.target = null; ikR.data = d; }
            if (ikL != null) { var d = ikL.data; d.target = null; ikL.data = d; }
            // grip 的 bind 姿态常量需要一把枪做分析,但引用不保留(运行时绑定)
            AnalyzeGripsFromPrefab(gripR, gripL, root);

            // --- RigBuilder 层序:LowerBody -> UpperBody -> Hands ---
            var rigBuilder = root.GetComponent<RigBuilder>();
            if (rigBuilder == null) rigBuilder = root.AddComponent<RigBuilder>();
            rigBuilder.layers.Clear();
            rigBuilder.layers.Add(new RigLayer(rigLower, true));
            rigBuilder.layers.Add(new RigLayer(rigUpper, true));
            rigBuilder.layers.Add(new RigLayer(rigHands, true));

            // --- 运行时驱动器 ---
            var setup = root.GetComponent<SoldierRigSetup>();
            if (setup == null) setup = root.AddComponent<SoldierRigSetup>();

            EditorUtility.SetDirty(root);
            return made;
        }

        /// <summary>删除模型内的枪模(名以 M4/枪械关键词开头者),返回删除数量。</summary>
        static int StripGunsInsideModel(GameObject root)
        {
            var victims = new List<Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root.transform) continue;
                // 枪模根:名字含 M4 或是一个带 MeshRenderer 的枪预制体实例
                if (t.name == "M4_8" || t.name.StartsWith("M4 ") || t.name == "AK74")
                    victims.Add(t);
            }
            int n = 0;
            foreach (var v in victims)
            {
                if (v == null || v.parent == null) continue;
                Object.DestroyImmediate(v.gameObject);
                n++;
            }
            return n;
        }

        /// <summary>
        /// 用枪预制体的握把胶囊做一次 bind 姿态分析。分析常量全是“手骨/胶囊局部”量,
        /// 与枪的世界位姿无关,故临时实例化(放在模型下以便解析手骨)测完即销毁,
        /// 不把枪留在模型里、也不保留胶囊引用(运行时由 BindWeapon 绑外部基准的枪)。
        /// </summary>
        static void AnalyzeGripsFromPrefab(HandGripConstraint gripR, HandGripConstraint gripL, GameObject modelRoot)
        {
            var gunPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GunPath);
            if (gunPrefab == null)
            {
                Debug.LogWarning($"[FatuiRig] 找不到枪预制体 {GunPath}，跳过 grip 分析。");
                return;
            }

            var gun = Object.Instantiate(gunPrefab);
            gun.name = "__TempGripAnalysis";
            try
            {
                gun.transform.SetParent(modelRoot.transform, false);
                gun.transform.localPosition = Vector3.zero;
                gun.transform.localRotation = Quaternion.identity;
                gun.transform.localScale = Vector3.one;

                var capR = Find(gun.transform, RightGripCapsule)?.GetComponent<CapsuleCollider>();
                var capL = Find(gun.transform, LeftGripCapsule)?.GetComponent<CapsuleCollider>();
                AnalyzeOne(gripR, capR, modelRoot);
                AnalyzeOne(gripL, capL, modelRoot);
            }
            finally
            {
                Object.DestroyImmediate(gun);
            }
        }

        static void AnalyzeOne(HandGripConstraint grip, CapsuleCollider cap, GameObject modelRoot)
        {
            if (grip == null) return;
            if (cap == null)
            {
                Debug.LogWarning($"[FatuiRig] {grip.name} 未找到握把胶囊,跳过分析。");
                return;
            }

            var anim = modelRoot.GetComponent<Animator>();
            ref var d = ref grip.data;
            d.grip = cap;
            d.thumbXBias = 0.8f;          // 用户决定：拇指偏手骨局部 X 弯曲（掌心是面）
            d.analyzed = false;

            if (!HandGripAnalyzer.ResolveSkeleton(ref d, anim))
            {
                Debug.LogWarning($"[FatuiRig] {grip.name} 骨架解析失败，请检查骨骼命名。");
                return;
            }
            HandGripAnalyzer.AnalyzePose(ref d);

            d.grip = null;                // 引用不保留:运行时绑定外部基准下的枪
            d.analyzed = true;
            EditorUtility.SetDirty(grip);
        }

        static int BuildFootIK(Transform rigParent, Transform modelRoot, string name, Transform foot, Transform target, Transform hint)
        {
            var ik = EnsureConstraint<TwoBoneIKConstraint>(rigParent, name);
            var d = ik.data;
            d.root = Find(modelRoot, foot != null ? foot.name.Replace("foot", "thigh") : "DEF-thigh.L");
            d.mid = Find(modelRoot, foot != null ? foot.name.Replace("foot", "shin") : "DEF-shin.L");
            d.tip = foot;
            d.target = target;
            d.hint = hint;
            d.targetPositionWeight = 1f;
            // 脚骨 forward 轴非标准：强制旋转权重 0，只钉位置（与 SoldierRigSetup 运行时一致，
            // 否则 LookRotation(实体forward,法线) 会把脚翻成鞋底朝上）。
            d.targetRotationWeight = 0f;
            d.hintWeight = 1f;
            ik.data = d;
            ik.weight = 1f;
            return 1;
        }

        static int BuildArmIK(Transform rigParent, Transform modelRoot, string name, string side)
        {
            var ik = EnsureConstraint<TwoBoneIKConstraint>(rigParent, name);
            var d = ik.data;
            d.root = Find(modelRoot, "DEF-upper_arm." + side);
            d.mid = Find(modelRoot, "DEF-forearm." + side);
            d.tip = Find(modelRoot, "DEF-hand." + side);
            d.target = null;   // 运行时 BindWeapon -> RearGrip / BarrelGrip
            d.hint = Find(modelRoot, side == "R" ? "RightHint" : "LeftHint");
            d.targetPositionWeight = 1f;
            d.targetRotationWeight = 1f;
            d.hintWeight = 1f;
            ik.data = d;
            ik.weight = 1f;
            return 1;
        }

        // =================================================================
        // 帮助函数
        // =================================================================

        /// <summary>模型根局部空间的前方：用 脚->脚尖 的水平方向推导（不受骨架内部翻转影响）。</summary>
        static Vector3 ModelForward(Transform modelRoot)
        {
            var foot = Find(modelRoot, "DEF-foot.L");
            var toe = Find(modelRoot, "DEF-toe.L");
            if (foot != null && toe != null)
            {
                Vector3 f = modelRoot.InverseTransformPoint(toe.position) - modelRoot.InverseTransformPoint(foot.position);
                f.y = 0f;
                if (f.sqrMagnitude > 1e-8f) return f.normalized;
            }
            return Vector3.forward;
        }

        static void SetFootDrivers(Transform target, Transform hint, Transform foot, Transform modelRoot, Vector3 fwd, float footHeight)
        {
            Vector3 footL = foot != null ? modelRoot.InverseTransformPoint(foot.position) : new Vector3(0f, 0.11f, -0.03f);
            target.localPosition = new Vector3(footL.x, footHeight, footL.z);
            target.localRotation = Quaternion.identity;
            hint.localPosition = target.localPosition + Vector3.up * 0.4f + fwd * 0.35f;
            hint.localRotation = Quaternion.identity;
        }

        static Rig EnsureRig(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (t == null) { t = new GameObject(name).transform; t.SetParent(parent, false); }
            var r = t.GetComponent<Rig>();
            if (r == null) r = t.gameObject.AddComponent<Rig>();
            r.weight = 1f;
            return r;
        }

        static T EnsureConstraint<T>(Transform parent, string name) where T : Component
        {
            var t = parent.Find(name);
            if (t == null) { t = new GameObject(name).transform; t.SetParent(parent, false); }
            var c = t.GetComponent<T>();
            if (c == null) c = t.gameObject.AddComponent<T>();
            return c;
        }

        static Transform EnsureChild(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (t == null) { t = new GameObject(name).transform; t.SetParent(parent, false); }
            return t;
        }

        static Transform Find(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; ++i)
            {
                var r = Find(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        // =================================================================
        // 本地验证场景（SoldierLocalTest）：Fatui 模型 + 新枪 + LocalSoldierDriver
        // =================================================================

        /// <summary>
        /// 重建 SoldierLocalTest 的角色(纯白板,按 PHASE14 结构):
        ///   SoldierRoot(胶囊位)
        ///     ├─ Fatui with Collider   角色模型(Animator + rig)
        ///     └─ WeaponBasis           枪械基准(与模型同级)
        ///          └─ M4_8             枪模
        /// 相机/光照/地面保持不动。驱动挂在根上,枪基准不再进动画层级。
        /// </summary>
        [MenuItem("HagenDa/Rigging/Setup Fatui Local Test")]
        public static void SetupLocalTest()
        {
            var modelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(FatuiPath);
            var gunPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GunPath);
            if (modelPrefab == null || gunPrefab == null)
            {
                Debug.LogError("[FatuiRig] 预制体缺失，无法搭建本地测试。");
                return;
            }

            // 清掉旧结构(旧根、直接摆的模型/枪、演示根)
            foreach (var stale in new[] { "Fatui with Collider", "M4_8", "GripDemoRoot", "SoldierRoot" })
            {
                var go = GameObject.Find(stale);
                if (go != null) Object.DestroyImmediate(go);
            }

            var root = new GameObject("SoldierRoot");
            root.transform.position = Vector3.zero;
            root.transform.rotation = Quaternion.identity;

            var model = (GameObject)PrefabUtility.InstantiatePrefab(modelPrefab);
            model.name = "Fatui with Collider";
            model.transform.SetParent(root.transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;

            // 枪械基准 + 枪(与模型同级;基准在动画层级之外)
            var basis = new GameObject("WeaponBasis");
            basis.transform.SetParent(root.transform, false);
            basis.transform.localPosition = Vector3.zero;
            basis.transform.localRotation = Quaternion.identity;

            var gun = (GameObject)PrefabUtility.InstantiatePrefab(gunPrefab);
            gun.name = "M4_8";
            gun.transform.SetParent(basis.transform, false);
            gun.transform.localPosition = Vector3.zero;
            gun.transform.localRotation = Quaternion.identity;
            gun.transform.localScale = Vector3.one;
            DisableGunAim(gun);

            var driver = root.GetComponent<LocalSoldierDriver>();
            if (driver == null) driver = root.AddComponent<LocalSoldierDriver>();

            var rig = model.GetComponent<SoldierRigSetup>();
            if (rig != null) rig.SetRigEnabled(true);

            EditorUtility.SetDirty(root);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Selection.activeGameObject = root;
            Debug.Log("[FatuiRig] SoldierLocalTest 已重建(PHASE14 结构):" +
                      "SoldierRoot -> [模型 + WeaponBasis -> 枪]。左键=枪机, 右键=ADS, Shift+前进=冲刺, C=蹲。");
        }

        /// <summary>枪自带 AimConstraint 在 rig 之后求值会让握把锚点滞后,一律停用。</summary>
        static void DisableGunAim(GameObject gun)
        {
            var ac = gun.GetComponent<AimConstraint>();
            if (ac == null) ac = gun.AddComponent<AimConstraint>();
            ac.constraintActive = false;
        }
    }
}
