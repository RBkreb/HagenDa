# 资源加载，局外系统

## 规格（原始）
- 1.进入游戏时不加载任何地图，模型，不进行host连接
- 2.点击开始游戏按钮后进入匹配大厅，可以主持host或选择host地址
- 3.完成host连接后进入房间
- *需要有断开host连接和退出游戏，退出至大厅功能*
## 局间状态
- 房间中有3种状态，对局状态，空闲状态，准备状态
- 空闲状态->准备状态，host决定开始对局进入，开始加载地图资源并分配小队，优先把真人分在同一小队，且均摊红蓝双方，完成分配后用AI填充剩余
- 准备状态->对局状态，地图资源加载完成且小队分配完成，进入正式对局，所有实体暂不部署，模型不进入地图，开局时可以先选择部署点，正式开局后模型进入选择的部署点；未选择的则暂不部署
- 对局状态->空闲状态，对局正常结束进入，或者对局内所有真人已退出强制结束
- 对局状态需要允许新加入host的真人玩家挤占AI，真人退出后用AI补位
- *AI和真人实际上使用相同的服务端权威输入路径，所以转移上层输入即可完成补位；真人挤占优先挤占已死亡的AI，无死亡AI则强制击杀1个AI*
## 局中状态
- 实体可以在部署界面切换配装，部署之后将选择的配装与模型组合并部署

# 实现（PHASE15）

TDD：先写纯逻辑测试（EditMode），再写网络层测试（PlayMode），最后接线 UI 与构建器。
基线由 113/79 → **159 EditMode / 102 PlayMode，全绿**（2026-09-20）。

## 界面系统（局外）
| 文件 | 职责 |
| --- | --- |
| `Match/LobbyConnection.cs` | host/加入/断开/退出至大厅/退出游戏。**不自动连接**（规格 1）。无 NetworkManager 时安全失败并给可读原因 |
| `Match/LobbyScreen.cs` | 三步 uGUI：开始游戏 → 匹配大厅（主持 / 地址输入 / 加入）→ 房间（相位驱动）。运行时构建，无预制体依赖 |
| `Editor/Builders/LobbySceneBuilder.cs` | `HagenDa/PHASE15/Create Lobby Scene`：生成**离线**大厅场景（无地图、无实体、NetworkManager 存在但未启动） |

## 局间系统（房间三态）
| 文件 | 职责 |
| --- | --- |
| `Match/RoomPhase.cs` | `RoomPhase{Idle,Ready,Match}` + `RoomPhaseRules`（合法迁移表、`AllowsDeploy`/`AllowsHostStart`，纯逻辑） |
| `Match/TeamBalancer.cs` | 真人分队：**先均摊红蓝，再同队聚拢到最低小队**；`AiFillCount` 算剩余席位的 AI 补位 |
| `Match/AiSeatPolicy.cs` | 挤占选择：目标队伍内死席 → 任意死席 → 目标队伍活席（强制击杀）→ 任意活席。**顺序稳定、可复现** |
| `Match/DeployRoster.cs` | 局中部署名册：`BeginMatch` 清空 → `SetChoice` 仅记录 → `CommitDeployment` 只落地已选者 |
| `Match/NetworkRoomController.cs` | 服务器权威宿主：相位 SyncVar、队伍/小队登记、AI 补位/挤占、相位冻结、开局批量落地 |
| `Match/NetworkRoomManager.cs` | `NetworkManager` 子类：连接时登记 host；对局中 `OnServerAddPlayer` 先尝试接管 AI 身体 |

关键接线：
- **相位迁移全部经 `RoomPhaseRules.CanTransition` 校验**，非法迁移（如 空闲→对局）返回 false。
- **真人挤占 AI**：对局中 `TryPossessAiForHuman` → `AiSeatPolicy` 选席 → 死席直接用 / 活席 `ForceKill`
  → `AddPlayerForConnection`(新连接) 或 `ReplacePlayerForConnection` 把 AI 身体所有权转给真人。
  因为 AI 与真人共用同一条服务端输入通路，转交身体即完成补位。
- **真人退出**：`RemoveHuman`；对局中真人**全退** → `EndMatch()` 强制回到空闲（规格）。
- **未部署门控**：`NetworkPlayerHealth.holdInPlace`（SyncVar）。非对局相位生成的实体在
  `OnStartServer` 即冻结；准备→对局时 `ApplyPhaseHoldToAllCombatants(false)` 解冻。
- **开局落地**：`CompleteMatchLoad` 里 AI 走 `ServerPlaceAtDeployPoint(1)` 立即落地；
  真人解冻后仍在部署界面选点，未选者暂不部署。

## 局中系统（部署）
- `DeployScreen` 已存在（配装切换 + 部署点选择）；仅追加相位门控：`holdInPlace` 时不显示。
- `NetworkPlayerHealth.ServerPlaceAtDeployPoint(choice)` 为服务器批量落地入口，
  绕过玩家交互用的 `awaitingInitialDeploy`/`redeployDeadline` 门控。

## 测试
- `Assets/Tests/EditMode/Match/`：`RoomPhaseRulesTests`、`TeamBalancerTests`、`AiSeatPolicyTests`、
  `DeployRosterTests`、`LobbyConnectionTests`（纯逻辑，无网络）。
- `Assets/Tests/PlayMode/RoomFlowTests.cs`：相位迁移、分队、挤占（死席/强制击杀）、
  自动登记席位、退出强制结束、相位冻结/解冻、开局落地。

## 与旧场景的兼容
`holdInPlace` 默认 `false`，且只有 `NetworkRoomController` 会置位。既有战斗场景
（FSMBattle*、TrainingArena 等）不含该组件，故行为不变（全量测试已覆盖）。

## 已知欠缺（非阻塞）
- 跨场景地图加载只在 `mapSceneName` 非空时触发；单场景战场（推荐）留空即就地管理相位。
- 客户端"开始对局"按钮目前只做本地判定；生产环境应改为 `[Command]` 请求。
- 挤占后真人继承 AI 身体当前配装，尚未按"局中切换配装"重新校验。
