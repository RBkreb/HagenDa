using System.Collections;
using System.Collections.Generic;
using HagenDa.Networking;
using HagenDa.Tests.PlayMode.Harness;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HagenDa.Tests.PlayMode
{
    /// <summary>
    /// 伤害与生命值（服务端权威）。覆盖护甲吸收顺序、治疗封顶、死亡/击杀计分联动、
    /// 爆伤减免，以及 EMP 对装备的干扰。
    ///
    /// 这些 API 全是 <c>[Server]</c>，因此必须在活动服务端下调用；实体则需要真正
    /// <c>Spawn</c> 过，让 <c>OnStartServer</c> 把 <c>health</c> 初始化到 <c>maxHealth</c>
    ///（否则默认 health=0 会被 <c>TakeDamageInternal</c> 的 IsDead 早退吞掉）。
    /// </summary>
    [TestFixture]
    public class CombatDamageTests
    {
        private readonly List<PlayerFixture> _players = new List<PlayerFixture>();
        private readonly List<GameObject> _extras = new List<GameObject>();
        private NetworkMatchManager _manager;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            PlayModeServer.StartServer();
            _manager = PlayModeServer.SpawnComponent<NetworkMatchManager>("TestMatchManager");
            _manager.winScore = 1000000;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var p in _players) p.Dispose();
            _players.Clear();

            foreach (var go in _extras)
                if (go != null) PlayModeServer.Destroy(go);
            _extras.Clear();

            if (_manager != null)
            {
                PlayModeServer.Destroy(_manager.gameObject);
                _manager = null;
            }

            PlayModeServer.StopServer();
            yield return null;
        }

        private PlayerFixture SpawnTarget(Vector3 pos, int teamId, float maxHealth = 100f)
        {
            var p = PlayerFixture.Create(pos, withAI: false, withFloor: false);
            _players.Add(p);
            p.Health.maxHealth = maxHealth;
            p.Health.health = maxHealth;
            p.Combatant.teamId = teamId;
            p.Combatant.squadId = 0;
            return p;
        }

        // ---------------------------------------------------------------
        // 基础伤害
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator OnStartServer_InitializesHealthToMax()
        {
            var t = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue, maxHealth: 120f);
            yield return new WaitForFixedUpdate();

            Assert.AreEqual(120f, t.Health.health, 0.01f,
                "生成时 OnStartServer 应把 health 初始化为 maxHealth。");
            Assert.IsFalse(t.Health.IsDead);
        }

        [UnityTest]
        public IEnumerator TakeDamage_ReducesHealthByExactAmount()
        {
            var t = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue, maxHealth: 100f);
            yield return new WaitForFixedUpdate();

            t.Health.TakeDamage(30f);

            Assert.AreEqual(70f, t.Health.health, 0.01f,
                "无护甲时应直接扣除等量生命值。");
        }

        [UnityTest]
        public IEnumerator TakeDamage_NegativeValue_DealsDamage_NotHeal()
        {
            var t = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue, maxHealth: 100f);
            yield return new WaitForFixedUpdate();

            t.Health.TakeDamage(-50f);

            // 契约：TakeDamageInternal 取 Mathf.Abs(damage)，因此负值**不会变成治疗**，
            // 而是按等量伤害结算。锁定这个语义，防止有人"顺手"改成 += 治疗。
            Assert.AreEqual(50f, t.Health.health, 0.01f,
                "负伤害不应变成治疗；应取绝对值后作为伤害（100-50=50）。");
        }

        // ---------------------------------------------------------------
        // 护甲先于生命值吸收
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator Armor_AbsorbsDamageBeforeHealth()
        {
            var t = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue, maxHealth: 100f);
            yield return new WaitForFixedUpdate();

            t.Health.AddArmor(50f);
            t.Health.TakeDamage(30f);

            Assert.AreEqual(20f, t.Health.armor, 0.01f, "护甲应先被消耗。");
            Assert.AreEqual(100f, t.Health.health, 0.01f,
                "伤害未超过护甲时生命值不应减少。");
        }

        [UnityTest]
        public IEnumerator Armor_Overflow_SpillsIntoHealth()
        {
            var t = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue, maxHealth: 100f);
            yield return new WaitForFixedUpdate();

            t.Health.AddArmor(20f);
            t.Health.TakeDamage(50f);       // 20 破甲 + 30 掉血

            Assert.AreEqual(0f, t.Health.armor, 0.01f, "护甲应恰好耗尽。");
            Assert.AreEqual(70f, t.Health.health, 0.01f,
                "超出护甲的部分应溢出到生命值。");
        }

        // ---------------------------------------------------------------
        // 治疗
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator Heal_CapsAtMaxHealth()
        {
            var t = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue, maxHealth: 100f);
            yield return new WaitForFixedUpdate();

            t.Health.TakeDamage(40f);
            t.Health.Heal(1000f);

            Assert.AreEqual(100f, t.Health.health, 0.01f,
                "治疗不应超过 maxHealth 上限。");
        }

        [UnityTest]
        public IEnumerator Heal_DoesNotReviveDeadEntity()
        {
            var t = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue, maxHealth: 100f);
            yield return new WaitForFixedUpdate();

            t.Health.TakeDamage(100f);
            yield return new WaitForFixedUpdate();
            Assert.IsTrue(t.Health.IsDead, "前置条件：目标应已死亡。");

            t.Health.Heal(50f);

            Assert.IsTrue(t.Health.IsDead, "治疗不应复活已死亡单位（需走 Rescue）。");
        }

        // ---------------------------------------------------------------
        // 死亡
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator LethalDamage_SetsDeadAndFloorsHealthAtZero()
        {
            var t = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue, maxHealth: 100f);
            yield return new WaitForFixedUpdate();

            t.Health.TakeDamage(150f);
            yield return new WaitForFixedUpdate();

            Assert.IsTrue(t.Health.IsDead);
            Assert.AreEqual(0f, t.Health.health, 0.01f,
                "生命值应被钳在 0，不得为负。");
        }

        [UnityTest]
        public IEnumerator DamageAfterDeath_IsIgnored()
        {
            var t = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue, maxHealth: 50f);
            yield return new WaitForFixedUpdate();

            t.Health.TakeDamage(100f);
            yield return new WaitForFixedUpdate();

            t.Health.TakeDamage(100f);      // 死后再次受伤应无效

            Assert.AreEqual(0f, t.Health.health, 0.01f,
                "死亡后不应再受到伤害（IsDead 早退）。");
        }

        [UnityTest]
        public IEnumerator ForceKill_SetsUnrevivable()
        {
            var t = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue);
            yield return new WaitForFixedUpdate();

            t.Health.ForceKill();
            yield return new WaitForFixedUpdate();

            Assert.IsTrue(t.Health.IsDead, "ForceKill 应立即致死。");
            Assert.IsTrue(t.Health.unrevivable,
                "ForceKill（安全区击杀）应标记不可救援。");

            t.Health.Rescue();
            Assert.IsTrue(t.Health.IsDead,
                "不可救援状态下的 Rescue 应无效。");
        }

        [UnityTest]
        public IEnumerator Death_ReportsKill_ToMatchManager()
        {
            var attacker = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Red);
            var victim = SpawnTarget(new Vector3(1f, 0f, 0f), (int)MatchTeam.Blue);
            yield return new WaitForFixedUpdate();

            int before = _manager.redScore;

            // 走带攻击者的入口，让 lastAttacker 被记录 → DieInternal 会 ReportKill。
            victim.Health.TakeDamage(999f, attacker.Combatant);
            yield return new WaitForFixedUpdate();

            Assert.AreEqual(before + 1, _manager.redScore,
                "红方击杀蓝方应经 ReportKill 给红方 +1。");
        }

        // ---------------------------------------------------------------
        // 爆炸伤害减免
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator ExplosionMultiplier_ReducesExplosionDamage()
        {
            var shielded = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue, maxHealth: 100f);
            yield return new WaitForFixedUpdate();

            // 模拟防爆盾：爆炸减免到 40%。
            shielded.Health.explosionDamageMultiplier = 0.4f;
            shielded.Health.TakeExplosionDamage(50f, null, Vector3.zero);

            Assert.AreEqual(80f, shielded.Health.health, 0.01f,
                "爆炸伤害应按 explosionDamageMultiplier 缩放（50*0.4=20）。");
        }

        [UnityTest]
        public IEnumerator BulletDamage_DoesNotApplyExplosionMultiplier()
        {
            var t = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue, maxHealth: 100f);
            yield return new WaitForFixedUpdate();

            // 子弹路径明确不乘爆炸减免（倍率已由 NetworkBullet 处理）。
            t.Health.explosionDamageMultiplier = 0.4f;
            t.Health.TakeBulletDamage(30f, Vector3.zero, Vector3.forward, null);

            Assert.AreEqual(70f, t.Health.health, 0.01f,
                "子弹伤害不应被 explosionDamageMultiplier 缩放。");
        }

        // ---------------------------------------------------------------
        // 爆炸范围与遮挡（ExplosionUtility）
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator Explosion_DamagesEnemyInRadius()
        {
            var target = SpawnTarget(new Vector3(2f, 0f, 0f), (int)MatchTeam.Blue, maxHealth: 100f);
            var attacker = SpawnTarget(new Vector3(-2f, 0f, 0f), (int)MatchTeam.Red);
            yield return new WaitForFixedUpdate();

            ExplosionUtility.ApplyDamage(Vector3.zero, 100f, 10f, attacker.Identity);

            Assert.Less(target.Health.health, 100f, "半径内的敌方目标应受到爆炸伤害。");
        }

        [UnityTest]
        public IEnumerator Explosion_DoesNotDamageOutsideRadius()
        {
            var far = SpawnTarget(new Vector3(50f, 0f, 0f), (int)MatchTeam.Blue, maxHealth: 100f);
            yield return new WaitForFixedUpdate();

            ExplosionUtility.ApplyDamage(Vector3.zero, 100f, 5f, null);

            Assert.AreEqual(100f, far.Health.health, 0.01f,
                "半径外的目标不应受到爆炸伤害。");
        }

        [UnityTest]
        public IEnumerator Explosion_Falloff_DecreasesWithDistance()
        {
            var near = SpawnTarget(new Vector3(1f, 0f, 0f), (int)MatchTeam.Blue, maxHealth: 100f);
            var far = SpawnTarget(new Vector3(8f, 0f, 0f), (int)MatchTeam.Blue, maxHealth: 100f);
            yield return new WaitForFixedUpdate();

            ExplosionUtility.ApplyDamage(Vector3.zero, 100f, 10f, null);

            float nearLoss = 100f - near.Health.health;
            float farLoss = 100f - far.Health.health;

            Assert.Greater(nearLoss, 0f, "近处目标应受伤。");
            Assert.Greater(nearLoss, farLoss,
                "爆炸伤害应随距离线性衰减（近处受创更重）。");
        }

        [UnityTest]
        public IEnumerator Explosion_ZeroRadius_DoesNothing()
        {
            var target = SpawnTarget(new Vector3(1f, 0f, 0f), (int)MatchTeam.Blue, maxHealth: 100f);
            yield return new WaitForFixedUpdate();

            ExplosionUtility.ApplyDamage(Vector3.zero, 100f, 0f, null);

            Assert.AreEqual(100f, target.Health.health, 0.01f,
                "radius<=0 时应直接返回，不造成伤害。");
        }

        // ---------------------------------------------------------------
        // EMP 对装备的干扰
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator Emp_DisablesVulnerableEquipmentForDuration()
        {
            var p = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue);
            yield return new WaitForFixedUpdate();

            Assert.IsFalse(p.Equipment.IsEmpDisabled, "前置条件：初始不应处于干扰态。");

            p.Equipment.ApplyEmp(5f);

            Assert.IsTrue(p.Equipment.IsEmpDisabled,
                "ApplyEmp 后装备应进入干扰态。");
            Assert.AreEqual(5f, p.Equipment.empExposure, 0.01f,
                "干扰时长应被记录。");
        }

        [UnityTest]
        public IEnumerator Emp_RepeatedApplication_KeepsLongestDuration()
        {
            var p = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue);
            yield return new WaitForFixedUpdate();

            p.Equipment.ApplyEmp(10f);
            p.Equipment.ApplyEmp(2f);      // 较短时长不应缩短已有效果

            Assert.AreEqual(10f, p.Equipment.empExposure, 0.01f,
                "重复施加 EMP 应取较长剩余时间（Mathf.Max 语义）。");
        }

        // ---------------------------------------------------------------
        // 标记
        // ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator Mark_SetThenExpire()
        {
            var t = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue);
            yield return new WaitForFixedUpdate();

            Assert.IsFalse(t.Combatant.IsMarked, "前置条件：初始未标记。");

            t.Combatant.SetMarked(Mirror.NetworkTime.time + 0.2, (int)MatchTeam.Red, null);
            Assert.IsTrue(t.Combatant.IsMarked, "标记后 IsMarked 应为 true。");

            float t0 = Time.time;
            while (Time.time - t0 < 0.6f && t.Combatant.IsMarked)
                yield return new WaitForFixedUpdate();

            Assert.IsFalse(t.Combatant.IsMarked, "超过标记到期时间后应自动失效。");
        }

        [UnityTest]
        public IEnumerator Mark_ImmuneTarget_IgnoresMarking()
        {
            var t = SpawnTarget(new Vector3(0f, 0f, 0f), (int)MatchTeam.Blue);
            yield return new WaitForFixedUpdate();

            t.Combatant.markImmune = true;
            t.Combatant.SetMarked(Mirror.NetworkTime.time + 10.0, (int)MatchTeam.Red, null);

            Assert.IsFalse(t.Combatant.IsMarked,
                "免疫标记期间（干扰器）任何标记都应无效。");
        }
    }
}
