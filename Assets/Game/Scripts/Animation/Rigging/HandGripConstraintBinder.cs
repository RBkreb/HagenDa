using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace HagenDa.Animation.Rigging
{
    /// <summary>
    /// Binder：解析/分析手骨与胶囊并把常量搬进 Job。
    /// 分析（bind 姿态测量）逻辑集中在 HandGripAnalyzer，编辑器工具复用。
    /// </summary>
    public sealed class HandGripConstraintJobBinder : AnimationJobBinder<HandGripConstraintJob, HandGripConstraintData>
    {
        public override HandGripConstraintJob Create(Animator animator, ref HandGripConstraintData data, Component component)
        {
            var job = new HandGripConstraintJob();

            // ---- 1) 解析骨骼 ----
            if (data.rootBone == null || data.midBone == null || data.tipBone == null ||
                data.fingers == null || data.fingers.Length != 3 ||
                data.fingers[0].root == null || data.fingers[1].root == null || data.fingers[2].root == null)
            {
                bool ok = HandGripAnalyzer.ResolveSkeleton(ref data, animator);
                if (!ok)
                {
                    Debug.LogWarning("[HandGrip] 骨骼解析失败：请确认约束挂在 Natlan/Fatui 这类 DEF-* 模型上，或手动指定骨骼。", component);
                    return job; // 空 job（无效数据，RigBuilder 会跳过/提示）
                }
            }

            // ---- 2) 需要时做 bind 姿态分析（编辑器工具已分析则跳过）----
            if (!data.analyzed)
                HandGripAnalyzer.AnalyzePose(ref data);

            // ---- 3) 绑定句柄 ----
            job.armRoot = ReadWriteTransformHandle.Bind(animator, data.rootBone);
            job.armMid = ReadWriteTransformHandle.Bind(animator, data.midBone);
            job.wrist = ReadWriteTransformHandle.Bind(animator, data.tipBone);
            if (data.grip != null)
                job.capsule = ReadOnlyTransformHandle.Bind(animator, data.grip.transform);

            job.chain0 = MakeChain(animator, ref data.fingers[0]);
            job.chain1 = MakeChain(animator, ref data.fingers[1]);
            job.chain2 = MakeChain(animator, ref data.fingers[2]);

            // ---- 4) 常量 ----
            job.axisLocal = data.axisLocal.sqrMagnitude > 0.01f ? data.axisLocal.normalized : Vector3.up;
            job.azLocal = data.azLocal.sqrMagnitude > 0.01f ? data.azLocal.normalized : Vector3.right;
            job.hingeLocal = data.hingeLocal;
            job.backLocal = data.backLocal;
            job.capRadius = data.grip != null ? data.grip.radius : 0.02f;
            job.capPerpScale = data.capPerpScale > 0.01f ? data.capPerpScale : 1f;
            job.axialOffset = data.axialOffset;
            job.reachBase = data.reachBase;
            job.anchorLen = data.anchorLen;
            job.reachFrac = data.reachFrac;
            job.curl = data.curl;
            job.autoCurl = data.autoCurl;
            job.gripRadiusFrac = data.gripRadiusFrac > 0.01f ? data.gripRadiusFrac : 0.42f;
            job.gripPalmFrac = data.gripPalmFrac > 0.01f ? data.gripPalmFrac : 0.5f;
            job.gripReleaseFrac = data.gripReleaseFrac > 0.01f ? data.gripReleaseFrac : 1f;
            job.twistDeg = data.twistDeg;
            job.armIK = data.armIK;
            job.armLength = data.armLength;
            job.useAnchorRot = data.useAnchorRot;
            if (data.wristAnchor != null)
            {
                job.anchor = ReadOnlyTransformHandle.Bind(animator, data.wristAnchor);
                job.hasAnchor = true;
            }
            if (data.gripDriver != null)
            {
                job.driver = ReadWriteTransformHandle.Bind(animator, data.gripDriver);
                job.hasDriver = true;
            }
            job.driverMaxAngle = data.driverMaxAngle > 0.5f ? data.driverMaxAngle : 90f;
            job.sideSign = data.side == HandGripSide.Left ? -1f : 1f;
            // 握持权重(之前缺失:SetHandsWeight 对握指不生效的隐患修复)
            job.jobWeight = FloatProperty.Bind(animator, component, "m_Weight");
            return job;
        }

        public override void Destroy(HandGripConstraintJob job) { }

        static HandGripChainJob MakeChain(Animator animator, ref HandGripFinger f)
        {
            var c = new HandGripChainJob();
            c.root = f.root != null ? ReadWriteTransformHandle.Bind(animator, f.root) : default;
            c.mid = f.mid != null ? ReadWriteTransformHandle.Bind(animator, f.mid) : default;
            c.axisA = f.axisA;
            c.axisB = f.axisB;
            c.flexA = f.flexA;
            c.flexB = f.flexB;
            c.infA = f.infA;
            c.infB = f.infB;
            c.sgnA = f.sgnA;
            c.sgnB = f.sgnB;
            c.role = f.role;
            c.q01Rel = f.q01Rel;
            c.q12Rel = f.q12Rel;
            return c;
        }
    }

    /// <summary>
    /// 静态分析器：骨骼名称解析 + bind 姿态几何测量（编辑器工具与 Binder 共用）。
    /// </summary>
    public static class HandGripAnalyzer
    {
        // 名称约定（大小写不敏感、可含任意前缀；只要求包含关键词并带 .L/.R 侧）
        static readonly string[] kArmTokens = { "upper_arm", "upperarm", "arm_up", "arm.upper" };
        static readonly string[] kForearmTokens = { "forearm", "lower_arm", "arm_low" };
        static readonly string[] kHandTokens = { "hand" };

        /// <summary>名称是否含关键词。</summary>
        static bool NameHit(string name, string[] tokens)
        {
            string n = name.ToLowerInvariant();
            for (int i = 0; i < tokens.Length; ++i)
                if (n.Contains(tokens[i])) return true;
            return false;
        }

        /// <summary>取名称尾部 .L / .R（也兼容 "_L"）。</summary>
        static char SideChar(string name)
        {
            int dot = name.LastIndexOf('.');
            int us = name.LastIndexOf('_');
            int at = Mathf.Max(dot, us);
            if (at >= 0 && at < name.Length - 1)
            {
                char c = name[at + 1];
                if (c == 'L' || c == 'l') return 'L';
                if (c == 'R' || c == 'r') return 'R';
            }
            return '?';
        }

        public static Transform Find(Transform root, System.Func<Transform, bool> match, int depth = 0)
        {
            if (depth > 48 || root == null) return null;
            if (match(root)) return root;
            for (int i = 0; i < root.childCount; ++i)
            {
                var r = Find(root.GetChild(i), match, depth + 1);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>
        /// 按名称解析骨架引用并写入 data（幂等）。失败返回 false。
        /// </summary>
        public static bool ResolveSkeleton(ref HandGripConstraintData data, Animator animator)
        {
            Transform scope = data.armatureRoot != null ? data.armatureRoot : (animator != null ? animator.transform : null);
            if (scope == null) return false;

            // 手骨：先按显式 side 查找 "DEF-hand.L/R"；Auto 时找第一个 DEF-hand.*
            char side = '?';
            if (data.side == HandGripSide.Left) side = 'L';
            else if (data.side == HandGripSide.Right) side = 'R';

            Transform hand = null;
            if (side != '?')
                hand = Find(scope, t => NameHit(t.name, kHandTokens) && SideChar(t.name) == side);
            else
            {
                hand = Find(scope, t => NameHit(t.name, kHandTokens) && (SideChar(t.name) == 'L' || SideChar(t.name) == 'R'));
                if (hand != null) side = SideChar(hand.name);
            }
            if (hand == null) return false;

            if (data.tipBone == null) data.tipBone = hand;
            char want = SideChar(hand.name);

            // 臂链：DEF-upper_arm.* / DEF-forearm.*（跳过 .001 中间骨，与既有 rig 一致）
            if (data.rootBone == null)
                data.rootBone = Find(scope, t => NameHit(t.name, kArmTokens) && !t.name.ToLowerInvariant().Contains(".001") && SideChar(t.name) == want && ContainsHandDescendant(t, hand));
            if (data.midBone == null)
                data.midBone = Find(scope, t => NameHit(t.name, kForearmTokens) && !t.name.ToLowerInvariant().Contains(".001") && SideChar(t.name) == want && ContainsHandDescendant(t, hand));
            if (data.rootBone == null || data.midBone == null) return false;
            if (data.armatureRoot == null)
                data.armatureRoot = scope;

            // 指链：hand 直接子级中按组关键词找 .01，再向下 .02 / end
            if (data.fingers == null || data.fingers.Length != 3) data.fingers = new HandGripFinger[3];
            for (int g = 0; g < 3; ++g)
            {
                ref var f = ref data.fingers[g];
                if (f.root == null)
                {
                    Transform j1 = Find(hand, t => t.parent == hand && NameHit(t.name, FingerTokens(g)) && SideChar(t.name) == want);
                    if (j1 == null)
                    {
                        // 兜底：关键词匹配但没侧后缀
                        j1 = Find(hand, t => t.parent == hand && NameHit(t.name, FingerTokens(g)));
                    }
                    if (j1 == null) return false;
                    f.root = j1;
                }
                if (f.mid == null)
                {
                    f.mid = Find(f.root, t => t.name.ToLowerInvariant().Contains("02") && SideChar(t.name) == want);
                    if (f.mid == null && f.root.childCount > 0) f.mid = f.root.GetChild(0);
                }
                if (f.mid == null) return false;
                f.role = g;
                f.infA = f.infB = f.sgnA = f.sgnB = 1f;
            }
            if (data.side == HandGripSide.Auto)
                data.side = side == 'L' ? HandGripSide.Left : HandGripSide.Right;
            return true;
        }

        static bool ContainsHandDescendant(Transform root, Transform hand)
        {
            return root == hand || Find(root, t => t == hand) != null;
        }

        static string[] FingerTokens(int g)
        {
            switch (g)
            {
                case 0: return new[] { "f_index", "index" };
                case 1: return new[] { "f_middle", "middle" };
                default: return new[] { "thumb" };
            }
        }

        /// <summary>
        /// bind 姿态分析：要求骨架当前处于 rest/bind（编辑器工具请先确保）。
        /// 写入 data 的分析常量并置 analyzed=true。
        /// </summary>
        public static void AnalyzePose(ref HandGripConstraintData data)
        {
            Transform hand = data.tipBone;
            CapsuleCollider cap = data.grip;
            if (hand == null || cap == null) return;

            // 调参默认值（仅在未设置时写入，保留 Inspector 手改）
            if (data.gripRadiusFrac <= 0.01f) data.gripRadiusFrac = 0.42f;
            if (data.gripPalmFrac <= 0.01f) data.gripPalmFrac = 0.5f;
            if (data.gripReleaseFrac <= 0.01f) data.gripReleaseFrac = 1f;
            if (data.thumbXBias <= 0f) data.thumbXBias = 0.8f;

            Vector3 hw = hand.position;

            // 指根环几何（reachBase：腕->指根平均距离，米）
            int cnt = 0;
            float baseLenSum = 0f;
            for (int i = 0; i < 3; ++i)
            {
                ref var f = ref data.fingers[i];
                f.len1 = Vector3.Distance(f.root.position, f.mid.position);
                if (i < 2)
                {
                    baseLenSum += Vector3.Distance(hw, f.root.position);
                    cnt++;
                }
            }

            // ---- 掌面坐标系 & 逐指链自然弯曲轴（bind 姿态，解析求轴）----
            // 弯曲轴 = 指节指向 × 掌心法线：落在掌平面内、⊥指节，绕它正向旋转
            // 即“收拢”。旧实现只在骨局部 ±X/±Y/±Z 六个主轴里试 90° 找离掌心
            // 最近者——本模型真轴是局部 XZ 平面内的斜向（如 0.98,0,0.22），六个
            // 主轴一个都命中不了，会挑到近似滚转/侧摆的轴（实测与真轴差 58°~78°，
            // 朝掌心分量为 ~0），手指因此侧摆或反向弯曲。解析求轴左右手同时正确。
            {
                Vector3 palmar = PalmNormalWorld(hand, data.fingers);
                for (int i = 0; i < data.fingers.Length; ++i)
                {
                    ref var f = ref data.fingers[i];
                    if (f.root == null || f.mid == null) continue;
                    // 满握角仅在未设置时写默认值（保留用户 Inspector 调参）
                    f.axisA = HingeAxis(f.root, f.mid.position, palmar);
                    if (f.flexA <= 0.5f) f.flexA = RoleDeg(i, 0);
                    // .02 末端指向优先取真实子骨（DEF-*.02_end），缺失时按 .01 方向外推
                    Vector3 probeB = f.mid.childCount > 0
                        ? f.mid.GetChild(0).position
                        : f.mid.position + (f.mid.position - f.root.position).normalized * f.len1;
                    f.axisB = HingeAxis(f.mid, probeB, palmar);
                    if (f.flexB <= 0.5f) f.flexB = RoleDeg(i, 1);

                    // 拇指：弯曲轴偏向手骨局部 X。Humanoid 肌肉只对拇指中/远节在
                    // 局部 X 方向开屈曲自由度，绕本模型解析出的斜向轴（≈局部Z）
                    // 弯曲会被钳制到 ~1/4；掌心是一个面，绕 X 同样朝掌心收拢，
                    // 且能通过肌肉限制。拇指两节都做同样偏置，保持轴向一致。
                    if (i == 2)
                    {
                        f.axisA = BiasToLocalX(f.root, f.axisA, palmar, data.thumbXBias);
                        f.axisB = BiasToLocalX(f.mid, f.axisB, palmar, data.thumbXBias);
                    }

                    // copy rotation 基准：bind 局部姿态
                    f.q01Rel = f.root.localRotation;
                    f.q12Rel = f.mid.localRotation;

                    if (f.infA <= 0f) f.infA = 1f;
                    if (f.infB <= 0f) f.infB = 1f;
                    if (f.sgnA == 0f) f.sgnA = 1f;
                    if (f.sgnB == 0f) f.sgnB = 1f;
                }
            }

            // 弯轴与手背法线：使用手骨局部轴向的解析约定（对 Natlan/Fatui 这类
            // Blender DEF-* 模型：+Z 横贯掌为弯轴、+X 为手背方向；左右手局部系
            // 互为镜像，用同一局部符号即自动镜像。若某模型相反，用 data.sign
            // 翻转向量方向或 twistDeg 微调。）
            data.hingeLocal = new Vector3(0f, 0f, 1f);
            data.backLocal = new Vector3(1f, 0f, 0f);

            data.reachBase = cnt > 0 ? baseLenSum / cnt : 0.09f;
            data.anchorLen = data.fingers.Length > 0 ? data.fingers[0].len1 : 0.045f;
            if (data.rootBone != null)
                data.armLength = Vector3.Distance(data.rootBone.position, data.tipBone.position);
            data.capPerpScale = PerpScale(cap);

            // 胶囊轴向 / az（轴->手背 的径向，胶囊局部）
            Vector3 axisW = cap.transform.rotation * LocalAxis(cap.direction);
            axisW.Normalize();
            Vector3 d = hw - cap.transform.position;
            d = d - axisW * Vector3.Dot(d, axisW);
            data.axisLocal = LocalAxis(cap.direction);
            if (d.sqrMagnitude > 1e-6f)
            {
                Vector3 dL = cap.transform.InverseTransformDirection(d);
                dL = dL - data.axisLocal * Vector3.Dot(dL, data.axisLocal);
                if (dL.sqrMagnitude > 1e-6f) data.azLocal = dL.normalized;
            }
            else data.azLocal = LocalAxis(cap.direction) == Vector3.up ? Vector3.right : Vector3.up;

            // 轴向偏移：胶囊中心即“握点”（手掌所在高度）；轴向沿轴调节用
            // data.axialOffset 显式提供，不自动从 bind 腕投影推导。
            data.axialOffset = 0f;

            data.analyzed = true;
        }

        static Vector3 LocalAxis(int dir)
        {
            return dir == 0 ? Vector3.right : (dir == 1 ? Vector3.up : Vector3.forward);
        }

        static float PerpScale(CapsuleCollider cap)
        {
            // 取垂直于胶囊轴那一对分量的最大缩放（半径方向缩放）。旧的实现把
            // 轴向分量当成 1 再取 max，导致只要有任一轴缩放≥1 就恒返回 1，
            // 半径被高估（实测 0.2 vs 实际 0.024），gripMax 因此被压到 ~0.15。
            Vector3 ls = cap.transform.lossyScale;
            Vector3 ax = LocalAxis(cap.direction);
            float px = Mathf.Abs(ax.x) > 0.5f ? 0f : Mathf.Abs(ls.x);
            float py = Mathf.Abs(ax.y) > 0.5f ? 0f : Mathf.Abs(ls.y);
            float pz = Mathf.Abs(ax.z) > 0.5f ? 0f : Mathf.Abs(ls.z);
            float perp = Mathf.Max(px, Mathf.Max(py, pz));
            return perp > 1e-4f ? perp : 1f;
        }

        /// <summary>默认收拢角（度）。(role, jointIdx) -> .01/.02。拇指天然更少。</summary>
        static float RoleDeg(int role, int jointIdx)
        {
            if (role == 2) return jointIdx == 0 ? 60f : 52f;
            if (role == 0) return jointIdx == 0 ? 85f : 78f;
            return jointIdx == 0 ? 92f : 84f;
        }

        /// <summary>
        /// 掌心法线（世界坐标，指向掌心一侧）。以手骨局部 +Y 为指向前方；手掌
        /// 厚度轴由手骨局部 X / Z 中与指根展向更垂直者确定（掌厚轴 ⊥ 展向）；符号
        /// 用拇指指节方向判定——拇指对掌，其指节天然偏向掌心，左右手因此各自
        /// 得到正确的掌心朝向（本模型实测：左手掌心 = -局部X，右手 = +局部X）。
        /// </summary>
        static Vector3 PalmNormalWorld(Transform hand, HandGripFinger[] fingers)
        {
            Vector3 xW = (hand.rotation * Vector3.right).normalized;
            Vector3 zW = (hand.rotation * Vector3.forward).normalized;

            // 指根展向（食指指根 - 中指组指根）：区分“掌厚轴 / 掌宽轴”
            Vector3 spread = Vector3.zero;
            if (fingers != null && fingers.Length > 1 &&
                fingers[0].root != null && fingers[1].root != null)
                spread = fingers[0].root.position - fingers[1].root.position;

            Vector3 palmAxis = xW;
            if (spread.sqrMagnitude > 1e-10f)
            {
                spread.Normalize();
                palmAxis = Mathf.Abs(Vector3.Dot(xW, spread)) <= Mathf.Abs(Vector3.Dot(zW, spread))
                    ? xW : zW;
            }

            // 符号：拇指指节方向（缺失则退回食指）在掌厚轴上的投影，指向掌心者为正
            Vector3 cue = Vector3.zero;
            if (fingers != null && fingers.Length > 2 &&
                fingers[2].root != null && fingers[2].mid != null)
                cue = fingers[2].mid.position - fingers[2].root.position;
            if (cue.sqrMagnitude < 1e-10f && fingers != null && fingers.Length > 0 &&
                fingers[0].root != null && fingers[0].mid != null)
                cue = fingers[0].mid.position - fingers[0].root.position;

            return Vector3.Dot(cue, palmAxis) < 0f ? -palmAxis : palmAxis;
        }

        /// <summary>
        /// 单关节弯曲轴（返回骨自身局部坐标）：绕该轴正角旋转 = 指节向掌心收拢。
        /// axis = 指节指向 × 掌心法线（⊥指节、落在掌平面内的“掌横轴”），
        /// 再校正符号使正角旋转的速度方向指向掌心。左右手由掌心法线符号区分。
        /// </summary>
        static Vector3 HingeAxis(Transform bone, Vector3 childPos, Vector3 palmarW)
        {
            Vector3 rel = childPos - bone.position;
            if (rel.sqrMagnitude < 1e-10f) return Vector3.right;
            Vector3 relDir = rel.normalized;

            // 掌心法线中垂直于指节的分量（去掉指节自身的掌心分量，保证轴 ⊥ 指节）
            Vector3 pal = palmarW - relDir * Vector3.Dot(palmarW, relDir);
            Vector3 hingeW = Vector3.Cross(relDir, pal);
            if (hingeW.sqrMagnitude < 1e-10f)
            {
                hingeW = Vector3.Cross(relDir, Vector3.up);
                if (hingeW.sqrMagnitude < 1e-8f) hingeW = Vector3.Cross(relDir, Vector3.forward);
            }
            hingeW.Normalize();

            // 正角旋转速度 dir = axis × rel，必须指向掌心一侧；否则翻轴
            if (Vector3.Dot(Vector3.Cross(hingeW, relDir), palmarW) < 0f) hingeW = -hingeW;

            return (Quaternion.Inverse(bone.rotation) * hingeW).normalized;
        }

        /// <summary>
        /// 把弯曲轴（骨局部坐标）朝手骨局部 X 靠拢，同时保持“正角=朝掌心”的符号。
        /// Humanoid 肌肉只为拇指在局部 X 方向提供屈曲自由度，绕本模型解析出的
        /// 斜向轴（≈局部 Z）弯曲会被钳制；掌心是一个面，绕 X 同样能把指节收向
        /// 掌面。bias∈0..1：0=完全用解析轴，1=完全用局部 X（取朝掌心那个符号）。
        /// 偏置后再移除 X 分量方向上的滚转成分（轴必须 ⊥ 该节的指节方向）。
        /// </summary>
        static Vector3 BiasToLocalX(Transform bone, Vector3 axisLocal, Vector3 palmarW, float bias)
        {
            if (bias <= 0.001f) return axisLocal;

            Transform child = bone.childCount > 0 ? bone.GetChild(0) : null;
            Vector3 relDirW = child != null
                ? (child.position - bone.position).normalized
                : (bone.rotation * Vector3.up);
            if (relDirW.sqrMagnitude < 1e-8f) return axisLocal;
            Vector3 relDirL = (Quaternion.Inverse(bone.rotation) * relDirW).normalized;

            // 骨局部 ±X：取绕它旋转时指向掌心的那个符号（世界叉积判符号）
            Vector3 xL = Vector3.right;
            Vector3 xW = bone.rotation * xL;
            if (Vector3.Dot(Vector3.Cross(xW, relDirW), palmarW) < 0f) xL = -xL;

            // 局部 X 去滚转（⊥ 指节）后与解析轴按 bias 混合
            Vector3 xPure = (xL - relDirL * Vector3.Dot(xL, relDirL)).normalized;
            Vector3 aPure = (axisLocal - relDirL * Vector3.Dot(axisLocal, relDirL)).normalized;
            if (xPure.sqrMagnitude < 1e-8f) return axisLocal;
            if (aPure.sqrMagnitude < 1e-8f) return xPure;

            Vector3 blended = Vector3.Slerp(aPure, xPure, Mathf.Clamp01(bias));
            // 保号：与解析轴同向（避免偏置过程翻向）
            if (Vector3.Dot(blended, aPure) < 0f) blended = -blended;
            return blended.normalized;
        }
    }
}
