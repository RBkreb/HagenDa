# 角色动画
- 混合树资源Assets/Scripts/Network/Animation/NetworkSoldierLayers.controller
- Fatui士兵预制体 Assets/Model/Fatui/Fatui with Collider.prefab
- M4枪模预制体 Assets/Model/Guns/M4_8.prefab
## 角色结构
角色主胶囊体，原先network中的胶囊体去除render显示
(角色模型，枪械模型基准(枪械模型))
## 角色模型结构
- L0 LocomotionFull   无 mask,八向混合树  
- L1 LocomotionLower  mask=下半身(腿+髋) 八向混合树
- L2 UpperActions     mask=上半身，禁止八向移动动画剪辑
- L3 Death            无 mask,死亡无出边,复活 Rebind。预期使用ragdoll，占位符
- Rig 
```
脚部贴地IK
手臂Two Bone IK
手HandGripConstraint(armIK=false)->约束于枪模
枪基准 Aim Constraint 源头头部骨骼子对象AimSource + 当前相机射线50m目标点
枪基准未初始化，建议放置右前胸，或摆好姿势后交由用户设置
```
## 姿态
- 上半身跟随胶囊体相机水平旋转
- 站:下半身T pose +  脚 IK 贴地 
- 蹲:multiPostionConstriant绑定角色骨骼根下降与上半身旋转 + 脚 IK 贴地 + 混合树继续
- 趴:剪辑冻结 + 膝/肘 地面IK;移动=身体极缓蹭行;multiPostionConstriant绑定角色骨骼根下降与上半身俯仰。此时上半身不能旋转，整个身体都是平趴状态，所以相机旋转时让整个身体跟着转
- 死亡
## 动作
- 跳: 轻微下蹲后回到混合树
- 滑铲:蹲姿，保持混合树冻结帧，脚IK，滑铲结束出边
- 滞空:停在滞空后0.1s的帧，让跳的下蹲播完伸直
## 脚部贴地IK专项
八向移动的动画剪辑中可以找到基础IK,Right Foot Q,Right Foot T,另一组是Left。在动画剪辑的脚落地帧，高度轴最低，对落地的脚启用地面IK,抬脚后禁用。IK使用animation rigging，或者射线检测，射线检测在脚骨上方，向下射0.1m，检查地面并启动IK。
站立/蹲/趴IK,三维固定；滑铲IK,仅y轴固定，让脚贴地滑行
## 转身专项
转身目的是让角色转向更自然，基本逻辑为保持脚部IK对地面锁定，让脚钉在地面，在特定条件下将IK定位点跟随旋转从而整体转身
- 当位于地面时，下半身启用混合树+脚IK。上半身立即跟随水平转向。下半身滞后，站/蹲静止状态上半身开始转向后1s，开始追赶转向。移动状态持续保持转向追赶。
- 转向追赶使用IK 基准点跟随旋转。
- 上半身脊柱反扭上限 60°，超出立即开始追赶
- 当位于空中时，上下半身保持立即同步水平旋转
- 趴下状态特殊处理
# 相机
不再使用绑定于网络角色胶囊体的相机，将相机放置于模型骨骼子对象 headcollider(一个球体)表面，注意是表面不是球心。让相机跟随球体移动。此处需要让网络代码同时兼容。注意：相机不仅要跟随坐标移动，还要时刻保持与球体表面法线平行，模拟在头部的眼睛
## 枪械专项
- 腰射:枪基准 Aim Constraint 源头头部骨骼子对象AimSource + 当前相机射线50m目标点,禁用冲刺基准Aim Constriant SprintAim
- 瞄准:根据枪械开镜时间将枪械的RearAim和SightAim约束至相机射线，要求可调整瞳距，禁用Aim Constriant
- 冲刺:枪自身Aim Constriant 源 人物子对象SprintAim,左右摆枪(局部X)，禁用枪基准Aim Constraint
- 开火:枪模根据后座力数值发生震动，bolt根据射速 局部z 0.156前向界限，0.185后向界限，来回移动，最终停在前向界限。可以用动画剪辑。由于枪械基准存在，所以枪不会因为后座力而影响其他动作
- 换弹占位符
# 网络
所有动作由服务端权威状态触发。
# 重要提醒
- 动画剪辑资源只使用8向混合树奔跑，其他资源不使用
- 当前natlan和Fatui均有各自的一条分支实现，这两条实现均为残缺型。
- 从0开始实现，不要混合两条分支的实现，这两条分支未经代码审查和测试
