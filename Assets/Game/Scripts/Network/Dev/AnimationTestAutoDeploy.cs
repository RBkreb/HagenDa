using System.Collections;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// AnimationTest 场景专用：出生后自动走真实部署流程（CmdDeploy），
    /// 跳过部署界面 —— 动画测试场景没有对局管理器，部署点由
    /// <see cref="NetworkPlayerHealth.ResolveDeployPoint"/> 的无对局回退
    /// 解析为场景出生点。配装从实体装备列表按类别自动挑选
    /// （可选1/可选2 取前两个不同项，特有/投掷物取第一项）。
    /// </summary>
    public class AnimationTestAutoDeploy : MonoBehaviour
    {
        [Tooltip("进入部署状态后再等待的秒数（等待同步与装备就绪）。")]
        public float delay = 1.0f;

        private IEnumerator Start()
        {
            float t = 0f;
            while (NetworkClient.localPlayer == null && t < 15f)
            {
                t += Time.deltaTime;
                yield return null;
            }
            if (NetworkClient.localPlayer == null) yield break;

            var health = NetworkClient.localPlayer.GetComponent<NetworkPlayerHealth>();
            var equipment = NetworkClient.localPlayer.GetComponent<NetworkEquipment>();
            if (health == null || equipment == null) yield break;

            // 等待 OnStartServer 置位首次部署状态。
            t = 0f;
            while (!health.awaitingInitialDeploy && t < 5f)
            {
                t += Time.deltaTime;
                yield return null;
            }

            if (delay > 0f) yield return new WaitForSeconds(delay);

            var loadout = BuildLoadout(equipment);
            if (loadout == null)
            {
                Debug.LogWarning("[AutoDeploy] 装备列表缺少类别项，无法自动部署 —— 请在部署界面手动选择");
                yield break;
            }

            // host 上 Command 在本地服务端执行（真实部署路径：校验配装→DoDeploy）。
            health.CmdDeploy(1, loadout);
            Debug.Log($"[AutoDeploy] deployed: opt1={loadout.optional1} opt2={loadout.optional2} special={loadout.special} throwable={loadout.throwable}");
        }

        private LoadoutDefinition BuildLoadout(NetworkEquipment equipment)
        {
            var list = equipment.equipmentList;
            if (list == null || list.Count == 0) return null;

            int opt1 = -1, opt2 = -1, special = -1, throwable = -1;
            for (int i = 0; i < list.Count; i++)
            {
                var def = list[i];
                if (def == null) continue;
                if (def.category == EquipmentCategory.Optional)
                {
                    if (opt1 < 0) opt1 = i;
                    else if (opt2 < 0) opt2 = i;
                }
                else if (def.category == EquipmentCategory.Special && special < 0) special = i;
                else if (def.category == EquipmentCategory.Throwable && throwable < 0) throwable = i;
            }

            if (opt1 < 0 || opt2 < 0 || special < 0 || throwable < 0) return null;
            return new LoadoutDefinition { optional1 = opt1, optional2 = opt2, special = special, throwable = throwable };
        }
    }
}