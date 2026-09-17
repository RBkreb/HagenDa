using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace HagenDa.Animation.EditorTools
{
    /// <summary>
    /// PHASE14 移动层调整(幂等,可反复运行):
    ///  1. 去除 Idle:两个移动混合树(全身 TreeFull / 下半身 TreeLower)位于 (0,0) 的
    ///     Idle 子项,其剪辑替换为“前向走路”,从根上杜绝 Idle 动画。
    ///  2. 冻帧:新增 float 参数 LocoSpeed,作为两个移动层 Loco 状态的 speed 倍率。
    ///     运行时静止 → LocoSpeed=0,移动层停在上一次移动结束的那一帧
    ///     (上半身 Shoot/Reload 等不受影响,因为只改移动层的状态速率)。
    /// </summary>
    public static class SoldierLocomotionTuner
    {
        const string ControllerPath = "Assets/Game/Animation/NetworkSoldierLayers.controller";
        const string LocoSpeedParam = "LocoSpeed";
        static readonly string[] LocoStateNames = { "Loco" };   // L0/L1 的移动状态名

        [MenuItem("HagenDa/Rigging/Remove Idle + Freeze Locomotion")]
        public static void Apply()
        {
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (ctrl == null) { Debug.LogError($"[LocoTuner] 找不到 {ControllerPath}"); return; }

            // 1) 新增 LocoSpeed 参数
            bool hasParam = false;
            foreach (var p in ctrl.parameters)
                if (p.name == LocoSpeedParam) { hasParam = true; break; }
            if (!hasParam) ctrl.AddParameter(LocoSpeedParam, AnimatorControllerParameterType.Float);

            int replaced = 0, speedWired = 0;
            foreach (var layer in ctrl.layers)
            {
                var sm = layer.stateMachine;
                if (sm == null) continue;

                // 2) 该层所有移动状态的 speed 倍率 = LocoSpeed
                foreach (var cs in sm.states)
                {
                    var st = cs.state;
                    if (st.motion == null) continue;
                    if (st.name != "Loco") continue;
                    st.speedParameterActive = true;
                    st.speedParameter = LocoSpeedParam;
                    speedWired++;
                }

                // 3) 混合树:把 (0,0) 的 Idle 替换为前向走路
                foreach (var cs in sm.states)
                {
                    if (cs.state.motion is BlendTree bt) replaced += RemoveIdle(bt);
                }
            }

            // 4) 移动剪辑层只作用于下半身:确保 LocomotionFull 也带 LowerBody 掩码。
            //    (原先 L1 无掩码 → 上半身一直播走路/跑步剪辑,与"上半身完全不应用原动画"
            //     的 PHASE14 要求冲突。掩码只含 Root+LeftLeg+RightLeg,上半身不受影响。)
            int masked = EnsureLowerBodyMask(ctrl);

            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
            Debug.Log($"[LocoTuner] 完成:LocoSpeed 参数={(hasParam ? "已存在" : "新增")}," +
                      $"移动状态接速率={speedWired},Idle 子项替换={replaced},下半身掩码=已确保({masked} 层)。");
        }

        /// <summary>
        /// 给移动层(LocomotionFull / LocomotionLower)统一挂下半身掩码,
        /// 保证上半身完全不参与移动剪辑。掩码缺失则新建(只激活 Root/双腿)。
        /// </summary>
        static int EnsureLowerBodyMask(AnimatorController ctrl)
        {
            const string maskPath = "Assets/Game/Animation/LowerBody.mask";
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath);
            if (mask == null)
            {
                mask = new AvatarMask { name = "LowerBody" };
                for (int p = 0; p < (int)AvatarMaskBodyPart.LastBodyPart; p++)
                    mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)p, false);
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Root, true);
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftLeg, true);
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightLeg, true);
                AssetDatabase.CreateAsset(mask, maskPath);
            }

            int n = 0;
            for (int i = 0; i < ctrl.layers.Length; i++)
            {
                string ln = ctrl.layers[i].name;
                if (ln != "LocomotionFull" && ln != "LocomotionLower") continue;
                if (ctrl.layers[i].avatarMask == mask) { n++; continue; }
                ctrl.layers[i].avatarMask = mask;
                n++;
            }
            return n;
        }

        /// <summary>把混合树中位于(或极靠近)(0,0)的 Idle 子项替换为前向走路剪辑。</summary>
        static int RemoveIdle(BlendTree bt)
        {
            int n = 0;
            // 先找该树里的“前向走路”参考剪辑(位置 y>0 且 |x| 小)
            Motion walkFwd = null;
            foreach (var c in bt.children)
            {
                if (c.motion != null && c.position.sqrMagnitude > 0.01f &&
                    Mathf.Abs(c.position.x) < 0.01f && c.position.y > 0f)
                { walkFwd = c.motion; break; }
            }
            if (walkFwd == null && bt.children.Length > 1) walkFwd = bt.children[1].motion;
            if (walkFwd == null) return 0;

            var children = bt.children;
            for (int i = 0; i < children.Length; i++)
            {
                var c = children[i];
                if (c.position.sqrMagnitude > 0.01f) continue;      // 只处理 (0,0)
                if (c.motion == walkFwd) continue;                  // 已是走路
                c.motion = walkFwd;                                 // 替换掉 Idle
                children[i] = c;
                n++;
            }
            bt.children = children;
            return n;
        }
    }
}
