using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-side AI host (ML-branch). Contains NO built-in behavior: it neither
    /// moves toward objectives nor engages enemies on its own. The body is a
    /// standard <see cref="NetworkPlayerController"/> whose
    /// <see cref="NetworkInputState"/> is written server-side every tick via
    /// <see cref="NetworkPlayerController.SetServerInput"/> — the exact same path
    /// a human client drives through CmdInput. An external driver (ML-agent
    /// policy, debug tools) steers the AI through <see cref="SetIntent"/>; with no
    /// driver the intent stays zero and the AI stands idle.
    ///
    /// Remaining responsibilities: per-team body colour (visual identity) and the
    /// death / redeploy hooks <see cref="NetworkPlayerHealth"/> calls (posture,
    /// momentum and collider state are owned by the player controller).
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkAIController : NetworkBehaviour
    {
        [Header("References")]
        public NetworkPlayerController controller;
        public GameObject visual;       // upright capsule mesh (Body)

        // Current driver intent, pushed to the controller every server tick
        // (mirrors a client sampling input each frame). Edge-triggered flags
        // (jump / reload / slot keys) must be set on the tick they fire and
        // cleared afterwards — same contract as the human input path.
        private NetworkInputState intent;
        private bool dead;
        private HagenDa.Soldier.SoldierAnimatorDriver soldierAnimator;   // PHASE14: 存在时跳过胶囊姿态模仿

        public override void OnStartServer()
        {
            if (controller == null)
                controller = GetComponent<NetworkPlayerController>();

            // PHASE7: 阵营分配后染色（延迟一帧等 NetworkMatchManager 分配 teamId）。
            Invoke(nameof(ApplyTeamColor), 0.2f);
        }

        private void FixedUpdate()
        {
            if (!isServer || controller == null) return;

            // 脚本陪练共存时让行：ScriptedAIController 自己驱动身体。
            if (GetComponent<ScriptedAIController>() != null) return;

            // Dead: forward zero input so stale edge-triggered flags cannot fire
            // on respawn (auto-redeploy is handled by NetworkPlayerHealth).
            if (dead)
            {
                controller.SetServerInput(default);
                UpdateVisualPosture();   // PHASE9: 死亡时也同步视觉趴姿
                return;
            }

            // Push the current intent ONCE, then auto-clear edge-triggered flags
            // (jump / toggles / reload / mark / slot keys / deploy choice). The
            // driver (ML policy) sets edges at its decision rate (6 Hz) and holds
            // continuous fields (move/look/fire/aim/sprint) between decisions —
            // identical contract to the human client path, where edges fire once
            // per press.
            var s = intent;
            controller.SetServerInput(s);

            intent.jump = false;
            intent.crouchToggle = false;
            intent.proneToggle = false;
            intent.reload = false;
            intent.switchFireMode = false;
            intent.mark = false;
            intent.slotPrimary = false;
            intent.slotOpt1 = false;
            intent.slotOpt2 = false;
            intent.slotSpecial = false;
            intent.slotThrowable = false;
            intent.deployChoice = 0;

            // PHASE9: 视觉姿态同步——AI 的 Body 胶囊网格随姿态旋转/缩放，
            // 否则趴姿/蹲姿只改了 collider 但视觉上仍是站立胶囊。
            UpdateVisualPosture();
        }

        private void UpdateVisualPosture()
        {
            if (visual == null || controller == null) return;

            // PHASE12: 士兵模型存在时，姿态动画由 SoldierAnimatorDriver 驱动，
            // 不再对胶囊网格做旋转/缩放模仿（模型是根的子对象，动了会错位）。
            if (soldierAnimator == null) soldierAnimator = GetComponent<HagenDa.Soldier.SoldierAnimatorDriver>();
            if (soldierAnimator != null) return;

            switch (controller.posture)
            {
                case PlayerPosture.Prone:
                    visual.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    visual.transform.localPosition = new Vector3(0f, 0.25f, 0f);
                    visual.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
                    break;
                case PlayerPosture.Crouch:
                    visual.transform.localRotation = Quaternion.identity;
                    visual.transform.localPosition = new Vector3(0f, 0.45f, 0f);
                    visual.transform.localScale = new Vector3(0.5f, 0.45f, 0.5f);
                    break;
                default:
                    visual.transform.localRotation = Quaternion.identity;
                    visual.transform.localPosition = new Vector3(0f, 0.9f, 0f);
                    visual.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
                    break;
            }
        }

        /// <summary>
        /// Server-side driver entry point (ML policy / debug tool). Set the full
        /// intent every tick, exactly like a client resends its input snapshot.
        /// </summary>
        [Server]
        public void SetIntent(NetworkInputState s) => intent = s;

        /// <summary>
        /// PHASE7: 根据 teamId 染色 AI 身体（红方红 / 蓝方蓝）。
        /// 同小队成员染绿色（仅对人类玩家的小队成员生效，由 PlayerHud 客户端覆写）。
        /// </summary>
        [Server]
        public void ApplyTeamColor()
        {
            var c = GetComponent<NetworkCombatant>();
            int team = c != null ? c.teamId : -1;

            Color color = team == (int)MatchTeam.Blue
                ? new Color(0.15f, 0.3f, 0.8f)
                : new Color(0.8f, 0.15f, 0.15f);

            if (visual != null)
            {
                var rend = visual.GetComponent<Renderer>();
                if (rend != null)
                {
                    // 用共享材质实例避免每次 new（运行时非持久化即可）。
                    var mat = new Material(rend.material);
                    mat.color = color;
                    rend.material = mat;
                }
            }

            RpcApplyTeamColor(team);
        }

        [ClientRpc]
        private void RpcApplyTeamColor(int team)
        {
            Color color = team == (int)MatchTeam.Blue
                ? new Color(0.15f, 0.3f, 0.8f)
                : new Color(0.8f, 0.15f, 0.15f);

            if (visual != null)
            {
                var rend = visual.GetComponent<Renderer>();
                if (rend != null)
                {
                    var mat = new Material(rend.material);
                    mat.color = color;
                    rend.material = mat;
                }
            }

            // 客户端：如果是本方小队成员，染绿色以标识同小队。
            if (NetworkClient.connection != null && NetworkClient.connection.identity != null)
            {
                var localCombatant = NetworkClient.connection.identity.GetComponent<NetworkCombatant>();
                var myCombatant = GetComponent<NetworkCombatant>();
                if (localCombatant != null && myCombatant != null &&
                    localCombatant.teamId == myCombatant.teamId &&
                    localCombatant.squadId == myCombatant.squadId &&
                    localCombatant != myCombatant)
                {
                    if (visual != null)
                    {
                        var rend2 = visual.GetComponent<Renderer>();
                        if (rend2 != null)
                            rend2.material.color = new Color(0.2f, 0.8f, 0.3f);
                    }
                }
            }
        }

        /// <summary>
        /// Death / rescue hook from <see cref="NetworkPlayerHealth"/>. Posture,
        /// momentum and collider state are handled by the player controller's own
        /// SetDead; here we only gate input forwarding.
        /// </summary>
        public void SetDead(bool value)
        {
            dead = value;
        }

        /// <summary>Redeploy hook: clear driver state so stale edge flags die with the old body.</summary>
        public void OnRedeploy(Vector3 pos)
        {
            dead = false;
            intent = default;
            ApplyTeamColor();
        }
    }
}
