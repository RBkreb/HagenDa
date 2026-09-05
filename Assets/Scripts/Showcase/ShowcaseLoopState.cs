using UnityEngine;

namespace HagenDa.Showcase
{
    /// <summary>
    /// Restart-on-exit behaviour for the animation showcase row.
    /// Lets non-looping clips (shoot / reload) repeat without touching
    /// third-party FBX import settings; looping clips are unaffected.
    /// </summary>
    public class ShowcaseLoopState : StateMachineBehaviour
    {
        public override void OnStateExit(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            animator.Play(stateInfo.shortNameHash, layerIndex, 0f);
        }
    }
}
