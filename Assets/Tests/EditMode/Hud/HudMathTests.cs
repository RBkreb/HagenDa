using HagenDa.Networking;
using NUnit.Framework;
using UnityEngine;

namespace HagenDa.Tests.EditMode.Hud
{
    /// <summary>
    /// HUD 纯数学（PHASE8）：HQ 争夺度 → 显示颜色。
    /// 争夺度范围 -60..+60，0 为白、负为红、正为蓝，两端之间线性插值。
    /// </summary>
    [TestFixture]
    public class HudMathTests
    {
        [Test]
        public void ZeroContention_IsWhite()
        {
            Assert.AreEqual(Color.white, HudMath.ContentionColor(0f));
        }

        [Test]
        public void NegativeContention_TrendsRed()
        {
            Color c = HudMath.ContentionColor(-60f);
            Assert.Greater(c.r, c.b, "负争夺度（红方占优）应偏红。");
            // 端点色为 (0.9, 0.2, 0.2)
            Assert.AreEqual(0.9f, c.r, 1e-4f);
            Assert.AreEqual(0.2f, c.g, 1e-4f);
            Assert.AreEqual(0.2f, c.b, 1e-4f);
        }

        [Test]
        public void PositiveContention_TrendsBlue()
        {
            Color c = HudMath.ContentionColor(60f);
            Assert.Greater(c.b, c.r, "正争夺度（蓝方占优）应偏蓝。");
            // 端点色为 (0.2, 0.4, 0.9)
            Assert.AreEqual(0.2f, c.r, 1e-4f);
            Assert.AreEqual(0.4f, c.g, 1e-4f);
            Assert.AreEqual(0.9f, c.b, 1e-4f);
        }

        [Test]
        public void SignOfContention_SelectsHueSide()
        {
            // 同样幅度、相反符号 → 分别偏红与偏蓝。
            Color red = HudMath.ContentionColor(-30f);
            Color blue = HudMath.ContentionColor(30f);

            Assert.Greater(red.r, red.b, "负值偏红。");
            Assert.Greater(blue.b, blue.r, "正值偏蓝。");
        }

        [Test]
        public void HalfContention_IsHalfwayToEndpoint()
        {
            // -30 是 0 与 -60 的中点 → 与端点色线性插值 t=0.5。
            Color white = Color.white;
            Color endpoint = new Color(0.9f, 0.2f, 0.2f);
            Color expected = Color.Lerp(white, endpoint, 0.5f);

            Color actual = HudMath.ContentionColor(-30f);
            Assert.AreEqual(expected.r, actual.r, 1e-4f);
            Assert.AreEqual(expected.g, actual.g, 1e-4f);
            Assert.AreEqual(expected.b, actual.b, 1e-4f);
        }

        [TestCase(-1000f)]
        [TestCase(-60.0001f)]
        [TestCase(60.0001f)]
        [TestCase(1000f)]
        public void OutOfRangeContention_ClampsToEndpoints(float contention)
        {
            Color c = HudMath.ContentionColor(contention);
            bool expectRed = contention < 0f;

            if (expectRed)
            {
                Assert.AreEqual(0.9f, c.r, 1e-4f, "超出下界应钳到红端。");
                Assert.AreEqual(0.2f, c.b, 1e-4f);
            }
            else
            {
                Assert.AreEqual(0.2f, c.r, 1e-4f, "超出上界应钳到蓝端。");
                Assert.AreEqual(0.9f, c.b, 1e-4f);
            }
        }

        [Test]
        public void Color_IsAlwaysOpaque_AndInGamut()
        {
            foreach (float contention in new[] { -60f, -30f, 0f, 30f, 60f, 999f, -999f })
            {
                Color c = HudMath.ContentionColor(contention);
                Assert.AreEqual(1f, c.a, 1e-4f, "alpha 应恒为不透明。");
                Assert.That(c.r, Is.InRange(0f, 1f), "r 越界");
                Assert.That(c.g, Is.InRange(0f, 1f), "g 越界");
                Assert.That(c.b, Is.InRange(0f, 1f), "b 越界");
            }
        }

        [Test]
        public void SmallContention_IsNearWhite()
        {
            // 争夺度接近 0 时颜色应接近白（避免轻微争夺就变色）。
            Color c = HudMath.ContentionColor(0.5f);
            Assert.AreEqual(1f, c.r, 0.05f);
            Assert.AreEqual(1f, c.g, 0.05f);
            Assert.AreEqual(1f, c.b, 0.05f);
        }
    }
}
