# HagenDa ML 训练计划（共识稿 v1.0）

> 2026-08-23 经 29 项决策点逐轮确认。ML-branch 主目标：ML-Agents 训练自主战斗 + 小队协作 AI。
> 前置改造已完成：AI 身体 = 玩家同款 `NetworkPlayerController`（力驱动 3C），无内置行为，
> 由 `NetworkAIController.SetIntent(NetworkInputState)` 服务端注入意图（与真人 `CmdInput` 完全同路径）。

## 0. 目标
用 ML-Agents（PPO + LSTM，纯向量观测，无视觉）训练**共享策略** AI：自主战斗 + 小队协作。

## 1. 成功标准（分阶段门槛）
| 阶段 | 门槛 |
|---|---|
| S1 1v1 vs 靶 | 命中率 ≥20%，10/10 回合完成击杀 |
| S2 3v3 vs 脚本队 | bo100 胜率 ≥70%，三兵种指标非零 |
| S3 5v5 自博弈 | 对 S2 冠军 checkpoint 胜率 ≥65% |
| S4 整局 | 对脚本队胜率 ≥70%（终局验收） |

## 2. 三兵种（固定配装，共享策略 + 兵种 one-hot）
| 兵种 | 配装（全部现有资产） | 专属指标 |
|---|---|---|
| 突击 ×2 | 榴弹炮+快速机动装置+治疗针+手雷 | 击杀、伤害输出、机动后生存率 |
| 支援 ×2 | 大补给箱+拦截装置+除颤仪+烟雾弹 | 补给送达、除颤救援、拦截挡弹、烟雾掩护 |
| 侦察 ×1 | 干扰器(自 GOAP-backup 移植)+部署信标+感应器+电磁手雷 | 标记引导击杀、信标重生价值 |
3 人队 = 1+1+1。禁用：遥控炸药、射击模式切换（人类专用）。
干扰器 = EquipmentType.Jammer（瞬发，清除自身标记 + 30s 免疫标记，SelfInstant）。

## 3. 感知（观测向量，全相对量+归一化）
- 自身 ~40 维：血/甲/姿态/滑铲/飞扑/落地/冲刺/本地速度/朝向 sin-cos/activeSlot/弹匣/备弹/换弹中/4 件装备(余量/冷却)/兵种 one-hot/近 2s 受击
- 射线：前向 2×16@120°/60m + 环形 2×8@360°/12m，脚+头双高度，每根 [距离归一化, 命中类型 9 类独热：无/墙/地/敌/友/信标/补给箱/感应器/拦截装置]
- 小队频道（全知穿墙，同 HeadMarker 语义）：4 槽 ×(存活/兵种 one-hot/相对位置/血量/姿态/速度)
- 标记记忆：4 条 ×(相对位置/年龄/是否有效)——敌方标记 10s 不穿墙（复用现有系统）
- 广播：8 条 ×(6 类独热/相对方位距离/年龄衰减)，6 类=[受击方向/目击敌人/阵亡求救/缺弹药/请求治疗/信标已部署]，全自动规则触发
- 全局：双方比分、**战略要地抽象层**（位置+归属+争夺度）、剩余时间
- 不给：队友当前目标（让协作涌现）、绝对坐标（多地图迁移）

## 4. 动作（6Hz = 每 10 个 Academy step，动作保持，角速度视角）
- 连续 6：moveX / moveY / yaw 角速度 / pitch 角速度 / fire 保持 / aim 保持
- 离散 6 分支：姿态(4: 无/站/蹲/趴) / 跳(2) / 冲刺(2) / 换弹(2) / 标记(2) / 槽位(6: 保持/主武器/装备1/装备2/特有/投掷物)
- S4 追加：部署选择(5: 保持/GR/HQ/小队/信标)
- 边沿动作每决策步至多一次；遥投掷物瞬发（无蓄力）

## 5. 奖励（无占点项；S1 只开前三行）
| 事件 | 奖励 |
|---|---|
| 伤害输出 | +0.002/HP（满血击杀累计 ≈ +0.2） |
| 击杀 | +0.2（小队助攻者各 +0.05） |
| 阵亡 | -0.1；受击 -0.001/HP |
| 支援效用 | 治疗/补给/除颤有效使用 +0.05/次，拦截挡弹 +0.03 |
| 侦察效用 | 标记引导击杀 +0.05，信标重生 +0.02/次 |
| 团队共享 | 团队击杀收益的 20% 平分全员 |
| 终局 | 胜 +1 / 负 -0.5（S2 起） |
全部"仅实效给分"（不在动作本身给分，防刷分）。

## 6. 训练器与网络
- **A 段（S1–S2）**：mlagents-learn；MLP 3×512 + LSTM 128 / seq 64；超参先试跑再定
- **B 段（S2 末起，S3 必用）**：自定义 PyTorch，结构定稿：

```
射线编码器:    前向32+环形16根 → MLP[96→256→128]
自身状态编码器:  ~40维 → MLP[→256→128]
小队频道编码器:  4队友×12维 → 每人MLP[12→64] → sum+max池化 → 128   (Deep Sets)
标记记忆编码器:  4条×6维 → 每条MLP[6→64] → 注意力池化 → 64
广播编码器:     8条×9维 → 每条MLP[9→64] → 注意力池化 → 64
全局编码器:     ~15维 → MLP[→128]
─────────────────────────────────────
拼接(640) → 主干 MLP[640→512→512] → LSTM 512(2层)
→ 连续头: 6维高斯(均值 tanh 缩放, 状态相关 log std)
→ 离散头: 6 分类(4/2/2/2/2/6)
```
- 记忆：死亡重部署清零（Q20-A）
- 观测/动作接口 A/B 两段完全一致，切换零 Unity 侧改动

## 7. 基建
- Python：py3.9 venv（mlagents/mlagents-envs 已按 Unity 文档安装）；M0b 验证与 Release 20 Unity 包（2.3.0-exp.3）握手
- 运行时：编辑器先行 → 独立构建 + `--num-envs` 并行 + timescale≈20
- 场地：小型专用场（双方 home + 中央单争夺点 + 高/矮/高位掩体 + 斜面，掩体间距宽松）；squadsPerTeam=1
- 脚本陪练：复活旧 NavMesh AI + 增强（受击找掩体/姿态应对）

## 8. 里程碑
| # | 内容 | 验收 |
|---|---|---|
| M0a 接口审计与补齐 | 广播系统(参考 GOAP-backup IntelBroadcast)、射线感知器、观测收集器、动作映射器、奖励事件钩子、Jammer 移植、StrategicZone 抽象层 | 接口清单 + 单元级人工验证 |
| M0b 握手冒烟 | venv ↔ mlagents-learn ↔ Tuanjie 编辑器最小闭环 | Trainer 收发决策 |
| M1 | 训练场地 + TrainingSessionManager + 增强脚本 AI + Agent 桥接入 prefab | S1 配置可跑 |
| M2 | S1 训练（A） | 20% 命中率 + 10/10 |
| M3 | S2 训练（A） | 70% + 兵种指标非零 |
| M4 | B 训练器开发 | 复现 S2 ±5% |
| M5 | S3 自博弈（B + 并行构建） | 65% vs 冠军 |
| M6 | S4 整局 + 部署动作 + 指挥官接口预留 | 70% vs 脚本 |

## 9. 风险与预案
- 6Hz 远距跟踪粗 → 每 agent 决策频率开关（升 10Hz 重训）
- Tuanjie headless 握手未验证 → M0b 前置冒烟
- 自博弈漂移 → 冻结 checkpoint 锚定对手
- A 段表达力不足 → B 段网络已定稿，接口不变热切

## 10. 环境事实备忘
- Unity 包：`file:D:/HagenTa/ml-agents-release_20`（com.unity.ml-agents 2.3.0-exp.3 + extensions 0.6.1-preview），本地含完整训练器源码/config
- mlagents-envs 0.30.0 要求 Python ≥3.8.13 ≤3.10.8；训练用 venv = py3.9（项目根 `venv/` 为 py3.12+mlagents0.28，勿用）
- 团结官方手册含 ML-Agents 页（编辑器 2022.3 对应），无专属 fork
- 现有小队/队伍：squadsPerTeam=2(训练设1)/squadSize=5；AI 按出生 z 分队，小队轮转分配

## 附录 A — M0a 接口清单（2026-08-23 实装并验证）

### 感知侧（AI 输入）
| 接口 | 文件 | 说明 |
|---|---|---|
| 射线感知 | `AgentRaySensor.cs` | 前向 2×16@120°/60m + 环形 2×8@360°/12m，脚 0.3m/头 1.5m；`Sample(pos,yaw,team)` 采样，`Write(float[])` 输出 480 维；`Latest[]` 供广播复用。9 类命中（含缓存字典分类） |
| 观测收集 | `MLAgentBridge.CollectObservations` | 694 维 = 自身38 + 射线480 + 小队4×12 + 标记4×6 + 广播8×10 + 全局24；全相对量+归一化 |
| 小队频道 | `MLAgentBridge.WriteSquadBlock` | 全知穿墙（同 HeadMarker 语义），经 `NetworkMatchManager.GetAllCombatants` 注册表 |
| 标记记忆 | `NetworkCombatant.markedByTeam/lastMarker` + `SetMarked(until,byTeam,marker)` | 观测只读本队标记的敌人（位置/剩余/兵种） |
| 团队广播 | `TeamIntel.cs` | 6 类事件环形缓冲（8 条/10s 窗口，按发射者+类型节流）；`Broadcast` / `GetRecent` / `Reset` |
| 战略要地 | `StrategicZone.cs` | `StrategicZoneRegistry`（场景级）+ `StrategicZone`（可包 CapturePoint 或纯要地）；`GetZones` 快照 ≤3 个 |

### 广播发射规则（全自动）
| 事件 | 触发点 | 位置语义 |
|---|---|---|
| Damaged | `NetworkPlayerHealth.TakeDamageInternal`（实际掉血>0） | 攻击者位置 |
| EnemySpotted | `MLAgentBridge.EmitRuleBroadcasts`（射线命中敌人，每决策步≤1 条） | 敌人位置 |
| DeathSOS | `NetworkPlayerHealth.DieInternal` | 阵亡位置 |
| LowAmmo | `MLAgentBridge`（弹匣+备弹 < 20%） | 自身位置 |
| NeedHeal | `MLAgentBridge`（血量 < 40%） | 自身位置 |
| BeaconDeployed | `NetworkEquipment.UseDeploy`（信标） | 信标位置 |

### 动作侧（AI 输出）
| 接口 | 说明 |
|---|---|
| `NetworkAIController.SetIntent(NetworkInputState)` | 唯一驱动入口；边沿标志推送**一个 tick 后自动清除**（等价人类按键一次） |
| `NetworkPlayerController.SetServerInput` | 与 `CmdInput` 完全同语义 |
| `MLAgentBridge.OnActionReceived` | 连续6 + 离散6分支 → NetworkInputState；姿态绝对目标→单 toggle 翻译（任意姿态单 toggle 互达）；视角角速度限幅 ±360/±180 °/s |
| 行为参数 | 代码装配：BehaviorName=`HagenDaSquad`，ActionSpec 混合，DecisionPeriod=10（60Hz→6Hz），TakeActionsBetweenDecisions=true |

### 奖励事件（RewardBus → MLAgentBridge.AddReward）
| 事件 | 钩子点 | 数值 |
|---|---|---|
| Damage | `TakeDamageInternal`（**实际损失 HP**，过量不计） | 攻 +0.002/HP，守 -0.001/HP |
| Kill | `DieInternal` | +0.2；助攻（击杀者同小队 5s 内伤害过）各 +0.05；团队共享 0.2×0.2/5 |
| MarkLedKill | 击杀时受害者被击杀方有效标记 | 标记者 +0.05 |
| Heal/Rescue | `CompleteChannel`（治疗针/除颤，除颤需实际救活同阵营） | +0.05 |
| Supply | `LargeSupplyCrate.Update`（首个受益 tick，每箱一次；**新增阵营过滤**） | +0.05 |
| InterceptorBlock | `NetworkInterceptor.Intercept` | +0.03 |
| BeaconRespawn | `DeployBeacon.ConsumeDeployPoint` | +0.02 |
| MatchEnd | 会话管理器（M1） | +1 / -0.5 |

### M0a 顺带修复/移植
- **Jammer** 移植自 GOAP-backup（type=Jammer 瞬发清标记+30s 免疫+30s 冷却，EMP 可禁；`Jammer.asset` 已生成，prefab 装备清单 20 项）
- 除颤仪增加**阵营过滤**（此前敌我不分都能救）
- 大型补给箱增加**阵营过滤**（此前敌我不分都能补给）
- 过量伤害按实际 HP 计奖（防鞭尸刷分，实测 9999 过量击杀 = +0.40 精确）

### M0a 验证记录（Play 模式实测）
射线 48/480 维正常（墙/友分类正确）；广播 3 类事件位置正确；奖励数值与公式一致（±0.001 精度）；Jammer 全流程（标记→使用→清除→免疫拦截）通过；要地注册/快照通过；ML-Agents Academy↔Agent 通信正常（无 trainer 时 Heuristic 占位零动作待命）。
**遗留（M1 处理）**：MLAgentBridge 尚未挂到 prefab（M1 桥接进 builder）；Heuristic 未实现（可加键鼠调试驱动）；`TrainingSessionManager` 未建（回合/终局/计时驱动）。

## 附录 B — M0b 握手冒烟记录（2026-08-23 通过）

**环境修复过程**（venv 曾半损坏：27 个包 dist-info 丢失 METADATA、torch 包体损坏）：
1. 清华镜像重装 26 个小包（元数据重建）
2. torch 2.8.0+cu126 ← **阿里云 pytorch-wheels/cu126 镜像**（官方节点过慢，`-f https://mirrors.aliyun.com/pytorch-wheels/cu126/`）；RTX 4060 Laptop CUDA 可用
3. mlagents 0.30.0 + mlagents-envs 0.30.0 ← 本地 Release 20 源码普通安装（老 setup.py 不支持 PEP 660 editable；先补 `wheel` 包）
4. 兼容性降级：`protobuf==3.20.3`（6.x 破坏 pb2）、`tensorboard==2.12.2`（2.21 要求新 protobuf）、`setuptools<81`（mlagents 0.30 用 pkg_resources）

**冒烟结论**：
- `mlagents-learn Training\config\HagenDaSquad.yaml --run-id=m0b-smoke`（需 `PYTHONUTF8=1`，中文 Windows 默认 GBK 解码 YAML 会失败——**训练配置一律纯 ASCII**）
- Trainer ↔ Tuanjie 编辑器（2022.3.62t12 + com.unity.ml-agents 2.3.0-exp.3）**握手成功**（Tuanjie 兼容性风险点解除）
- 19 × MLAgentBridge 运行时挂载，`HagenDaSquad` 行为（694 维观测 + 连续6/离散6 混合动作）被 trainer 接受，无 shape 错误
- 采样 ~780 experience steps/s（= 19 agents × 物理 tick，符合 DecisionPeriod=10 的动作重复语义）
- Unity 侧零错误零异常
- 注：`--timeout` 非 0.30 CLI 参数；Unity 许可证弹窗会冻结主线程导致握手超时（需人工处理）

**M0 全部完成。下一步 M1**：训练场地 + TrainingSessionManager（回合驱动/EndEpisode/M0 遗留三项）+ 增强版脚本 AI + 桥接进 prefab。

## 附录 C — M1 泛化补充需求（2026-08-24 用户追加）

- **多地图轮换训练**（防过拟合）：每类地图中央单个**抽象争夺点**（StrategicZone），双方 home 对置：
  - A. 正方形小图
  - B. 水平波浪形小图（正弦扰动纵深）
  - 掩体规格不变：高掩体/矮掩体/高位掩体/斜面，掩体间距宽松
- **对手阵容轮换**（四种，每回合轮换）：全突击 / 全支援 / 全侦察 / 混合
- 实现落点：地图布局进 `TrainingMapBuilder`（程序化生成，布局随机化种子可复现）；阵容轮换进 `TrainingSessionManager`（每回合重排双方 classId + 固定配装）。
