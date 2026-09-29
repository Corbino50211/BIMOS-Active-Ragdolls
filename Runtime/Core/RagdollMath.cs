using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>Math helpers shared by the active ragdoll systems. Every method is allocation free.</summary>
    public static class RagdollMath
    {
        public static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        public static bool IsFinite(Vector3 v) => IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);

        public static bool IsFinite(Quaternion q) => IsFinite(q.x) && IsFinite(q.y) && IsFinite(q.z) && IsFinite(q.w);

        public static Vector3 Flatten(Vector3 v) => new Vector3(v.x, 0f, v.z);

        public static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        {
            float sqr = v.sqrMagnitude;
            return sqr > 1e-10f ? v / Mathf.Sqrt(sqr) : fallback;
        }

        /// <summary>Shortest-arc rotation taking <paramref name="from"/> to <paramref name="to"/>, as a world-space rotation vector (axis * radians).</summary>
        public static Vector3 AngularError(Quaternion from, Quaternion to)
        {
            return ToRotationVector(to * Quaternion.Inverse(from));
        }

        /// <summary>Converts a rotation to axis * angle (radians), always taking the shortest arc.</summary>
        public static Vector3 ToRotationVector(Quaternion q)
        {
            if (q.w < 0f)
            {
                q.x = -q.x;
                q.y = -q.y;
                q.z = -q.z;
                q.w = -q.w;
            }

            float sinHalf = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
            if (sinHalf < 1e-6f)
                return Vector3.zero;

            float angle = 2f * Mathf.Atan2(sinHalf, q.w);
            float scale = angle / sinHalf;
            return new Vector3(q.x * scale, q.y * scale, q.z * scale);
        }

        /// <summary>Yaw (degrees, around world up) of a direction; returns <paramref name="fallback"/> for vertical/zero vectors.</summary>
        public static float Yaw(Vector3 direction, float fallback)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-8f)
                return fallback;
            return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        }

        public static Quaternion YawRotation(float yawDegrees) => Quaternion.Euler(0f, yawDegrees, 0f);

        /// <summary>Frame-rate independent exponential smoothing factor.</summary>
        public static float Smoothing(float sharpness, float deltaTime)
        {
            if (sharpness <= 0f) return 0f;
            return 1f - Mathf.Exp(-sharpness * deltaTime);
        }

        public static float SmoothStep01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        public static float EaseOutQuad(float t)
        {
            t = Mathf.Clamp01(t);
            return 1f - (1f - t) * (1f - t);
        }

        public static float InverseLerpUnclamped(float a, float b, float value)
        {
            float range = b - a;
            return Mathf.Abs(range) < 1e-6f ? 0f : (value - a) / range;
        }

        /// <summary>Quadratic Bezier (the same arc BIMOS's procedural feet use for steps).</summary>
        public static Vector3 QuadraticBezier(Vector3 a, Vector3 control, Vector3 b, float t)
        {
            return Vector3.Lerp(Vector3.Lerp(a, control, t), Vector3.Lerp(control, b, t), t);
        }

        /// <summary>
        /// Rotation that converts a body-local delta rotation into the joint's drive space
        /// (axis = X, secondaryAxis = Y). This is the standard ConfigurableJoint target rotation frame.
        /// </summary>
        public static Quaternion JointSpace(ConfigurableJoint joint)
        {
            Vector3 right = joint.axis;
            Vector3 forward = Vector3.Cross(joint.axis, joint.secondaryAxis);
            Vector3 up = Vector3.Cross(forward, right);
            if (forward.sqrMagnitude < 1e-8f || up.sqrMagnitude < 1e-8f)
                return Quaternion.identity;
            return Quaternion.LookRotation(forward.normalized, up.normalized);
        }

        /// <summary>
        /// Computes <see cref="ConfigurableJoint.targetRotation"/> so the joint drives the child's rotation
        /// relative to its connected (parent) body toward <paramref name="targetRelative"/>.
        /// </summary>
        /// <param name="jointSpace">Result of <see cref="JointSpace"/>.</param>
        /// <param name="jointSpaceInverse">Inverse of <paramref name="jointSpace"/>.</param>
        /// <param name="initialRelative">Inverse(parent.rotation) * child.rotation when the joint was created.</param>
        /// <param name="targetRelative">Desired Inverse(parent.rotation) * child.rotation.</param>
        public static Quaternion JointTargetRotation(Quaternion jointSpace, Quaternion jointSpaceInverse, Quaternion initialRelative, Quaternion targetRelative)
        {
            return jointSpaceInverse * Quaternion.Inverse(targetRelative) * initialRelative * jointSpace;
        }
    }
}
