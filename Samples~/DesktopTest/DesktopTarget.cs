using UnityEngine;

namespace ActiveRagdoll.Samples
{
    /// <summary>Team-0 <see cref="CombatTarget"/> for <see cref="DesktopTestPlayer"/>: NPC strikes aim at the camera.</summary>
    public sealed class DesktopTarget : CombatTarget
    {
        public void Configure(Transform head, Rigidbody body)
        {
            Team = 0;
            SetPoints(head, transform, body);
        }

        public override Vector3 CenterPoint => transform.position + Vector3.up * 1f;
    }
}
