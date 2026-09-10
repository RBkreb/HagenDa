角色动画
│
| 绑定顺序：1 角色胶囊体->人物模型 + 枪基准对象>枪模型
├─ 1. 架构载体 
│   ├─ Animation Rigging 1.2.1(已装),
│   ├─ Animator 剪辑层(重建 NetworkSoldier.controller)
│   │   ├─ L0 LocomotionFull   无 mask,八向混合树  ←无武器时权重1
│   │   ├─ L1 LocomotionLower  mask=下半身(腿+髋)   ←有武器时权重1，播放八向移动动画剪辑和Idle
│   │   ├─ L2 UpperActions     mask=上半身,Shoot/Reload(exit time 出边)，禁止八向移动动画剪辑和Idle
│   │   └─ L3 Death            无 mask,死亡无出边,复活 Rebind
│   │   └─ 移除:Aim/Crouch/Prone/Damage 状态、Posture/Aiming 参数
│   └─ Rig 4层 (RigBuilder)
│       ├─ LowerBody:双脚 Two Bone IK 贴地 + BodyPosition(hips 加法偏移,自定义约束)
│       ├─ UpperBody:立即跟随水平瞄准方向(下半身脊柱反扭 + 30% pitch + 发生水平移动1s后播放一次走路动画追赶)
│       └─ Hands:双手 Two Bone IK + 双手 HandGripConstraint(armIK=false)->约束于枪模
|       └─ 枪基准 Aim Constraint 源头头部骨骼子对象AimSource + 当前相机射线50m目标点
│
├─ 2. 差速转身
│   ├─ root yaw=服务器同步水平瞄准方向;下半身在上半身发生水平移动1s后播放一次走路动画追赶,SmoothDampAngle旋转追赶
│   ├─ 脊柱反扭上限 60°,超出→下半身立刻播放走路动画并追赶;参数 Inspector 可调
│   ├─ 下半身转身瞬间脚 IK 权重降0;远程 yaw 网络步进用快速阻尼吸收
│   └─ 无武器时 twist 权重→0(剪辑接管脊柱,避免与 L0 打架)←派生补充
│
├─ 3. 姿态
|   ├─ 站:下半身Idle,上半身无idle动画
│   ├─ 蹲:multiPostionConstriant绑定角色骨骼根下降 + 脚 IK 贴地 + 混合树继续
│   ├─ 趴:剪辑冻结 + 膝/肘 地面IK;移动=身体极缓蹭行
│   ├─ 过渡 SmoothDamp 沿用 PHASE2 时长(0.1/0.25/0.15s)
|   ├─ 跳: 轻微下蹲后回到动画树
│   ├─ 滑铲:占位符
│   └─ 滞空:停在滞空后0.1s的帧，让跳的下蹲播完伸直
│
├─ 4. 动作 
│   ├─ Shoot:上半身 mask 播放,枪模围绕枪基准随机震动，根据枪的后座力数值变化
|   ├─ aim:
│   ├─ Death:全身剪辑,三 rig 层权重→0,枪模隐藏;复活 Rebind+恢复
│   └─ Damage:整体移除(NetworkPlayerHealth 调用点一并清理)
│
├─ 5. 枪械/配装 [Q5✓ Q9✓ Q10✓ Q15✓]
│   ├─ NetworkGun 持有第三人称 M4(挂瞄准链下,非手骨);WeaponGripData 接口通用化
│   ├─ M4_8.prefab 追加 GripRoot/ForeGrip/AimSource/握把胶囊(纯追加)
│   ├─ activeSlot 切换:M4 显隐骨架 + 手 IK→0;非主武器近期无模型(L0 fallback 接管)
│   └─ ADS:aimAmount 驱动锚点胸口→眼高混合
│
├─ 6. 脚 IK [Q6✓ Q12✓]
│   └─ idle/蹲/趴=1,走/跑=0,滑铲/滞空/死亡=0,大转身瞬降;目标=踝下 raycast 地面投影
│
├─ 7. 构建 
│   ├─ HandGrip:编辑器 bake 进 Natlan wrapper prefab(现有 HandGripRigBuilder)
│   ├─ 其余约束:运行时 RigSetup 人形骨骼映射程序化构建(Natlan/Fatui 同一路径)
│   └─ 全实体启用,预留总开关;Fatui 本轮不做
│
├─ 8. Bug 修复 
│   └─ Shoot/Reload/Aiming/Death 边沿失效排查(NetworkGun 字段生命周期/死亡 trigger 时序)
│
├─ 9. 验收 
│   ├─ 我:自动化 Play Mode 相位验证(状态机+rig 权重采样)
│   └─ 你:最终交付目视检查
│
└─ 10. 实施顺序
    ①边沿 bug 修复 → ②controller 重建(4 剪辑层) → ③RigSetup+自定义约束
    → ④HandGrip bake(Natlan) → ⑤NetworkGun 枪模接线/ADS/槽位显隐 → ⑥自动化验证交付


# 角色动画
## 角色结构
角色主胶囊体，原先network中的胶囊体去除render显示
├─角色模型
├─枪械模型基准
    ├─枪械模型
## 角色模型结构
- L0 LocomotionFull   无 mask,八向混合树  
- L1 LocomotionLower  mask=下半身(腿+髋) 八向混合树
- L2 UpperActions     mask=上半身，禁止八向移动动画剪辑和Idle
- L3 Death            无 mask,死亡无出边,复活 Rebind。预期使用ragdoll，占位符
- Rig 
```
脚部贴地IK
手臂Two Bone IK
手HandGripConstraint(armIK=false)->约束于枪模
枪基准 Aim Constraint 源头头部骨骼子对象AimSource + 当前相机射线50m目标点
```
## 姿态
- 上半身无idle，跟随胶囊体相机水平旋转
- 站:下半身Idle +  脚 IK 贴地 
- 蹲:multiPostionConstriant绑定角色骨骼根下降与上半身旋转 + 脚 IK 贴地 + 混合树继续
- 趴:剪辑冻结 + 膝/肘 地面IK;移动=身体极缓蹭行;multiPostionConstriant绑定角色骨骼根下降与上半身俯仰。此时上半身不能旋转，整个身体都是平趴状态，所以相机旋转时让整个身体跟着转
- 死亡
## 动作
- 跳: 轻微下蹲后回到混合树
- 滑铲:蹲姿，保持混合树冻结帧，脚IK权重0，滑铲结束出边
- 滞空:停在滞空后0.1s的帧，让跳的下蹲播完伸直
## 脚部贴地IK专项