# HagenDa ML 训练现状报告（2026-08-24）

## 一、项目进度总览

| 里程碑 | 状态 | 备注 |
|---|---|---|
| M0a 接口审计与补齐 | ✅ 完成 | Jammer/广播/射线/要地/桥接/奖励总线 |
| M0b 握手冒烟 | ✅ 完成 | Trainer↔Tuanjie 连通 |
| M1 训练基建 | ✅ 完成 | 地图/阵容轮换+回合驱动+脚本陪练+prefab桥接 |
| M2 S1 训练（v1） | ❌ 失败 | 50 万步，0 命中率，模型不动 |
| M2 S1 训练（v2） | ❌ 停止 | 70 万步，Value→0 + entropy 居高不下 |

**训练环境**：Tuanjie 2022.3.62t12 / ML-Agents 2.3.0-exp.3 (Release 20) / py3.9 venv / torch 2.8+cu126（RTX 4060）/ mlagents 0.30.0

**已跑训练**：
- s1（50 万步，单区域）→ 评估 0% 命中率
- s1-multi（50 万步，20 区域）→ 未评估（直接升级 v2）
- s1-v2（70 万步，100 区域 × 3 靶 + 课程奖励）→ Value Estimate 降至 0，entropy 居高不下

## 二、奖励链路静态检查结果

### 🔴 缺陷 #1（致命）：`RewardBus.Reset()` 在回合开始时清空了桥接注册表

**位置**：`TrainingSessionManager.StartRound()` L199
```csharp
TeamIntel.Reset();
RewardBus.Reset();     // ← 清空 bridges 字典！
```

**链路**：
1. `MLAgentBridge.OnEnable()` → `RewardBus.Register(combatant, this)`（Play 时注册）
2. `TrainingSessionManager.OnStartServer()` → `StartRound()` → `RewardBus.Reset()` → **bridges.Clear()**
3. 此后所有 `RewardBus.Damage/Kill/MatchEnd` 调用 `Award()` 时 `bridges` 为空 → **全部奖励丢失**

**影响**：训练从第一回合起就没有收到任何伤害/击杀/团队奖励。**这是 Value Estimate 归零的直接原因。**

**修复方案**：`RewardBus.Reset()` 只清 `damageLog`，不清 `bridges`（或在 Reset 后重新注册所有 bridge）。

### 🔴 缺陷 #2（致命）：课程奖励在 `CollectObservations` 中调用 `AddReward`

**位置**：`MLAgentBridge.CollectObservations()` L158
```csharp
if (!dead)
    ApplyCurriculumReward();   // ← 在观测收集阶段调用 AddReward
```

**问题**：ML-Agents 中 `CollectObservations` 阶段加的 reward 会写入上一个 step 的 trajectory 而非当前 step（reward 与 action 的因果配对错位）。更严重的是，当 Academy 未连接 trainer（Heuristic 模式）时 `CollectObservations` 的调用时序可能不同，导致行为不一致。

**修复方案**：将 `ApplyCurriculumReward()` 移到 `OnActionReceived()` 末尾。

### 🟡 缺陷 #3（严重）：`TrainingSessionManager.EndRoundByTimeout()` 平局时 `EndRound(false)`

**位置**：L253
```csharp
if (winner < 0)
{
    foreach (var ai in mlTeam)
    {
        var bridge = ai.GetComponent<MLAgentBridge>();
        if (bridge != null) bridge.EndRound(false);   // ← 平局也 -0.5
    }
}
```

**问题**：S1 场景 300 靶 vs 100 ML，靶不反击 → ML 几乎不可能全灭 → 全部回合走超时 → ML 全部 -0.5。同时 `RewardBus.MatchEnd` 未被调用（winner=-1 不进 EndRound(int)），但 `EndRound(false)` 给了 LossPenalty。每 60s 回合 ML 全员 -0.5，且没有任何正奖励来源（缺陷 #1），**critic 学到"一切皆负"→ Value Estimate 持续下降**。

**修复方案**：S1 阶段平局不给终局奖惩（或给 0）。

### 🟡 缺陷 #4（严重）：`EndRound(bool won)` 双重给奖

**位置**：`TrainingSessionManager.EndRound(int winnerTeam)` L220
```csharp
RewardBus.MatchEnd(winnerTeam);          // ← 给 WinReward/LossPenalty
foreach (var ai in mlTeam)
{
    var bridge = ai.GetComponent<MLAgentBridge>();
    if (bridge != null)
        bridge.EndRound(...);             // ← 又给一次 WinReward/LossPenalty
}
```

**问题**：`RewardBus.MatchEnd` 已经通过 `AddReward` 给了终局奖励，`bridge.EndRound` 又给一次 → **双倍终局奖励**。

**修复方案**：删除其中一个。

### 🟢 确认正常的链路

| 链路 | 状态 |
|---|---|
| 子弹命中 → `NetworkBullet.Update` → `TakeBulletDamage` → `TakeDamageInternal` → `RewardBus.Damage` | ✅ 代码正确（attacker 正确传递） |
| 击杀 → `DieInternal` → `RewardBus.Kill` | ✅ 代码正确 |
| `NetworkGun.TryShoot` → `S1EvaluationStats.RecordShot` | ✅ 正常 |
| `MLAgentBridge.OnEnable` → `RewardBus.Register` | ✅ 正常（Play 时注册） |
| 动作映射 → `SetIntent` → `NetworkAIController.FixedUpdate` → `SetServerInput` → `SimulateServer` | ✅ 正常（已实测驱动移动+射击） |
| 观测收集（694 维） | ✅ 正常（已验证 trainer 接受） |

## 三、其他发现

### 训练加速工作正常
- 100 区域 × 3 靶 → ~660 steps/s（vs 单区域 67 steps/s，**10 倍加速**）
- 20 区域 → ~770 steps/s
- onnx 检查点导出正常（已修复 onnx 模块缺失）

### TensorBoard 运行中
- http://127.0.0.1:6006（logdir=results）
- 可看 s1 / s1-multi / s1-v2 三个 run 的曲线对比

### 评估场景残留
- `S1Eval.scene` 从 20 区域版裁剪而来，仅 Area_0 有效
- 评估时需运行时重设 ONNX 模型（场景序列化的模型在 Play 后未生效，原因未查明——可能因 `ConfigureBehaviorParameters` 在 Awake 时覆盖了 Inspector 序列化值）

### 环境注意事项
- 训练 YAML 必须纯 ASCII（中文 Windows GBK locale）+ `PYTHONUTF8=1`
- Unity 许可弹窗会冻结主线程导致握手超时
- `--timeout` 不是 0.30 的 CLI 参数
- 多区域场景中 NavMesh 警告（靶不在 NavMesh 上）无害——靶 targetMode 不移动
- venv 依赖锁定：mlagents 0.30.0 / protobuf 3.20.3 / tensorboard 2.12.2 / onnx / setuptools<81

## 四、修复优先级

1. **缺陷 #1**（bridges 被 Reset 清空）→ 伤害/击杀奖励恢复
2. **缺陷 #3**（S1 平局 -0.5）→ 消除持续负信号
3. **缺陷 #2**（课程奖励时机）→ reward-action 因果配对正确
4. **缺陷 #4**（终局双倍奖励）→ 量级减半

修复后建议从零重训（当前 checkpoint 的 critic 已被"全负"信号污染）。

## 五、文件清单（ML 相关）

| 文件 | 职责 |
|---|---|
| `MLAgentBridge.cs` | Agent 桥接：观测 694 维 + 混合动作 + 课程奖励 |
| `AgentRaySensor.cs` | 射线感知（前向 2×16@120°/60m + 环形 2×8@360°/12m） |
| `RewardBus.cs` | 奖励事件总线（常数+注册表+分发） |
| `TeamIntel.cs` | 团队广播（6 类事件，10s 窗口） |
| `StrategicZone.cs` | 战略要地抽象层 |
| `TrainingMap.cs` | 地图数据 SO（Square/Wave，种子化布局） |
| `TrainingArena.cs` | 运行时几何重建 + CoverRegistry |
| `TrainingSessionManager.cs` | 回合生命周期 + 地图/阵容轮换 + 重生 |
| `ScriptedAIController.cs` | 脚本陪练（含 targetMode） |
| `S1EvaluationStats.cs` | S1 评估统计器 |
| `Editor/NetworkSetup.cs` | 场景/prefab 生成菜单 |
| `Training/config/HagenDaSquad_S1.yaml` | 当前训练配置 |
