using UnityEngine;

namespace Echo.AI
{
    /// Presentation: a loop that is only audible while the agent lies in wait (guide §5.6, Ambusher: "a low
    /// breathing only within 4 m while waiting - fair warning"). Uses mute, so AudioOcclusion can keep owning volume.
    public class AgentWaitAudio : MonoBehaviour
    {
        public AudioSource source;
        AmbusherBrain brain;

        void Awake() => brain = GetComponent<AmbusherBrain>();

        void Update()
        {
            if (source != null) source.mute = !(brain != null && brain.IsAlive && brain.IsWaiting);
        }
    }
}
