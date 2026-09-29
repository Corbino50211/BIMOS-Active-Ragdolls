using System;
using System.Collections.Generic;
using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// A weapon an active-ragdoll NPC can pick up and use. The NPC grips it with its physical hand (a
    /// FixedJoint to the weapon's Rigidbody or ArticulationBody) and aims it with its arm muscles, so recoil,
    /// shoves and grabs all act on the real weapon.
    /// <para>
    /// Implementations: <see cref="HitscanGun"/>; the generated BIMOS demo-pistol adapter
    /// (Tools > Active Ragdoll > Install BIMOS Demo Weapon Support); or derive your own and implement
    /// <see cref="NPCFire"/>.
    /// </para>
    /// </summary>
    public abstract class NPCWeapon : MonoBehaviour
    {
        private static readonly List<NPCWeapon> s_all = new List<NPCWeapon>();
        private static float s_lastDiscovery = float.NegativeInfinity;

        /// <summary>Every enabled weapon. Do not modify.</summary>
        public static IReadOnlyList<NPCWeapon> All => s_all;

        /// <summary>
        /// Optional hook invoked (rate limited) when an NPC looks for weapons, so integrations can attach
        /// adapters to weapons spawned at runtime.
        /// </summary>
        public static Action DiscoveryHandler;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_all.Clear();
            s_lastDiscovery = float.NegativeInfinity;
        }

        public static void RequestDiscovery()
        {
            if (DiscoveryHandler == null || Time.unscaledTime - s_lastDiscovery < 2f)
                return;
            s_lastDiscovery = Time.unscaledTime;
            DiscoveryHandler();
        }

        [Header("NPC Use")]
        [Tooltip("Where the NPC's palm goes. Defaults to a child named 'Grip', else the weapon's centre of mass.")]
        [SerializeField] private Transform _grip = null;

        [Tooltip("Barrel tip, pointing along the line of fire.")]
        [SerializeField] private Transform _muzzle = null;

        [Tooltip("Maximum range (m) at which NPCs will shoot with it.")]
        [SerializeField, Min(0.1f)] private float _range = 30f;

        [Tooltip("Seconds between NPC shots.")]
        [SerializeField, Min(0.02f)] private float _npcFireInterval = 0.35f;

        [Tooltip("How closely (degrees) the arm aims the muzzle before the NPC is considered on target.")]
        [SerializeField, Range(0.5f, 30f)] private float _aimTolerance = 5f;

        private Transform _defaultGrip;

        public NPCWeaponHolder Holder { get; internal set; }
        public Rigidbody Rigidbody { get; private set; }

        /// <summary>The articulation link the weapon's grip belongs to (BIMOS demo guns are articulations).</summary>
        public ArticulationBody Articulation { get; private set; }

        /// <summary>Root of the articulation chain, if any (what has to be teleported).</summary>
        public ArticulationBody ArticulationRoot { get; private set; }

        /// <summary>The body the NPC's hand joint connects to.</summary>
        public Transform BodyTransform => Articulation != null ? Articulation.transform : (Rigidbody != null ? Rigidbody.transform : transform);

        /// <summary>The object that owns every collider of the weapon.</summary>
        public Transform RootTransform => ArticulationRoot != null ? ArticulationRoot.transform : BodyTransform;

        public virtual Transform Muzzle => _muzzle != null ? _muzzle : transform;
        public virtual Transform Grip => _grip != null ? _grip : _defaultGrip;
        public Vector3 GripPosition => Grip != null ? Grip.position : BodyTransform.position;
        public float Range => _range;
        public float FireInterval => _npcFireInterval;
        public float AimTolerance => _aimTolerance;

        /// <summary>True while something other than an NPC holds it (e.g. a BIMOS hand). NPC holders let go.</summary>
        public virtual bool IsHeldExternally => false;

        /// <summary>False when the weapon can't fire any more (NPCs then drop it).</summary>
        public virtual bool HasAmmo => true;

        public bool IsAvailable => isActiveAndEnabled && Holder == null && !IsHeldExternally && (Rigidbody != null || Articulation != null);

        /// <summary>Fires one shot for an NPC. <paramref name="shooter"/> is the NPC (for damage attribution).</summary>
        public abstract bool NPCFire(GameObject shooter);

        /// <summary>
        /// Fires one NPC shot straight at <paramref name="point"/> (aim assist): the muzzle is pointed at it
        /// for the duration of the shot only, so the gun's own firing code, effects and recoil are used.
        /// </summary>
        public bool NPCFireAt(GameObject shooter, Vector3 point)
        {
            Transform muzzle = Muzzle;
            Vector3 direction = muzzle != null ? point - muzzle.position : Vector3.zero;
            if (muzzle == null || direction.sqrMagnitude < 1e-4f
                || muzzle.GetComponent<Rigidbody>() != null || muzzle.GetComponent<ArticulationBody>() != null)
                return NPCFire(shooter);

            Quaternion saved = muzzle.rotation;
            muzzle.rotation = Quaternion.LookRotation(direction, muzzle.up);
            try
            {
                return NPCFire(shooter);
            }
            finally
            {
                muzzle.rotation = saved;
            }
        }

        protected virtual void Awake()
        {
            ResolveBodies();
        }

        protected void ResolveBodies()
        {
            Articulation = GetComponentInParent<ArticulationBody>();
            if (Articulation != null)
            {
                ArticulationRoot = Articulation;
                foreach (ArticulationBody ab in GetComponentsInParent<ArticulationBody>())
                    if (ab.isRoot)
                        ArticulationRoot = ab;
            }
            else
            {
                Rigidbody = GetComponentInParent<Rigidbody>();
            }

            if (_muzzle == null)
            {
                // A separate marker, so aim assist can turn it without turning the gun itself.
                var marker = new GameObject("NPC Muzzle (auto)");
                marker.transform.SetParent(transform, false);
                _muzzle = marker.transform;
            }

            if (_grip == null)
            {
                Transform named = FindChild(RootTransform, "Grip");
                if (named != null)
                    _defaultGrip = named;
                else
                {
                    var marker = new GameObject("NPC Grip (auto)");
                    marker.transform.SetParent(BodyTransform, false);
                    Vector3 com = Rigidbody != null ? Rigidbody.worldCenterOfMass : (Articulation != null ? Articulation.worldCenterOfMass : BodyTransform.position);
                    marker.transform.position = com;
                    _defaultGrip = marker.transform;
                }
            }
        }

        private static Transform FindChild(Transform root, string childName)
        {
            if (root == null) return null;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (string.Equals(t.name, childName, StringComparison.OrdinalIgnoreCase))
                    return t;
            return null;
        }

        protected virtual void OnEnable()
        {
            if (!s_all.Contains(this))
                s_all.Add(this);
        }

        protected virtual void OnDisable()
        {
            s_all.Remove(this);
            if (Holder != null)
                Holder.Drop();
        }

        protected virtual void OnValidate()
        {
            _range = Mathf.Max(0.1f, _range);
        }
    }
}
