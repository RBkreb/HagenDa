using HagenDa.Animation.Rigging;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HagenDa.Animation.RigGraph
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
        [Tooltip("触地带宽(米,相对该脚观测到的最低踝高=落地帧)。低于 最低+该值 = 触地。")]
        public float footPlantBand = 0.02f;
        [Tooltip("释放带宽(米,相对最低踝高)。高于 最低+该值 = 完全释放(剪辑自然摆腿)。")]
        public float footLiftBand = 0.09f;
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
        private float minAnkleL = float.MaxValue, minAnkleR = float.MaxValue;   // 逐脚最低踝高(自适应基准)
        private Transform footBoneL, footBoneR;
        private bool crouched;
        private float poseCrouchCur;
        private bool yawInit;
        private float dampedYaw;
        private float aimAmount;      // PHASE14:右键 ADS 平滑量
        private uint shotCount;       // PHASE14:枪机往复触发(无网络时本地模拟)

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
                rig.SetSpineWeight(1f);       // 上半身立即跟随水平瞄准(脊柱反扭)
                rig.SetPoseWeights(0f, 0f);   // Fatui 无 SkeletonPose 姿态资产(蹲用 MultiPosition 下移)
                rig.SetHandsWeight(1f);

                yawDriver = rig.YawDriver;
                footBoneL = rig.FootTipL;
                footBoneR = rig.FootTipR;
                TryBindWeapon();
            }

            // ---- Head aim pivot ----
            if (headAimPivot == null) headAimPivot = transform.Find("HeadAimPivot");
            if (headAimPivot == null)
            {
                var go = new GameObject("HeadAimPivot");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = headPivotLocalPos;
                headAimPivot = go.transform;
            }

            if (animator != null)
            {
                animator.SetFloat(MoveX, 0f);
                animator.SetFloat(MoveZ, 0f);
            }
        }

        /// <summary>
        /// 绑定枪械基准下的枪(PHASE14:基准与模型同级挂在胶囊根下)。
        /// 打包前 Start 可能早于 Animator 就绪,BindWeapon 内部只登记一次待重建,
        /// 等 Animator 就绪后由 SoldierRigSetup 执行 —— 无逐帧重试。
        /// </summary>
        private bool TryBindWeapon()
        {
            if (rig == null || weaponBound) return weaponBound;
            var gunT = FindDeep(transform, "M4_8");
            if (gunT == null) return false;

            rig.BindWeapon(gunT.gameObject);
            weaponBound = true;
            gunRoot = gunT;
            return true;
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

            // ---- 蹲:MultiPositionConstraint 把骨架根(hips)压低 + 脚 IK 贴地 → 屈膝。
            //      (PHASE14 姿态专项;与 NetworkSoldierAnimator 同款机制,无姿态资产依赖) ----
            if (rig != null && transform.position.y <= 0.001f)
            {
                poseCrouchCur = Mathf.MoveTowards(poseCrouchCur, crouched ? 1f : 0f, crouchRampSpeed * dt);
                rig.SetBodyOffset(Vector3.Lerp(Vector3.zero, crouchOffset, poseCrouchCur));
                rig.SetHipsPosWeight(poseCrouchCur);
            }

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
            //      基准 = 该脚观测到的最低踝高(落地帧),自适应标定,模型无关。
            //      权重上限分两档:移动=软钉(0.15,剪辑保留抬脚自由度),
            //      静止=完全钉地(站立/转身时脚不漂)。 ----
            if (rig != null)
            {
                float speed01 = Mathf.Clamp01(Mathf.Max(Mathf.Abs(moveX), Mathf.Abs(moveZ)) / 0.25f);
                float cap = Mathf.Lerp(footIKCapIdle, footIKCapMoving, speed01);
                float rootY = transform.position.y;
                footWeightL = StepFootIK(footWeightL, footBoneL != null ? footBoneL.position.y - rootY : 0f, cap, dt, ref minAnkleL);
                footWeightR = StepFootIK(footWeightR, footBoneR != null ? footBoneR.position.y - rootY : 0f, cap, dt, ref minAnkleR);
                rig.SetFootIKWeight(footWeightL, footWeightR);
            }

            // ---- 枪模瞄准(经 AimConstraint 的瞄准目标;世界 yaw 用根朝向,pitch 用鼠标) ----
            if (rig != null)
            {
                Vector3 eye = transform.position + Vector3.up * eyeHeight;
                Quaternion aimDir = Quaternion.Euler(pitch, aimYaw, 0f);
                rig.SetAimPose(eye, aimDir);
                rig.SetAimTarget(eye + aimDir * Vector3.forward * aimTargetDistance);

                // PHASE14 枪械表现:右键 ADS(瞳距拉近) + Shift 冲刺摆枪。
                // 无网络,直接喂状态量;枪机往复用模拟 shotCount(左键按下时递增)。
                bool ads = mouse != null && mouse.rightButton.isPressed;
                aimAmount = Mathf.MoveTowards(aimAmount, ads ? 1f : 0f, dt * 4f);
                bool sprint = kb != null && kb.leftShiftKey.isPressed && Mathf.Abs(moveZ) > 0.1f;
                if (mouse != null && mouse.leftButton.wasPressedThisFrame) shotCount++;
                rig.SetWeaponState(aimAmount, sprint, 0f, shotCount);
            }

            // 枪的位置/朝向现由 WeaponAnchor 驱动(SetWeaponState),不再手工公转——
            // 手工改枪的 transform 会让握把锚点脱离手臂 IK 目标,手指/手腕再次脱节。

            // ---- 持枪层权重(与 NetworkSoldierAnimator.UpdateLocomotionLayers 相同) ----
            if (animator != null)
            {
                float l0 = weaponBound ? 0f : 1f;
                animator.SetLayerWeight(LayerFull, l0);
                animator.SetLayerWeight(LayerLower, 1f - l0);
            }
        }

        /// <summary>
        /// 单脚 IK 权重步进:基准 = 该脚观测到的最低踝高(落地帧),自适应标定。
        /// 低(触地)=启用,高(抬脚)=释放,中间平滑过渡,上限 cap。
        /// </summary>
        private float StepFootIK(float cur, float ankleRel, float cap, float dt, ref float minAnkle)
        {
            if (ankleRel < minAnkle) minAnkle = ankleRel;                              // 记录新的最低点
            else minAnkle = Mathf.MoveTowards(minAnkle, ankleRel, dt * 0.05f);         // 极慢回弹,适应坡面
            float t = (ankleRel - (minAnkle + footPlantBand)) / Mathf.Max(0.01f, footLiftBand - footPlantBand);
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
