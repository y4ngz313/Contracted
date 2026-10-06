using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy.AI;

/// <summary>
/// Gives <see cref="BundyAIBase"/> a LateUpdate slot for the audio work that has to be the last
/// word in the frame.
///
/// Lethal Company's <c>OccludeAudio</c> owns the <see cref="AudioReverbFilter"/> it adds to every
/// Bundy source and writes <c>enabled</c> from its own <c>Update</c> — outdoors it disables the
/// filter every frame. Anything we set from an Update is therefore a coin flip on Unity's script
/// execution order. LateUpdate always runs after every Update, so writing the reverb profile from
/// here is deterministic, and the worst case if that ever stopped being true is exactly today's
/// behaviour rather than a flicker.
///
/// This is a separate component rather than a <c>LateUpdate</c> on <c>BundyAIBase</c> on purpose:
/// Unity messages are found by name on the concrete type, so a same-named method in a subclass
/// shadows a base class's message instead of extending it. Adding one to an <c>EnemyAI</c>
/// subclass is a silent way to stop a base-class message from ever running.
/// </summary>
internal sealed class BundyAudioLateTick : MonoBehaviour
{
    internal BundyAIBase Owner;

    private void LateUpdate()
    {
        if (Owner == null)
            return;

        Owner.UpdateAudioLate();
    }
}
