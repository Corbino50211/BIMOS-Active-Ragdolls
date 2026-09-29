using UnityEngine;

namespace ActiveRagdoll
{
    public enum BladeFeedback
    {
        /// <summary>The tip hit something hard enough to wound but it didn't stick.</summary>
        Stab,
        /// <summary>The blade went in and is now stuck. Intensity = entry speed (m/s).</summary>
        Embed,
        /// <summary>Continuous while an embedded blade slides or twists. Intensity 0..1.</summary>
        Grind,
        /// <summary>The blade was pulled out cleanly.</summary>
        Extract,
        /// <summary>The blade was wrenched sideways out of the wound (joint broke).</summary>
        TearOut,
        /// <summary>The tip hit an impenetrable surface or came in too shallow.</summary>
        Glance,
    }

    /// <summary>
    /// Whoever is holding a <see cref="BladeWeapon"/> (a BIMOS hand, an NPC). Put it on the blade's GameObject
    /// or register it with <see cref="BladeWeapon.SetWielder"/>. It decides whether a stuck blade weakens the
    /// limb it's in (so you can drag the NPC by the handle), who gets blamed for the wound, and gets haptics.
    /// </summary>
    public interface IBladeWielder
    {
        /// <summary>True while a hand is actually holding the blade.</summary>
        bool IsHoldingBlade { get; }

        /// <summary>The attacker to blame (NPCs aggro on its <see cref="CombatTarget"/>). May be null.</summary>
        GameObject Wielder { get; }

        void OnBladeFeedback(BladeWeapon blade, BladeFeedback kind, float intensity);
    }
}
