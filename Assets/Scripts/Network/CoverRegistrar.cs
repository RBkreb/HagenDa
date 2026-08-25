using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// 静态场景掩体注册器（PHASE9 战场场景）。训练场景的掩体由
    /// TrainingArena.BuildCovers 在运行时注册；静态战场场景的掩体是编辑期
    /// 摆放的固定几何，本组件在场景加载时一次性注册进 CoverRegistry，
    /// 供 FSM 大脑（生存状态找掩体）与脚本陪练查询。
    /// </summary>
    public class CoverRegistrar : MonoBehaviour
    {
        private void Awake()
        {
            foreach (var col in GetComponentsInChildren<Collider>())
                CoverRegistry.Register(col.gameObject);
        }
    }
}
