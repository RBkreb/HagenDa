using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Every equipment item (PHASE6). Grouped by how they are used.
    /// </summary>
    public enum EquipmentType
    {
        // 通用投掷物
        Grenade,            // 手雷（延时爆炸）
        SmokeGrenade,       // 烟雾手雷（撞击触发）
        EmpGrenade,         // 电磁手雷（撞击触发 EMP 场）

        // 栓动发射器
        GrenadeLauncher,    // 榴弹炮
        SmokeLauncher,      // 烟雾发射器
        Rpg,                // RPG

        // 炸药类
        SignalCharge,       // 信号炸药（遥控，EMP 可禁）
        WiredCharge,        // 线控炸药（遥控，EMP 不可禁）
        DelayedBomb,        // 延时炸弹（EMP 可禁）

        // 补给
        SmallSupplyPack,    // 小型补给包
        LargeSupplyCrate,   // 大型补给箱

        // 部署 / 自身 / 特有
        Interceptor,        // 拦截系统
        QuickDash,          // 快速机动装置
        ArmorPlate,         // 护甲板
        BlastShield,        // 防爆盾
        HealingSyringe,     // 治疗针（特有）
        Defibrillator       // 除颤仪（特有）
    }

    /// <summary>
    /// How the selected equipment is activated (dispatches the server-side use).
    /// </summary>
    public enum EquipmentUseStyle
    {
        Throw,          // 投掷（手雷/烟雾/EMP/延时/补给包）
        BoltLauncher,   // 栓动发射（榴弹炮/烟雾发射器/RPG）
        RemoteCharge,   // 右键投出不引爆、左键引爆（信号/线控）
        Deploy,         // 放置部署（大型补给箱/拦截系统）
        SelfInstant,    // 瞬时自身（快速机动）
        SelfChannel,    // 引导自身（护甲板/治疗针）
        TargetChannel,  // 引导目标（除颤仪）
        ShieldToggle    // 开关（防爆盾）
    }

    /// <summary>
    /// Loadout slot category (PHASE8). Every equipment item belongs to one of the
    /// three selectable categories in the deploy loadout picker:
    ///   主武器 | 可选配备 | 特有配备 | 通用投掷物
    /// </summary>
    public enum EquipmentCategory
    {
        Optional,   // 可选配备（可选1 / 可选2）
        Special,    // 特有配备
        Throwable   // 通用投掷物
    }

    /// <summary>
    /// Data-driven equipment template (PHASE6). A single ScriptableObject holds
    /// every tunable of an equipment item, driven by the shared
    /// <see cref="NetworkEquipment"/> runtime (player & AI).
    ///
    /// Throwable-based equipment (grenade/launcher/charge/bomb/supply pack) point at
    /// a <see cref="throwablePrefab"/> whose own tuned values (yield/radius/smoke/
    /// EMP) are configured at prefab-build time in NetworkSetup; this asset holds the
    /// meta values (carry count, independent supply cost, bolt time, throw speed).
    /// </summary>
    [CreateAssetMenu(menuName = "HagenDa/Equipment Definition", fileName = "EquipmentDefinition")]
    public class EquipmentDefinition : ScriptableObject
    {
        [Header("Identity")]
        public EquipmentType type;
        public string displayName = "装备";

        [Header("Loadout (PHASE8)")]
        [Tooltip("所属配装槽类别：可选 / 特有 / 通用投掷物。")]
        public EquipmentCategory category = EquipmentCategory.Optional;

        [Tooltip("瞬发型：按槽位键直接生效，不切换到该配备；普通型：切换后才能使用。")]
        public bool instantUse = false;

        [Header("Carry / Supply")]
        [Tooltip("携带上限（弹药/次数）。")]
        public int maxCarry = 1;

        [Tooltip("独立补给度成本：该装备补给度达到此值时 +1 并清零。")]
        public int supplyCost = 100;

        [Tooltip("每隔多少秒自动 +1 弹药（0 = 不自动回复）。用于小型补给包(10s)/除颤仪(3s)/大型补给箱(10s)。")]
        public float ammoRegenInterval = 0f;

        [Tooltip("可被电磁脉冲（EMP）禁用。")]
        public bool empVulnerable = false;

        [Header("Use")]
        public EquipmentUseStyle useStyle;

        [Tooltip("栓动发射器：两发之间的间隔（秒）。")]
        public float boltTime = 1.5f;

        [Tooltip("引导类（护甲板/治疗针/除颤仪）使用耗时（秒）。")]
        public float channelTime = 1.5f;

        [Header("Projectile")]
        [Tooltip("NetworkThrowable 预制体（投掷/发射/放置时生成）。")]
        public GameObject throwablePrefab;

        [Tooltip("投掷/发射初速（m/s）。")]
        public float throwSpeed = 15f;

        [Header("Self / Deploy params")]
        [Tooltip("护甲板：获得的护甲值。")]
        public float armorGrant = 25f;

        [Tooltip("快速机动装置：冷却（秒）。")]
        public float dashCooldown = 15f;

        [Tooltip("防爆盾：爆炸伤害减免比例（0.6 = 减少 60%）。")]
        public float shieldExplosionReduction = 0.6f;

        [Header("EMP (干扰)")]
        [Tooltip("电磁手雷：干扰半径。")]
        public float empRadius = 6f;

        [Tooltip("电磁手雷：存活时间（秒）。")]
        public float empLifetime = 10f;

        [Tooltip("电磁手雷：干扰持续时间（秒）。")]
        public float empInterfereDuration = 2f;
    }
}
