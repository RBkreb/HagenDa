using UnityEditor;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using HagenDa.Animation.Rigging;

namespace HagenDa.Animation.Showcase.EditorTools
{
    /// <summary>
    /// 演示装配（在 AnimationTest 场景内）：验证通用胶囊握持约束。
    /// 结构：
    ///   GripDemoRoot/
    ///     Soldier (Natlan Soldier FBX.prefab)  [Animator + RigBuilder]
    ///       HandGripRig/  Rig
    ///         LeftGrip  HandGripConstraint
    ///         RightGrip HandGripConstraint
    ///       M4_8  [两个子 CapsuleCollider：GripCapR / GripCapL = 握把胶囊]
    /// 之后运行 Play 即可看到双手两骨骼 IK + 指链握持。
    /// </summary>
    public static class HandGripRigBuilder
    {
        const string SoldierPath = "Assets/Game/Characters/natlan/Natlan Soldier FBX.prefab";
        const string M4Path = "Assets/ThirdParty/Low Poly Weapons VOL.1/Prefabs/M4_8.prefab";

        const string DemoRootName = "GripDemoRoot";

        // 枪械在士兵局部坐标下的放置（复制自既有 Natlan+M4 rig 的默认持枪位）
        static readonly Vector3 GunLocalPos = new Vector3(0.082f, 1.42f, 0.40f);
        static readonly Vector3 GunLocalRot = new Vector3(0f, 180f, 0f);

        // 两个握把胶囊（枪械局部坐标）
        static readonly Vector3 CapR_Center = new Vector3(-0.024f, -0.025f, 0.16f);  // 右握（枪握把区）
        static readonly Vector3 CapL_Center = new Vector3(0.040f, 0.005f, -0.05f);   // 左握（护木/前握区）
        const float CapR_Radius = 0.022f, CapR_Height = 0.10f;
        const float CapL_Radius = 0.026f, CapL_Height = 0.22f;

        [MenuItem("HagenDa/Rigging/Build Hand-Grip Demo")]
        public static void BuildDemo()
        {
            var old = GameObject.Find(DemoRootName);
            if (old != null)
            {
                // 非破坏：已存在则只重分析（保留场景里手动摆放的胶囊/枪位）
                ReanalyzeAll(old.transform);
                Debug.Log("[HandGrip] Demo root exists -> only re-analyzed grips (capsule/weapon placement kept).");
                return;
            }

            var root = new GameObject(DemoRootName);
            root.transform.position = new Vector3(0f, 0.05f, 0f);

            // ---- 士兵 ----
            var soldierPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SoldierPath);
            var soldier = (GameObject)PrefabUtility.InstantiatePrefab(soldierPrefab);
            soldier.name = "Soldier";
            soldier.transform.SetParent(root.transform, false);
            soldier.transform.localPosition = Vector3.zero;
            soldier.transform.localRotation = Quaternion.identity;

            var anim = soldier.GetComponent<Animator>();
            if (anim == null) anim = soldier.AddComponent<Animator>();
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            anim.updateMode = AnimatorUpdateMode.Normal;

            var rb = soldier.GetComponent<RigBuilder>();
            if (rb == null) rb = soldier.AddComponent<RigBuilder>();
            rb.layers.Clear();

            // ---- Rig + 两个约束 ----
            var rigGo = new GameObject("HandGripRig");
            rigGo.transform.SetParent(soldier.transform, false);
            var rig = rigGo.AddComponent<Rig>();
            rig.weight = 1f;

            AddHandRig(soldier, rigGo.transform, "LeftGrip", false);
            AddHandRig(soldier, rigGo.transform, "RightGrip", true);

            rb.layers.Add(new RigLayer(rig, true));

            // ---- 枪 + 握把胶囊 ----
            var m4Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(M4Path);
            GameObject gun = null;
            if (m4Prefab != null)
            {
                gun = (GameObject)PrefabUtility.InstantiatePrefab(m4Prefab);
                gun.name = "M4_8";
                gun.transform.SetParent(soldier.transform, false);
                gun.transform.localPosition = GunLocalPos;
                gun.transform.localRotation = Quaternion.Euler(GunLocalRot);
                gun.transform.localScale = Vector3.one;
            }
            var capR = MakeGripCapsule(gun, "GripCapR", CapR_Center, Vector3.up, CapR_Radius, CapR_Height);
            var capL = MakeGripCapsule(gun, "GripCapL", CapL_Center, Vector3.forward, CapL_Radius, CapL_Height);

            // 分析（绑定胶囊引用后必须在 bind 姿态跑一次）
            Reanalyze(soldier, "LeftGrip", capL);
            Reanalyze(soldier, "RightGrip", capR);
            EnsureArmIK(soldier, false);
            EnsureArmIK(soldier, true);

            // ---- 相机 ----
            EnsureDemoCamera(root.transform);

            EditorUtility.SetDirty(soldier);
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = soldier;
            Debug.Log("[HandGrip] Demo built. Enter Play Mode to verify grip.");
        }

        static void ReanalyzeAll(Transform root)
        {
            var soldier = root != null ? root.Find("Soldier") : null;
            if (soldier == null) return;
            var capR = FindChild(soldier, "GripCapR")?.GetComponent<CapsuleCollider>();
            var capL = FindChild(soldier, "GripCapL")?.GetComponent<CapsuleCollider>();
            EnsureArmIK(soldier.gameObject, false);
            EnsureArmIK(soldier.gameObject, true);
            Reanalyze(soldier.gameObject, "LeftGrip", capL);
            Reanalyze(soldier.gameObject, "RightGrip", capR);
            EditorUtility.SetDirty(soldier.gameObject);
        }

        // 手臂 IK（TwoBoneIK，用户手动加的临时手臂 IK）挂到独立对象并置于最前，
        // 先于手指握持约束执行：手位/手姿由用户调好的 GripRoot 锚点决定，
        // 握持约束只读该结果并逐指收拢（消除同链双解算器互写）。
        static void EnsureArmIK(GameObject soldier, bool right)
        {
            string suffix = right ? "R" : "L";
            var rigGo = soldier.transform.Find("HandGripRig");
            if (rigGo == null) return;

            // 清掉挂在握持约束对象上的 TwoBoneIK（避免同对象双解算器互写）
            foreach (var gripName in new string[] { "LeftGrip", "RightGrip" })
            {
                var gripT = rigGo.Find(gripName);
                if (gripT == null) continue;
                var stray = gripT.GetComponent<TwoBoneIKConstraint>();
                if (stray != null) UnityEngine.Object.DestroyImmediate(stray);
            }

            string goName = "ArmIK_" + suffix;
            var goT = rigGo.Find(goName);
            var go = goT != null ? goT.gameObject : new GameObject(goName);
            if (goT == null) go.transform.SetParent(rigGo, false);
            go.transform.SetSiblingIndex(0);   // 先于 LeftGrip/RightGrip 执行

            var two = go.GetComponent<TwoBoneIKConstraint>();
            if (two == null) two = go.AddComponent<TwoBoneIKConstraint>();
            var dat = two.data;
            dat.root = FindChild(soldier.transform, "DEF-upper_arm." + suffix);
            dat.mid = FindChild(soldier.transform, "DEF-forearm." + suffix);
            dat.tip = FindChild(soldier.transform, "DEF-hand." + suffix);
            var capT = FindChild(soldier.transform, "GripCap" + suffix);
            dat.target = capT != null ? FindChild(capT, "GripRoot" + suffix) : null;
            dat.hint = FindChild(soldier.transform, (right ? "Right" : "Left") + "Hint");
            EditorUtility.SetDirty(two);
        }

        static Transform FindChild(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; ++i)
            {
                var r = FindChild(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        static void AddHandRig(GameObject soldier, Transform rigParent, string name, bool right)
        {
            var go = new GameObject(name);
            go.transform.SetParent(rigParent, false);
            var c = go.AddComponent<HandGripConstraint>();
            c.data.side = right ? HandGripSide.Right : HandGripSide.Left;
        }

        static CapsuleCollider MakeGripCapsule(GameObject parent, string name, Vector3 center, Vector3 axis, float r, float h)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent != null ? parent.transform : null, false);
            go.transform.localPosition = center;
            go.transform.localRotation = Quaternion.identity;
            var col = go.AddComponent<CapsuleCollider>();
            col.direction = AxisIndex(axis);
            col.radius = r;
            col.height = h;
            col.isTrigger = true;
            return col;
        }

        static int AxisIndex(Vector3 axis)
        {
            if (axis == Vector3.right) return 0;
            if (axis == Vector3.forward) return 2;
            return 1;
        }

        static void Reanalyze(GameObject soldier, string constraintName, CapsuleCollider cap)
        {
            var rigParent = soldier.transform.Find("HandGripRig");
            var t = rigParent != null ? rigParent.Find(constraintName) : null;
            if (t == null) return;
            var c = t.GetComponent<HandGripConstraint>();
            if (c == null) return;
            var anim = soldier.GetComponent<Animator>();
            ref var d = ref c.data;
            d.grip = cap;
            d.analyzed = false;
            if (!HandGripAnalyzer.ResolveSkeleton(ref d, anim))
            {
                Debug.LogWarning($"[HandGrip] 解析失败 {constraintName}；请手动检查骨骼名。");
                return;
            }
            HandGripAnalyzer.AnalyzePose(ref d);

            // 胶囊下预放的 GripRoot* 空物体作为手腕锚点（供 ArmIK 的 TwoBoneIK 使用）
            d.wristAnchor = null;
            for (int i = 0; i < cap.transform.childCount; ++i)
            {
                var chd = cap.transform.GetChild(i);
                if (chd.name.StartsWith("GripRoot")) { d.wristAnchor = chd; break; }
            }
            // 手位/手姿由用户手动的 TwoBoneIK（target=GripRoot）负责（见 EnsureArmIK），
            // 本约束只做手指握持：驱动骨骼 + copy rotation，不写手臂/手腕
            d.armIK = false;

            // 驱动骨骼：握紧程度控制（DEF-hand 子物体，缺失则创建）
            if (d.tipBone != null)
            {
                var drv = d.tipBone.Find("GripDriver");
                if (drv == null)
                {
                    var go = new GameObject("GripDriver");
                    go.transform.SetParent(d.tipBone, false);
                    drv = go.transform;
                }
                d.gripDriver = drv;
            }
            if (d.driverMaxAngle <= 0.5f) d.driverMaxAngle = 90f;

            EditorUtility.SetDirty(c);
        }

        static void EnsureDemoCamera(Transform parent)
        {
            var camGo = new GameObject("GripDemoCamera");
            camGo.transform.SetParent(parent, true);
            camGo.transform.position = new Vector3(1.55f, 1.55f, -2.35f);
            camGo.transform.rotation = Quaternion.Euler(8f, -26f, 0f);
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.03f;
        }
    }
}
