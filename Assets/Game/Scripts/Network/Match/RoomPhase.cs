using System;

namespace HagenDa.Networking
{
    /// <summary>
    /// 房间三态(PHASE15 局间系统)。
    ///
    ///  - <see cref="Idle"/>  空闲:刚进房间/上一局已结束。地图与实体均未加载,等待 host 开局。
    ///  - <see cref="Ready"/> 准备:host 已决定开局,正在加载地图资源并分配小队。玩家在此等待。
    ///  - <see cref="Match"/> 对局:地图与小队均就绪,正式对局中。实体尚未部署,玩家选点后入场。
    ///
    /// 序号写入 SyncVar(以 int 上网),**不可重排**——与 <c>WireContractTests</c> 的枚举钉住约定一致。
    /// </summary>
    public enum RoomPhase
    {
        Idle = 0,
        Ready = 1,
        Match = 2
    }

    /// <summary>
    /// 房间相位迁移规则(PHASE15,纯逻辑,无 Unity 依赖 → 可 EditMode 直测)。
    ///
    /// 规格映射:
    ///  空闲 → 准备:host 决定开始对局(开始加载地图 + 分配小队)。
    ///  准备 → 对局:地图加载完成且小队分配完成。
    ///  对局 → 空闲:对局正常结束,或对局内真人全部退出而强制结束。
    ///  准备 → 空闲:取消开局 / host 解散房间。
    ///
    /// 非法迁移(如 空闲→对局 跳过加载、对局→准备 回退)一律拒绝。
    /// </summary>
    public static class RoomPhaseRules
    {
        /// <summary>该迁移是否合法。</summary>
        public static bool CanTransition(RoomPhase from, RoomPhase to)
        {
            switch (from)
            {
                case RoomPhase.Idle:
                    return to == RoomPhase.Ready;
                case RoomPhase.Ready:
                    return to == RoomPhase.Idle || to == RoomPhase.Match;
                case RoomPhase.Match:
                    return to == RoomPhase.Idle;
                default:
                    return false;
            }
        }

        /// <summary>是否处于"地图/小队已就绪"的对局态(对局中才允许部署)。</summary>
        public static bool AllowsDeploy(RoomPhase phase) => phase == RoomPhase.Match;

        /// <summary>是否处于"host 可发起开局"的空闲态。</summary>
        public static bool AllowsHostStart(RoomPhase phase) => phase == RoomPhase.Idle;

        /// <summary>迁移失败时的可读原因(日志/断言用)。</summary>
        public static string DescribeTransition(RoomPhase from, RoomPhase to)
        {
            if (CanTransition(from, to)) return $"{from} → {to}";
            return $"非法迁移 {from} → {to}(允许:{AllowedTargets(from)})";
        }

        private static string AllowedTargets(RoomPhase from)
        {
            switch (from)
            {
                case RoomPhase.Idle: return "Ready";
                case RoomPhase.Ready: return "Idle, Match";
                case RoomPhase.Match: return "Idle";
                default: return "无";
            }
        }

        /// <summary>把相位解析为枚举(越界值时抛错,便于捕获协议错位)。</summary>
        public static RoomPhase FromInt(int value)
        {
            if (!Enum.IsDefined(typeof(RoomPhase), value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "未知的 RoomPhase 取值。");
            return (RoomPhase)value;
        }
    }
}
