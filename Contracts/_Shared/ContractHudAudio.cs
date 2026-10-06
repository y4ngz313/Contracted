using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>#1343. Local-only one-shots on vanilla's UI source; never owns a clip, source or RPC.</summary>
    internal static class ContractHudAudio
    {
        private static readonly ContractHudAudioCooldown Cooldown = new ContractHudAudioCooldown();

        internal static void Play(ContractHudAudioKind kind)
        {
            if (ContractsBootstrap.ContractHudAudioEnabled == null || !ContractsBootstrap.ContractHudAudioEnabled.Value
                || ContractsBootstrap.ContractHudAudioVolume == null || !ContractHUDManager.HudAudioVisible())
                return;

            float volume = ContractHudAudioPolicy.EffectiveVolume(kind, ContractsBootstrap.ContractHudAudioVolume.Value);
            if (!(volume > 0f))
                return;

            HUDManager hud = HUDManager.Instance;
            if (hud == null || hud.UIAudio == null || !hud.UIAudio.isActiveAndEnabled)
                return;

            AudioClip clip = ContractHudAudioPolicy.Clip(kind) switch
            {
                ContractHudAudioClip.Scan => hud.scanSFX,
                ContractHudAudioClip.ScrapTally => hud.addToScrapTotalSFX,
                ContractHudAudioClip.ScrapFinish => hud.finishAddingToTotalSFX,
                ContractHudAudioClip.WarningFirst => FirstClip(hud.warningSFX),
                ContractHudAudioClip.TipFirst => FirstClip(hud.tipsSFX),
                _ => null
            };
            if (clip == null || !Cooldown.TryBegin(kind, Time.realtimeSinceStartupAsDouble, audible: true))
                return;

            hud.UIAudio.PlayOneShot(clip, volume);
        }

        private static AudioClip FirstClip(AudioClip[] clips) => clips != null && clips.Length > 0 ? clips[0] : null;
    }
}
