using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Analytic two-bone IK that works on any rig regardless of bone axis conventions. Each bone's
    /// orientation is rebuilt from (bone direction, bend-plane normal) relative to the same basis captured in
    /// the bind pose, so twist stays consistent with the joint limits the ragdoll builder authors.
    /// </summary>
    public sealed class LimbIK
    {
        private Quaternion _upperBindInverse = Quaternion.identity;
        private Quaternion _lowerBindInverse = Quaternion.identity;
        private Vector3 _lastBend = Vector3.forward;

        public Transform Upper { get; private set; }
        public Transform Lower { get; private set; }
        public Transform End { get; private set; }
        public float UpperLength { get; private set; }
        public float LowerLength { get; private set; }
        public float Length => UpperLength + LowerLength;
        public bool IsValid { get; private set; }

        /// <summary>
        /// Captures the bind pose. <paramref name="bendHint"/> is the direction the middle joint would move
        /// if the limb bent (knees: forward, elbows: backward); it is only used when the bind pose is straight.
        /// </summary>
        public bool Bind(Transform upper, Transform lower, Transform end, Vector3 bendHint)
        {
            IsValid = false;
            if (upper == null || lower == null || end == null)
                return false;

            Upper = upper;
            Lower = lower;
            End = end;

            Vector3 root = upper.position;
            Vector3 mid = lower.position;
            Vector3 tip = end.position;
            Vector3 d1 = mid - root;
            Vector3 d2 = tip - mid;
            UpperLength = d1.magnitude;
            LowerLength = d2.magnitude;
            if (UpperLength < 1e-3f || LowerLength < 1e-3f)
                return false;

            Vector3 dir = RagdollMath.SafeNormalize(tip - root, d1 / UpperLength);
            Vector3 bend = d1 - dir * Vector3.Dot(d1, dir);
            if (bend.sqrMagnitude < UpperLength * UpperLength * 4e-4f)
                bend = bendHint - dir * Vector3.Dot(bendHint, dir);
            if (bend.sqrMagnitude < 1e-8f)
                return false;
            bend.Normalize();
            _lastBend = bend;

            Vector3 normal = Vector3.Cross(dir, bend).normalized;
            Quaternion upperInv = Quaternion.Inverse(upper.rotation);
            Quaternion lowerInv = Quaternion.Inverse(lower.rotation);
            _upperBindInverse = Quaternion.Inverse(Quaternion.LookRotation(upperInv * d1, upperInv * normal));
            _lowerBindInverse = Quaternion.Inverse(Quaternion.LookRotation(lowerInv * d2, lowerInv * normal));
            IsValid = true;
            return true;
        }

        /// <summary>Rotates the upper and lower bones so the end reaches toward <paramref name="target"/>.</summary>
        /// <param name="bendHint">World direction the middle joint should point (knee forward, elbow down/out).</param>
        /// <param name="weight">0 keeps the incoming pose, 1 fully applies the IK.</param>
        public void Solve(Vector3 target, Vector3 bendHint, float weight)
        {
            if (!IsValid || weight <= 1e-4f || !RagdollMath.IsFinite(target))
                return;

            Vector3 root = Upper.position;
            Vector3 toTarget = target - root;
            float distance = toTarget.magnitude;
            if (distance < 1e-4f)
                return;

            Vector3 dir = toTarget / distance;
            float a = UpperLength;
            float b = LowerLength;
            distance = Mathf.Clamp(distance, Mathf.Abs(a - b) + 1e-3f, (a + b) * 0.9999f);

            Vector3 bend = bendHint - dir * Vector3.Dot(bendHint, dir);
            if (bend.sqrMagnitude < 1e-6f)
            {
                bend = _lastBend - dir * Vector3.Dot(_lastBend, dir);
                if (bend.sqrMagnitude < 1e-6f)
                    bend = Vector3.Cross(dir, Mathf.Abs(dir.x) < 0.9f ? Vector3.right : Vector3.forward);
            }
            bend.Normalize();
            _lastBend = bend;

            float cosA = Mathf.Clamp((a * a + distance * distance - b * b) / (2f * a * distance), -1f, 1f);
            float sinA = Mathf.Sqrt(Mathf.Max(0f, 1f - cosA * cosA));
            Vector3 mid = root + dir * (a * cosA) + bend * (a * sinA);
            Vector3 end = root + dir * distance;
            Vector3 normal = Vector3.Cross(dir, bend);

            Quaternion upperRotation = Quaternion.LookRotation(mid - root, normal) * _upperBindInverse;
            if (weight >= 0.999f)
            {
                Upper.rotation = upperRotation;
                Lower.rotation = Quaternion.LookRotation(end - mid, normal) * _lowerBindInverse;
            }
            else
            {
                Upper.rotation = Quaternion.Slerp(Upper.rotation, upperRotation, weight);
                Lower.rotation = Quaternion.Slerp(Lower.rotation, Quaternion.LookRotation(end - mid, normal) * _lowerBindInverse, weight);
            }
        }
    }
}
