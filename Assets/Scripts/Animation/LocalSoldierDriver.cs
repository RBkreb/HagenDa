using HagenDa.Animation.Rigging;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HagenDa.Animation
{
    /// <summary>
    /// 纯本地士兵动画测试驱动(SoldierLocalTest 场景专用,无 Mirror/NetworkPlayer)。
    ///
    /// 职责(与 NetworkSoldierAnimator 的对应子集):
    ///  - WASD → Animator MoveX/MoveZ(8 向 blend tree)+ 根在世界空间的平移,箭头键等效
    ///  - 空格 → 简单跳跃(无刚体:垂直速度 + 重力积分,落地钳制 y>=0)
    ///  - 按住右键移动鼠标 → 根朝向 yaw(角色转身)+ 枪模 pitch(AimConstraint 瞄准目标)
    ///  - 枪位置绕 HeadAimPivot(头部空对象)公转:pitch 旋转中心=头部,不是枪自身
    ///  - 下半身经 HipsPose 差速滞后追赶(指数趋近 + 超 60° 加速)
    ///  - L0/L1 层权重按是否持枪互补(持枪=下半身层,空手=全身层)
    ///  - Rig 权重:hips=1(转身),hands=1(绑烘焙 M4);脚 IK 逐脚按踝高门控:
    ///    触地=软钉贴合(权重上限<1,否则抬脚被锁死),抬脚=释放(8 向剪辑自然摆腿)
    ///  - C 键蹲:MultiPositionConstraint 把根骨骼(avatar Hips)压低 crouchOffset,
    ///    脚 IK 钉地 → 屈膝成蹲(权重与位移同步渐变)
    ///
    /// 明确不做(本场景验证目标之外):开火/换弹、瞄准的脊柱扭转。
    /// debugInput 非零时覆盖键盘输入(自动化验证用);pitch 为 public 供自动化/现场调参。
    /// </summary>
    public class LocalSoldierDriver : MonoBehaviour
    {
        [Header("Locomotion")]
        [Tooltip("平移速度(m/s)。与剪辑脚步速度匹配度决定脚 IK 是否贴地不滑步。")]
        public float walkSpeed = 3f;
        [Tooltip("MoveX/MoveZ 每秒变化速率(blend 过渡速度)。")]
        public float blendSpeed = 6f;

        [Header("Jump")]
        public float jumpVel = 6f;
        public float gravity = 20f;

        [Header("Crouch (根骨骼下移 + 脚 IK 屈膝)")]
        [Tooltip("C 键切换蹲。蹲 = MultiPositionConstraint 把根骨骼(avatar Hips)压低该位移,脚 IK 钉地 → 屈膝。")]
        public Vector3 crouchOffset = new Vector3(0f, -0.5f, 0f);
        [Tooltip("蹲姿权重/位移渐变速率(每秒)。")]
        public float crouchRampSpeed = 5f;
        [Tooltip("自动化验证:强制蹲姿。")]
        public bool debugCrouch;

        [Header("Foot IK gating (触地软钉 / 抬脚释放,逐脚独立)")]
        [Tooltip("脚踝低于此高度=触地,该脚 IK 权重升至上限。剪辑踝高 rest≈0.11。")]
        public float footPlantHeight = 0.12f;
        [Tooltip("脚踝高于此高度=抬脚,该脚 IK 权重归零(剪辑自然摆腿)。实测左脚剪辑峰值 0.201/右脚 0.230——阈值需低于较低一侧,否则低摆幅脚的级联释放在摆幅顶点前完不成。")]
        public float footLiftHeight = 0.15f;
        [Tooltip("移动时的 IK 权重上限(<1,关键):恒 1 会把脚踝锁死贴地,门控永远看不到抬脚。0.15 时摆幅混合高度≈0.18>lift 阈值,级联释放能完成。")]
        [Range(0f, 1f)] public float footIKCapMoving = 0.15f;
        [Tooltip("静止时的 IK 权重上限(1=完全钉地,站立/转身时脚不漂)。")]
        [Range(0f, 1f)] public float footIKCapIdle = 1f;
        [Tooltip("脚 IK 权重每秒过渡速率。")]
        public float footWeightSpeed = 15f;

        [Header("Aim (枪模 pitch)")]
        public float eyeHeight = 1.5f;
        public float aimTargetDistance = 30f;
        public float pitchMin = -60f;
        public float pitchMax = 60f;

        [Header("Head aim pivot (枪绕头转动)")]
        [Tooltip("头部瞄准 pivot 空对象;留空则按名字查找,仍无则自动创建。")]
        public Transform headAimPivot;
        [Tooltip("自动创建 pivot 时的本地位置(相对模型根,约眼睛高度)。")]
        public Vector3 headPivotLocalPos = new Vector3(0f, 1.55f, 0.10f);
        [Tooltip("枪相对 pivot 的持枪偏移(根空间,绑定后自动采集;随瞄准 pitch 绕 pivot 公转)。")]
        public Vector3 gunHoldOffset;

        [Header("Differential turn (与 NetworkSoldierAnimator 一致)")]
        public float turnSmoothTime = 0.18f;
        public float turnCatchupBoost = 6f;
        private const float SpineMaxTwist = 60f;

        [Tooltip("鼠标灵敏度(度/像素)。")]
        public float mouseSensitivity = 0.08f;

        [Header("Debug (自动化验证)")]
        [Tooltip("非零时覆盖键盘输入(仅调试用)。")]
        public Vector2 debugInput;
        [Tooltip("当前瞄准 pitch(度)。public 供自动化/现场调参。")]
        public float pitch;

        private const int LayerFull = 1, LayerLower = 2;   // 与 NetworkSoldierAnimator 相同
        private static readonly int MoveX = Animator.StringToHash("MoveX");
        private static readonly int MoveZ = Animator.StringToHash("MoveZ");

        private Animator animator;
        private SoldierRigSetup rig;
        private Transform yawDriver;
        private Transform gunRoot;
        private bool weaponBound;

        private float moveX, moveZ;
        private float vy;
        private float footWeightL = 1f, footWeightR = 1f;
        private Transform footBoneL, footBoneR;
        private bool crouched;
        private float poseCrouchCur;
        private bool yawInit;
        private float dampedYaw;

        private void Awake()
        {
            animator = GetComponentInChildren<Animator>(true);
            rig = GetComponentInChildren<SoldierRigSetup>(true);
        }

        private void Start()
        {
            if (rig != null)
            {
                rig.SetHipsWeight(1f);        // 差速转身开
                rig.SetSpineWeight(0f);       // 无脊柱瞄准
                rig.SetPoseWeights(0f, 0f);   // 无蹲/趴姿态
                rig.SetHandsWeight(0f);       // 绑枪成功后再开

                yawDriver = rig.YawDriver;
                footBoneL = rig.FootTipL;
                footBoneR = rig.FootTipR;

                // 绑烘焙在模型内的 M4(GripCap 下补建 GripRoot 手腕锚点后 BindWeapon)。
                var gunT = FindDeep(transform, "M4_8");
                if (gunT != null)
                {
                    var gun = gunT.gameObject;
                    EnsureGripRoot(gun.transform, "GripCapR", "GripRootR");
                    EnsureGripRoot(gun.transform, "GripCapL", "GripRootL");

                    // 枪模挂 WeaponAnchor(生产同款):蹲/瞄准时锚点跟胸骨走,
                    // 手臂 IK 跟枪,身体下蹲时手臂不被硬拉。
                    if (rig.WeaponAnchor != null)
                    {
                        gunT.SetParent(rig.WeaponAnchor, false);
                        gunT.localPosition = Vector3.zero;
                        gunT.localRotation = Quaternion.identity;
                    }

                    rig.BindWeapon(gun);
                    weaponBound = rig.BoundWeapon == gun;
                    if (weaponBound) rig.SetHandsWeight(1f);
                    gunRoot = gunT;
                }
            }

            // ---- Head aim pivot:枪绕头部转动的旋转中心 ----
            if (headAimPivot == null) headAimPivot = transform.Find("HeadAimPivot");
            if (headAimPivot == null)
            {
                var go = new GameObject("HeadAimPivot");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = headPivotLocalPos;
                headAimPivot = go.transform;
            }
            if (gunRoot != null && headAimPivot != null)
                gunHoldOffset = transform.InverseTransformVector(gunRoot.position - headAimPivot.position);

            if (animator != null)
            {
                animator.SetFloat(MoveX, 0f);
                animator.SetFloat(MoveZ, 0f);
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;

            // ---- 8 向 blend 输入 + 平移 ----
            Vector2 input = debugInput.sqrMagnitude > 1e-4f ? debugInput : ReadKeyboard();
            moveX = Mathf.MoveTowards(moveX, Mathf.Clamp(input.x, -1f, 1f), blendSpeed * dt);
            moveZ = Mathf.MoveTowards(moveZ, Mathf.Clamp(input.y, -1f, 1f), blendSpeed * dt);
            if (animator != null)
            {
                animator.SetFloat(MoveX, moveX);
                animator.SetFloat(MoveZ, moveZ);
            }

            Vector3 move = (transform.forward * moveZ + transform.right * moveX) * walkSpeed * dt;
            transform.position += new Vector3(move.x, 0f, move.z);

            // ---- 鼠标:按住右键才转身/抬枪(避免 Game 视图里光标持续 delta 导致角色自转) ----
            var mouse = Mouse.current;
            if (mouse != null && mouse.rightButton.isPressed)
            {
                Vector2 d = mouse.delta.ReadValue();
                if (Mathf.Abs(d.x) > 0.01f)
                    transform.Rotate(0f, d.x * mouseSensitivity, 0f, Space.World);
                if (Mathf.Abs(d.y) > 0.01f)
                    pitch = Mathf.Clamp(pitch - d.y * mouseSensitivity, pitchMin, pitchMax);
            }

            // ---- 跳跃(无刚体:垂直速度 + 重力,落地钳制) + C 键蹲切换 ----
            var kb = Keyboard.current;
            if (kb != null && kb.spaceKey.wasPressedThisFrame && transform.position.y <= 0.001f)
                vy = jumpVel;
            if (kb != null && kb.cKey.wasPressedThisFrame) crouched = !crouched;
            vy -= gravity * dt;
            float y = transform.position.y + vy * dt;
            if (y <= 0f) { y = 0f; vy = 0f; }
            transform.position = new Vector3(transform.position.x, y, transform.position.z);

            // ---- 下半身差速追赶(逻辑与 NetworkSoldierAnimator.DriveRig 相同) ----
            float aimYaw = transform.eulerAngles.y;
            if (!yawInit) { dampedYaw = aimYaw; yawInit = true; }
            float lag = Mathf.DeltaAngle(dampedYaw, aimYaw);
            float absLag = Mathf.Abs(lag);
            float rate = absLag / Mathf.Max(0.01f, turnSmoothTime);
            float excess = absLag - SpineMaxTwist;
            if (excess > 0f) rate += excess * turnCatchupBoost;
            float step = Mathf.Min(absLag, rate * dt);
            dampedYaw += Mathf.Sign(lag) * step;
            if (rig != null) rig.SetHipsYaw(dampedYaw);
            if (yawDriver != null) yawDriver.rotation = Quaternion.Euler(0f, dampedYaw, 0f);

            // ---- 脚部地面 IK:逐脚按踝高门控——低(触地)=软钉贴合,高(抬脚)=释放。
            //      权重上限分两档:移动=软钉(0.15,剪辑保留抬脚自由度,级联释放能完成),
            //      静止=完全钉地(站立/转身时脚不漂)。 ----
            if (rig != null)
            {
                float speed01 = Mathf.Clamp01(Mathf.Max(Mathf.Abs(moveX), Mathf.Abs(moveZ)) / 0.25f);
                float cap = Mathf.Lerp(footIKCapIdle, footIKCapMoving, speed01);
                footWeightL = StepFootIK(footWeightL, footBoneL != null ? footBoneL.position.y : 0f, cap, dt);
                footWeightR = StepFootIK(footWeightR, footBoneR != null ? footBoneR.position.y : 0f, cap, dt);
                rig.SetFootIKWeight(footWeightL, footWeightR);
            }

            // ---- 枪模瞄准(经 AimConstraint 的瞄准目标;世界 yaw 用根朝向,pitch 用鼠标) ----
            if (rig != null)
            {
                Vector3 eye = transform.position + Vector3.up * eyeHeight;
                Quaternion aimDir = Quaternion.Euler(pitch, aimYaw, 0f);
                rig.SetAimPose(eye, aimDir);
                rig.SetAimTarget(eye + aimDir * Vector3.forward * aimTargetDistance);
            }

            // ---- 枪绕头部 pivot 公转:位置 = pivot + 瞄准旋转 × 持枪偏移。
            //      旋转中心从枪自身移到头部;AimConstraint 仍负责枪的指向。 ----
            if (gunRoot != null && headAimPivot != null)
                gunRoot.position = headAimPivot.position + Quaternion.Euler(pitch, aimYaw, 0f) * gunHoldOffset;

            // ---- 持枪层权重(与 NetworkSoldierAnimator.UpdateLocomotionLayers 相同) ----
            if (animator != null)
            {
                float l0 = weaponBound ? 0f : 1f;
                animator.SetLayerWeight(LayerFull, l0);
                animator.SetLayerWeight(LayerLower, 1f - l0);
            }
        }

        /// <summary>单脚 IK 权重步进:踝高在 [plant,lift] 区间线性降落,平滑 + cap 钳制。</summary>
        private float StepFootIK(float cur, float ankleY, float cap, float dt)
        {
            float t = (ankleY - footPlantHeight) / Mathf.Max(0.01f, footLiftHeight - footPlantHeight);
            float target = Mathf.Clamp01(1f - t) * cap;
            return Mathf.MoveTowards(cur, target, footWeightSpeed * dt);
        }

        private static Vector2 ReadKeyboard()
        {
            var kb = Keyboard.current;
            if (kb == null) return Vector2.zero;
            float x = 0f, y = 0f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) y += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) y -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1f;
            return new Vector2(x, y);
        }

        /// <summary>GripCap* 下没有 GripRoot* 手腕锚点时补建(BindWeapon 契约)。</summary>
        private static void EnsureGripRoot(Transform gun, string capName, string rootName)
        {
            var cap = FindDeep(gun, capName);
            if (cap == null) return;
            var existing = cap.Find(rootName);
            if (existing != null) return;
            var go = new GameObject(rootName);
            go.transform.SetParent(cap, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
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
