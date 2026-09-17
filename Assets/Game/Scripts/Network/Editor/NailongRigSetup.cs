using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using HagenDa.Animation;

namespace HagenDa.EditorTools
{
    /// <summary>
    /// 幂等搭建 animation 场景的持枪跑步演示。
    /// 每个方法独立、可重复调用：
    ///   1) CreateStage    平台 + 方向光
    ///   2) PrepareNailong 指定 Run 控制器、关 rootMotion
    ///   3) MountGun       M4 挂到 nailong 根 + ChestHold 锚点 + 左右握点
    ///   4) BuildRig       RigBuilder + 双手 TwoBoneIK(目标=握点)
    ///   5) WireCameras    Display1 主视角(眼睛) / Display2 斜视(全身)
    ///   6) FullSetup      依序执行上面全部
    /// </summary>
    public static class NailongRigSetup
    {
        static Transform FindRoot(string n) { var g = GameObject.Find(n); return g != null ? g.transform : null; }

        // ---------- 1 ----------
        public static void CreateStage()
        {
            var p = FindRoot("Platform");
            if (p == null)
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "Platform";
                cube.transform.position = new Vector3(0f, -0.1f, 0f);
                cube.transform.localScale = new Vector3(8f, 0.2f, 8f);
                var mr = cube.GetComponent<MeshRenderer>();
                var sh = Shader.Find("HDRP/Lit");
                if (sh != null)
                {
                    mr.sharedMaterial = new Material(sh) { name = "StageMat" };
                    if (mr.sharedMaterial.HasProperty("_BaseColor")) mr.sharedMaterial.SetColor("_BaseColor", new Color(0.18f, 0.18f, 0.2f));
                }
            }
            if (FindRoot("Sun") == null)
            {
                var go = new GameObject("Sun");
                go.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
                var l = go.AddComponent<Light>();
                l.type = LightType.Directional;
                l.intensity = 1.2f;
            }
        }

        // ---------- 2 ----------
        public static void PrepareNailong()
        {
            var nl = FindRoot("nailong");
            if (nl == null) { Debug.LogError("[RigSetup] nailong not found"); return; }
            nl.position = Vector3.zero;
            nl.rotation = Quaternion.identity;
            var anim = nl.GetComponent<Animator>();
            if (anim == null) anim = nl.gameObject.AddComponent<Animator>();
            var ctrl = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(
                "Assets/Game/Characters/nailong/nailong.controller");
            if (ctrl != null) anim.runtimeAnimatorController = ctrl;
            var avatar = AssetDatabase.LoadAssetAtPath<Avatar>("Assets/Game/Characters/nailong/nailong.fbx");
            if (avatar != null && avatar.isHuman) anim.avatar = avatar;
            anim.applyRootMotion = false;
        }

        // ---------- 3 ----------
        public static void MountGun()
        {
            var nl = FindRoot("nailong");
            var m4go = GameObject.Find("M4_8");
            if (nl == null || m4go == null) { Debug.LogError("[RigSetup] nailong or M4_8 missing"); return; }
            var m4 = m4go.transform;
            if (m4.parent != nl)
            {
                var wp = m4.position; var wr = m4.rotation;
                m4.SetParent(nl, true);
                m4.position = wp; m4.rotation = wr;
            }

            // 胸前持枪锚点（非瞄准）
            var chT = nl.Find("ChestHold");
            if (chT == null)
            {
                var go = new GameObject("ChestHold");
                go.transform.SetParent(nl, false);
                chT = go.transform;
            }
            chT.localPosition = new Vector3(0.26f, 1.34f, 0.30f);
            chT.localRotation = Quaternion.Euler(0f, 180f, 0f);

            CreateGrip(m4, "GripR", new Vector3(0f, 0.02f, 0.10f));
            CreateGrip(m4, "GripL", new Vector3(0f, 0.06f, -0.14f));
        }

        static void CreateGrip(Transform parent, string name, Vector3 localPos)
        {
            var g = parent.Find(name);
            if (g == null)
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                g = go.transform;
            }
            g.localPosition = localPos;
            g.localRotation = Quaternion.identity;
        }

        // ---------- 4 ----------
        public static void BuildRig()
        {
            var nl = FindRoot("nailong");
            if (nl == null) return;
            var anim = nl.GetComponent<Animator>();
            var m4 = nl.Find("M4_8");
            if (anim == null || m4 == null) { Debug.LogError("[RigSetup] nailong animator or M4 missing"); return; }

            var rb = nl.GetComponent<RigBuilder>();
            if (rb == null) rb = nl.gameObject.AddComponent<RigBuilder>();

            var rigT = nl.Find("AimRig");
            if (rigT == null)
            {
                var go = new GameObject("AimRig");
                go.transform.SetParent(nl, false);
                rigT = go.transform;
            }
            var rig = rigT.GetComponent<Rig>();
            if (rig == null) rig = rigT.gameObject.AddComponent<Rig>();
            if (rb.layers == null) rb.layers = new List<RigLayer>();
            rb.layers.Clear();
            rb.layers.Add(new RigLayer(rig, true));

            var gripL = m4.Find("GripL");
            var gripR = m4.Find("GripR");
            var ikL = EnsureTwoBoneIK(rigT, "LeftHandIK",
                HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, gripL);
            var ikR = EnsureTwoBoneIK(rigT, "RightHandIK",
                HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, gripR);

            var ac = nl.GetComponent<AimController>();
            if (ac == null) ac = nl.gameObject.AddComponent<AimController>();
            ac.weapon = m4;
            ac.rearSight = m4.Find("Rear_Sight");
            ac.frontSight = m4.Find("Sight");
            ac.weaponBone = anim.GetBoneTransform(HumanBodyBones.Chest);
            var chRef = nl.Find("ChestHold");
            if (chRef == null) chRef = anim.GetBoneTransform(HumanBodyBones.Chest).Find("ChestHold");
            ac.hipPose = chRef;
            ac.ikL = ikL;
            ac.ikR = ikR;
            Camera c0 = null;
            foreach (var c in Camera.allCameras) if (c.targetDisplay == 0) c0 = c;
            if (c0 == null && Camera.main != null) c0 = Camera.main;
            ac.aimCam = c0;
        }

        static TwoBoneIKConstraint EnsureTwoBoneIK(Transform rigParent, string name,
            HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones hand, Transform target)
        {
            var anim = FindRoot("nailong").GetComponent<Animator>();
            var go = rigParent.Find(name);
            TwoBoneIKConstraint c;
            if (go == null)
            {
                var nobj = new GameObject(name);
                nobj.transform.SetParent(rigParent, false);
                c = nobj.AddComponent<TwoBoneIKConstraint>();
            }
            else c = go.GetComponent<TwoBoneIKConstraint>();
            c.data.root = anim.GetBoneTransform(upper);
            c.data.mid = anim.GetBoneTransform(lower);
            c.data.tip = anim.GetBoneTransform(hand);
            c.data.target = target;
            c.data.targetPositionWeight = 1f;
            c.data.targetRotationWeight = 0.4f;
            c.weight = 0.85f;
            return c;
        }

        // ---------- 5 ----------
        public static void WireCameras()
        {
            var nl = FindRoot("nailong");
            if (nl == null) return;
            var anim = nl.GetComponent<Animator>();

            // ---- Display 1: 主视角（眼睛高度，稳定水平视线）----
            var c1go = FindRoot("Main Camera");
            Camera c1;
            HeadCamFollow follow;
            if (c1go == null)
            {
                c1go = new GameObject("Main Camera").transform;
                c1go.tag = "MainCamera";
                c1 = c1go.gameObject.AddComponent<Camera>();
                follow = c1go.gameObject.AddComponent<HeadCamFollow>();
            }
            else
            {
                c1 = c1go.GetComponent<Camera>();
                if (c1 == null) c1 = c1go.gameObject.AddComponent<Camera>();
                follow = c1go.GetComponent<HeadCamFollow>();
                if (follow == null) follow = c1go.gameObject.AddComponent<HeadCamFollow>();
            }
            c1.targetDisplay = 0;
            c1.fieldOfView = 65;
            c1.nearClipPlane = 0.05f;
            follow.target = anim.GetBoneTransform(HumanBodyBones.Head);
            follow.offset = new Vector3(0f, 0f, 0.08f);
            follow.followYaw = true;
            follow.followPitch = false;   // 稳定水平，避免跑步点头导致画面乱晃
            follow.lerp = 12f;

            // ---- Display 2: 斜视全身 ----
            var c2go = FindRoot("Oblique Camera");
            Camera c2;
            ThirdPersonCam tp;
            if (c2go == null)
            {
                c2go = new GameObject("Oblique Camera").transform;
                c2 = c2go.gameObject.AddComponent<Camera>();
                tp = c2go.gameObject.AddComponent<ThirdPersonCam>();
            }
            else
            {
                c2 = c2go.GetComponent<Camera>();
                if (c2 == null) c2 = c2go.gameObject.AddComponent<Camera>();
                tp = c2go.GetComponent<ThirdPersonCam>();
                if (tp == null) tp = c2go.gameObject.AddComponent<ThirdPersonCam>();
            }
            c2.targetDisplay = 1;
            c2.fieldOfView = 45;
            c2.nearClipPlane = 0.1f;
            tp.target = nl;
            tp.distance = 3.0f;
            tp.height = 1.2f;
            tp.pitch = 15f;
            tp.yaw = 35f;   // 左前斜视
            tp.lerp = 8f;
        }

        // ---------- 6 ----------
        public static void FullSetup()
        {
            CreateStage();
            PrepareNailong();
            MountGun();
            BuildRig();
            WireCameras();
        }
    }
}
