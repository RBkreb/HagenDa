using System.Collections.Generic;

namespace HagenDa.Networking
{
    /// <summary>
    /// 部署名册(PHASE15 局中系统,纯逻辑 → EditMode 直测)。
    ///
    /// 规格:"进入正式对局,所有实体暂不部署,模型不进入地图,开局时可以先选择部署点,
    /// 正式开局后模型进入选择的部署点;未选择的则暂不部署"。
    ///
    /// 语义:
    ///  - 对局开始(<see cref="BeginMatch"/>)时清空全部状态 → 所有实体"未部署"。
    ///  - 实体选择部署点(<see cref="SetChoice"/>),仅记录不落地。
    ///  - 正式开局(<see cref="CommitDeployment"/>)后,返回**已选择**的实体列表;
    ///    未选择者保持未部署(留在名册外,可稍后补选)。
    /// </summary>
    public class DeployRoster
    {
        private readonly Dictionary<int, int> choices = new Dictionary<int, int>();
        private readonly HashSet<int> deployed = new HashSet<int>();

        /// <summary>是否已有实体选择过(去重后计数)。</summary>
        public int ChoiceCount => choices.Count;

        /// <summary>已确认部署的实体数。</summary>
        public int DeployedCount => deployed.Count;

        /// <summary>开局:清空选择与部署状态。</summary>
        public void BeginMatch()
        {
            choices.Clear();
            deployed.Clear();
        }

        /// <summary>记录一个实体的部署点选择(choice:1=GR/2=HQ/3=squad/4=beacon)。</summary>
        public void SetChoice(int entityId, int choice)
        {
            if (choice <= 0) return;   // 0 = 未选择
            choices[entityId] = choice;
        }

        /// <summary>该实体是否已选择部署点。</summary>
        public bool HasChoice(int entityId) => choices.ContainsKey(entityId);

        /// <summary>取该实体的选择(未选则 0)。</summary>
        public int GetChoice(int entityId) =>
            choices.TryGetValue(entityId, out int c) ? c : 0;

        /// <summary>
        /// 正式开局:把"已选择"的实体标记为已部署并返回其 entityId(顺序稳定,升序)。
        /// 未选择者不会出现在结果中(暂不部署)。
        /// </summary>
        public List<int> CommitDeployment()
        {
            var committed = new List<int>();
            foreach (var kv in choices)
            {
                if (deployed.Add(kv.Key))
                    committed.Add(kv.Key);
            }
            committed.Sort();
            return committed;
        }

        /// <summary>
        /// 对局进行中,某实体补选后单独落地;返回其 entityId(已部署过则返回 -1)。
        /// </summary>
        public int DeploySingle(int entityId)
        {
            if (!choices.ContainsKey(entityId)) return -1;
            return deployed.Add(entityId) ? entityId : -1;
        }

        /// <summary>清除某实体的状态(实体销毁/真人退出)。</summary>
        public void Remove(int entityId)
        {
            choices.Remove(entityId);
            deployed.Remove(entityId);
        }

        /// <summary>已部署实体(升序)。</summary>
        public List<int> DeployedEntities()
        {
            var list = new List<int>(deployed);
            list.Sort();
            return list;
        }
    }
}
