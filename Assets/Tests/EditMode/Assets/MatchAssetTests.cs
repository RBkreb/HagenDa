using System.Linq;
using HagenDa.Networking;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace HagenDa.Tests.EditMode.Assets
{
    /// <summary>
    /// 对局与地图数据资产不变量。这些资产由 <c>NetworkSetup</c> / <c>MatchSceneBuilder</c>
    /// 程序化生成并被场景构建器消费，字段越界会在构建对局时才暴露（且往往静默）。
    /// </summary>
    [TestFixture]
    public class MatchAssetTests
    {
        private const string MatchFolder = "Assets/Game/Match";

        private MatchConfig[] _matchConfigs;
        private MapDefinition[] _mapDefinitions;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _matchConfigs = AssetDatabase
                .FindAssets("t:MatchConfig", new[] { MatchFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<MatchConfig>)
                .Where(a => a != null)
                .ToArray();

            _mapDefinitions = AssetDatabase
                .FindAssets("t:MapDefinition", new[] { MatchFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<MapDefinition>)
                .Where(a => a != null)
                .ToArray();
        }

        // ---------------------------------------------------------------
        // MatchConfig
        // ---------------------------------------------------------------

        [Test]
        public void MatchConfigs_CountIsExpected()
        {
            Assert.AreEqual(6, _matchConfigs.Length,
                $"预期 {MatchFolder} 下有 6 个 MatchConfig。");
        }

        [Test]
        public void MatchConfigs_AllReferenceAMap()
        {
            foreach (var cfg in _matchConfigs)
                Assert.IsNotNull(cfg.map,
                    $"{cfg.name} 未引用 MapDefinition —— 场景构建器将无法生成地图。");
        }

        [Test]
        public void MatchConfigs_SquadArithmetic_IsPositiveAndConsistent()
        {
            foreach (var cfg in _matchConfigs)
            {
                Assert.Greater(cfg.squadsPerTeam, 0, $"{cfg.name}.squadsPerTeam 必须为正。");
                Assert.Greater(cfg.squadSize, 0, $"{cfg.name}.squadSize 必须为正。");
                Assert.Greater(cfg.winScore, 0, $"{cfg.name}.winScore 必须为正（否则开局即结束）。");
                Assert.GreaterOrEqual(cfg.redAiCount, 0, $"{cfg.name}.redAiCount 不得为负。");
                Assert.GreaterOrEqual(cfg.blueAiCount, 0, $"{cfg.name}.blueAiCount 不得为负。");
            }
        }

        [Test]
        public void MatchConfigs_AiCounts_FitIntoDeclaredSquads()
        {
            // AI 数量不应超过队伍编制上限，否则 AssignCombatant 会出现无队可归的实体。
            foreach (var cfg in _matchConfigs)
            {
                int capacity = cfg.squadsPerTeam * cfg.squadSize;
                Assert.LessOrEqual(cfg.redAiCount, capacity,
                    $"{cfg.name}: redAiCount={cfg.redAiCount} 超过编制 {capacity}。");
                Assert.LessOrEqual(cfg.blueAiCount, capacity,
                    $"{cfg.name}: blueAiCount={cfg.blueAiCount} 超过编制 {capacity}。");
            }
        }

        [Test]
        public void MatchConfigs_EnumsAreDefined()
        {
            foreach (var cfg in _matchConfigs)
            {
                Assert.IsTrue(System.Enum.IsDefined(typeof(MatchBrain), cfg.brain),
                    $"{cfg.name}.brain 越界：{(int)cfg.brain}");
                Assert.IsTrue(System.Enum.IsDefined(typeof(HumanTeamPolicy), cfg.humanTeamPolicy),
                    $"{cfg.name}.humanTeamPolicy 越界：{(int)cfg.humanTeamPolicy}");
                Assert.IsTrue(System.Enum.IsDefined(typeof(CommanderMode), cfg.commander),
                    $"{cfg.name}.commander 越界：{(int)cfg.commander}");
                Assert.IsTrue(System.Enum.IsDefined(typeof(FsmClass), cfg.aiClassOverride),
                    $"{cfg.name}.aiClassOverride 越界：{(int)cfg.aiClassOverride}");
            }
        }

        [Test]
        public void MatchConfigs_Timings_AreNonNegative()
        {
            foreach (var cfg in _matchConfigs)
            {
                Assert.GreaterOrEqual(cfg.redeployDelay, 0f, $"{cfg.name}.redeployDelay");
                Assert.GreaterOrEqual(cfg.autoDeployTimeout, 0f, $"{cfg.name}.autoDeployTimeout");
            }
        }

        [Test]
        public void MatchConfigs_HumanSlots_AreNonNegative()
        {
            foreach (var cfg in _matchConfigs)
            {
                Assert.GreaterOrEqual(cfg.redHumanSlots, 0, $"{cfg.name}.redHumanSlots");
                Assert.GreaterOrEqual(cfg.blueHumanSlots, 0, $"{cfg.name}.blueHumanSlots");
            }
        }

        [Test]
        public void MatchConfigs_OnlyFsmBrain_IsCurrentlyImplemented()
        {
            // MatchBrain 目前只枚举了 Fsm。若新增枚举值，请同步实现分派逻辑。
            foreach (var cfg in _matchConfigs)
                Assert.AreEqual(MatchBrain.Fsm, cfg.brain,
                    $"{cfg.name} 使用了尚未实现的大脑类型 {cfg.brain}。");
        }

        // ---------------------------------------------------------------
        // MapDefinition
        // ---------------------------------------------------------------

        [Test]
        public void MapDefinitions_CountIsExpected()
        {
            Assert.AreEqual(5, _mapDefinitions.Length,
                $"预期 {MatchFolder} 下有 5 个 MapDefinition。");
        }

        [Test]
        public void MapDefinitions_HavePositiveSize()
        {
            foreach (var map in _mapDefinitions)
            {
                Assert.Greater(map.sizeX, 0f, $"{map.name}.sizeX 必须为正。");
                Assert.Greater(map.sizeZ, 0f, $"{map.name}.sizeZ 必须为正。");
                Assert.GreaterOrEqual(map.wallHeight, 0f, $"{map.name}.wallHeight 不得为负。");
            }
        }

        [Test]
        public void MapDefinitions_CoverSettings_AreSane()
        {
            foreach (var map in _mapDefinitions)
            {
                Assert.GreaterOrEqual(map.coverCount, 0, $"{map.name}.coverCount 不得为负。");
                Assert.Greater(map.coverMinGap, 0f,
                    $"{map.name}.coverMinGap 必须为正（0 会让掩体互相重叠）。");
            }
        }

        [Test]
        public void MapDefinitions_KindAndResolve_AreDefined()
        {
            foreach (var map in _mapDefinitions)
            {
                Assert.IsTrue(System.Enum.IsDefined(typeof(MapKind), map.kind),
                    $"{map.name}.kind 越界：{(int)map.kind}");
                Assert.IsTrue(System.Enum.IsDefined(typeof(AnchorResolve), map.anchorResolve),
                    $"{map.name}.anchorResolve 越界：{(int)map.anchorResolve}");
            }
        }

        [Test]
        public void MapDefinitions_AllHaveCapturePointAnchors()
        {
            // 没有据点锚点就无法产生争夺点，对局无法计分。
            foreach (var map in _mapDefinitions)
            {
                int captureAnchors = map.anchors.Count(a => a.role == AnchorRole.CapturePoint);
                Assert.Greater(captureAnchors, 0,
                    $"{map.name} 没有任何 CapturePoint 锚点。");
            }
        }

        [Test]
        public void MapDefinitions_Anchors_HaveValidRolesAndGeometry()
        {
            foreach (var map in _mapDefinitions)
            {
                foreach (var anchor in map.anchors)
                {
                    Assert.IsTrue(System.Enum.IsDefined(typeof(AnchorRole), anchor.role),
                        $"{map.name} 存在越界 role：{(int)anchor.role}");

                    Assert.GreaterOrEqual(anchor.radius, 0f,
                        $"{map.name}/{anchor.role}.radius 不得为负。");
                    Assert.GreaterOrEqual(anchor.extent.x, 0f,
                        $"{map.name}/{anchor.role}.extent.x 不得为负（负值会让矩形判定失效）。");
                    Assert.GreaterOrEqual(anchor.extent.y, 0f,
                        $"{map.name}/{anchor.role}.extent.y 不得为负。");
                    Assert.GreaterOrEqual(anchor.deployPointCount, 0,
                        $"{map.name}/{anchor.role}.deployPointCount 不得为负。");
                }
            }
        }

        [Test]
        public void MapDefinitions_CapturePointLetters_AreUniquePerMap()
        {
            // 据点字母用于 HUD 显示与部署点选择，同图内重复会让玩家无法区分。
            foreach (var map in _mapDefinitions)
            {
                var letters = map.anchors
                    .Where(a => a.role == AnchorRole.CapturePoint)
                    .Select(a => a.letter)
                    .ToArray();

                CollectionAssert.AllItemsAreUnique(letters,
                    $"{map.name} 的据点字母存在重复：{string.Join(",", letters)}");
            }
        }

        [Test]
        public void MapDefinitions_MovingAnchors_HaveWaypointsAndPositiveSpeed()
        {
            foreach (var map in _mapDefinitions)
            {
                foreach (var anchor in map.anchors.Where(a => a.moving))
                {
                    Assert.IsNotNull(anchor.waypoints, $"{map.name}/{anchor.letter} waypoints 为 null。");
                    Assert.Greater(anchor.waypoints.Length, 0,
                        $"{map.name}/{anchor.letter} 标记为 moving 但没有航点。");
                    Assert.Greater(anchor.moveSpeed, 0f,
                        $"{map.name}/{anchor.letter} 标记为 moving 但 moveSpeed 非正。");
                    Assert.GreaterOrEqual(anchor.dwellSeconds, 0f,
                        $"{map.name}/{anchor.letter}.dwellSeconds 不得为负。");
                }
            }
        }

        [Test]
        public void MapDefinitions_NonMovingAnchors_HaveNoWaypoints()
        {
            // 反向一致性：非移动锚点带航点通常意味着漏改 moving 标记。
            foreach (var map in _mapDefinitions)
            {
                foreach (var anchor in map.anchors.Where(a => !a.moving))
                {
                    Assert.AreEqual(0, anchor.waypoints?.Length ?? 0,
                        $"{map.name}/{anchor.letter} 有航点但 moving=false。");
                }
            }
        }
    }
}
