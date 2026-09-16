# 模型移植清单（Fatui → Natlan）

把 Soldier 基础动画系统移植到一个新角色模型（本清单以 **Natlan Soldier** 为目标）需要创建/修改的对象。
所有名字都是**代码里的硬编码字面量**（不是约定俗成的叫法），必须逐字一致。

参考基准：`Assets/Model/Fatui/Fatui with Collider.prefab`（已完成）+ `Assets/Scripts/Network/Prefabs/NetworkPlayer.prefab`
目标：`Assets/Model/natlan/Natlan Soldier FBX with collider.prefab`

图例：**必须** = 缺了功能坏掉/报错；**推荐** = 缺了会退化到不明显但更差的回退路径；*(自动)* = 运行时/烘焙脚本会自己建，不用手放。

---

## 0. 先决条件：骨骼命名与关键差异（最重要）

代码里硬编码了 Blender `DEF-*` 骨骼名。**Natlan 与 Fatui 的命名基本一致，但人形 Head 骨骼不同**：

| 人形骨骼 | Fatui | Natlan | 代码里找的名字 |
|---|---|---|---|
| Hips | Fatui bodyguard rig.001 | Natlan Soldier Rig.001 | —（用 humanoid 映射） |
| Chest | DEF-spine.001 | DEF-spine.001 | 一致 ✅ |
| Neck | DEF-spine.004 | DEF-spine.004 | 一致 ✅ |
| **Head** | **DEF-spine.005** | **DEF-spine.006** | ⚠️ **不一致** |

`DEF-spine` 链两者结构平行（`.001 → .002 → .004 → .005 → .006`），但：

- `SoldierRigBaker.HeadBone = "DEF-spine.005"`
- `SoldierRigDriver.EnsureCache` 里 `FindDeep(modelRoot, "DEF-spine.005")`

**这两处在 Natlan 上会"静默选错骨骼"**（`DEF-spine.005` 在 Natlan 也存在 —— 它是**脖子**，
`.006` 才是头）：

```
Natlan 实测解析结果：
  FindDeep("DEF-spine.005") -> .../DEF-spine.002/DEF-spine.004/DEF-spine.005   ← 脖子！(不是头)
  FindDeep("DEF-spine.006") -> .../DEF-spine.004/DEF-spine.005/DEF-spine.006   ← 真正的头
  humanoid Head 实际 =  .../DEF-spine.005/DEF-spine.006
```

因为名字存在，`FindDeep` 会成功返回，**不会报错也不会走 humanoid 兜底** ——
后果是 `AimSource` 挂到脖子上、上半身瞄准与相机的头部基准都偏一节。

**处理方式（二选一）**：

1. 把这两处改成 `animator.GetBoneTransform(HumanBodyBones.Head)` 优先、`DEF-*` 名兜底（推荐，模型无关）；
2. 或者按模型改常量：Natlan 用 `DEF-spine.006`，Fatui 保持 `DEF-spine.005`。

> 其余骨骼名两边完全一致，无需改动：
> `DEF-spine` / `DEF-spine.002`（胸部约束用，两边都有）/ `DEF-thigh.L|R` / `DEF-shin.L|R` /
> `DEF-foot.L|R` / `DEF-upper_arm.L|R` / `DEF-forearm.L|R` / `DEF-hand.L|R`

另外 Natlan 用 `100` 的臂架缩放（Fatui 用 `~70`）。驱动全部按**世界空间**运算，缩放不影响逻辑；
但下面所有**模型本地坐标**（Hint / GunCylinder / SprintAim）必须按新模型重新摆放。

### Natlan 现状快照（实测）

| 对象 | Natlan 现状 | 目标 |
|---|---|---|
| `Rig_Drivers` | 存在，但含旧 `Driver_Yaw`/`Driver_Offset`/`WeaponAnchor` | 6 个 `Driver_*`（烘焙会重建） |
| `Rig_Hands` | 存在：`ArmIK_R/L` + `RightGrip`/`LeftGrip` | 保留（烘焙只重建 ArmIK） |
| `Rig_LowerBody` | 存在，含旧 `HipsPose` | `FootIK_L/R`（烘焙会重建） |
| `Rig_UpperBody` | 存在，含旧 `SpineAim` | `UpperAim`（烘焙会重建） |
| `Rig_Pose` | 存在：`CrouchPose`/`PronePose` | **删除**（手动） |
| `Hint` | **缺失** | 建 4 个静态点（§5） |
| `GunCylinder` | **缺失** | 建（§6） |
| `SprintAim` | **缺失** | 建（§7） |
| `headcollider` | **缺失** | 建（§9） |
| `NetworkHitbox` | **无（0 个）** | 建命中箱套件（§9） |
| `AimSource` | 错挂在 `M4_8/AimSource` | 移到头骨骼下（§8） |
| `M4_8` | 挂在**模型根**下（带 `RigTransform`） | 移到实体根 `WeaponBasis` 下 |
| Animator Controller | `NetworkSoldierLayers`（旧） | `SoldierLoco`（§10） |

---

## 1. 模型根（= Animator 节点）直接子对象

全部放在**模型根下**、与骨架（`... Rig.001`）**同级**。之所以必须同级：动画骨架内的
Transform 由 AnimationStream 持有独立副本，骨架外的才是稳定可读的。

```
<模型根>/
├── Rig_Drivers/              ← 必须是这个容器名
│   ├── Driver_AimSource      **必须**  眼位（运行时每帧写入世界位姿）
│   ├── Driver_AimTarget      **必须**  瞄准目标点（约束的源）
│   ├── Driver_FootTarget_L   **必须**  左脚 IK 目标
│   ├── Driver_FootTarget_R   **必须**  右脚 IK 目标
│   ├── Driver_FootHint_L     **推荐**  膝 pole 兜底（运行时通常被 Hint/LtKnee 覆盖）
│   └── Driver_FootHint_R     **推荐**
├── Rig_Hands/                 **必须**  挂 Rig 组件；子节点见 §2
├── Rig_LowerBody/             **必须**  挂 Rig 组件；子节点见 §3
├── Rig_UpperBody/             **必须**  挂 Rig 组件；子节点见 §4
├── Hint/                      **必须**  4 个静态基准点，见 §5
│   ├── RtKnee  LtKnee  RtElbow  LtElbow
├── GunCylinder/               **必须**  右肩柱体，见 §6（配 CapsuleCollider）
└── SprintAim/                 **推荐**  冲刺收枪目标点，见 §7

<模型根的 Animator>/
└── .../DEF-spine.0xx/<头骨骼>/
    └── AimSource              **必须**  头骨骼的子物体（空物体，localPos=0）
```

模型根自身组件：

| 组件 | 说明 |
|---|---|
| `Animator` | Controller = `SoldierLoco`（见 §10），**applyRootMotion = false** |
| `RigBuilder` | 自动收集下面 3 个 Rig 层（`Build()` 由驱动延迟调用） |
| `SoldierRigDriver` | 核心驱动 |
| `BoneRenderer` | 可选（编辑器 gizmo），可 Disable |

*(自动)* `ModelDrop` —— 实体根与模型之间的中间节点，驱动 `EnsureCache` 发现缺失会自建并改父级。
不用手放，但注意模型根必须挂在实体根下（不能更浅，也不能嵌在别处）。

---

## 2. `Rig_Hands/` 的内容（手臂 IK + 握把）

`Rig_Hands` 自身：`Rig` 组件，weight = 1。

| 子对象 | 组件 | 配置 |
|---|---|---|
| `ArmIK_R` | `TwoBoneIKConstraint` | root=`DEF-upper_arm.R` mid=`DEF-forearm.R` tip=`DEF-hand.R` target=*枪的 RearGrip（运行时绑）* hint=`Hint/RtElbow` **posW=1 rotW=1** hintW=1 |
| `ArmIK_L` | `TwoBoneIKConstraint` | 同上，用 `.L` 三个骨骼 + hint=`Hint/LtElbow` |
| `RightGrip` | `HandGripConstraint` | `side = Right`（手指卷握；`armIK=false`） |
| `LeftGrip` | `HandGripConstraint` | `side = Left` |

> `HandGripConstraint` 的 bind 分析常量存在组件上，**不要删掉这两个节点重建**（重烘焙会保留它们）。
> `SoldierRigBaker` 只删 `ArmIK_L/R` 并重建，正是为此。

---

## 3. `Rig_LowerBody/` 的内容（腿 IK）

`Rig_LowerBody` 自身：`Rig` 组件，weight = 1。

| 子对象 | 组件 | 配置 |
|---|---|---|
| `FootIK_L` | `TwoBoneIKConstraint` | root=`DEF-thigh.L` mid=`DEF-shin.L` tip=`DEF-foot.L` target=`Driver_FootTarget_L` hint=`Driver_FootHint_L` **posW=1 rotW=0** hintW=1 |
| `FootIK_R` | `TwoBoneIKConstraint` | 同上，用 `.R` |

> ⚠️ **`rotW` 必须是 0**：脚 IK 只钉位置，朝向交给动画。
> 若设为 1（或脚本直接写 `tip.rotation`），脚掌会被翻到脚底朝上 —— 已踩过的坑。

---

## 4. `Rig_UpperBody/` 的内容（脊柱反扭瞄准）

`Rig_UpperBody` 自身：`Rig` 组件，weight = 1。

| 子对象 | 组件 | 配置 |
|---|---|---|
| `UpperAim` | `UpperBodyAimConstraint` | root=模型根，lowerBody=模型根，**chest=`DEF-spine.002`**，head=头骨骼，aimSource=`Rig_Drivers/Driver_AimSource`，maxTwist=60，chestPitchShare=0.3，headPitchShare=0.6，facingAxis=(0,0,1) |

> 这个约束负责"上半身即时跟随相机"：它把 chest/head 朝向 aimSource。
> 它是**即时**的，不要改成本地滞后（会让上半身跟着滞后 —— 已踩过的坑）。
> "下半身滞后"由**脚 IK 基准点滞后**实现（`footLagYaw`），见 PHASE14.md「转身专项」。

---

## 5. `Hint/` —— 4 个静态基准点（防肘/膝翻转）

`Hint` 是模型根的**直接子物体**（在骨架之外），内含 4 个**空物体**。
它们相对角色静态（只随整体 yaw/俯仰转），给 TwoBoneIK 当弯曲平面 pole，防止关节翻面。

Fatui 实测值（模型根本地坐标；`Hint` 容器本身有偏移，下面给的是**最终世界/本地落点**）：

| 点 | Fatui 本地坐标 | 摆放意图 |
|---|---|---|
| `RtKnee` | ( 0.133, 0.673, 0.253) | 右膝**正前方** |
| `LtKnee` | (-0.157, 0.673, 0.253) | 左膝正前方 |
| `RtElbow` | ( 0.348, 1.273, -0.047) | 右肘**后方/外侧** |
| `LtElbow` | (-0.372, 1.273, -0.047) | 左肘后方/外侧 |

**Natlan 必须重新放**：由于骨架缩放 100（Fatui ~70）与身体比例不同，直接抄数字会错。
摆法：站立 T-pose 下，膝点放在膝**前方**约一个膝厚（决定膝朝前弯）；肘点放在肘**后方偏外侧**
（决定肘朝后下弯）。放好后在场景里转几圈确认肘/膝不翻面。

---

## 6. `GunCylinder/` —— 右肩柱体（腰射枪托滑轨）

空物体 + `CapsuleCollider`（**isTrigger 随便**，只用来量半径；`MeshFilter` 可留可删）。

| 项 | Fatui 实测 | 说明 |
|---|---|---|
| localPos | (0.162, 1.576, 0.025) | 右肩位置 |
| localRot | (0, 0, 90) | 让胶囊轴沿手臂/竖直 |
| CapsuleCollider | dir=Y, radius=0.343, height=3.79 | **本地**尺寸 |
| 最终世界半径 | ≈ **0.093 m** | 驱动实际用的柱面半径 |

驱动 `CylinderWorldRadius()` 从 **Collider.bounds** 推半径，所以世界半径是唯一有效量。
**Natlan 摆法**：放在右肩关节处，朝向让胶囊轴线与"枪托绕肩运动"的转轴一致（≈竖直），
调整 radius 使**世界半径落在 0.09–0.14** 之间（这是枪绕肩运动的轨道半径）。

> 缺 `GunCylinder` 会回退到 `hipHoldOffset`（眼空间固定偏移），会复现"低头时枪捅进后背"的老问题。
> **必须**有。

---

## 7. `SprintAim/` —— 冲刺收枪目标点

空物体。AimConstraint 瞄的是源的**位置**，所以它决定冲刺时枪指向哪里。
Fatui 实测 localPos = (-0.731, 1.311, 0.339)（偏左前上）。

**Natlan 摆法**：放在角色左前下方（收枪抱在身前的方向）。摆放时注意冲刺姿态**锁俯仰**，
所以高度取"平视时想要的位置"即可。缺了驱动会自建到 (0.22, 1.05, 0.30)，位置不对但不会崩。

---

## 8. 头骨骼下的 `AimSource`

- 位置：**人形 Head 骨骼**的子物体（Natlan = `DEF-spine.006`），空物体，localPos = (0,0,0)。
- 作用：规范的"头部瞄准节点"，上半身约束/枪 AIM 的实时基准。
- `SoldierRigBaker` 会自动建（在它认定的 HeadBone 下）。若 §0 里改成了 humanoid 映射，就会挂对。

> ⚠️ **Natlan 现状**：它现有的 `AimSource` 挂在 **`M4_8/AimSource`（枪下！）** —— 这是旧分支的残留，
> 位置完全不对。**必须删掉，重新挂到头部骨骼下**。

---

## 9. `headcollider/` —— 头部球（相机 + 命中）

放在头骨骼下，空物体 + `SphereCollider`(**trigger**) + `NetworkHitbox`(part = **Head**)。

| 项 | Fatui 实测 |
|---|---|
| 头骨骼 | `DEF-spine.005`（Natlan 对应 .006） |
| localPos | (0, 0.002, 0) |
| SphereCollider | radius = 0.01（**本地**） |
| lossyScale | ≈ 10.86 → **世界半径 ≈ 0.109 m** |
| NetworkHitbox.part | `Head`（2x 伤害） |

**相机就挂在这个球的表面**（沿视线方向法线外推）。世界半径直接决定眼睛离头心的距离，
Natlan 按自己头围调到世界半径 ≈ 0.10–0.12 即可。

> 缺 `headcollider`：相机球面路径失效、头部命中判定丢失。**必须**有。

### 其余命中箱（可选，但影响伤害部位判定）

同款 `SphereCollider`/`CapsuleCollider`(**trigger**) + `NetworkHitbox`，全部**绑在骨骼下**：

| 对象名 | part | Fatui 本地尺寸 | 挂点 |
|---|---|---|---|
| `headcollider` | Head | Sphere r=0.01 | 头骨骼 |
| `bodycapsule` | Body | Capsule r=0.01 h=0.04 | 胸部（`DEF-spine.002`） |
| `leftarmcapsule` / `rightarmcapsule` | Limb | Capsule r=0.06 h=0.64 | 前臂骨骼 |
| `leftlegcapsule` / `rightlegcapsule` | Limb | Capsule r=0.08 h=1.01 | 小腿骨骼 |

> **只要有任意一个 NetworkHitbox，子弹射线就会忽略移动胶囊**，只打命中箱。
> 所以这套要建就建全（头/躯干/四肢），否则会出现"打不到"的判定空洞。

---

## 10. 动画控制器与遮罩

| 资产 | 路径与设置 |
|---|---|
| Controller | `Assets/Scripts/Soldier/SoldierLoco.controller`（用 `HagenDa/SoldierAnim/Bake Soldier Controller` 生成） |
| 下半身遮罩 | `Assets/Scripts/Network/Animation/LowerBody.mask` |
| 上半身遮罩 | `Assets/Scripts/Network/Animation/UpperBody.mask` |
| Animator | `applyRootMotion = false`（Humanoid 剪辑即使关根运动也会重写模型根 localPosition） |

Natlan 当前用的是旧的 `NetworkSoldierLayers` —— **要换成 `SoldierLoco`**。
`SoldierLoco` 5 层：L0 绑定姿态 / L1 全身八向 / L2 下半身八向 / L3 上半身(恒0) / L4 死亡。

---

## 11. 要**删除**的旧分支残留（Natlan 现状）

Natlan 预制体目前带着**旧 rig 分支**。好消息是一部分烘焙会自动清掉，但**不是全部**。

**烘焙会自动清理的**（`SoldierRigBaker` 逻辑）：

| 对象 | 为什么自动 |
|---|---|
| `SoldierRigSetup` 组件 | 在剥离组件列表里 |
| `SpineAimConstraint` @ `Rig_UpperBody/SpineAim` | 组件被剥离；且 `Rig_UpperBody` **整个节点被删除重建** → 连节点一起没 |
| `HipsPoseConstraint` @ `Rig_LowerBody/HipsPose` | 同上，`Rig_LowerBody` 整个重建 → 连节点一起没 |
| `Rig_Drivers` 下的 `Driver_Yaw` / `Driver_Offset` / `WeaponAnchor` | `Rig_Drivers` **整个节点被删除重建**（且 `WeaponAnchor` 还在删除名单里） |
| `Rig_Hands` 下的 `ArmIK_L/R` | 只删这两个子节点，`RightGrip`/`LeftGrip` 保留 |

**必须手动处理的**：

| 对象 / 组件 | 处理 | 原因 |
|---|---|---|
| `Rig_Pose/`（含 `CrouchPose`、`PronePose`） | **手动删整棵** | 烘焙**不碰** `Rig_Pose`；`SkeletonPoseConstraint` 组件会被剥离，只剩空节点 + 一个空 Rig 层 |
| `RigTransform` @ `M4_8` | 删组件 | 不在剥离列表 |
| 模型根下的 `M4_8` | **移出**，改挂实体根的 `WeaponBasis` 下（§12/§13） | 枪必须挂实体层，不能留在模型里 |
| `AimSource` @ `M4_8/AimSource` | 删除（重新挂到头部骨骼，§8） | 旧残留，位置错误 |
| `GripCapR` / `GripCapL` | 删除 | 旧命名。新代码要 `RearGripCap`/`BarrelGripCap`（在**枪**上，§13） |
| `NetworkSoldierLayers.controller` 引用 | 换成 `SoldierLoco`（§10） | 旧控制器层结构与新驱动不匹配 |

> 注意：`Rig_Hands` 节点本身**会被保留**（`RightGrip`/`LeftGrip` 的 HandGrip 分析常量存在组件上），
> 所以不要删 `Rig_Hands`。

---

## 12. 实体根（NetworkPlayer / AIEntity）结构

```
<实体根>                        [NetworkIdentity, NetworkTransform, Rigidbody,
│                                CapsuleCollider ×2(站/蹲), NetworkPlayerController,
│                                NetworkPlayerHealth, NetworkCombatant, NetworkGun, ...]
├── ModelDrop/                  *(自动)* 驱动自建
│   └── <模型根>                 (= §1 的模型根)
├── WeaponBasis/                *(自动)* NetworkGun 缺则自建
│   └── M4_8                    ← 枪模放这里（与角色模型**同级**）
├── Body                        ↑ 旧胶囊渲染体，Renderer 应 Disable
└── Camera
```

`NetworkPlayerController.eyeAnchor` 指向 `headcollider`。

`NetworkGun` 的两个字段：

| 字段 | 值 |
|---|---|
| `thirdPersonModelPrefab` | 枪预制体（如 `M4_8`） |
| `controller` | 同实体根的 `NetworkPlayerController` |

> 每换一把武器就是换 `thirdPersonModelPrefab` + 对应的 `WeaponDefinition`。

---

## 13. 新增一把**枪**预制体需要放哪些子对象

枪挂到 `WeaponBasis` 下后，`SoldierRigDriver.BindWeapon(gun)` 会按名字找锚点。
名字全部是硬编码字面量。

```
<枪根>/
├── Bolt              **必须**(枪机)  局部 z 在 [0.156, 0.185] 往复
├── RearGrip          **必须**(右手)  右手 TwoBoneIK 目标 = 后握把
├── BarrelGrip        **必须**(左手)  左手 TwoBoneIK 目标 = 护木/前握把
├── RearGripCap       **必须**(手指)  CapsuleCollider，右手卷握分析用
├── BarrelGripCap     **必须**(手指)  CapsuleCollider，左手卷握分析用
├── RearAim           **必须**(ADS)   贴眼锚点（精确瞄准点，放近眼瞄具处）
├── SightAim          **必须**(ADS)   枪管轴前端点（瞄准轴 = SightAim − RearAim）
├── Stock             **必须**(腰射)  枪托点；腰射时它落在右肩 GunCylinder 柱面上
├── Rear_Sight        **推荐**        近眼瞄具 3D 件（RearAim 缺失时的轴回退）
├── Sight             **推荐**        近枪口瞄具 3D 件（SightAim 缺失时的回退）
├── Mag               (外观)          弹匣
│   └── MagGripCap    (外观)
├── MagGrip           (外观)          弹匣井/握持参考
└── Trigger           (外观)          扳机
```

判定细节（来自代码）：

- `RearGrip` 或 `BarrelGrip` 任一缺失 → `BindWeapon` **直接 return false**，双手 IK 不绑。
- `RearGripCap`/`BarrelGripCap` 必须带 `CapsuleCollider`（手指卷握靠它分析），否则指节不弯。
- **枪管轴**优先取 `SightAim − RearAim`；两者都缺时退 `Sight − Rear_Sight`；再缺就假设本地 +Z。
  轴错了 → 腰射/ADS 全都指向错。
- `Stock` 是腰射绕肩运动的锚点，缺了腰射位退化。
- `Bolt` 缺了只是没有枪机往复动画。

**不用手放**（驱动运行时自建）：

- `WeaponBasis` 上的 `AimConstraint`（腰射基准，源 = `Driver_AimTarget`）
- 枪自身的 `AimConstraint`（冲刺用，源 = `SprintAim`，`aimVector` = 枪管轴）

> 枪预制体本身无需任何脚本 —— `BindWeapon` 会写它的 `localRotation`（对齐旋转，使基准本地 +Z = 枪管轴）。
> 所以枪的 rest 旋转会被驱动覆盖，摆放时只需关心**锚点的相对位置**。

---

## 14. 执行顺序（建议）

1. **先处理 §0**（Head 骨骼解析改成 humanoid 优先）——否则后面都对不齐。
2. 在 Natlan 模型根下建 §1 的骨架节点（可先只建 `Rig_Drivers`、`Hint`、`GunCylinder`、`SprintAim`）。
3. 把 §11 的旧残留清干净（尤其 `Rig_Pose`）。
4. 跑烘焙：`HagenDa/SoldierAnim/Bake Fatui Rig`（会重建 `Rig_Hands/LowerBody/UpperBody` 与约束）。
   *注：该菜单里的预制体路径是硬编码 Fatui 的，移植时需临时改成 natlan 路径或改造成参数化。*
5. 建 `headcollider` + 其余命中箱（§9），把 `NetworkPlayerController.eyeAnchor` 指过去。
6. 跑 `HagenDa/SoldierAnim/Bake Soldier Controller` 生成 `SoldierLoco`，指到 Animator。
7. 摆 §5 `Hint`、§6 `GunCylinder`、§7 `SprintAim` 的位置（按各自说明试摆）。
8. 跑 `HagenDa/SoldierAnim/Verify Fatui Rig` 核对约束绑定，再进 Play 实测：
   站立/蹲/趴、走/跑、转身滞后、脚贴地、ADS、冲刺、开火后座。

---

## 15. 易错点速查（本项目实测踩过的）

| 症状 | 原因 |
|---|---|
| 脚底朝上翻转 | 脚 IK `rotW` 被设为 1，或脚本写了 `tip.rotation` |
| 蹲走脚穿地 / 腿被钉住 | 蹲走必须走程序化解算（`useCrouchWalkLegs`），见 PHASE14.md |
| 肘/膝乱翻 | `Hint` 缺失或 pole 点与"关节→末端"轴近共线 |
| 上半身跟着滞后 | 给模型根加了差速；应为**即时**，滞后只在脚锚 |
| 下半身不滞后 | 脚锚反向公转被"滞后已收敛"门控短路（已修） |
| 开火时枪横向漂移 | 后座位移写了世界方向到 `localPosition`（已修，须用基准局部轴） |
| 冲刺枪水平跟随不同步 | 冲刺偏移用了带俯仰的 `eye.forward/up`（应用去俯仰基） |
| 低头枪捅后背 / 看到身体内部 | 缺 `GunCylinder`，或近裁剪面/相机球面外推未配 |
| 蹲下手到胯部 / 脚卡地 | 在 LateUpdate 二次压模型根（根位移应只在 `ModelDrop` 上） |

---

## 16. 调参落盘

手调过的值用 `HagenDa/SoldierAnim/Apply Rig Tuning` 写进预制体（**只填未手调的字段**）。
它当前硬编码路径为 Fatui / NetworkPlayer / AIEntity —— 移植时同样需要改成 natlan。
