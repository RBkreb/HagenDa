using System;
using System.Collections;
using HagenDa.Networking;
using Mirror;
using UnityEngine;

namespace HagenDa.Tests.PlayMode.Harness
{
    /// <summary>
    /// 测试用玩家实体工厂。
    ///
    /// 为什么在代码里搭而不是加载预制体：PlayMode 测试程序集不限制平台，因此拿不到
    /// <c>AssetDatabase</c>；而且预制体的组件依赖（standCollider/crouchCollider/gun/equipment）
    /// 必须显式接线，代码搭建更可控、更能暴露装配错误。
    ///
    /// 关键装配顺序（踩过坑，不能改）：
    /// 1. 先 <c>SetActive(false)</c> —— 否则 <c>NetworkIdentity.Awake</c> 会在只加了 identity
    ///    时立刻运行，把 NetworkBehaviour 缓存建成空表，之后 <c>OnStartServer</c> 就永远
    ///    不会调用到后续组件（表现为 <c>NetworkBehaviours</c> 为 null 的 NRE）。
    /// 2. 失活状态下加完**所有**组件并接线。
    /// 3. 最后 <c>NetworkServer.Spawn</c> —— 它内部会 <c>SetActive(true)</c>，此时 Awake 才运行，
    ///    Mirror 才能一次性看到全部 NetworkBehaviour。
    ///
    /// 组件清单来自 <c>EditorPrefabFactory</c> 生成的 NetworkPlayer 预制体（取其最小可用子集）。
    /// </summary>
    public sealed class PlayerFixture
    {
        public GameObject Go;
        public NetworkIdentity Identity;
        public Rigidbody Body;
        public CapsuleCollider StandCollider;
        public CapsuleCollider CrouchCollider;

        public NetworkPlayerController Controller;
        public NetworkPlayerHealth Health;
        public NetworkCombatant Combatant;
        public NetworkGun Gun;
        public NetworkEquipment Equipment;

        /// <summary>可选：附加 AI 宿主（测 SetIntent → SetServerInput 同一通路时用）。</summary>
        public NetworkAIController AI;

        public GameObject Floor;

        /// <summary>默认站立用的胶囊尺寸（与预制体一致）。</summary>
        private const float StandHeight = 1.8f;
        private const float StandRadius = 0.25f;
        private const float CrouchHeight = 0.9f;

        /// <summary>
        /// 创建并生成一个服务端玩家实体。<paramref name="withAI"/> 为 true 时同时挂
        /// <see cref="NetworkAIController"/>（此时它会成为该身体的输入生产者）。
        /// </summary>
        public static PlayerFixture Create(Vector3 position = default, bool withAI = false,
                                           bool withFloor = true,
                                           HumanTeamPolicy teamPolicyOnNewManager = HumanTeamPolicy.AllRed)
        {
            var fx = new PlayerFixture();

            fx.Go = new GameObject("TestPlayer");
            fx.Go.SetActive(false);                       // 步骤 1：必须先失活
            fx.Go.transform.position = position;

            // 步骤 2：加全部组件（顺序：identity → 物理 → 行为）
            fx.Identity = fx.Go.AddComponent<NetworkIdentity>();

            fx.Body = fx.Go.AddComponent<Rigidbody>();
            fx.Body.freezeRotation = true;
            fx.Body.useGravity = false;                   // 控制器手动施加重力
            fx.Body.interpolation = RigidbodyInterpolation.None;

            fx.StandCollider = fx.Go.AddComponent<CapsuleCollider>();
            fx.StandCollider.direction = 1;
            fx.StandCollider.height = StandHeight;
            fx.StandCollider.radius = StandRadius;
            fx.StandCollider.center = new Vector3(0f, StandHeight * 0.5f, 0f);

            fx.CrouchCollider = fx.Go.AddComponent<CapsuleCollider>();
            fx.CrouchCollider.direction = 1;
            fx.CrouchCollider.height = CrouchHeight;
            fx.CrouchCollider.radius = StandRadius;
            fx.CrouchCollider.center = new Vector3(0f, CrouchHeight * 0.5f, 0f);
            fx.CrouchCollider.enabled = false;

            fx.Gun = fx.Go.AddComponent<NetworkGun>();
            fx.Equipment = fx.Go.AddComponent<NetworkEquipment>();
            fx.Combatant = fx.Go.AddComponent<NetworkCombatant>();
            fx.Health = fx.Go.AddComponent<NetworkPlayerHealth>();
            fx.Controller = fx.Go.AddComponent<NetworkPlayerController>();

            // 显式接线：这些引用在预制体里是序列化的，代码搭建必须自己接。
            fx.Controller.standCollider = fx.StandCollider;
            fx.Controller.crouchCollider = fx.CrouchCollider;
            fx.Controller.gun = fx.Gun;
            fx.Controller.equipment = fx.Equipment;

            if (withAI)
                fx.AI = fx.Go.AddComponent<NetworkAIController>();

            if (withFloor)
                fx.Floor = CreateFloor(position);

            PlayModeServer.Spawn(fx.Go);                  // 步骤 3：生成（内部会激活）
            return fx;
        }

        /// <summary>
        /// 在实体下方铺一块足够大的地板，便于测试接地相关逻辑。
        ///
        /// 地板顶面刻意低于出生点 0.05m（而不是刚好相切）：相切时接触判定有概率不触发，
        /// 会让 grounded 相关断言变得随机。留一点间隙让它自然落下压上去更可靠。
        /// </summary>
        private static GameObject CreateFloor(Vector3 near)
        {
            var floor = new GameObject("TestFloor");
            floor.transform.position = new Vector3(near.x, near.y - 0.15f, near.z);
            var box = floor.AddComponent<BoxCollider>();
            box.size = new Vector3(50f, 0.2f, 50f);   // 顶面 = near.y - 0.05
            return floor;
        }

        /// <summary>注入输入（等价于 CmdInput / SetIntent 落到服务端的那一步）。</summary>
        public void Inject(NetworkInputState state) => Controller.SetServerInput(state);

        /// <summary>构造一个全零输入，便于只改关心的字段。</summary>
        public static NetworkInputState Input() => default;

        /// <summary>
        /// 等待实体落地。接地由 <c>OnCollisionStay</c> 驱动，需要真实物理步进。
        /// </summary>
        public IEnumerator Settle(int maxFrames = 200)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                if (Controller.Grounded) yield break;
                // 空中时控制器每 tick 施加重力；确保它被启用（本 fixture 不禁用控制器）。
                yield return new WaitForFixedUpdate();
            }
        }

        public void Dispose()
        {
            if (Go != null)
            {
                PlayModeServer.Destroy(Go);
                Go = null;
            }
            if (Floor != null)
            {
                UnityEngine.Object.Destroy(Floor);
                Floor = null;
            }
        }
    }
}
