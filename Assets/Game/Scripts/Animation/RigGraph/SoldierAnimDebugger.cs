using UnityEngine;
using HagenDa.Networking;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace HagenDa.Animation.RigGraph
{
    /// <summary>
    /// 士兵动画调试器(PHASE13 蹲姿排查)。Play 时在场景中实例化一个持续存在的
    /// NetworkPlayer(health=100 保持存活),键盘 1/2/3 切换 站/蹲/趴,并每 0.5s
    /// 打印关键骨骼(hips/chest/knee/foot)与模型根世界位置,便于在 Scene 视图
    /// 选中骨骼对照"根骨骼/骨连接"问题。实例不销毁,方便 Play 状态下手动调参。
    /// 编辑器 API 用 #if UNITY_EDITOR 包裹,不进 build。
    /// </summary>
    public class SoldierAnimDebugger : MonoBehaviour
    {
        [Tooltip("NetworkPlayer prefab 资源路径")]
        public string prefabPath = "Assets/Game/Prefabs/NetworkPlayer.prefab";

        [Tooltip("生成位置(默认放本对象位置)")]
        public Vector3 spawnOffset = new Vector3(0f, 0.02f, 0f);

        [Header("蹲/趴下移量(运行时覆盖 NetworkSoldierAnimator 字段,便于现场调参)")]
        public bool overrideOffsets = false;
        public Vector3 crouchOffset = new Vector3(0f, -0.5f, 0f);
        public Vector3 proneOffset = new Vector3(0f, -0.9f, 0.25f);

        private GameObject instance;
        private NetworkPlayerController controller;
        private NetworkSoldierAnimator soldier;
        private Animator anim;

        private void Start()
        {
#if UNITY_EDITOR
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
#else
            var prefab = (GameObject)null;
#endif
            if (prefab == null)
            {
                Debug.LogError("[SoldierAnimDebugger] prefab 不存在: " + prefabPath);
                return;
            }

            instance = Instantiate(prefab, transform.position + spawnOffset, Quaternion.identity);
            instance.name = "DebugSoldier";
            controller = instance.GetComponent<NetworkPlayerController>();
            soldier = instance.GetComponent<NetworkSoldierAnimator>();
            var health = instance.GetComponent<NetworkPlayerHealth>();
            if (health != null) health.health = 100f;

            // 关闭 owner 隐藏路径与自身 AudioListener(避免干扰 + 噪音)
            foreach (var al in instance.GetComponentsInChildren<AudioListener>(true)) al.enabled = false;

            anim = soldier != null && soldier.redModel != null
                ? soldier.redModel.GetComponentInChildren<Animator>(true)
                : instance.GetComponentInChildren<Animator>();

            if (overrideOffsets && soldier != null)
            {
                soldier.crouchModelOffset = crouchOffset;
                soldier.proneModelOffset = proneOffset;
            }

            Debug.Log("[SoldierAnimDebugger] 就绪。1=站 2=蹲 3=趴;每 0.5s 打印骨骼位置;实例持续存在不销毁。");
            DumpSkeleton();
            InvokeRepeating(nameof(LogBones), 0.5f, 0.5f);
        }

        private void Update()
        {
            if (controller == null) return;
            if (Input.GetKeyDown(KeyCode.Alpha1)) controller.posture = PlayerPosture.Stand;
            if (Input.GetKeyDown(KeyCode.Alpha2)) controller.posture = PlayerPosture.Crouch;
            if (Input.GetKeyDown(KeyCode.Alpha3)) controller.posture = PlayerPosture.Prone;
        }

        private void DumpSkeleton()
        {
            if (anim == null || anim.avatar == null || !anim.avatar.isValid) return;
            Debug.Log("[SoldierAnimDebugger] 骨骼映射: " +
                $"Hips={Bone(HumanBodyBones.Hips)} | Spine={Bone(HumanBodyBones.Spine)} | Chest={Bone(HumanBodyBones.Chest)} | " +
                $"LThigh={Bone(HumanBodyBones.LeftUpperLeg)} | LShin={Bone(HumanBodyBones.LeftLowerLeg)} | LFoot={Bone(HumanBodyBones.LeftFoot)}");
        }

        private string Bone(HumanBodyBones b)
        {
            var t = anim.GetBoneTransform(b);
            return t == null ? "NULL" : $"{t.name}(父={t.parent?.name})";
        }

        private void LogBones()
        {
            if (anim == null || soldier == null || controller == null) return;
            var hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            var chest = anim.GetBoneTransform(HumanBodyBones.Chest);
            var knee = anim.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            var foot = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
            string model = soldier.redModel != null ? soldier.redModel.transform.localPosition.ToString("F3") : "?";
            Debug.Log($"[SoldierAnimDebugger] posture={controller.posture} modelLP={model} " +
                $"hipsY={P(hips)} chestY={P(chest)} kneeY={P(knee)} footY={P(foot)}");
        }

        private static string P(Transform t) => t == null ? "?" : t.position.y.ToString("F3");
    }
}
