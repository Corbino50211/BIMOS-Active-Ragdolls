using System;
using System.Collections.Generic;
using BIMOS;
using UnityEngine;
using UnityEngine.Events;

namespace ActiveRagdoll.BIMOSIntegration
{
    /// <summary>
    /// Lets BIMOS hands grab any part of an active ragdoll, alive or dead. Each body collider gets a
    /// <see cref="Grab"/> (BIMOS finds grabs by OverlapBox on any layer and joins the hand to the body's
    /// rigidbody with a FixedJoint). While a limb is held its whole chain is weakened through
    /// <see cref="JointMotorDriver.SetGrabbed"/>, so the player can wrench an arm around or drag the NPC by
    /// the head, while the rest of the body keeps fighting. Grabbing a live NPC also makes it aggro on you.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/BIMOS/BIMOS Ragdoll Grabs")]
    [RequireComponent(typeof(ActiveRagdollCharacter))]
    public sealed class BIMOSRagdollGrabs : MonoBehaviour
    {
        [Tooltip("Add a basic Grab to every ragdoll collider that has none. Existing Grabs (e.g. with custom hand poses) are kept.")]
        [SerializeField] private bool _addGrabsAutomatically = true;

        [Tooltip("Hand pose for automatically added grabs (optional).")]
        [SerializeField] private HandPose _handPose = null;

        [Tooltip("Allow grabbing while the NPC is alive (Boneworks-style). Otherwise only corpses are grabbable.")]
        [SerializeField] private bool _grabbableWhenAlive = true;

        [Tooltip("Grabbing a live NPC makes it target the grabber.")]
        [SerializeField] private bool _aggroOnGrab = true;

        [SerializeField] private UnityEvent<BoneRole> _onGrabbed = new UnityEvent<BoneRole>();
        [SerializeField] private UnityEvent<BoneRole> _onReleased = new UnityEvent<BoneRole>();

        private ActiveRagdollCharacter _character;
        private Grab[][] _grabs = Array.Empty<Grab[]>();
        private bool[] _held = Array.Empty<bool>();
        private bool _grabsEnabled = true;
        private bool _initialized;

        public event Action<BoneRole> Grabbed;
        public event Action<BoneRole> Released;

        public bool IsHeld(BoneRole role)
        {
            int i = _character != null ? _character.GetBoneIndex(role) : -1;
            return i >= 0 && i < _held.Length && _held[i];
        }

        public bool IsAnyPartHeld
        {
            get
            {
                for (int i = 0; i < _held.Length; i++)
                    if (_held[i]) return true;
                return false;
            }
        }

        private void Start()
        {
            _character = GetComponent<ActiveRagdollCharacter>();
            if (_character == null || !_character.IsValid)
            {
                enabled = false;
                return;
            }

            var bones = _character.Bones;
            _grabs = new Grab[bones.Count][];
            _held = new bool[bones.Count];
            var list = new List<Grab>(4);
            for (int i = 0; i < bones.Count; i++)
            {
                RagdollBone bone = bones[i];
                list.Clear();
                if (bone.body != null)
                    bone.body.GetComponentsInChildren(true, list);

                if (list.Count == 0 && _addGrabsAutomatically && bone.body != null)
                {
                    foreach (Collider c in bone.body.GetComponentsInChildren<Collider>(true))
                    {
                        if (c.isTrigger || c.attachedRigidbody != bone.body || c.GetComponent<Grab>() != null)
                            continue;
                        Grab grab = c.gameObject.AddComponent<Grab>();
                        grab.HandPose = _handPose;
                        grab.IsLeftHanded = true;
                        grab.IsRightHanded = true;
                        grab.EnableGrabs = Array.Empty<Grab>();
                        grab.DisableGrabs = Array.Empty<Grab>();
                        list.Add(grab);
                    }
                }
                _grabs[i] = list.ToArray();
            }

            _character.Died += OnDied;
            _character.Revived += OnRevived;
            _initialized = true;
            ApplyGrabbable();
        }

        private void OnDestroy()
        {
            if (_character == null)
                return;
            _character.Died -= OnDied;
            _character.Revived -= OnRevived;
        }

        private void OnDied(ActiveRagdollCharacter character) => ApplyGrabbable();

        private void OnRevived(ActiveRagdollCharacter character) => ApplyGrabbable();

        private void ApplyGrabbable()
        {
            bool enable = _grabbableWhenAlive || _character.IsDead;
            if (enable == _grabsEnabled)
                return;
            _grabsEnabled = enable;
            for (int i = 0; i < _grabs.Length; i++)
                foreach (Grab grab in _grabs[i])
                    if (grab != null)
                        grab.enabled = enable; // disabling a BIMOS Grab also releases any hand on it
        }

        private void FixedUpdate()
        {
            if (!_initialized)
                return;

            JointMotorDriver motors = _character.Motors;
            var bones = _character.Bones;
            for (int i = 0; i < _grabs.Length; i++)
            {
                Hand holder = null;
                Grab[] grabs = _grabs[i];
                for (int g = 0; g < grabs.Length && holder == null; g++)
                {
                    Grab grab = grabs[g];
                    if (grab == null) continue;
                    if (grab.LeftHand != null) holder = grab.LeftHand;
                    else if (grab.RightHand != null) holder = grab.RightHand;
                }

                bool held = holder != null;
                if (held == _held[i])
                    continue;

                _held[i] = held;
                if (motors != null && motors.IsInitialized)
                    motors.SetGrabbed(i, held);

                BoneRole role = bones[i].role;
                if (held)
                {
                    if (_aggroOnGrab && !_character.IsDead)
                    {
                        NPCPerception perception = GetComponent<NPCPerception>();
                        if (perception != null)
                            perception.NotifyAttacked(holder.gameObject);
                    }
                    Grabbed?.Invoke(role);
                    _onGrabbed?.Invoke(role);
                }
                else
                {
                    Released?.Invoke(role);
                    _onReleased?.Invoke(role);
                }
            }
        }

        private void OnDisable()
        {
            if (!_initialized)
                return;
            JointMotorDriver motors = _character != null ? _character.Motors : null;
            for (int i = 0; i < _held.Length; i++)
            {
                if (_held[i] && motors != null && motors.IsInitialized)
                    motors.SetGrabbed(i, false);
                _held[i] = false;
            }
        }
    }
}
