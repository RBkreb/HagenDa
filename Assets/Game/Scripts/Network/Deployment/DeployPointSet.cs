using System.Collections.Generic;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// 重新部署点模板：一个可复用的组件 + 子物体结构，实例化到任意安全区
    /// (<see cref="GarrisonZone"/>) 或据点 (<see cref="CapturePoint"/>) 下，
    /// 为其配置多个重新部署点位置。直接子物体即点位（可在编辑器中自由摆放）；
    /// 运行时自动注册进同物体的 GarrisonZone / CapturePoint 的 deployPoints
    /// 列表（仅在列表为空时填充，不覆盖手动配置）。
    /// </summary>
    public class DeployPointSet : MonoBehaviour
    {
        [Tooltip("部署点列表；留空则自动收集全部直接子物体。")]
        public List<Transform> points = new List<Transform>();

        private void Awake()
        {
            CollectIfNeeded();
            Register();
        }

        private void CollectIfNeeded()
        {
            if (points != null && points.Count > 0) return;

            points = new List<Transform>();
            for (int i = 0; i < transform.childCount; i++)
                points.Add(transform.GetChild(i));
        }

        private void Register()
        {
            if (points == null || points.Count == 0) return;

            var garrison = GetComponent<GarrisonZone>();
            if (garrison != null && garrison.deployPoints.Count == 0)
                garrison.deployPoints.AddRange(points);

            var capture = GetComponent<CapturePoint>();
            if (capture != null && capture.deployPoints.Count == 0)
                capture.deployPoints.AddRange(points);
        }
    }
}
