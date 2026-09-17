using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace HagenDa.Animation
{
    /// <summary>
    /// 角色持枪瞄准控制器（演示用）。
    /// 武器挂在「胸骨 Chest」下。非瞄准时贴胸前(hipLocal)，瞄准时抬到相机视线轴(ADS)，
    /// 两者都在 Chest 局部空间做平滑插值——因此跑步时武器会跟随躯干起伏，双手(IK 握枪点)随之甩动。
    /// 按住右键(Mouse1 / 新InputSystem右键)进入瞄准，松开回到胸前。
    /// </summary>
    [DefaultExecutionOrder(5)]
    public class AimController : MonoBehaviour
    {
        [Header("References")]
        public Transform weapon;          // M4_8 根
        public Transform rearSight;       // Rear_Sight（照门）
        public Transform frontSight;      // Sight（准星）
        public Transform weaponBone;      // 武器应跟随的骨骼（Chest）—— 武器必须是它的子物体
        public Transform hipPose;         // 场景里摆放"胸前持枪位"用的参考物体（Chest 子物体）
        public Camera aimCam;             // 瞄准基准相机（display1 主视角）
        public TwoBoneIKConstraint ikL;
        public TwoBoneIKConstraint ikR;

        [Header("Tuning")]
        public float smoothTime = 10f;      // 位姿过渡速度
        public float hipWeight = 0.95f;     // 非瞄准时手 IK 权重
        public float aimWeight = 1f;        // 瞄准时手 IK 权重
        public float adsRearDistance = 0.15f;// 瞄准时照门到相机距离（m）
        public float adsRearDrop = 0.012f;  // 照门相对视线中心下沉量

        public bool aiming { get; private set; }
        float t;

        // 缓存的局部位姿
        Vector3 hipLocalPos; Quaternion hipLocalRot;

        void Reset() => TryAutoWire();
        void Awake() => TryAutoWire();

        void Start()
        {
            if (weapon != null && hipPose != null && weaponBone != null)
            {
                // hipPose 是 weaponBone(Chest) 的子物体：取其局部位姿作为"胸前持枪位"
                hipLocalPos = hipPose.localPosition;
                hipLocalRot = hipPose.localRotation;
            }
        }

        void TryAutoWire()
        {
            if (weapon == null) weapon = transform.Find("M4_8");
            if (weapon == null) { var g = GameObject.Find("M4_8"); if (g != null) weapon = g.transform; }
            if (rearSight == null && weapon != null) rearSight = weapon.Find("Rear_Sight");
            if (frontSight == null && weapon != null) frontSight = weapon.Find("Sight");
            if (weaponBone == null)
            {
                var anim = GetComponent<Animator>();
                if (anim != null) weaponBone = anim.GetBoneTransform(HumanBodyBones.Chest);
            }
            if (hipPose == null) { var go = GameObject.Find("ChestHold"); if (go != null) hipPose = go.transform; }
            if (aimCam == null)
            {
                foreach (var c in Camera.allCameras) if (c.targetDisplay == 0) { aimCam = c; break; }
            }
        }

        static bool RightMouseHeld()
        {
            bool old = Input.GetMouseButton(1);
#if ENABLE_INPUT_SYSTEM
            try
            {
                var m = UnityEngine.InputSystem.Mouse.current;
                if (m != null && m.rightButton != null && m.rightButton.isPressed) return true;
            }
            catch (System.Exception) { }
#endif
            return old;
        }

        void Update()
        {
            bool press = RightMouseHeld();
            aiming = press;
            t = Mathf.MoveTowards(t, aiming ? 1f : 0f, Time.deltaTime * smoothTime);

            float w = Mathf.Lerp(hipWeight, aimWeight, t);
            if (ikL) ikL.weight = w;
            if (ikR) ikR.weight = w;

            ApplyWeaponLocal(t);
        }

        void LateUpdate()
        {
            if (Mathf.Approximately(t, 0f) || Mathf.Approximately(t, 1f))
                ApplyWeaponLocal(t);
        }

        /// <summary>把武器写到 Chest 局部空间的插值位姿。</summary>
        void ApplyWeaponLocal(float k)
        {
            if (weapon == null || weaponBone == null) return;
            if (weapon.parent != weaponBone)
            {
                // 自动纠正：确保是 weaponBone 子物体
                weapon.SetParent(weaponBone, true);
            }

            // ADS 世界位 → chest 局部
            Vector3 adsLocalPos = Vector3.zero; Quaternion adsLocalRot = Quaternion.identity;
            bool hasAds = aimCam != null && rearSight != null && frontSight != null;
            if (hasAds)
            {
                Pose adsWorld = ComputeAdsWorldPose();
                adsLocalPos = weaponBone.InverseTransformPoint(adsWorld.position);
                adsLocalRot = Quaternion.Inverse(weaponBone.rotation) * adsWorld.rotation;
            }

            Vector3 pos = Vector3.Lerp(hipLocalPos, adsLocalPos, k);
            Quaternion rot = Quaternion.Slerp(hipLocalRot, adsLocalRot, k);
            weapon.localPosition = pos;
            weapon.localRotation = rot;
        }

        Pose ComputeAdsWorldPose()
        {
            Vector3 camPos = aimCam.transform.position;
            Vector3 camFwd = aimCam.transform.forward;
            Vector3 camUp = aimCam.transform.up;

            Vector3 gunAimDir = (frontSight.position - rearSight.position).normalized;
            Quaternion align = Quaternion.FromToRotation(gunAimDir, camFwd);
            Quaternion newRot = align * weapon.rotation;

            Vector3 rearDesired = camPos + camFwd * adsRearDistance - camUp * adsRearDrop;
            Vector3 rearLocal = weapon.InverseTransformPoint(rearSight.position);
            Vector3 newPos = rearDesired - newRot * rearLocal;
            return new Pose(newPos, newRot);
        }
    }
}
