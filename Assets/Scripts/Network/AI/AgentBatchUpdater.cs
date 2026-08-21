using System.Collections.Generic;
using CrashKonijn.Agent.Runtime;
using UnityEngine;

namespace HagenDa.Networking.AI
{
    /// <summary>
    /// Batched agent update: spreads <c>AgentBehaviour.Run()</c> calls across frames
    /// instead of running all 59 AI every frame. Each frame only a slice of agents
    /// is updated, so per-frame cost is O(slice) not O(total).
    ///
    /// The slice size is auto-calculated to cover all agents in ~6 frames at 60fps,
    /// giving an effective ~10 Hz update per agent (action Perform still runs every
    /// tick for the current slice, just not all 59 at once).
    /// </summary>
    public class AgentBatchUpdater : MonoBehaviour
    {
        [Tooltip("How many frames to spread the full agent update over. " +
                 "59 AI / 6 frames ≈ 10 AI per frame at 60fps = ~10 Hz per agent.")]
        public int framesPerCycle = 6;

        private readonly List<AgentBehaviour> agents = new List<AgentBehaviour>(128);
        private int currentIndex;

        private void Update()
        {
            if (agents.Count == 0) return;

            // Calculate how many agents to process this frame.
            int sliceSize = Mathf.Max(1, Mathf.CeilToInt((float)agents.Count / framesPerCycle));
            int processed = 0;

            while (processed < sliceSize)
            {
                if (currentIndex >= agents.Count)
                    currentIndex = 0;

                var agent = agents[currentIndex];
                if (agent != null && agent.enabled)
                {
                    // RunInUnityUpdate is false — we drive Run() manually.
                    // Use a fixed deltaTime so Perform() logic stays consistent.
                    agent.Run(Time.deltaTime);
                }
                else
                {
                    // Prune null / destroyed agents lazily.
                    agents.RemoveAt(currentIndex);
                    if (currentIndex >= agents.Count)
                        currentIndex = 0;
                    continue;
                }

                currentIndex++;
                processed++;
            }
        }

        /// <summary>Register an agent for batched updates (called by GoapAgentInitializer).</summary>
        public void Register(AgentBehaviour agent)
        {
            if (agent == null || agents.Contains(agent)) return;
            agent.RunInUnityUpdate = false;
            agents.Add(agent);
        }

        /// <summary>Unregister an agent (called on disable / destroy).</summary>
        public void Unregister(AgentBehaviour agent)
        {
            agents.Remove(agent);
        }
    }
}
