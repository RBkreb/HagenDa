using NUnit.Framework;
using UnityEngine;

namespace HagenDa.Tests.EditMode.Combat
{
    /// <summary>
    /// 多部位伤害倍率（PHASE4/PHASE12）。头 2x / 躯干 1x / 四肢 0.5x，
    /// 且只对"直立（Y 轴）"胶囊生效——趴姿（direction != 1）整体回退 1x。
    /// </summary>
    [TestFixture]
    public class HitboxUtilityTests
    {
        private GameObject _go;
        private CapsuleCollider _capsule;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("HitboxTest");
            _capsule = _go.AddComponent<CapsuleCollider>();
            _capsule.direction = 1;      // Y 轴（直立）
            _capsule.radius = 0.5f;
            _capsule.height = 3f;        // 圆柱段半长 = 3/2 - 0.5 = 1.0
            _capsule.center = Vector3.zero;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
        }

        [Test]
        public void NullCapsule_FallsBackToBodyMultiplier()
        {
            Assert.AreEqual(HagenDa.Networking.HitboxUtility.BodyMultiplier,
                HagenDa.Networking.HitboxUtility.GetMultiplier(null, Vector3.zero));
        }

        [Test]
        public void TopHemisphere_IsHead()
        {
            // y > halfCylinder(=1.0) → 头部
            Assert.AreEqual(HagenDa.Networking.HitboxUtility.HeadMultiplier,
                HagenDa.Networking.HitboxUtility.GetMultiplier(_capsule, new Vector3(0f, 1.5f, 0f)));
        }

        [Test]
        public void MiddleCylinder_IsBody()
        {
            Assert.AreEqual(HagenDa.Networking.HitboxUtility.BodyMultiplier,
                HagenDa.Networking.HitboxUtility.GetMultiplier(_capsule, new Vector3(0f, 0f, 0f)));
        }

        [Test]
        public void BottomHemisphere_IsLimb()
        {
            // y < -halfCylinder → 腿部
            Assert.AreEqual(HagenDa.Networking.HitboxUtility.LegMultiplier,
                HagenDa.Networking.HitboxUtility.GetMultiplier(_capsule, new Vector3(0f, -1.5f, 0f)));
        }

        [TestCase(1.0001f, 2f)]      // 恰好越过头/躯干分界
        [TestCase(0.9999f, 1f)]      // 恰好未越过
        [TestCase(-1.0001f, 0.5f)]
        [TestCase(-0.9999f, 1f)]
        public void HalfCylinderBoundary_IsExclusive(float y, float expected)
        {
            Assert.AreEqual(expected,
                HagenDa.Networking.HitboxUtility.GetMultiplier(_capsule, new Vector3(0f, y, 0f)),
                1e-6f);
        }

        [Test]
        public void CenterOffset_IsRespected()
        {
            // center 抬高后，同一世界点应落入更低的分段。
            _capsule.center = new Vector3(0f, 1f, 0f);
            Assert.AreEqual(HagenDa.Networking.HitboxUtility.BodyMultiplier,
                HagenDa.Networking.HitboxUtility.GetMultiplier(_capsule, new Vector3(0f, 0f, 0f)));
        }

        [Test]
        public void NonUprightCapsule_FallsBackToBody()
        {
            // 趴姿（Z 轴）不做部位区分。
            _capsule.direction = 2;
            Assert.AreEqual(HagenDa.Networking.HitboxUtility.BodyMultiplier,
                HagenDa.Networking.HitboxUtility.GetMultiplier(_capsule, new Vector3(0f, 1.5f, 0f)));
        }

        [Test]
        public void TransformScale_DoesNotBreakClassification()
        {
            _go.transform.localScale = new Vector3(2f, 2f, 2f);
            // InverseTransformPoint 已归一化到局部空间，分类应与缩放无关。
            Assert.AreEqual(HagenDa.Networking.HitboxUtility.HeadMultiplier,
                HagenDa.Networking.HitboxUtility.GetMultiplier(_capsule, new Vector3(0f, 3f, 0f)));
        }

        [Test]
        public void NetworkHitbox_GetMultiplier_MapsPartToConstant()
        {
            Assert.AreEqual(HagenDa.Networking.HitboxUtility.HeadMultiplier,
                HagenDa.Networking.NetworkHitbox.GetMultiplier(HagenDa.Networking.HitboxPart.Head));
            Assert.AreEqual(HagenDa.Networking.HitboxUtility.BodyMultiplier,
                HagenDa.Networking.NetworkHitbox.GetMultiplier(HagenDa.Networking.HitboxPart.Body));
            Assert.AreEqual(HagenDa.Networking.HitboxUtility.LegMultiplier,
                HagenDa.Networking.NetworkHitbox.GetMultiplier(HagenDa.Networking.HitboxPart.Limb));
        }

        [Test]
        public void MultiplierConstants_AreOrdered_HeadBodyLimb()
        {
            // 头 > 躯干 > 四肢 是设计意图，防止有人"顺手"调错。
            Assert.Greater(HagenDa.Networking.HitboxUtility.HeadMultiplier,
                HagenDa.Networking.HitboxUtility.BodyMultiplier);
            Assert.Greater(HagenDa.Networking.HitboxUtility.BodyMultiplier,
                HagenDa.Networking.HitboxUtility.LegMultiplier);
        }
    }
}
