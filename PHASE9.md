# PHASE9 — GOAP AI 系统 Spec（v2 — 已确认所有决策）

## 0. 概述

用 CrashKonijn GOAP v3.1.2 替换现有 `NetworkAIController` 的简单 FSM（Wander/Seek/Attack），构建分层战术 AI 系统，为未来大模型指挥官留接口。

### 设计决策总表

| # | 决策项 | 选择 | 理由 |
|---|--------|------|------|
| 1 | AI 复杂度 | 完整战术 AI | 夺点/防守/撤退/补给/小队配合/全配备使用 |
| 2 | Goal 选择 | 混合（硬规则 + Utility） | 残血硬触发撤退，中低优先级 Utility 评分 |
| 3 | 配置方式 | ScriptableObject | Capability + AgentTypeConfig SO 资产 |
| 4 | AgentType | 单一 + 动态启用 | 共享 AgentType，按配装 enable/disable Action |
| 5 | 换弹 | 给 NetworkGun 加 `RequestReload()` | AI 直接调用，干净可控 |
| 6 | AI 标记 | 仅感应器间接标记 | AI 不直接标记，但部署感应器自动标记敌人 |
| 7 | 爆破物 | PHASE9 跳过 | SignalCharge/WiredCharge/DelayedBomb 禁用 |
| 8 | 发射器瞄准 | 直线瞄准 | 用 transform.forward，不考虑抛物线 |
| 9 | 性能 | 60 AI 多线程 + 分批 | 充分利用 GOAP 多线程，分批计算 |
| 10 | 小队长 | 第一个 AI（实例 ID 最小） | 先 spawn 的为队长，与人类无关 |
| 11 | 补给 | 有则自部署，无则靠近友军广播 | 找不到补给箱时靠近友军并广播补给请求 |
| 12 | 掩体 | NavMesh.Raycast | 用射线找最近障碍物作为掩体 |
| 13 | 姿态 | 进攻站立，防御蹲下/趴下 | 根据掩体决定射击高度 |
| 14 | 除颤仪 | 有条件复活 | 仅在无近期威胁时才复活友军 |
| 15 | 巡逻 | 小队长选点位，小队跟随 | 队长选未占领/争夺中点位，队员跟随 |
| 16 | 差异化 | 配装驱动 | 行为差异由配装决定 |
| 17 | 防爆盾 | AI 禁用 BlastShield | 用户指定：AI 不使用防爆盾 |
| 18 | 后座力/散布 | AI 受后座力和散布影响 | 与玩家一致（压枪机制待讨论） |
| 19 | 视野 | 前方 160° + 感知 50m | 用户指定 |
| 20 | 攻击距离 | 无限（视野/标记/广播提供目标） | 用户指定：无视野时靠标记+队友广播 |
| 21 | 烟雾遮挡 | 服务器端烟雾遮挡 AI 视野 | 用户指定：AI 视野不能穿透烟雾 |

### 五层架构

```
┌──────────────────────────────────────────────────┐
│  Layer 0: SquadOrderReceiver（未来 LLM 指挥官）   │  宏观：小队级指令
├──────────────────────────────────────────────────┤
│  Layer 1: GoalSelector（硬规则 + Utility）        │  策略：选当前 Goal
├──────────────────────────────────────────────────┤
│  Layer 2: Sensors（感知层）                       │  感知：WorldState
├──────────────────────────────────────────────────┤
│  Layer 3: GOAP Resolver（规划层）                │  规划：选最佳行动链
├──────────────────────────────────────────────────┤
│  Layer 4: Actions（执行层）                       │  执行：驱动现有系统
└──────────────────────────────────────────────────┘
```

---

## 1. 文件结构

```
Assets/Scripts/Network/AI/
├── GoapAgentInitializer.cs       — 初始化 GOAP Agent（设 AgentType + 动态启用 Action）
├── AgentNavMeshMove.cs            — NavMeshAgent 移动适配（监听 GOAP 事件）
├── GoalSelector.cs                — 混合目标选择器
├── SquadOrderReceiver.cs          — 小队指令接收器（未来 LLM 指挥官接口）
├── AIDataProvider.cs              — AI 本地数据缓存（供 Sensor 读取的统一接口）
├── CoverSystem.cs                 — NavMesh.Raycast 掩体查找
├── VisionSystem.cs                — AI 视野（160° 视锥 + 50m + 烟雾/几何遮挡）
├── ServerSmokeRegistry.cs         — 服务器端烟雾注册表（AI 视野遮挡用）
├── IntelBroadcast.cs              — AI 情报广播（队友共享敌人位置）
│
├── Goals/
│   ├── SurviveGoal.cs
│   ├── ResupplyGoal.cs
│   ├── CaptureObjectiveGoal.cs
│   ├── DefendObjectiveGoal.cs
│   ├── EliminateEnemyGoal.cs
│   ├── SupportSquadGoal.cs
│   └── PatrolGoal.cs
│
├── Actions/
│   ├── AttackEnemyAction.cs
│   ├── ReloadAction.cs
│   ├── ThrowGrenadeAction.cs
│   ├── ThrowSmokeAction.cs
│   ├── ThrowEmpGrenadeAction.cs
│   ├── FireGrenadeLauncherAction.cs
│   ├── FireSmokeLauncherAction.cs
│   ├── FireRpgAction.cs
│   ├── ThrowSupplyPackAction.cs
│   ├── DeploySupplyCrateAction.cs
│   ├── DeployInterceptorAction.cs
│   ├── DeploySensorAction.cs
│   ├── UseJammerAction.cs
│   ├── QuickDashAction.cs
│   ├── ApplyArmorPlateAction.cs
│   ├── UseHealingSyringeAction.cs
│   ├── UseDefibrillatorAction.cs
│   ├── ToggleBlastShieldAction.cs
│   ├── MoveToCapturePointAction.cs
│   ├── MoveToDefendPointAction.cs
│   ├── RetreatToGarrisonAction.cs
│   ├── MoveToSupplyAction.cs
│   ├── FollowSquadAction.cs
│   └── WanderAction.cs
│
├── Sensors/
│   ├── LocalWorld/
│   │   ├── HealthLevelSensor.cs
│   │   ├── AmmoLevelSensor.cs
│   │   ├── ArmorLevelSensor.cs
│   │   ├── IsReloadingSensor.cs
│   │   ├── EnemyInSightSensor.cs
│   │   ├── EnemyInAttackRangeSensor.cs
│   │   ├── IsUnderFireSensor.cs
│   │   ├── AtCapturePointSensor.cs
│   │   ├── SelfIsMarkedSensor.cs
│   │   ├── SelfMarkImmuneSensor.cs
│   │   ├── EnemyMarkedNearbySensor.cs
│   │   ├── SensorActiveSensor.cs
│   │   ├── AllyDownedSensor.cs
│   │   ├── HasKnownEnemySensor.cs     — 有已知目标（视野/标记/广播）
│   │   ├── SquadLeaderAliveSensor.cs
│   │   └── EquipmentSensors.cs       — 多个 Has* Sensor 用 MultiSensorBase 合并
│   ├── LocalTarget/
│   │   ├── NearestEnemySensor.cs
│   │   ├── NearestMarkedEnemySensor.cs
│   │   ├── NearestCapturePointSensor.cs
│   │   ├── NearestOwnedPointSensor.cs
│   │   ├── NearestGarrisonSensor.cs
│   │   ├── NearestSupplySensor.cs
│   │   ├── NearestDownedAllySensor.cs
│   │   ├── NearestLowAmmoAllySensor.cs
│   │   ├── NearestEnemyDeployableSensor.cs
│   │   ├── SquadLeaderPosSensor.cs
│   │   └── WanderPointSensor.cs
│   └── GlobalWorld/
│       ├── AnyPointContestedSensor.cs
│       └── MatchOverSensor.cs
│
├── Keys/
│   ├── WorldKeys.cs               — 所有 WorldKeyBase 子类
│   └── TargetKeys.cs              — 所有 TargetKeyBase 子类
│
└── Config/
    ├── AICombatantGenerator.asset   — GOAP Generator（scope 入口）
    ├── CombatantCapability.asset    — Capability SO
    └── CombatantAgentType.asset     — AgentTypeConfig SO
```

---

## 2. WorldKeys

所有 WorldKey 值为 `int`。

### 2.1 基础状态

| WorldKey | 含义 | 取值 | Sensor |
|----------|------|------|--------|
| `HealthLevel` | 血量百分比 | 0-100 | HealthLevelSensor |
| `ArmorLevel` | 护甲百分比 | 0-100 | ArmorLevelSensor |
| `AmmoLevel` | 弹药百分比 | 0-100 | AmmoLevelSensor |
| `IsReloading` | 换弹中 | 0/1 | IsReloadingSensor |

### 2.2 敌情感知

| WorldKey | 含义 | 取值 | Sensor |
|----------|------|------|--------|
| `EnemyInSight` | 50m 内 160° 视锥内直接看到敌人（无几何/烟雾遮挡） | 0/1 | EnemyInSightSensor |
| `HasKnownEnemy` | 有已知目标（视野 OR 标记 OR 情报广播） | 0/1 | HasKnownEnemySensor |
| `IsUnderFire` | 近 3s 受过伤害 | 0/1 | IsUnderFireSensor |
| `EnemyMarkedNearby` | 附近有被标记敌人 | 0/1 | EnemyMarkedNearbySensor |

### 2.3 标记状态

| WorldKey | 含义 | 取值 | Sensor |
|----------|------|------|--------|
| `SelfIsMarked` | 自身被标记中 | 0/1 | SelfIsMarkedSensor |
| `SelfMarkImmune` | 自身标记免疫中 | 0/1 | SelfMarkImmuneSensor |
| `SensorActive` | 己方感应器在场 | 0/1 | SensorActiveSensor |

### 2.4 目标/位置

| WorldKey | 含义 | 取值 | Sensor |
|----------|------|------|--------|
| `AtCapturePoint` | 在某个点位内 | 0/1 | AtCapturePointSensor |
| `IsSafe` | 在己方 GR 范围内 | 0/1 | (local, 派生) |
| `SquadLeaderAlive` | 小队长存活 | 0/1 | SquadLeaderAliveSensor |
| `AllyDowned` | 附近有倒地友军 | 0/1 | AllyDownedSensor |

### 2.5 配装状态（用 MultiSensorBase 合并到 EquipmentSensors）

每个 Has* WorldKey 对应一种装备是否有弹药：

| WorldKey | 装备 | 取值 |
|----------|------|------|
| `HasGrenade` | Grenade | 0/1 |
| `HasSmokeGrenade` | SmokeGrenade | 0/1 |
| `HasEmpGrenade` | EmpGrenade | 0/1 |
| `HasGrenadeLauncher` | GrenadeLauncher | 0/1 |
| `HasSmokeLauncher` | SmokeLauncher | 0/1 |
| `HasRpg` | Rpg | 0/1 |
| `HasSupplyPack` | SmallSupplyPack | 0/1 |
| `HasSupplyCrate` | LargeSupplyCrate | 0/1 |
| `HasInterceptor` | Interceptor | 0/1 |
| `HasSensorEquip` | Sensor | 0/1 |
| `HasQuickDash` | QuickDash | 0/1 |
| `HasJammer` | Jammer | 0/1 |
| `HasArmorPlate` | ArmorPlate | 0/1 |
| `HasHealingSyringe` | HealingSyringe | 0/1 |
| `HasDefibrillator` | Defibrillator | 0/1 |

> SignalCharge / WiredCharge / DelayedBomb 在 PHASE9 禁用，不设 WorldKey/Action。
> BlastShield 在 PHASE9 禁用（用户指定 AI 不使用防爆盾），不设 WorldKey/Action。

---

## 3. TargetKeys

| TargetKey | 含义 | Sensor |
|-----------|------|--------|
| `NearestEnemy` | 最近敌人位置 | NearestEnemySensor |
| `NearestMarkedEnemy` | 最近被标记敌人（优先） | NearestMarkedEnemySensor |
| `NearestCapturePoint` | 最近未占领/争夺中点位 | NearestCapturePointSensor |
| `NearestOwnedPoint` | 最近己方已占领点位 | NearestOwnedPointSensor |
| `NearestGarrison` | 最近己方 GR | NearestGarrisonSensor |
| `NearestSupply` | 最近已部署补给箱 | NearestSupplySensor |
| `NearestDownedAlly` | 最近倒地友军 | NearestDownedAllySensor |
| `NearestLowAmmoAlly` | 最近弹药不足的友军 | NearestLowAmmoAllySensor |
| `NearestEnemyDeployable` | 最近敌方部署物 | NearestEnemyDeployableSensor |
| `SquadLeaderPos` | 小队长位置 | SquadLeaderPosSensor |
| `WanderPoint` | 小队长选定的巡逻目标点 | WanderPointSensor |

> NearestEnemySensor 目标优先级：① 被标记敌人（`NearestMarkedEnemy`）② 直接视野内敌人（50m/160°/无遮挡）③ 情报广播的敌人位置。攻击距离无限，但必须"知道"目标位置（上述三层来源之一）。

---

## 3.5 视野系统（VisionSystem）— 用户指定

### 视野规范

| 参数 | 值 | 说明 |
|------|-----|------|
| 视锥角度 | 前方 160°（半角 80°） | 与 forward 夹角 ≤ 80° 才在视野内 |
| 感知距离 | 50m | 超过 50m 不主动感知（除非标记/广播） |
| 几何遮挡 | 是 | Physics.Raycast 检测墙体/障碍 |
| 烟雾遮挡 | 是 | 高浓度烟雾遮挡视野（服务器端注册） |
| 攻击距离 | 无限 | 需通过视野/标记/广播获得目标 |

### 目标来源三层模型

```
┌─ ① 直接视野（50m 内 + 160° + 无几何/烟雾遮挡）── 完全自主感知
├─ ② 标记系统（敌方被感应器/玩家标记）── 无论距离，位置已知
└─ ③ 情报广播（队友 AI 看到敌人后广播）── 队友共享敌人位置（有时效）
```

### VisionSystem 实现

```csharp
public class VisionSystem : MonoBehaviour
{
    public float viewAngle = 160f;     // 视锥角度
    public float senseRange = 50f;     // 感知距离

    /// <summary>敌人是否在直接视野内（50m + 160° + 无遮挡）。</summary>
    public bool CanSee(NetworkCombatant self, NetworkCombatant target)
    {
        Vector3 to = target.transform.position - self.transform.position;
        float dist = to.magnitude;
        if (dist > senseRange) return false;

        // 视锥角：前方 160°（与 forward 夹角 ≤ 80°）。
        float angle = Vector3.Angle(self.transform.forward, to.normalized);
        if (angle > viewAngle * 0.5f) return false;

        // 几何遮挡。
        if (Physics.Raycast(EyePos(self), to.normalized, dist,
                            Physics.DefaultRaycastLayers,
                            QueryTriggerInteraction.Ignore))
            return false;

        // 烟雾遮挡（服务器端烟雾球体）。
        if (ServerSmokeRegistry.Instance.IsSmokeBlocked(EyePos(self), target.transform.position))
            return false;

        return true;
    }
}
```

### 服务器端烟雾注册（ServerSmokeRegistry）

烟雾目前是纯客户端的（`NetworkSmoke` 由 ClientRpc 触发）。为让 AI 视野被烟雾遮挡，新增服务器端注册表：

```csharp
public class ServerSmokeRegistry
{
    public static ServerSmokeRegistry Instance { get; }

    private readonly List<SmokeEntry> smokeClouds = new();

    public struct SmokeEntry
    {
        public Vector3 position;
        public float radius;
        public float concentration;
        public float startTime;
        public float decayTime;
    }

    /// <summary>SmokeThrowable.OnImpact 服务器端调用，注册烟雾。</summary>
    public void Register(Vector3 pos, float concentration, float radius, float decayTime)
    {
        smokeClouds.Add(new SmokeEntry { ... });
    }

    /// <summary>判断两点的连线是否被"有效浓度"烟雾遮挡。</summary>
    public bool IsSmokeBlocked(Vector3 from, Vector3 to)
    {
        // 对每个活跃烟雾：线段到球心距离 < 半径 且 当前浓度 > 0.3 → 遮挡。
        // 浓度随时间线性衰减：concentration * (1 - t/decayTime)。
        // 阈值 0.3：稀薄（即将消散）的烟雾不遮挡视野。
    }
}
```

`SmokeThrowable.OnImpact` 修改：服务器端也调用 `ServerSmokeRegistry.Instance.Register(...)`。

### 情报广播（IntelBroadcast）

攻击距离无限时，AI 依赖队友共享的敌人情报：

```csharp
public class IntelBroadcast : MonoBehaviour
{
    // 每个 AI 维护一份"已知敌人"缓存（位置 + 时间戳）。
    private readonly Dictionary<NetworkCombatant, float> knownEnemies = new(); // enemy → lastSeenTime

    /// <summary>AI 直接看到敌人时，广播给附近队友。</summary>
    public void BroadcastSeenEnemy(NetworkCombatant enemy)
    {
        // 广播范围：同小队 + 100m 半径内。
        // 队友收到后更新自己的 knownEnemies 缓存。
    }

    /// <summary>获取所有已知敌人（视野 + 标记 + 广播），供 NearestEnemySensor 使用。</summary>
    public NetworkCombatant GetNearestKnownEnemy() { ... }
}
```

广播参数：半径 **100m**，情报失效 **5s**（`lastSeenTime` 超过 5s 后从 knownEnemies 移除）。

---

## 4. Goals

### 4.1 Goal 列表与条件

| Goal | Conditions | 目的 |
|------|-----------|------|
| `SurviveGoal` | `IsSafe >= 1` | 撤退到 GR / 掩体 |
| `ResupplyGoal` | `AmmoLevel >= 50` 且 `HealthLevel >= 50` | 补给或自部署补给箱 |
| `CaptureObjectiveGoal` | `AtCapturePoint >= 1` | 到达争夺中点位并站住 |
| `DefendObjectiveGoal` | `AtCapturePoint >= 1` | 到达己方点位并站住 |
| `EliminateEnemyGoal` | `HasKnownEnemy = 0` | 清除所有已知敌人 |
| `SupportSquadGoal` | (跟随小队长) | 跟随小队长移动 |
| `PatrolGoal` | (无条件，永远可达) | 默认兜底 |

### 4.2 GoalSelector — 混合选择算法

```
每 2s 执行一次（GOAP 多线程分批，60 AI 分 6 批 × 10 AI/批）：

// === 硬规则（绝对优先）===
if matchOver        → Goal = null（冻结）
if health < 25       → SurviveGoal
if SelfIsMarked && HasJammer → SurviveGoal（驱动 UseJammerAction）

// === SquadOrder 覆盖（未来 LLM 指挥官）===
if squadOrder != None:
    Capture → CaptureObjectiveGoal (×2 权重)
    Defend  → DefendObjectiveGoal (×2 权重)
    Attack  → EliminateEnemyGoal (×2 权重)
    Regroup → SupportSquadGoal (×2 权重)
    Hold    → PatrolGoal at position (×2 权重)

// === Utility 评分 ===
scores = {
    ResupplyGoal:      (100 - AmmoLevel) * 0.4 + (100 - HealthLevel) * 0.3
    CaptureObjective:  contestedPointExists ? 50 : 0 + distanceBonus
    DefendObjective:   ownedPointContested ? 65 : 0 + distanceBonus
    EliminateEnemy:    hasKnownEnemy ? 40 : 0
    SupportSquad:      squadLeaderAlive ? 25 : 0
    PatrolGoal:        10  // 兜底
}
→ 选最高分 Goal，agent.SetGoal(goal)
```

---

## 5. Actions

### 5.1 完整 Action 列表（禁用 3 个爆破 + 1 个防爆盾 = 20 个活跃 Action）

#### 攻击型

| Action | Target | Conditions | Effects | Cost | StopDist | MoveMode | 说明 |
|--------|--------|-----------|---------|------|---------|----------|------|
| AttackEnemyAction | NearestEnemy | HasKnownEnemy=1 | HasKnownEnemy Decrease | 5 | 0 | PerformWhileMoving | 攻击已知目标，距离无限，进攻时站立 |
| ReloadAction | (self) | IsReloading=0, AmmoLevel<30 | AmmoLevel Increase | 2 | 0 | - | 调 gun.RequestReload() |
| ThrowGrenadeAction | NearestEnemy | HasGrenade=1, HasKnownEnemy=1 | HasGrenade Decrease | 8 | 30 | MoveBeforePerforming | 抛物线投掷 |
| FireGrenadeLauncherAction | NearestEnemy | HasGrenadeLauncher=1, HasKnownEnemy=1 | HasGrenadeLauncher Decrease | 6 | 40 | MoveBeforePerforming | 直线瞄准 |
| FireRpgAction | NearestEnemy | HasRpg=1, HasKnownEnemy=1 | HasRpg Decrease | 5 | 50 | MoveBeforePerforming | 优先打集群 |
| ThrowEmpGrenadeAction | NearestEnemyDeployable | HasEmpGrenade=1 | HasEmpGrenade Decrease | 7 | 25 | MoveBeforePerforming | 打敌方部署物 |

#### 战术辅助型

| Action | Target | Conditions | Effects | Cost | StopDist | MoveMode | 说明 |
|--------|--------|-----------|---------|------|---------|----------|------|
| ThrowSmokeAction | (self/方向) | HasSmokeGrenade=1, IsUnderFire=1 | HasSmokeGrenade Decrease | 6 | 0 | - | 被射击时投烟雾 |
| FireSmokeLauncherAction | (self/方向) | HasSmokeLauncher=1, IsUnderFire=1 | HasSmokeLauncher Decrease | 6 | 0 | - | 栓动发射烟雾 |
| ThrowSupplyPackAction | NearestLowAmmoAlly | HasSupplyPack=1 | HasSupplyPack Decrease | 5 | 15 | MoveBeforePerforming | 投给弹药低的友军 |

#### 部署型

| Action | Target | Conditions | Effects | Cost | StopDist | MoveMode | 说明 |
|--------|--------|-----------|---------|------|---------|----------|------|
| DeploySupplyCrateAction | (self) | HasSupplyCrate=1, (AmmoLevel<30 OR HealthLevel<50) | HasSupplyCrate Decrease | 4 | 0 | - | 自部署补给箱 |
| DeployInterceptorAction | (self) | HasInterceptor=1 | HasInterceptor Decrease | 6 | 0 | - | 防御性部署 |
| DeploySensorAction | (self) | HasSensorEquip=1, SensorActive=0 | HasSensorEquip Decrease | 6 | 0 | - | 标记附近敌人 |

#### 自身瞬时型

| Action | Target | Conditions | Effects | Cost | StopDist | MoveMode | 说明 |
|--------|--------|-----------|---------|------|---------|----------|------|
| UseJammerAction | (self) | HasJammer=1, SelfIsMarked=1 | HasJammer Decrease, SelfMarkImmune Increase | 3 | 0 | - | 清除标记+免疫 |
| QuickDashAction | (DashDirection) | HasQuickDash=1, IsUnderFire=1 | HasQuickDash Decrease | 4 | 0 | - | 闪避冲刺脱离火力 |

#### 自身引导型

| Action | Target | Conditions | Effects | Cost | StopDist | MoveMode | 说明 |
|--------|--------|-----------|---------|------|---------|----------|------|
| ApplyArmorPlateAction | (self) | HasArmorPlate=1, ArmorLevel<10 | HasArmorPlate Decrease, ArmorLevel Increase | 5 | 0 | - | 引导1.5s加护甲 |
| UseHealingSyringeAction | (self) | HasHealingSyringe=1, HealthLevel<40 | HasHealingSyringe Decrease | 5 | 0 | - | 引导后持续回血 |
| UseDefibrillatorAction | NearestDownedAlly | HasDefibrillator=1, AllyDowned=1, EnemyInSight=0 | HasDefibrillator Decrease | 3 | 2 | MoveBeforePerforming | 仅安全时复活 |

#### 移动型

| Action | Target | Conditions | Effects | Cost | StopDist | MoveMode | 说明 |
|--------|--------|-----------|---------|------|---------|----------|------|
| MoveToCapturePointAction | NearestCapturePoint | - | AtCapturePoint Increase | 3 | 3 | MoveBeforePerforming | 移到争夺中点位 |
| MoveToDefendPointAction | NearestOwnedPoint | - | AtCapturePoint Increase | 3 | 3 | MoveBeforePerforming | 移到己方点位防守 |
| RetreatToGarrisonAction | NearestGarrison | HealthLevel<25 | IsSafe Increase | 1 | 3 | MoveBeforePerforming | 撤退到GR |
| MoveToSupplyAction | NearestSupply | AmmoLevel<50 OR HealthLevel<50 | - | 4 | 3 | MoveBeforePerforming | 移到已部署补给箱 |
| FollowSquadAction | SquadLeaderPos | SquadLeaderAlive=1 | - | 7 | 5 | MoveBeforePerforming | 跟随小队长 |
| WanderAction | WanderPoint | - | - | 10 | 1 | MoveBeforePerforming | 小队长选点位，队员跟随 |

#### 禁用的 Action（PHASE9 不实现）

| Action | 装备 | 理由 |
|--------|------|------|
| ~~DeploySignalChargeAction~~ | SignalCharge | 多步规划复杂，PHASE9 跳过 |
| ~~DeployWiredChargeAction~~ | WiredCharge | 同上 |
| ~~DeployDelayedBombAction~~ | DelayedBomb | 同上 |
| ~~ToggleBlastShieldAction~~ | BlastShield | 用户指定：AI 不使用防爆盾 |

### 5.2 Action 与现有系统对接

```csharp
// AttackEnemyAction.Perform
gun.Tick(fire=true, aim=adsDesired, eyePos, aimForward, false);
// aimForward 已包含压枪补偿（见 §5.5 后座力/散布处理）。
// 进攻姿态：站立。防御姿态（DefendGoal 激活时）：蹲下或趴下，根据掩体高度。

// ReloadAction.Start
gun.RequestReload();  // 新增的公开方法
// Perform 等待 gun.reloading == false → Completed

// ThrowGrenadeAction.Perform
equipment.Use(throwableIndex, false, eyePos, throwDir, Vector3.zero, true);

// FireGrenadeLauncherAction.Perform
equipment.Use(optIndex, false, eyePos, transform.forward, Vector3.zero, true);
// 直线瞄准，用 transform.forward

// DeploySensorAction.Perform
equipment.Use(specialIndex, false, eyePos, transform.forward, Vector3.zero, true);

// UseJammerAction.Perform
equipment.Use(optIndex, false, eyePos, transform.forward, Vector3.zero, true);

// UseDefibrillatorAction.Perform — 仅在 EnemyInSight=0 时可执行
equipment.Use(specialIndex, false, eyePos, transform.forward, Vector3.zero, true);

// QuickDashAction.Perform — 朝远离火力源方向冲刺
Vector3 dashDir = (transform.position - lastDamageSource).normalized;
equipment.Use(optIndex, false, eyePos, dashDir, new Vector3(0,1,0), true);
```

### 5.3 姿态系统

Action 执行时根据当前 Goal 决定姿态：

| Goal 上下文 | 姿态 | 射击高度 | 说明 |
|------------|------|---------|------|
| EliminateEnemyGoal（进攻） | 站立 | 1.8m | 进攻时站立移动射击 |
| DefendObjectiveGoal（防守） | 蹲下 | 0.9m | 防守时蹲下提高精度 |
| SurviveGoal + IsUnderFire | 趴下 | 0.5m | 被压制时趴下减小受弹面积 |
| DefendObjectiveGoal + 掩体 | 按掩体高度 | 匹配掩体 | 蹲下/趴下到刚好能射击掩体上方 |

```csharp
// 在 AttackEnemyAction 或 AgentNavMeshMove 中
void SetPostureForContext(GoalType currentGoal, CoverInfo cover)
{
    if (currentGoal == GoalType.Defend)
    {
        if (cover.hasCover)
            SetPosture(cover.height < 0.7f ? Posture.Prone : Posture.Crouch);
        else
            SetPosture(Posture.Crouch);
    }
    else // Attack / Patrol / etc
    {
        SetPosture(Posture.Stand);
    }
}
```

### 5.4 动态启用逻辑

`GoapAgentInitializer` 在 AI spawn 时遍历 4 个配装槽位：

```csharp
void EnableActionForEquip(int slotIndex)
{
    if (slotIndex < 0) return;
    var def = equipment.equipmentList[slotIndex];
    switch (def.type)
    {
        case EquipmentType.Grenade:          provider.Enable<ThrowGrenadeAction>(); break;
        case EquipmentType.SmokeGrenade:     provider.Enable<ThrowSmokeAction>(); break;
        case EquipmentType.EmpGrenade:       provider.Enable<ThrowEmpGrenadeAction>(); break;
        case EquipmentType.GrenadeLauncher:  provider.Enable<FireGrenadeLauncherAction>(); break;
        case EquipmentType.SmokeLauncher:    provider.Enable<FireSmokeLauncherAction>(); break;
        case EquipmentType.Rpg:             provider.Enable<FireRpgAction>(); break;
        // SignalCharge/WiredCharge/DelayedBomb 不启用
        case EquipmentType.SmallSupplyPack:  provider.Enable<ThrowSupplyPackAction>(); break;
        case EquipmentType.LargeSupplyCrate: provider.Enable<DeploySupplyCrateAction>(); break;
        case EquipmentType.Interceptor:      provider.Enable<DeployInterceptorAction>(); break;
        case EquipmentType.Sensor:           provider.Enable<DeploySensorAction>(); break;
        case EquipmentType.QuickDash:        provider.Enable<QuickDashAction>(); break;
        case EquipmentType.Jammer:           provider.Enable<UseJammerAction>(); break;
        case EquipmentType.ArmorPlate:       provider.Enable<ApplyArmorPlateAction>(); break;
        // BlastShield 不启用（用户指定 AI 不使用防爆盾）
        case EquipmentType.HealingSyringe:   provider.Enable<UseHealingSyringeAction>(); break;
        case EquipmentType.Defibrillator:    provider.Enable<UseDefibrillatorAction>(); break;
    }
}
```

### 5.5 射击规则系统（AIShootingProfile）— 已确认

AI 射击规则由主武器散布参数驱动，**AI 部署时自动初始化**：

| 决策 | 选择 |
|------|------|
| 后座力 | AI 免后座力（recoil 不累积），仅受散布影响 |
| 开镜 | 按有效射击距离自动切换（腰射/瞄准） |
| 射击节奏 | 始终全自动模式，点射通过"打几发停一下"实现 |
| 精度 | 可配置难度乘数（默认 1.0） |

#### 有效射击距离公式

```
有效射击距离 = 10 / 散布平均值(度)
```

以默认主武器为例（用户举例）：
- 腰射散布平均值 2° → 腰射有效距离 = 10 / 2° = **5m**
- 瞄准散布平均值 0.5° → 瞄准有效距离 = 10 / 0.5° = **20m**

> 散布平均值从 `WeaponDefinition` 读取：腰射 = (hipSpreadMin + hipSpreadMax)/2，瞄准 = (adsSpreadMin + adsSpreadMax)/2。散布值再乘以难度乘数。

#### 射击规则（按距离分三段）

```
距离 < 腰射有效距离 (5m)          → 腰射（aim=false，全自动连射）
腰射有效距离 ≤ 距离 < 瞄准有效距离  → 瞄准全自动（aim=true，连射）
距离 ≥ 瞄准有效距离 (20m)         → 瞄准点射（aim=true，打几发停一下让散布恢复）
```

#### 实现

```csharp
// NetworkGun 新增字段
public bool applyRecoil = true;   // AI 设为 false（免后座力）
public float spreadMultiplier = 1f; // 难度乘数（AI 可配置）

// TryShoot 中
bloom = Mathf.Min(currentMaxSpread, bloom + definition.spreadPerShot * spreadMultiplier);
if (applyRecoil) recoil += definition.screenRecoilPerShot;

// AIShootingProfile（部署时初始化）
public class AIShootingProfile
{
    public float hipSpreadAvg;       // (hipSpreadMin + hipSpreadMax) / 2
    public float adsSpreadAvg;       // (adsSpreadMin + adsSpreadMax) / 2
    public float hipEffectiveRange;  // 10 / hipSpreadAvg
    public float adsEffectiveRange;  // 10 / adsSpreadAvg

    // AttackEnemyAction 每帧根据目标距离决定 aim 和 fire 节奏
    public (bool aim, bool fire) DecideShooting(float distToTarget)
    {
        if (distToTarget < hipEffectiveRange)
            return (aim: false, fire: true);        // 腰射全自动
        if (distToTarget < adsEffectiveRange)
            return (aim: true, fire: true);          // 瞄准全自动
        return (aim: true, fire: ShouldBurst());     // 瞄准点射
    }

    // 点射：打 N 发停 M 秒（让散布恢复），全自动模式靠 fire 间歇实现。
    private bool ShouldBurst() { ... }
}
```

- AI 始终用 `FireMode.Auto`，点射通过 `fire` 的间歇（打 3 发停 0.5s）实现，无需切换射击模式
- 难度乘数作用于散布：`spreadMultiplier < 1` 更准（精英），`> 1` 更散（新兵）
- 后座力免除以 `applyRecoil=false` 实现，散布仍正常累积/恢复

---

## 6. 标记系统对 AI 的影响

### 标记系统现状

| 来源 | 机制 | 标记时长 |
|------|------|---------|
| 玩家 Q 键 | `TryMark` 射线命中敌方 → `SetMarked` | 10s |
| 感应器 (Sensor) | 每 5s 标记 20m 内敌方 → `SetMarked` | 3s |
| 干扰器 (Jammer) | `ClearMark()` + 30s `markImmune` | - |
| EMP | 终止 Jammer 免疫 + 禁用 Sensor | - |

### AI 与标记系统的交互

1. **AI 不直接标记敌人**（不模拟 Q 键）
2. **AI 部署感应器间接标记** → DeploySensorAction 的效果
3. **AI 自身被标记时**：
   - 有干扰器 → GoalSelector 硬规则触发 SurviveGoal → UseJammerAction
   - 无干扰器 → GoalSelector 提升 SurviveGoal → 撤退到掩体/GR
4. **敌方被标记时**：
   - NearestEnemySensor 优先返回被标记的敌人（`GetNearestMarkedEnemy()`）
   - 被 Sensor 标记的敌人对小队全员可见（SyncVar）
5. **感应器管理**：
   - SensorActiveSensor 检测场上是否有己方活着的感应器
   - SensorActive=1 时 DeploySensorAction 条件不满足，避免重复部署
   - EMP 可摧毁感应器 → SensorActive 归 0 → 可重新部署

---

## 7. 掩体系统 — CoverSystem

### 原理

使用 `NavMesh.Raycast` 从 AI 位置朝远离敌人方向射线，找到最近障碍物边缘作为掩体点。

```csharp
public struct CoverInfo
{
    public bool hasCover;
    public Vector3 coverPosition;    // 掩体后方位置
    public float height;             // 掩体高度估算（Raycast 垂直探测）
}

public class CoverSystem
{
    /// <summary>朝远离敌人方向找最近掩体。</summary>
    public CoverInfo FindCover(Vector3 aiPos, Vector3 enemyPos, float maxDist = 10f)
    {
        Vector3 awayFromEnemy = (aiPos - enemyPos).normalized;
        awayFromEnemy.y = 0;

        // NavMesh 射线找障碍边缘
        if (NavMesh.Raycast(aiPos, aiPos + awayFromEnemy * maxDist, out NavMeshHit hit, NavMesh.AllAreas))
        {
            // hit.position 是障碍边缘，后退一点作为掩体位置
            Vector3 coverPos = hit.position - awayFromEnemy * 0.5f;
            float height = EstimateHeight(coverPos);
            return new CoverInfo { hasCover = true, coverPosition = coverPos, height = height };
        }
        return new CoverInfo { hasCover = false };
    }

    private float EstimateHeight(Vector3 pos)
    {
        // 向上射线探测障碍物高度
        if (Physics.Raycast(pos + Vector3.up * 0.1f, Vector3.up, out RaycastHit hit, 2f))
            return hit.point.y - pos.y;
        return 2f; // 默认全高
    }
}
```

### 使用场景

- **IsUnderFire + SurviveGoal** → GoalSelector 设定 SurviveGoal，GOAP 选择撤退到掩体
- **DefendObjectiveGoal** → 到达点位后找掩体，按掩体高度决定蹲下/趴下
- **AttackEnemyAction** → 进攻时不找掩体（站立射击）

---

## 8. AIDataProvider — 统一数据缓存

```csharp
public class AIDataProvider : NetworkBehaviour
{
    // 每 0.5s 更新的缓存
    private List<NetworkCombatant> enemies;
    private List<NetworkCombatant> friendlies;
    private NetworkCombatant nearestEnemy;
    private NetworkCombatant nearestMarkedEnemy;
    private NetworkCombatant squadLeader;          // 小队中实例 ID 最小的 AI
    private NetworkCombatant nearestDownedAlly;
    private NetworkCombatant nearestLowAmmoAlly;
    private LargeSupplyCrate nearestSupply;
    private CapturePoint nearestUncapturedPoint;
    private CapturePoint nearestOwnedPoint;
    private GarrisonZone nearestGarrison;
    private SensorProbe nearestFriendlySensor;
    private bool hasFriendlySensorActive;
    private float lastDamageTime;
    private Vector3 lastDamageSource;

    // 补给广播（找不到补给箱时）
    public bool needsSupply;                        // 广播补给请求
    public NetworkCombatant nearestAllyWithSupply;  // 最近有补给能力的友军

    public void RefreshCache() { ... }              // 0.5s 定时
    public NetworkCombatant GetNearestEnemy() => nearestEnemy;
    public NetworkCombatant GetNearestMarkedEnemy() => nearestMarkedEnemy;
    public NetworkCombatant GetSquadLeader() => squadLeader;
    // ...
}
```

### 小队长定义

```csharp
NetworkCombatant FindSquadLeader()
{
    // 同小队中实例 ID 最小的 AI（先 spawn 的）
    int mySquad = combatant.squadId;
    int myTeam = combatant.teamId;
    NetworkCombatant leader = null;
    int minId = int.MaxValue;

    foreach (var c in allCombatants)
    {
        if (c.teamId != myTeam || c.squadId != mySquad) continue;
        if (c.IsDead) continue;
        if (c.GetInstanceID() < minId)
        {
            minId = c.GetInstanceID();
            leader = c;
        }
    }
    return leader;
}
```

### 补给广播机制

当 AI 弹药/血量不足且找不到已部署补给箱时：
1. `AIDataProvider.needsSupply = true`
2. 搜索附近友军中是否有携带 LargeSupplyCrate 的
3. 如果有 → MoveToSupplyAction 目标改为该友军位置
4. 如果无 → 靠近最近的友军（FollowSquadAction 降权变体）
5. 未来 LLM 指挥官可读取 `needsSupply` 状态进行调度

---

## 9. 移动适配 — AgentNavMeshMove

```csharp
public class AgentNavMeshMove : MonoBehaviour
{
    private AgentBehaviour agent;
    private NavMeshAgent navAgent;
    private ITarget currentTarget;
    private bool shouldMove;

    void OnEnable() {
        agent.Events.OnTargetInRange += OnInRange;
        agent.Events.OnTargetChanged += OnTargetChanged;
        agent.Events.OnTargetNotInRange += OnNotInRange;
        agent.Events.OnTargetLost += OnTargetLost;
    }

    void Update() {
        if (!shouldMove || currentTarget == null) return;
        navAgent.SetDestination(currentTarget.Position);
    }
}
```

速度由当前 Goal 上下文决定：进攻=runSpeed，防守/巡逻=walkSpeed。

---

## 10. SquadOrderReceiver — 未来 LLM 指挥官接口

```csharp
public enum SquadOrderType { None, Capture, Defend, Attack, Regroup, Hold }

public class SquadOrderReceiver : NetworkBehaviour
{
    [SyncVar] public int orderType = (int)SquadOrderType.None;
    [SyncVar] public Vector3 orderTarget;
    [SyncVar] public int orderPointId = -1;

    public SquadOrder Current => ...;

    [Server]
    public void SetOrder(SquadOrder order) { ... }
}
```

GoalSelector 读取此组件：有指令时提升对应 Goal 权重 ×2。

---

## 11. 性能架构 — 60 AI 多线程分批

### GOAP 多线程

CrashKonijn GOAP v3.1 支持 Controller 配置。使用 `GoapBehaviour` 的多线程 Controller：

- GraphBuilder 只在 AgentType 创建时执行一次（所有 AI 共享同一 AgentType → 只构建一次）
- Resolver 在 Controller 中配置为多线程
- Sensor 更新由 Controller 调度

### 分批更新

```csharp
// GoapBehaviour 配置
agentBehaviour.RunInUnityUpdate = false;

// GoalSelector 分批：60 AI 分 6 批，每批 10 AI，每 0.33s 处理一批
// → 每个 AI 的 Goal 评估间隔 = 2s，但每帧只处理 10 个 AI
```

### AIDataProvider 刷新策略

- 全场 combatant 列表：1s 刷新一次（所有 AI 共享同一份缓存）
- 每个 AI 的 nearest 敌人/友军：0.5s 刷新
- Sensor 读取缓存数据，不做 `FindObjectsOfType`

---

## 12. NetworkGun 修改

新增 `RequestReload()` 公开方法 + AI 射击控制字段：

```csharp
// NetworkGun.cs 新增
public bool applyRecoil = true;    // AI 设为 false（免后座力）
public float spreadMultiplier = 1f; // 难度乘数（AI 可配置，<1 更准）

[Server]
public void RequestReload()
{
    if (reloadState != ReloadState.Idle) return;
    if (magAmmo >= definition.magazineCapacity) return;
    if (reserveAmmo <= 0) return;

    reloadState = ReloadState.Reloading;
    reloadRemaining = magAmmo > 0 ? definition.reloadFastTime : definition.reloadSlowTime;
    reloadDuration = reloadRemaining;
    reloading = true;
}

// TryShoot 修改（散布乘数 + 后座力开关）
bloom = Mathf.Min(currentMaxSpread, bloom + definition.spreadPerShot * spreadMultiplier);
if (applyRecoil) recoil += definition.screenRecoilPerShot;
```

AI 的 ReloadAction 调用 `RequestReload()` 并等待 `reloading == false`。

---

## 13. 与 NetworkAIController 的关系

### 保留

- `SetDead` / `OnRedeploy` / `SetProne` — 死亡/重部署/姿态管理
- `ApplyTeamColor` / `RpcApplyTeamColor` — 染色
- NavMeshAgent 初始化（walkSpeed/runSpeed）
- `FaceTarget` — AttackEnemyAction 复用

### 删除

- `AIState` 枚举 + `Wander/Seek/Attack` 状态机
- `Update()` 中的 FSM 逻辑
- `RefreshTarget` / `NearestEnemy` → 移到 `AIDataProvider`
- `PickRandomHq` → 移到 `GoalSelector` / `WanderPointSensor`

### 新增姿态控制方法

```csharp
// NetworkAIController 新增
public enum Posture { Stand, Crouch, Prone }

public void SetAIPosture(Posture p)
{
    SetProne(p == Posture.Prone);
    // 蹲下：缩小 capsule 高度但不旋转（复用现有 crouch 逻辑）
    // 站立：恢复正常
}
```

---

## 14. 实施步骤

### WP1: GOAP 基础设施 + NetworkGun 修改 + 视野系统
- 创建 `AI/` 文件夹结构
- 创建 Generator + Capability + AgentTypeConfig SO 资产
- 创建 `Keys/WorldKeys.cs` + `Keys/TargetKeys.cs`（所有 Key 类）
- 场景中添加 `GoapBehaviour` 全局控制器
- **修改 NetworkGun.cs**：新增 `RequestReload()` 公开方法
- **修改 SmokeThrowable.cs + 新增 ServerSmokeRegistry.cs**：服务器端烟雾注册
- 编写 `AgentNavMeshMove.cs`
- 编写 `GoapAgentInitializer.cs`（动态启用 Action 逻辑）
- 编写 `CoverSystem.cs`、`VisionSystem.cs`（160°/50m/烟雾遮挡）、`IntelBroadcast.cs`
- **验证**: 编译通过，场景有 GoapBehaviour

### WP2: 数据层 — AIDataProvider + 全部 Sensors
- 编写 `AIDataProvider.cs`（统一缓存 + 小队长查找 + 补给广播 + 已知敌人）
- 编写所有 LocalWorldSensor（含 EquipmentSensors MultiSensor + HasKnownEnemySensor）
- 编写所有 LocalTargetSensor（含 NearestMarkedEnemySensor + NearestEnemyDeployableSensor）
- 编写所有 GlobalWorldSensor
- 在 Capability SO 中注册所有 Sensor + Key
- **验证**: 编译通过，Capability Inspector 显示所有 Sensor

### WP3: 行动层 — 全部 Actions（20 个）
- 编写 6 个攻击型 Action
- 编写 3 个战术辅助型 Action
- 编写 3 个部署型 Action
- 编写 2 个自身瞬时型 Action
- 编写 3 个自身引导型 Action
- 编写 6 个移动型 Action
- 在 Capability SO 中注册所有 Action
- 编写动态启用/禁用逻辑
- **验证**: 编译通过，GOAP Graph Builder 成功（Console 无错误）

### WP4: 目标层 — Goals + GoalSelector + 姿态系统
- 编写 7 个 Goal 类
- 编写 `GoalSelector.cs`（混合选择算法 + SquadOrder 覆盖）
- 在 Capability SO 中注册所有 Goal
- 集成姿态控制（进攻站立/防守蹲下/被压制趴下）
- **验证**: 编译通过，AI 有合理行为

### WP5: 集成 — NetworkAIController 重构 + Prefab 更新
- 删除 NetworkAIController 的 FSM 代码
- 新增 `SetAIPosture` 方法
- `NetworkSetup.cs` 更新 BuildAIEntityPrefab（添加 7 个新组件）
- 更新 AIEntity prefab
- 配置 GoapBehaviour 多线程 + 分批
- **验证**: Host + Client 双端测试

### WP6: 小队指令接口
- 编写 `SquadOrderReceiver.cs`
- GoalSelector 集成 SquadOrder 覆盖逻辑
- 小队长巡逻逻辑（选未占领/争夺中点位，队员跟随）
- **验证**: 设置 Order 后 AI 行为改变

### WP7: 性能调优 + 60 AI 测试
- 配置 GOAP Controller 多线程
- 配置分批更新（6 批 × 10 AI）
- 调整 Action Cost / Goal 评分权重
- 调整 Sensor 刷新频率
- 60 AI 性能测试
- **验证**: 60 AI 帧率 >= 30fps

---

## 15. 注意事项

- GOAP 运行在**服务器端**，客户端的 AgentBehaviour/GoapActionProvider 应禁用
- NavMeshAgent 在客户端已禁用（`OnStartClient`），AgentNavMeshMove 仅服务器运行
- `GoapBehaviour` 全局控制器通过 `NetworkSetup.cs` 自动创建
- Generator SO 的命名空间设为 `HagenDa.Networking.AI`
- 所有 GOAP 类文件放在 `Assets/Scripts/Network/AI/` 下（Generator scope 内）
- GOAP 用 `[GoapId]` GUID 标识类，不靠枚举整数 → 无 PHASE8 的序列化问题
- SignalCharge/WiredCharge/DelayedBomb 三种爆破物在 PHASE9 完全禁用（不设 WorldKey/Action），但 EquipmentDefinition 中保留其枚举值不变
- BlastShield 在 PHASE9 禁用（用户指定 AI 不使用防爆盾），EquipmentDefinition 中保留枚举值不变
- 烟雾目前是纯客户端视觉，PHASE9 新增 ServerSmokeRegistry 记录服务器端烟雾（AI 视野遮挡用），SmokeThrowable.OnImpact 需服务器端注册
- AI 视野：前方 160°（半角 80°）+ 50m 感知距离 + 几何/烟雾遮挡 + 攻击距离无限（靠视野/标记/广播）
- 60 AI 测试需要足够大的地图和 NavMesh

---

## 16. 待讨论问题（全部已确认 ✅）

| # | 问题 | 结论 |
|---|------|------|
| 1 | 烟雾遮挡阈值 | 浓度 > 0.3 才遮挡，稀薄烟雾可穿透 |
| 2 | 情报广播范围/时效 | 半径 100m，情报失效 5s |

> 所有设计决策已确认，spec 定稿。
