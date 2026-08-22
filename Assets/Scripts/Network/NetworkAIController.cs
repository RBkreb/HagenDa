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

            // Dead: forward zero input so stale edge-triggered flags cannot fire
            // on respawn (auto-redeploy is handled by NetworkPlayerHealth).
            controller.SetServerInput(dead ? default : intent);
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
