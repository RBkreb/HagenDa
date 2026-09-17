using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace HagenDa.Animation.RigDriver.EditorTools
{
    /// <summary>
    /// PHASE14 士兵 Animator 控制器生成（AnimatorController API，不手改序列化文本）。
    ///
    /// 产物：Assets/Game/Animation/SoldierLoco.controller
    /// 层（与参考资源 NetworkSoldierLayers 同构，但**只用**八向混合树剪辑）：
    ///   L0 Base           —— 空状态（绑定姿态；站/蹲静止露出 → 下半身 T pose）
    ///   L1 LocomotionFull —— 无 mask，八向 2D 树（走环 r=3.5 / 跑环 r=7.5，16 剪辑）
    ///   L2 LocomotionLower —— 下半身 mask，同一棵树
    ///   L3 UpperActions   —— 上半身 mask，空（换弹占位；禁播八向/动作剪辑）
    ///   L4 Death          —— 无 mask，AnyState --Death--> Dead（无运动、无出边；复活 Rebind）
    /// 参数：MoveX / MoveZ / LocoSpeed（状态速度倍率，0=冻结帧）/ Death（trigger）。
    /// 剪辑源：Kevin Iglesias HumanM@Walk01_* / HumanM@Run01_* 各 8 向。
    /// </summary>
    public static class SoldierControllerBaker
    {
        const string ControllerPath = "Assets/Game/Animation/SoldierLoco.controller";
        const string LowerMaskPath = "Assets/Game/Animation/LowerBody.mask";
        const string UpperMaskPath = "Assets/Game/Animation/UpperBody.mask";

        const string WalkFolder = "Assets/ThirdParty/Kevin Iglesias/Human Animations/Animations/Male/Movement/Walk";
        const string RunFolder = "Assets/ThirdParty/Kevin Iglesias/Human Animations/Animations/Male/Movement/Run";

        // 与参考控制器一致的 2D Freeform 位置（走环 3.5 / 跑环 7.5 = 速度）。
        static readonly (string key, Vector2 walkPos, Vector2 runPos)[] Directions =
        {
            ("Forward",        new Vector2(0f, 3.5f),        new Vector2(0f, 7.5f)),
            ("ForwardRight",   new Vector2(2.4748738f, 2.4748738f),  new Vector2(5.3033008f, 5.3033008f)),
            ("Right",          new Vector2(3.5f, 0f),        new Vector2(7.5f, 0f)),
            ("BackwardRight",  new Vector2(2.4748738f, -2.4748738f), new Vector2(5.3033008f, -5.3033008f)),
            ("Backward",       new Vector2(0f, -3.5f),       new Vector2(0f, -7.5f)),
            ("BackwardLeft",   new Vector2(-2.4748738f, -2.4748738f), new Vector2(-5.3033008f, -5.3033008f)),
            ("Left",           new Vector2(-3.5f, 0f),       new Vector2(-7.5f, 0f)),
            ("ForwardLeft",    new Vector2(-2.4748738f, 2.4748738f), new Vector2(-5.3033008f, 5.3033008f)),
        };

        [MenuItem("HagenDa/SoldierAnim/Bake Soldier Controller")]
        public static void Bake()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            }

            // ---- 参数 ----
            EnsureParam(controller, "MoveX", AnimatorControllerParameterType.Float);
            EnsureParam(controller, "MoveZ", AnimatorControllerParameterType.Float);
            EnsureParam(controller, "LocoSpeed", AnimatorControllerParameterType.Float);
            EnsureParam(controller, "Death", AnimatorControllerParameterType.Trigger);

            // ---- 八向剪辑 ----
            var walk = LoadDirectionClips(WalkFolder, "Walk01");
            var run = LoadDirectionClips(RunFolder, "Run01");
            if (walk.Count < 8 || run.Count < 8)
            {
                Debug.LogError($"[SoldierBake] 八向剪辑缺失 walk={walk.Count}/8 run={run.Count}/8 —— 检查 Kevin Iglesias 目录");
                return;
            }

            // ---- L1 LocomotionFull ----
            var full = FindOrAddLayer(controller, "LocomotionFull", null);
            var fullLoco = EnsureLocoState(full.stateMachine, "TreeFull", walk, run);
            // ---- L2 LocomotionLower ----
            var lowerMask = AssetDatabase.LoadAssetAtPath<AvatarMask>(LowerMaskPath);
            var lower = FindOrAddLayer(controller, "LocomotionLower", lowerMask);
            var lowerLoco = EnsureLocoState(lower.stateMachine, "TreeLower", walk, run);
            // ---- L3 UpperActions（空，占位）----
            FindOrAddLayer(controller, "UpperActions",
                AssetDatabase.LoadAssetAtPath<AvatarMask>(UpperMaskPath));
            // ---- L4 Death（占位，无剪辑；无出边，复活 Rebind）----
            var death = FindOrAddLayer(controller, "Death", null);
            EnsureDeathStates(death.stateMachine);

            // ---- L0 Base（默认层：TPose 剪辑 = 显式绑定姿态）----
            // 不能用空状态：空状态露出模型 rest pose（Fatui 为 Blender 类蹲站姿）。
            // TPose.anim（Humanoid 肌肉剪辑）对所有 Humanoid 自动重定向 → 真 T pose，
            // 配合脚 IK 贴地即 PHASE14"站=下半身T pose"。
            var tposeClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(
                "Assets/Game/Animation/TPose.anim");
            var baseLayer = controller.layers[0];
            baseLayer.stateMachine.name = "Base";
            baseLayer.stateMachine.hideFlags = HideFlags.HideInHierarchy;
            if (baseLayer.stateMachine.states.Length == 0)
            {
                var tpose = baseLayer.stateMachine.AddState("TPose");
                tpose.motion = tposeClip;
                baseLayer.stateMachine.defaultState = tpose;
            }
            else
            {
                // 幂等：迁移既有 Base 状态（旧烘焙为空状态"Empty"→ rest pose 类蹲，替换之）。
                var st = baseLayer.stateMachine.states[0].state;
                st.name = "TPose";
                st.motion = tposeClip;
            }
            controller.layers[0] = baseLayer;

            // ---- 全状态 Write Defaults=true ----
            // WD=false 时层权重归零后骨骼残留上一剪辑最后一帧（"移动→静止"出现
            // T-pose+走路残影混合态）。WD=true 让未覆盖骨骼回到绑定值，层切换干净。
            foreach (var l in controller.layers)
                foreach (var cs in l.stateMachine.states)
                    cs.state.writeDefaultValues = true;

            // 层序整理为 Base / Full / Lower / Upper / Death（FindOrAddLayer 追加，Base 已在 0）。
            AssetDatabase.SaveAssets();
            Debug.Log($"[SoldierBake] 控制器生成完成 → {ControllerPath}\n" +
                      $"  Loco states: full={fullLoco.name} lower={lowerLoco.name}; 记得在模型 Animator 上指向本控制器（Assemble 会自动设置）。");
        }

        static void EnsureParam(AnimatorController controller, string name, AnimatorControllerParameterType type)
        {
            if (controller.parameters.Any(p => p.name == name))
                return;
            controller.AddParameter(name, type);
        }

        static AnimatorControllerLayer FindOrAddLayer(AnimatorController controller, string name, AvatarMask mask)
        {
            foreach (var l in controller.layers)
                if (l.name == name) return l;

            var layer = new AnimatorControllerLayer
            {
                name = name,
                defaultWeight = 1f,
                stateMachine = new AnimatorStateMachine
                {
                    name = name,
                    hideFlags = HideFlags.HideInHierarchy
                },
                avatarMask = mask,
                blendingMode = AnimatorLayerBlendingMode.Override,
            };
            // 状态机必须作为资产子对象保存，否则引用丢失。
            AssetDatabase.AddObjectToAsset(layer.stateMachine, controller);
            var list = new List<AnimatorControllerLayer>(controller.layers) { layer };
            controller.layers = list.ToArray();
            return layer;
        }

        static AnimatorState EnsureLocoState(AnimatorStateMachine sm, string treeName,
            Dictionary<string, AnimationClip> walk, Dictionary<string, AnimationClip> run)
        {
            // 已有 Loco 状态则清空重建（幂等）。
            AnimatorState loco = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "Loco");
            if (loco == null)
            {
                loco = sm.AddState("Loco", new Vector3(250f, 0f, 0f));
                sm.defaultState = loco;
            }
            if (loco.motion is BlendTree oldTree)
                Object.DestroyImmediate(oldTree, true);
            loco.speed = 1f;
            loco.speedParameter = "LocoSpeed";   // 0 = 冻结帧（滑铲/趴/滞空）

            var tree = new BlendTree
            {
                name = treeName,
                hideFlags = HideFlags.HideInHierarchy,
                blendType = BlendTreeType.FreeformDirectional2D,
                blendParameter = "MoveX",
                blendParameterY = "MoveZ",
            };
            var children = new List<ChildMotion>();
            foreach (var dir in Directions)
            {
                children.Add(new ChildMotion
                { motion = walk[dir.key], position = dir.walkPos, timeScale = 1f });
                children.Add(new ChildMotion
                { motion = run[dir.key], position = dir.runPos, timeScale = 1f });
            }
            tree.children = children.ToArray();
            loco.motion = tree;
            AssetDatabase.AddObjectToAsset(tree, AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath));
            return loco;
        }

        static void EnsureDeathStates(AnimatorStateMachine sm)
        {
            if (sm.states.Length == 0)
            {
                var empty = sm.AddState("Empty");
                sm.defaultState = empty;
                var dead = sm.AddState("Dead", new Vector3(250f, 0f, 0f));
                dead.motion = null;   // 无剪辑占位（PHASE14：其他资源不使用，ragdoll 后续接入）
                dead.speed = 1f;
                var any = sm.AddAnyStateTransition(dead);
                any.hasExitTime = false;
                any.duration = 0.15f;
                any.canTransitionToSelf = false;
                any.AddCondition(AnimatorConditionMode.If, 0f, "Death");
            }
        }

        static Dictionary<string, AnimationClip> LoadDirectionClips(string folder, string prefix)
        {
            var result = new Dictionary<string, AnimationClip>();
            foreach (var dir in Directions)
            {
                string fbxPath = $"{folder}/HumanM@{prefix}_{dir.key}.fbx";
                var clips = AssetDatabase.LoadAllAssetsAtPath(fbxPath)
                    .OfType<AnimationClip>()
                    .Where(c => !c.name.StartsWith("__preview__"))
                    .ToArray();
                if (clips.Length == 0)
                {
                    Debug.LogWarning($"[SoldierBake] 剪辑缺失: {fbxPath}");
                    continue;
                }
                var clip = clips.FirstOrDefault(c => c.name.Contains(dir.key)) ?? clips[0];
                result[dir.key] = clip;
            }
            return result;
        }
    }
}
