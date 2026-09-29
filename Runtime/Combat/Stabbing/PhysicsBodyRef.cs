using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// A Rigidbody, an ArticulationBody (BIMOS demo items are articulations) or nothing (static world),
    /// behind one interface so stabbing works on any combination.
    /// </summary>
    internal readonly struct PhysicsBodyRef
    {
        public readonly Rigidbody Rigidbody;
        public readonly ArticulationBody Articulation;

        public PhysicsBodyRef(Rigidbody rigidbody, ArticulationBody articulation)
        {
            Rigidbody = rigidbody;
            Articulation = rigidbody == null ? articulation : null;
        }

        public static PhysicsBodyRef FromCollider(Collider collider)
        {
            if (collider == null)
                return default;
            return new PhysicsBodyRef(collider.attachedRigidbody, collider.attachedArticulationBody);
        }

        public static PhysicsBodyRef FromTransform(Transform t)
        {
            if (t == null)
                return default;
            Rigidbody rb = t.GetComponentInParent<Rigidbody>();
            ArticulationBody ab = t.GetComponentInParent<ArticulationBody>();
            // Take whichever is the closer ancestor.
            if (rb != null && ab != null)
                return ab.transform.IsChildOf(rb.transform) ? new PhysicsBodyRef(null, ab) : new PhysicsBodyRef(rb, null);
            return new PhysicsBodyRef(rb, ab);
        }

        /// <summary>False for the static world (and destroyed bodies).</summary>
        public bool Exists => Rigidbody != null || Articulation != null;

        /// <summary>True when forces move it (a kinematic rigidbody or immovable articulation acts as world).</summary>
        public bool IsDynamic => Rigidbody != null ? !Rigidbody.isKinematic : (Articulation != null && !Articulation.immovable);

        public Transform Transform => Rigidbody != null ? Rigidbody.transform : (Articulation != null ? Articulation.transform : null);
        public GameObject GameObject => Transform != null ? Transform.gameObject : null;

        public Vector3 LinearVelocity
        {
            get => Rigidbody != null ? Rigidbody.linearVelocity : (Articulation != null ? Articulation.linearVelocity : Vector3.zero);
            set
            {
                if (Rigidbody != null && !Rigidbody.isKinematic) Rigidbody.linearVelocity = value;
                else if (Articulation != null && !Articulation.immovable) Articulation.linearVelocity = value;
            }
        }

        public Vector3 AngularVelocity
        {
            get => Rigidbody != null ? Rigidbody.angularVelocity : (Articulation != null ? Articulation.angularVelocity : Vector3.zero);
            set
            {
                if (Rigidbody != null && !Rigidbody.isKinematic) Rigidbody.angularVelocity = value;
                else if (Articulation != null && !Articulation.immovable) Articulation.angularVelocity = value;
            }
        }

        public Vector3 WorldCenterOfMass => Rigidbody != null ? Rigidbody.worldCenterOfMass
            : (Articulation != null ? Articulation.worldCenterOfMass : Vector3.zero);

        public Vector3 GetPointVelocity(Vector3 worldPoint)
        {
            if (!Exists)
                return Vector3.zero;
            return LinearVelocity + Vector3.Cross(AngularVelocity, worldPoint - WorldCenterOfMass);
        }

        public void WakeUp()
        {
            if (Rigidbody != null) Rigidbody.WakeUp();
            else if (Articulation != null) Articulation.WakeUp();
        }

        public bool Is(PhysicsBodyRef other) => Rigidbody == other.Rigidbody && Articulation == other.Articulation;
    }
}
