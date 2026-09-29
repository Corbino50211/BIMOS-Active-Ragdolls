using System;
using BIMOS;
using UnityEngine;
using InputDevice = UnityEngine.XR.InputDevice;
using InputDevices = UnityEngine.XR.InputDevices;
using HapticCapabilities = UnityEngine.XR.HapticCapabilities;
using XRNode = UnityEngine.XR.XRNode;

namespace ActiveRagdoll.BIMOSIntegration
{
    /// <summary>
    /// Connects a <see cref="BladeWeapon"/> to BIMOS hands. While a BIMOS hand holds the knife:
    /// <list type="bullet">
    /// <item>a limb it's stuck in goes weak, so you can drag, twist and pin the NPC by the handle;</item>
    /// <item>wounds are blamed on you (NPCs aggro on the player);</item>
    /// <item>the holding controller gets haptics: a thump on the way in, a grinding buzz while you push,
    /// pull or twist it in the wound, a pop when it comes out.</item>
    /// </list>
    /// Added automatically to every BladeWeapon that has a BIMOS <see cref="Grab"/>.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/BIMOS/BIMOS Blade")]
    [RequireComponent(typeof(BladeWeapon))]
    public sealed class BIMOSBlade : MonoBehaviour, IBladeWielder
    {
        [SerializeField] private bool _haptics = true;
        [SerializeField, Range(0f, 2f)] private float _hapticStrength = 1f;

        [Header("Sounds (optional)")]
        [SerializeField] private AudioClip[] _stabSounds = Array.Empty<AudioClip>();
        [SerializeField] private AudioClip[] _extractSounds = Array.Empty<AudioClip>();
        [SerializeField, Range(0f, 1f)] private float _volume = 0.8f;

        private BladeWeapon _blade;
        private Grab[] _grabs = Array.Empty<Grab>();
        private AudioSource _audio;

        public bool IsHoldingBlade => HoldingHand(out _) != null;

        public GameObject Wielder
        {
            get
            {
                Hand hand = HoldingHand(out _);
                return hand != null ? hand.gameObject : null;
            }
        }

        private void Awake()
        {
            _blade = GetComponent<BladeWeapon>();
            _blade.SetWielder(this);
            RefreshGrabs();
        }

        /// <summary>Re-scans the knife for BIMOS grabs (call after adding grabs at runtime).</summary>
        public void RefreshGrabs()
        {
            Transform root = transform;
            ArticulationBody articulation = GetComponentInParent<ArticulationBody>();
            if (articulation != null)
            {
                foreach (ArticulationBody ab in GetComponentsInParent<ArticulationBody>())
                    if (ab.isRoot)
                        root = ab.transform;
            }
            _grabs = root.GetComponentsInChildren<Grab>(true);
        }

        private Hand HoldingHand(out Hand other)
        {
            Hand first = null;
            other = null;
            foreach (Grab grab in _grabs)
            {
                if (grab == null)
                    continue;
                if (Consider(grab.LeftHand, ref first, ref other) || Consider(grab.RightHand, ref first, ref other))
                    break;
            }
            return first;
        }

        private static bool Consider(Hand hand, ref Hand first, ref Hand other)
        {
            if (hand == null || hand == first)
                return false;
            if (first == null)
            {
                first = hand;
                return false;
            }
            other = hand;
            return true;
        }

        public void OnBladeFeedback(BladeWeapon blade, BladeFeedback kind, float intensity)
        {
            switch (kind)
            {
                case BladeFeedback.Embed:
                    Buzz(Mathf.Clamp01(0.45f + intensity * 0.1f), 0.08f);
                    Play(_stabSounds);
                    break;
                case BladeFeedback.Stab:
                    Buzz(0.35f + 0.3f * intensity, 0.05f);
                    Play(_stabSounds);
                    break;
                case BladeFeedback.Grind:
                    Buzz(0.1f + 0.35f * intensity, 0.06f);
                    break;
                case BladeFeedback.Extract:
                    Buzz(0.35f, 0.05f);
                    Play(_extractSounds);
                    break;
                case BladeFeedback.TearOut:
                    Buzz(0.8f, 0.1f);
                    Play(_extractSounds);
                    break;
                case BladeFeedback.Glance:
                    Buzz(0.25f + 0.25f * intensity, 0.04f);
                    break;
            }
        }

        private void Buzz(float amplitude, float duration)
        {
            if (!_haptics || _hapticStrength <= 0f)
                return;
            Hand hand = HoldingHand(out Hand other);
            Send(hand, amplitude, duration);
            Send(other, amplitude, duration);
        }

        private void Send(Hand hand, float amplitude, float duration)
        {
            if (hand == null)
                return;
            InputDevice device = InputDevices.GetDeviceAtXRNode(hand.IsLeftHand ? XRNode.LeftHand : XRNode.RightHand);
            if (device.isValid && device.TryGetHapticCapabilities(out HapticCapabilities caps) && caps.supportsImpulse)
                device.SendHapticImpulse(0u, Mathf.Clamp01(amplitude * _hapticStrength), duration);
        }

        private void Play(AudioClip[] clips)
        {
            if (clips == null || clips.Length == 0)
                return;
            AudioClip clip = clips[UnityEngine.Random.Range(0, clips.Length)];
            if (clip == null)
                return;
            if (_audio == null)
            {
                _audio = gameObject.AddComponent<AudioSource>();
                _audio.playOnAwake = false;
                _audio.spatialBlend = 1f;
            }
            _audio.PlayOneShot(clip, _volume);
        }

        private void OnDestroy()
        {
            if (_blade != null)
                _blade.SetWielder(null);
        }
    }
}
