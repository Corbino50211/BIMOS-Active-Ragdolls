using System;
using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// How a surface takes a blade: how hard it is to get in, how firmly it holds, and how much it lets the
    /// blade twist and wiggle. Forces are in newtons, torques in newton-metres, at full blade depth (a blade
    /// that is only a little way in is held proportionally less).
    /// </summary>
    [Serializable]
    public sealed class StabMaterial
    {
        [Tooltip("Blades can stick into it. Off = stabs glance off (metal, stone).")]
        public bool penetrable = true;

        [Header("Getting in")]
        [Tooltip("Tip speed along the blade (m/s) needed to punch in.")]
        [Min(0f)] public float minStabSpeed = 1.4f;

        [Tooltip("Steady push along the blade (N) that forces it in without a swing. 0 = only fast stabs get in.")]
        [Min(0f)] public float minPressForce = 120f;

        [Tooltip("Fraction of the blade's speed along its axis it keeps after punching in (the rest is lost to the surface).")]
        [Range(0f, 1f)] public float entrySpeedKept = 0.55f;

        [Tooltip("Deepest the blade can go (m). 0 = the whole blade, up to the guard.")]
        [Min(0f)] public float maxDepth = 0f;

        [Header("Holding")]
        [Tooltip("Static friction (N) along the blade. The blade stays put until pushed or pulled harder than this.")]
        [Min(0f)] public float slideFriction = 90f;

        [Tooltip("Friction torque (N·m) resisting twisting the blade about its own axis.")]
        [Min(0f)] public float twistFriction = 1.5f;

        [Tooltip("How far (degrees) the blade can be levered side to side in the wound.")]
        [Range(0f, 30f)] public float swingLimit = 6f;

        [Tooltip("Sideways force (N) or torque (N·m) that tears the blade out of the surface.")]
        [Min(1f)] public float breakForce = 2500f;

        [Header("Wound")]
        [Tooltip("How much a full turn of twisting or levering loosens the grip (0 = never, 1 = a turn frees it).")]
        [Range(0f, 1f)] public float woundWidening = 0.35f;

        [Tooltip("Continuous damage from cutting deeper, sawing and twisting is multiplied by this.")]
        [Min(0f)] public float woundDamageScale = 1f;

        public StabMaterial Clone() => (StabMaterial)MemberwiseClone();

        /// <summary>Muscle, organs: easy in, moderate hold, some give.</summary>
        public static StabMaterial Flesh() => new StabMaterial();

        /// <summary>Wood, drywall, crates: needs a real stab, holds hard enough to hang from.</summary>
        public static StabMaterial Wood() => new StabMaterial
        {
            minStabSpeed = 3f,
            minPressForce = 450f,
            entrySpeedKept = 0.2f,
            maxDepth = 0.06f,
            slideFriction = 450f,
            twistFriction = 8f,
            swingLimit = 1.5f,
            breakForce = 6000f,
            woundWidening = 0.1f,
            woundDamageScale = 0f,
        };

        /// <summary>Metal, stone: blades glance off.</summary>
        public static StabMaterial Impenetrable() => new StabMaterial { penetrable = false, woundDamageScale = 0f };
    }
}
