using UnityEditor;
using UnityEngine;

namespace HagenDa.EditorTools
{
    /// <summary>
    /// 为 nailong 生成人形动画剪辑(肌肉曲线)。
    ///   CreateIdlePose / SaveIdlePoseAsset -> 端枪站立基准
    /// </summary>
    public static class NailongClipGen
    {
        const float FPS = 30f;

        static void Muscle(AnimationClip c, string m, float v)
        {
            var b = new EditorCurveBinding { path = "", type = typeof(Animator), propertyName = m };
            var k0 = new Keyframe(0f, v); k0.outTangent = 0f;
            var k1 = new Keyframe(1f, v); k1.inTangent = 0f;
            AnimationUtility.SetEditorCurve(c, b, new AnimationCurve(k0, k1));
        }

        /// <summary>端枪站立基准。默认: 手臂微垂前送形成"端枪预备"，由 IK 精修到握点。</summary>
        public static AnimationClip CreateIdlePose(float length = 0.2f,
            float shoulderDown = 0.5f, float armForward = 0.55f, float armIn = 0.10f,
            float elbowBend = 0.35f, float spineLean = 0.12f, float kneeBend = 0.10f)
        {
            var clip = new AnimationClip { name = "IdlePose", frameRate = FPS };
            float d = Mathf.Max(0.02f, length);

            // 腿: 自然站立微屈, 脚平
            Muscle(clip, "Left Upper Leg Front-Back", 0.02f);
            Muscle(clip, "Right Upper Leg Front-Back", 0.02f);
            Muscle(clip, "Left Upper Leg In-Out", 0.0f);
            Muscle(clip, "Right Upper Leg In-Out", 0.0f);
            Muscle(clip, "Left Lower Leg Stretch", -kneeBend);
            Muscle(clip, "Right Lower Leg Stretch", -kneeBend);
            Muscle(clip, "Left Foot Up-Down", 0f);
            Muscle(clip, "Right Foot Up-Down", 0f);

            // 躯干: 微前倾 + 轻微挺胸
            Muscle(clip, "Spine Front-Back", spineLean);
            Muscle(clip, "Spine Left-Right", 0f);
            Muscle(clip, "Chest Front-Back", -0.03f);
            Muscle(clip, "Chest Left-Right", 0f);
            Muscle(clip, "UpperChest Front-Back", -0.02f);

            // 手臂: 下垂 + 前送 + 内收 + 屈肘 -> 手到胸前(握枪预备)
            Muscle(clip, "Left Shoulder Down-Up", shoulderDown);
            Muscle(clip, "Right Shoulder Down-Up", shoulderDown);
            Muscle(clip, "Left Shoulder Front-Back", armForward);
            Muscle(clip, "Right Shoulder Front-Back", armForward);
            Muscle(clip, "Left Upper Arm In-Out", -armIn);
            Muscle(clip, "Right Upper Arm In-Out", -armIn);
            Muscle(clip, "Left Upper Arm Twist In-Out", 0f);
            Muscle(clip, "Right Upper Arm Twist In-Out", 0f);
            Muscle(clip, "Left Lower Arm Stretch", -elbowBend);
            Muscle(clip, "Right Lower Arm Stretch", -elbowBend);
            Muscle(clip, "Left Lower Arm Twist In-Out", 0f);
            Muscle(clip, "Right Lower Arm Twist In-Out", 0f);

            var s = AnimationUtility.GetAnimationClipSettings(clip);
            s.loopTime = false;
            AnimationUtility.SetAnimationClipSettings(clip, s);
            return clip;
        }

        public static void SaveIdlePoseAsset(string assetPath = "Assets/Anim/Nailong_IdlePose.anim",
            float shoulderDown = 0.5f, float armForward = 0.55f, float armIn = 0.10f,
            float elbowBend = 0.35f, float spineLean = 0.12f, float kneeBend = 0.10f)
        {
            System.IO.Directory.CreateDirectory("Assets/Anim");
            var clip = CreateIdlePose(0.2f, shoulderDown, armForward, armIn, elbowBend, spineLean, kneeBend);
            AssetDatabase.CreateAsset(clip, assetPath);
            var ctrl = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>("Assets/Model/nailong/nailong.controller");
            if (ctrl != null)
            {
                var st = ctrl.layers[0].stateMachine.states[0].state;
                st.name = "IdlePose";
                st.motion = clip;
                EditorUtility.SetDirty(ctrl);
                AssetDatabase.SaveAssets();
            }
        }
    }
}
