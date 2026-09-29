using System.Collections.Generic;
using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Weapon-agnostic hit helpers. Any gun, grenade or trap can call these; they route damage and impulse
    /// through <see cref="IDamageable"/> so ragdoll body parts react at the exact hit location.
    /// Main-thread only (static buffers).
    /// </summary>
    public static class Ballistics
    {
        private static readonly Collider[] s_overlap = new Collider[128];
        private static readonly HashSet<Rigidbody> s_seenBodies = new HashSet<Rigidbody>();
        private static readonly HashSet<ActiveRagdollCharacter> s_seenCharacters = new HashSet<ActiveRagdollCharacter>();
        private static readonly HashSet<IDamageable> s_seenDamageables = new HashSet<IDamageable>();

        /// <summary>
        /// Fires a hitscan round. The nearest non-trigger collider not under <paramref name="ignoreRoot"/> is hit.
        /// Damageables receive damage + impulse; plain rigidbodies receive the impulse.
        /// </summary>
        /// <param name="impulse">Knock-back (N·s). Real bullets carry ~4 N·s; games usually exaggerate to 8–15.</param>
        public static bool FireHitscan(Vector3 origin, Vector3 direction, float range, int layerMask, Transform ignoreRoot,
            float damage, float impulse, GameObject source, out RaycastHit hit, DamageType type = DamageType.Bullet)
        {
            hit = default;
            if (direction.sqrMagnitude < 1e-10f || !RagdollMath.IsFinite(origin) || !RagdollMath.IsFinite(direction))
                return false;

            Vector3 dir = direction.normalized;
            if (!PhysicsQuery.Raycast(origin, dir, range, layerMask, null, ignoreRoot, out hit))
                return false;

            Vector3 impulseVector = dir * impulse;
            IDamageable target = Damageables.Find(hit.collider);
            if (target != null)
            {
                target.ApplyDamage(new DamageInfo(damage, type, hit.point, dir, impulseVector, source, hit.collider));
            }
            else
            {
                Rigidbody rb = hit.rigidbody;
                if (rb != null && !rb.isKinematic)
                    rb.AddForceAtPosition(impulseVector, hit.point, ForceMode.Impulse);
            }
            return true;
        }

        /// <summary>
        /// Radial explosion. Every rigidbody in range gets a velocity kick that falls off with distance; each
        /// ragdoll character takes damage once (at its nearest part) while all of its parts are thrown.
        /// </summary>
        /// <returns>Number of bodies affected.</returns>
        public static int Explode(Vector3 center, float radius, float maxDamage, float maxVelocityChange, int layerMask,
            GameObject source, float upwardsBias = 0.4f)
        {
            if (radius <= 0f || !RagdollMath.IsFinite(center))
                return 0;

            int count = Physics.OverlapSphereNonAlloc(center, radius, s_overlap, layerMask, QueryTriggerInteraction.Ignore);
            s_seenBodies.Clear();
            s_seenCharacters.Clear();
            s_seenDamageables.Clear();
            int affected = 0;

            for (int i = 0; i < count; i++)
            {
                Collider c = s_overlap[i];
                if (c == null)
                    continue;

                Vector3 closest = c.ClosestPoint(center);
                float falloff = 1f - Mathf.Clamp01(Vector3.Distance(center, closest) / radius);
                if (falloff <= 0f)
                    continue;

                Rigidbody rb = c.attachedRigidbody;
                IDamageable target = Damageables.Find(c);

                if (rb != null)
                {
                    if (!s_seenBodies.Add(rb))
                        continue;
                    Vector3 dir = RagdollMath.SafeNormalize(rb.worldCenterOfMass - center, Vector3.up) + Vector3.up * upwardsBias;
                    dir.Normalize();
                    Vector3 impulse = rb.isKinematic ? Vector3.zero : dir * (maxVelocityChange * falloff * rb.mass);

                    float damage = maxDamage * falloff;
                    if (target is BodyPart part && part.Character != null)
                    {
                        if (!s_seenCharacters.Add(part.Character))
                            damage = 0f;
                    }
                    else if (target != null && !s_seenDamageables.Add(target))
                    {
                        damage = 0f;
                    }

                    if (target != null)
                        target.ApplyDamage(new DamageInfo(damage, DamageType.Explosion, rb.worldCenterOfMass, dir, impulse, source, c));
                    else if (!rb.isKinematic)
                        rb.AddForce(impulse, ForceMode.Impulse);
                    affected++;
                }
                else if (target != null && s_seenDamageables.Add(target))
                {
                    Vector3 dir = RagdollMath.SafeNormalize(closest - center, Vector3.up);
                    target.ApplyDamage(new DamageInfo(maxDamage * falloff, DamageType.Explosion, closest, dir, Vector3.zero, source, c));
                    affected++;
                }
            }

            s_seenBodies.Clear();
            s_seenCharacters.Clear();
            s_seenDamageables.Clear();
            return affected;
        }
    }
}
