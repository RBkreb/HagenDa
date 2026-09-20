using HagenDa.Networking;
using NUnit.Framework;
using UnityEngine;

namespace HagenDa.Tests.EditMode.Effects
{
    /// <summary>
    /// 服务器端烟雾体积（PHASE9）。客户端烟雾只是视觉；AI 射线能否"看透"完全取决于
    /// 这里的线段-球体相交判定，因此几何正确性直接影响 AI 行为。
    ///
    /// 注意：<see cref="NetworkSmokeVolume"/> 是静态注册表，测试之间必须
    /// <see cref="NetworkSmokeVolume.Clear"/>，否则互相污染。
    /// </summary>
    [TestFixture]
    public class NetworkSmokeVolumeTests
    {
        private const float VisibilityThreshold = 0.4f;

        [SetUp]
        public void SetUp() => NetworkSmokeVolume.Clear();

        [TearDown]
        public void TearDown() => NetworkSmokeVolume.Clear();

        [Test]
        public void EmptyRegistry_NeverIntersects()
        {
            Assert.IsFalse(NetworkSmokeVolume.SegmentIntersects(
                Vector3.zero, Vector3.forward, 10f, out float hitDist));
            Assert.AreEqual(10f, hitDist, "未命中时 hitDist 应为原始距离。");
        }

        [Test]
        public void RayThroughSmokeCenter_Intersects()
        {
            NetworkSmokeVolume.Register(Vector3.forward * 5f, 1f, 30f, 1f);

            Assert.IsTrue(NetworkSmokeVolume.SegmentIntersects(
                Vector3.zero, Vector3.forward, 10f, out float hitDist));
            // 球心在 5m、半径 1m → 近交点应在 4m。
            Assert.AreEqual(4f, hitDist, 1e-3f);
        }

        [Test]
        public void RayMissingSphere_DoesNotIntersect()
        {
            // 球在 +z 方向，射线朝 +x → 不相交。
            NetworkSmokeVolume.Register(Vector3.forward * 5f, 1f, 30f, 1f);

            Assert.IsFalse(NetworkSmokeVolume.SegmentIntersects(
                Vector3.zero, Vector3.right, 10f, out float hitDist));
            Assert.AreEqual(10f, hitDist);
        }

        [Test]
        public void SmokeBeyondRayDistance_DoesNotIntersect()
        {
            // 球在 20m 处，但射线只走 10m → 不算命中。
            NetworkSmokeVolume.Register(Vector3.forward * 20f, 1f, 30f, 1f);

            Assert.IsFalse(NetworkSmokeVolume.SegmentIntersects(
                Vector3.zero, Vector3.forward, 10f, out _));
        }

        [Test]
        public void OriginInsideSphere_IntersectsAtExitPoint()
        {
            // 射线起点在球心：t0 < 0，应取 t1（出射点）作为 hitDist。
            NetworkSmokeVolume.Register(Vector3.zero, 2f, 30f, 1f);

            Assert.IsTrue(NetworkSmokeVolume.SegmentIntersects(
                Vector3.zero, Vector3.forward, 10f, out float hitDist));
            Assert.AreEqual(2f, hitDist, 1e-3f, "起点在球内时应取前方的出射交点。");
        }

        [Test]
        public void MultipleSpheres_ReturnsNearestHit()
        {
            NetworkSmokeVolume.Register(Vector3.forward * 8f, 1f, 30f, 1f);
            NetworkSmokeVolume.Register(Vector3.forward * 3f, 1f, 30f, 1f);

            Assert.IsTrue(NetworkSmokeVolume.SegmentIntersects(
                Vector3.zero, Vector3.forward, 20f, out float hitDist));
            Assert.AreEqual(2f, hitDist, 1e-3f, "应返回最近的那个球（3m 处球的近交点 2m）。");
        }

        [Test]
        public void FaintSmoke_BelowThreshold_DoesNotBlockSight()
        {
            // concentration=0.4 恰好等于阈值 → CurrentAlpha <= 0.4 被跳过。
            NetworkSmokeVolume.Register(Vector3.forward * 5f, 1f, 30f, VisibilityThreshold);

            Assert.AreEqual(VisibilityThreshold, NetworkSmokeVolume.CurrentAlpha(
                new NetworkSmokeVolume.Volume { startTime = Time.time, decayTime = 30f,
                                                concentration = VisibilityThreshold }),
                1e-3f);

            Assert.IsFalse(NetworkSmokeVolume.SegmentIntersects(
                Vector3.zero, Vector3.forward, 10f, out _),
                "alpha 不高于 0.4 的烟雾不应遮挡视线。");
        }

        [Test]
        public void DenseSmoke_BlocksSight()
        {
            NetworkSmokeVolume.Register(Vector3.forward * 5f, 1f, 30f, 1f);
            Assert.IsTrue(NetworkSmokeVolume.SegmentIntersects(
                Vector3.zero, Vector3.forward, 10f, out _));
        }

        [Test]
        public void CurrentAlpha_DecaysLinearly()
        {
            var half = new NetworkSmokeVolume.Volume
            {
                startTime = Time.time - 5f,
                decayTime = 10f,
                concentration = 1f,
            };
            Assert.AreEqual(0.5f, NetworkSmokeVolume.CurrentAlpha(half), 1e-2f);
        }

        [Test]
        public void CurrentAlpha_ClampsAtZero_AfterDecay()
        {
            var expired = new NetworkSmokeVolume.Volume
            {
                startTime = Time.time - 100f,
                decayTime = 10f,
                concentration = 1f,
            };
            Assert.AreEqual(0f, NetworkSmokeVolume.CurrentAlpha(expired));
        }

        [Test]
        public void CurrentAlpha_ZeroDecayTime_DoesNotThrowOrNaN()
        {
            // decayTime=0 会被 Mathf.Max(0.001f, ...) 保护，不得产生 NaN/Inf。
            // 刚注册时 t≈0，因此 alpha 应≈concentration（尚未开始衰减）。
            var degenerate = new NetworkSmokeVolume.Volume
            {
                startTime = Time.time,
                decayTime = 0f,
                concentration = 1f,
            };
            float alpha = NetworkSmokeVolume.CurrentAlpha(degenerate);
            Assert.IsFalse(float.IsNaN(alpha), "不得产生 NaN。");
            Assert.IsFalse(float.IsInfinity(alpha), "不得产生 Infinity。");
            Assert.That(alpha, Is.InRange(0f, 1f), "alpha 必须落在 [0,1]。");
            Assert.AreEqual(1f, alpha, 1e-3f, "刚注册的烟雾应为全不透明。");
        }

        [Test]
        public void RegisterAndCount_TrackVolumes()
        {
            Assert.AreEqual(0, NetworkSmokeVolume.Count);
            NetworkSmokeVolume.Register(Vector3.zero, 1f, 5f, 1f);
            NetworkSmokeVolume.Register(Vector3.one, 1f, 5f, 1f);
            Assert.AreEqual(2, NetworkSmokeVolume.Count);

            NetworkSmokeVolume.Clear();
            Assert.AreEqual(0, NetworkSmokeVolume.Count);
        }

        [Test]
        public void Cleanup_RemovesExpiredVolumes()
        {
            // decayTime 为负 → 立即过期，Cleanup 应清掉。
            NetworkSmokeVolume.Register(Vector3.zero, 1f, -1f, 1f);
            NetworkSmokeVolume.Cleanup();
            Assert.AreEqual(0, NetworkSmokeVolume.Count);
        }
    }
}
