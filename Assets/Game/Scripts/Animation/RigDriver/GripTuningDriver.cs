using HagenDa.Networking;   // PlayerPosture
using UnityEngine;
using UnityEngine.InputSystem;

namespace HagenDa.Animation.RigDriver
{
    /// <summary>
    /// 握持调参场景驱动（**纯本地，无 Mirror / NetworkPlayer**）。
    ///
    /// 存在意义：正常对局里 <see cref="SoldierRigDriver"/> 的每帧输入由
    /// <c>SoldierAnimatorDriver</c>（NetworkBehaviour）喂；调握持不需要联网，
    /// 所以这里用一个最小 MonoBehaviour 直接喂 <see cref="SoldierRigDriver.SetFrameState"/>，
    /// 让**双手 TwoBoneIK + 手指卷握**活起来，方便逐个道具手工摆握把锚点。
    ///
    /// 喂入的状态恒为「站立、静止、持械」，只把**眼位/朝向**交给鼠标，于是：
    ///   · 手臂 IK 目标 = 当前道具的 RearGrip / BarrelGrip（由 BindWeapon 绑定）
    ///   · 手指卷握 = 当前道具的 RearGripCap / BarrelGripCap 胶囊
    ///   · 上半身反扭 / 头部俯仰跟随 / 腰射枪位（GunCylinder 柱面）都按真实链路走
    ///
    /// 操作：
    ///   · 右键拖动 = 转视角（驱动眼位朝向 → 身体 yaw + 头部俯仰）
    ///   · Q         = 腰射 / ADS 切换（检验贴眼锚 RearAim/SightAim）
    ///   · 数字键 1..9 = 切换道具（0 = 第 10 个）
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]   // 与 SoldierAnimatorDriver 一致：早于 RigBuilder 求值
    public class GripTuningDriver : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("角色模型根（含 Animator + RigBuilder + SoldierRigDriver）。留空自动在子级查找。")]
        public GameObject model;
        [Tooltip("枪械基准（实体根下、与模型同级）。留空自动查找 'WeaponBasis'。")]
        public Transform weaponBasis;
        [Tooltip("可切换的道具名（WeaponBasis 的直接子物体）。留空则自动收集全部子物体。")]
        public string[] propNames;

        [Header("瞄准（右键拖动）")]
        [Tooltip("初始朝向 yaw（度）。")]
        public float yaw;
        [Tooltip("初始俯仰 pitch（度）。")]
        public float pitch;
        public float mouseSensitivity = 0.15f;
        public float pitchMin = -70f;
        public float pitchMax = 70f;
        [Tooltip("瞄准目标点距离（Driver_AimTarget = 眼位 + 视线 × 该值）。")]
        public float aimTargetDistance = 50f;
        [Tooltip("眼位高度（相对实体根；无 eyeSource 时使用）。与远端实体回退路径一致。")]
        public float eyeHeight = 1.63f;
        [Tooltip("勾选=眼位取 headcollider（随头部 IK 动，真实路径）；不勾=固定高度（确定性，便于调参）。")]
        public bool eyeFromHeadAnchor;
        [Tooltip("实体根随瞄准 yaw 一起转（身体朝向 = 瞄准方向）。")]
        public bool rotateBodyWithAim = true;

        [Header("状态")]
        [Tooltip("主武器在持。勾掉 = 手 IK 松开（对比动画原始姿态）。")]
        public bool weaponHeld = true;
        [Tooltip("0=腰射 1=全 ADS。运行时按 Q 切换。")]
        [Range(0f, 1f)] public float aimAmount;
        [Tooltip("摄像机（可选）：勾选后由该相机位姿直接驱动眼位/朝向。")]
        public Transform eyeSource;

        private SoldierRigDriver rig;
        private Animator animator;
        private int activeIndex = -1;

        private const int LayerFull = 1, LayerLower = 2, LayerUpper = 3;
        private static readonly int MoveX = Animator.StringToHash("MoveX");
        private static readonly int MoveZ = Animator.StringToHash("MoveZ");
        private static readonly int LocoSpeed = Animator.StringToHash("LocoSpeed");

        /// <summary>当前道具名（HUD 用）。</summary>
        public string ActiveProp =>
            propNames != null && activeIndex >= 0 && activeIndex < propNames.Length
                ? propNames[activeIndex] : "(none)";

        private void Awake()
        {
            ResolveRefs();
            yaw = transform.eulerAngles.y;
        }

        private void Start()
        {
            ResolveRefs();
            SelectProp(0);   // 建场时先绑第一个道具，进 Play 立刻能看到手握住
        }

        private void ResolveRefs()
        {
            if (model == null)
            {
                var a = GetComponentInChildren<Animator>(true);
                if (a != null) model = a.transform.gameObject;
            }
            if (model != null && rig == null)
            {
                rig = model.GetComponent<SoldierRigDriver>();
                if (rig == null) rig = model.GetComponentInChildren<SoldierRigDriver>(true);
            }
            if (rig != null && animator == null) animator = rig.ModelAnimator;

            if (weaponBasis == null && model != null)
            {
                var root = model.transform.parent != null ? model.transform.parent : model.transform;
                weaponBasis = root.Find("WeaponBasis");
            }
            if ((propNames == null || propNames.Length == 0) && weaponBasis != null)
            {
                propNames = new string[weaponBasis.childCount];
                for (int i = 0; i < weaponBasis.childCount; i++)
                    propNames[i] = weaponBasis.GetChild(i).name;
            }
        }

        private void Update()
        {
            ResolveRefs();
            if (rig == null || animator == null) return;

            ReadInput();

            // ---- 动画层：静止 → 层权重全 0（露 L0 绑定姿态 = 站立 T pose，四肢交给 IK）----
            animator.SetFloat(MoveX, 0f);
            animator.SetFloat(MoveZ, 0f);
            animator.SetFloat(LocoSpeed, 1f);
            animator.SetLayerWeight(LayerFull, 0f);
            animator.SetLayerWeight(LayerLower, 0f);
            animator.SetLayerWeight(LayerUpper, 0f);

            // ---- 眼位/朝向：相机优先，否则取头锚 + yaw/pitch ----
            Vector3 eyePos;
            Quaternion eyeRot;
            if (eyeSource != null)
            {
                eyePos = eyeSource.position;
                eyeRot = eyeSource.rotation;
            }
            else
            {
                var head = eyeFromHeadAnchor ? rig.HeadAnchor : null;
                eyePos = head != null
                    ? head.position
                    : transform.position + Vector3.up * eyeHeight;
                eyeRot = Quaternion.Euler(pitch, yaw, 0f);
            }

            // 身体朝向：模型根即时对齐瞄准 yaw（与真实路径一致 —— 上半身再即时跟随，
            // 下半身滞后由脚 IK 基准点承担）。实体根转了，模型作为子级一起转。
            if (rotateBodyWithAim && eyeSource == null)
                transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            var s = new SoldierFrameState
            {
                dead = false,
                posture = PlayerPosture.Stand,
                sliding = false,
                airborne = false,
                moving = false,
                eyePos = eyePos,
                eyeRot = eyeRot,
                weaponHeld = weaponHeld && ActiveObj() != null,
                aimAmount = aimAmount,
                sprinting = false,
                recoil = 0f,
                shotCount = 0,
                bodyOffsetExtra = Vector3.zero,
            };
            rig.SetFrameState(in s);
        }

        // ---------------------------------------------------------------
        // 输入
        // ---------------------------------------------------------------

        private void ReadInput()
        {
            var mouse = Mouse.current;
            if (mouse != null && mouse.rightButton.isPressed)
            {
                Vector2 d = mouse.delta.ReadValue();
                if (Mathf.Abs(d.x) > 0.01f) yaw += d.x * mouseSensitivity;
                if (Mathf.Abs(d.y) > 0.01f)
                    pitch = Mathf.Clamp(pitch - d.y * mouseSensitivity, pitchMin, pitchMax);
            }

            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.qKey.wasPressedThisFrame)
                aimAmount = aimAmount > 0.5f ? 0f : 1f;

            // 数字键 1..9 / 0(=第10) 切换道具
            for (int i = 0; i < 10; i++)
            {
                var key = kb[Key.Digit1 + i];
                if (key != null && key.wasPressedThisFrame)
                {
                    SelectProp(i);
                    break;
                }
            }
        }

        // ---------------------------------------------------------------
        // 道具切换
        // ---------------------------------------------------------------

        /// <summary>激活第 index 个道具并重新绑定双手 IK（幂等）。</summary>
        public void SelectProp(int index)
        {
            ResolveRefs();
            if (weaponBasis == null || propNames == null || propNames.Length == 0) return;
            if (index < 0 || index >= propNames.Length) return;
            if (index == activeIndex) return;

            for (int i = 0; i < weaponBasis.childCount; i++)
            {
                var c = weaponBasis.GetChild(i);
                c.gameObject.SetActive(c.name == propNames[index]);
            }
            activeIndex = index;

            if (rig == null) return;
            rig.ClearWeapon();
            var t = ActiveObj();
            if (t != null)
            {
                // 基准必须是道具的父级（BindWeapon 取 gun.transform.parent 作为 weaponBasis）
                rig.BindWeapon(t.gameObject);
            }
        }

        private Transform ActiveObj()
        {
            if (weaponBasis == null || propNames == null) return null;
            if (activeIndex < 0 || activeIndex >= propNames.Length) return null;
            return weaponBasis.Find(propNames[activeIndex]);
        }

        private void OnGUI()
        {
            var style = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            style.normal.textColor = Color.white;
            var dim = new GUIStyle(style) { fontSize = 12 };
            dim.normal.textColor = new Color(0.75f, 0.8f, 0.9f);

            GUILayout.BeginArea(new Rect(12f, 12f, 580f, 260f));
            GUILayout.Label($"[握持调参] 当前道具: {ActiveProp}", style);
            GUILayout.Label("右键拖动 = 转视角    Q = 腰射/ADS", style);
            GUILayout.Label("数字键 1..9 / 0 = 切换道具", style);
            GUILayout.Label($"aimAmount={(aimAmount > 0.5f ? "ADS" : "腰射")}   weaponHeld={weaponHeld}", style);
            GUILayout.Label("调参流程: 退出 Play → 拖动该道具的 RearGrip/BarrelGrip", dim);
            GUILayout.Label("与握把胶囊 → 菜单 HagenDa/SoldierAnim/Re-analyze Scene Grips", dim);
            GUILayout.Label("→ 再进 Play 验证。一次调一个道具（握持常量是逐个道具烘焙的）。", dim);
            if (propNames != null)
            {
                for (int i = 0; i < propNames.Length && i < 10; i++)
                {
                    string k = i == 9 ? "0" : (i + 1).ToString();
                    string mark = i == activeIndex ? "▶" : " ";
                    GUILayout.Label($"  {mark} [{k}] {propNames[i]}", style);
                }
            }
            GUILayout.EndArea();
        }
    }
}
