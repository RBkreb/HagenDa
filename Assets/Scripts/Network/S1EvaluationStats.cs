using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// S1 评估统计器（ML-TRAINING.md §1 门槛验证）。挂在训练场景中，
    /// Play 模式自动统计：发射数、命中数、命中率、击杀数、回合数、
    /// 各回合是否完成击杀。控制台输出评估报告。
    /// </summary>
    public class S1EvaluationStats : NetworkBehaviour
    {
        [Header("Evaluation")]
        [Tooltip("评估回合数。")]
        public int evalRounds = 10;
        [Tooltip("每回合时长（与训练一致）。")]
        public float roundDuration = 90f;

        // ---- 统计 ----
        private int shotsFired;
        private int shotsHit;
        private int kills;
        private int roundsCompleted;
        private int roundsWithKill;
        private float roundStart;
        private bool evaluating;

        private void Start()
        {
            if (isServer) StartEvaluation();
        }

        [Server]
        private void StartEvaluation()
        {
            shotsFired = 0;
            shotsHit = 0;
            kills = 0;
            roundsCompleted = 0;
            roundsWithKill = 0;
            roundStart = Time.time;
            evaluating = true;
            Debug.Log("[S1-Eval] Evaluation started: " + evalRounds + " rounds x " + roundDuration + "s");
        }

        [Server]
        private void Update()
        {
            if (!evaluating) return;

            float elapsed = Time.time - roundStart;
            if (elapsed >= roundDuration)
            {
                // 回合结束。
                roundsCompleted++;

                roundStart = Time.time;

                Debug.Log($"[S1-Eval] Round {roundsCompleted}/{evalRounds}: " +
                          $"hits={shotsHit}/{shotsFired} ({(shotsFired > 0 ? 100f * shotsHit / shotsFired : 0):F1}%) " +
                          $"kills={kills}");

                if (roundsCompleted >= evalRounds)
                {
                    evaluating = false;
                    ReportResults();
                }
            }
        }

        /// <summary>Gun 射击时调用（钩子由 NetworkGun 打）。服务器端。</summary>
        [Server]
        public void RecordShot()
        {
            shotsFired++;
        }

        /// <summary>子弹命中时调用（钩子由 NetworkBullet 打）。服务器端。</summary>
        [Server]
        public void RecordHit()
        {
            shotsHit++;
        }

        /// <summary>击杀时调用（钩子由 RewardBus/NetworkPlayerHealth 打）。服务器端。</summary>
        [Server]
        public void RecordKill()
        {
            kills++;
            roundsWithKill++;
        }

        [Server]
        private void ReportResults()
        {
            float hitRate = shotsFired > 0 ? 100f * shotsHit / shotsFired : 0f;

            Debug.Log("========================================");
            Debug.Log("  S1 EVALUATION REPORT");
            Debug.Log("========================================");
            Debug.Log($"  Rounds completed:  {roundsCompleted}/{evalRounds}");
            Debug.Log($"  Total shots fired: {shotsFired}");
            Debug.Log($"  Total hits:        {shotsHit}");
            Debug.Log($"  Hit rate:          {hitRate:F1}%  (target >= 20%)");
            Debug.Log($"  Total kills:       {kills}");
            Debug.Log($"  Rounds with kill:  {roundsWithKill}/{evalRounds}  (target = 10/10)");
            Debug.Log($"  PASS hit rate:     {(hitRate >= 20f ? "YES" : "NO")}");
            Debug.Log($"  PASS kill rounds:  {(roundsWithKill >= evalRounds ? "YES" : "NO")}");
            Debug.Log("========================================");
        }
    }
}
