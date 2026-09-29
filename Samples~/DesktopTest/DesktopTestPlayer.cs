using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ActiveRagdoll.Samples
{
    /// <summary>
    /// Mouse/keyboard stand-in for the BIMOS player, for testing active ragdoll NPCs without a headset.
    /// It is a physical capsule (NPC punches really shove it) with a <see cref="PlayerHealth"/> and a
    /// <see cref="CombatTarget"/> on team 0, so NPCs hunt it exactly as they would a BIMOS player.
    /// <para>
    /// WASD move · mouse look · LMB shoot · RMB shove · F stab (short-range thrust) · G grenade at crosshair ·
    /// V throw a knife (it sticks) · hold Q twist / E yank the knife you're looking at · K kill all · R respawn all ·
    /// T slow motion · Esc release cursor
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DesktopTestPlayer : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float _moveSpeed = 3.5f;
        [SerializeField] private float _lookSensitivity = 0.12f;
        [SerializeField] private float _eyeHeight = 1.65f;

        [Header("Weapons")]
        [SerializeField] private float _gunDamage = 34f;
        [SerializeField] private float _gunImpulse = 12f;
        [SerializeField] private float _shoveImpulse = 45f;
        [SerializeField] private float _stabDamage = 40f;
        [SerializeField] private float _stabImpulse = 10f;
        [SerializeField] private float _grenadeDamage = 80f;
        [SerializeField] private float _grenadeRadius = 3.5f;
        [SerializeField] private float _grenadeVelocity = 7f;
        [SerializeField] private float _knifeThrowSpeed = 12f;
        [Tooltip("Torque (N·m) Q applies about a stuck knife's blade, like a wrist turning it.")]
        [SerializeField] private float _knifeTwistTorque = 3f;
        [Tooltip("Force (N) E pulls a stuck knife out with.")]
        [SerializeField] private float _knifeYankForce = 250f;

        private Camera _camera;
        private Rigidbody _body;
        private PlayerHealth _health;
        private float _pitch;
        private float _yaw;
        private BladeWeapon _workedKnife;
        private bool _slowMotion;
        private string _lastEvent = "";
        private readonly List<(NPCStateMachine machine, Vector3 position, Quaternion rotation)> _spawns =
            new List<(NPCStateMachine, Vector3, Quaternion)>();

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            if (_body == null)
                _body = gameObject.AddComponent<Rigidbody>();
            _body.mass = 75f;
            _body.constraints = RigidbodyConstraints.FreezeRotation;
            _body.interpolation = RigidbodyInterpolation.Interpolate;

            if (GetComponent<Collider>() == null)
            {
                var capsule = gameObject.AddComponent<CapsuleCollider>();
                capsule.height = 1.8f;
                capsule.radius = 0.3f;
                capsule.center = new Vector3(0f, 0.9f, 0f);
            }

            _health = GetComponent<PlayerHealth>();
            if (_health == null)
                _health = gameObject.AddComponent<PlayerHealth>();

            _camera = GetComponentInChildren<Camera>();
            if (_camera == null)
            {
                var cam = new GameObject("Camera");
                cam.transform.SetParent(transform, false);
                _camera = cam.AddComponent<Camera>();
                _camera.nearClipPlane = 0.05f;
                cam.tag = "MainCamera";
            }
            _camera.transform.localPosition = new Vector3(0f, _eyeHeight, 0f);

            if (GetComponent<CombatTarget>() == null)
                gameObject.AddComponent<DesktopTarget>().Configure(_camera.transform, _body);

            _yaw = transform.eulerAngles.y;
        }

        private void Start()
        {
            foreach (NPCStateMachine machine in FindObjectsByType<NPCStateMachine>(FindObjectsSortMode.None))
            {
                ActiveRagdollCharacter c = machine.GetComponent<ActiveRagdollCharacter>();
                if (c != null && c.IsValid)
                    _spawns.Add((machine, c.Position - Vector3.up * c.StandingPelvisHeight, c.HeadingRotation));
            }
            Cursor.lockState = CursorLockMode.Locked;
        }

        private void Update()
        {
            Keyboard kb = Keyboard.current;
            Mouse mouse = Mouse.current;
            if (kb == null || mouse == null)
                return;

            if (kb.escapeKey.wasPressedThisFrame)
                Cursor.lockState = CursorLockMode.None;
            if (mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                return;
            }

            if (Cursor.lockState == CursorLockMode.Locked)
            {
                Vector2 delta = mouse.delta.ReadValue() * _lookSensitivity;
                _yaw += delta.x;
                _pitch = Mathf.Clamp(_pitch - delta.y, -85f, 85f);
                transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
                _camera.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);

                if (mouse.leftButton.wasPressedThisFrame) Shoot();
                if (mouse.rightButton.wasPressedThisFrame) Shove();
            }

            if (kb.fKey.wasPressedThisFrame) Stab();
            if (kb.gKey.wasPressedThisFrame) Grenade();
            if (kb.vKey.wasPressedThisFrame) ThrowKnife();
            if (kb.qKey.wasPressedThisFrame || kb.eKey.wasPressedThisFrame) PickKnife();
            if ((kb.qKey.isPressed || kb.eKey.isPressed) && _workedKnife != null)
            {
                Impalement wound = _workedKnife.Current;
                _lastEvent = wound != null
                    ? $"Knife: depth {wound.Depth * 100f:0.0} cm, twisted {wound.TwistAngle:0}°, loose {wound.Looseness:P0}"
                    : "Knife came out";
            }
            if (kb.kKey.wasPressedThisFrame) KillAll();
            if (kb.rKey.wasPressedThisFrame) RespawnAll();
            if (kb.tKey.wasPressedThisFrame)
            {
                _slowMotion = !_slowMotion;
                Time.timeScale = _slowMotion ? 0.25f : 1f;
            }
        }

        private void FixedUpdate()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null)
                return;
            WorkKnife(kb);
            Vector2 input = new Vector2(
                (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f),
                (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f));
            Vector3 move = transform.rotation * new Vector3(input.x, 0f, input.y);
            Vector3 target = Vector3.ClampMagnitude(move, 1f) * _moveSpeed;
            Vector3 v = _body.linearVelocity;
            Vector3 change = new Vector3(target.x - v.x, 0f, target.z - v.z);
            _body.AddForce(Vector3.ClampMagnitude(change, 1f) * 20f, ForceMode.Acceleration);
        }

        private Ray AimRay() => new Ray(_camera.transform.position, _camera.transform.forward);

        private void Shoot()
        {
            Ray ray = AimRay();
            if (Ballistics.FireHitscan(ray.origin, ray.direction, 200f, ~0, transform, _gunDamage, _gunImpulse, gameObject, out RaycastHit hit))
                _lastEvent = $"Shot {Describe(hit.collider)}";
        }

        private void Shove()
        {
            Ray ray = AimRay();
            if (!PhysicsQuery.Raycast(ray.origin, ray.direction, 3f, ~0, null, transform, out RaycastHit hit))
                return;
            IDamageable target = Damageables.Find(hit.collider);
            Vector3 impulse = ray.direction * _shoveImpulse;
            if (target != null)
                target.ApplyDamage(new DamageInfo(0f, DamageType.Blunt, hit.point, ray.direction, impulse, gameObject, hit.collider));
            else if (hit.rigidbody != null)
                hit.rigidbody.AddForceAtPosition(impulse, hit.point, ForceMode.Impulse);
            _lastEvent = $"Shoved {Describe(hit.collider)}";
        }

        private void Stab()
        {
            Ray ray = AimRay();
            if (Ballistics.FireHitscan(ray.origin, ray.direction, 1.8f, ~0, transform, _stabDamage, _stabImpulse, gameObject, out RaycastHit hit, DamageType.Stab))
                _lastEvent = $"Stabbed {Describe(hit.collider)}";
        }

        private void ThrowKnife()
        {
            Ray ray = AimRay();
            var knife = new GameObject("Thrown Knife");
            knife.transform.SetPositionAndRotation(ray.origin + ray.direction * 0.5f, Quaternion.LookRotation(ray.direction));
            var body = knife.AddComponent<Rigidbody>();
            body.mass = 0.25f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            KnifePart(knife, new Vector3(0f, 0f, 0.055f), new Vector3(0.028f, 0.022f, 0.11f));
            KnifePart(knife, new Vector3(0f, 0f, 0.115f), new Vector3(0.024f, 0.06f, 0.01f));
            Collider blade = KnifePart(knife, new Vector3(0f, 0f, 0.21f), new Vector3(0.006f, 0.03f, 0.18f));
            var tip = new GameObject("Tip").transform;
            tip.SetParent(knife.transform, false);
            tip.localPosition = new Vector3(0f, 0f, 0.3f);
            knife.AddComponent<BladeWeapon>().Configure(tip, Vector3.forward, 0.18f, new[] { blade });
            foreach (Collider c in GetComponentsInChildren<Collider>())
                foreach (Collider k in knife.GetComponentsInChildren<Collider>())
                    Physics.IgnoreCollision(c, k);
            body.linearVelocity = ray.direction * _knifeThrowSpeed + _body.linearVelocity;
            Destroy(knife, 60f);
            _lastEvent = "Threw a knife";
        }

        private static Collider KnifePart(GameObject knife, Vector3 localPosition, Vector3 size)
        {
            GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.transform.SetParent(knife.transform, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = size;
            return part.GetComponent<Collider>();
        }

        /// <summary>Picks the stuck blade nearest the crosshair for Q (twist) / E (yank), what a hand would do in VR.</summary>
        private void PickKnife()
        {
            Ray ray = AimRay();
            _workedKnife = null;
            float bestScore = 0.97f;
            foreach (BladeWeapon blade in BladeWeapon.All)
            {
                if (!blade.IsEmbedded) continue;
                Vector3 to = blade.transform.position - ray.origin;
                float score = Vector3.Dot(to.normalized, ray.direction);
                if (score > bestScore && to.magnitude < 6f) { bestScore = score; _workedKnife = blade; }
            }
            if (_workedKnife == null)
                _lastEvent = "No stuck knife in view";
        }

        private void WorkKnife(Keyboard kb)
        {
            if (_workedKnife == null || !_workedKnife.IsEmbedded)
                return;
            Rigidbody body = _workedKnife.GetComponent<Rigidbody>();
            if (body == null)
                return;
            if (kb.qKey.isPressed)
                body.AddTorque(_workedKnife.BladeAxisWorld * _knifeTwistTorque);
            if (kb.eKey.isPressed)
                body.AddForce(-_workedKnife.BladeAxisWorld * _knifeYankForce);
        }

        private void Grenade()
        {
            Ray ray = AimRay();
            Vector3 point = PhysicsQuery.Raycast(ray.origin, ray.direction, 60f, ~0, null, transform, out RaycastHit hit)
                ? hit.point
                : ray.origin + ray.direction * 10f;
            int affected = Ballistics.Explode(point, _grenadeRadius, _grenadeDamage, _grenadeVelocity, ~0, gameObject);
            _lastEvent = $"Explosion hit {affected} bodies";
        }

        private void KillAll()
        {
            foreach (ActiveRagdollCharacter c in ActiveRagdollCharacter.All.ToArrayCopy())
                c.Kill();
            _lastEvent = "Killed all";
        }

        private void RespawnAll()
        {
            foreach (var (machine, position, rotation) in _spawns)
            {
                if (machine == null) continue;
                if (!machine.gameObject.activeSelf) machine.gameObject.SetActive(true);
                machine.Respawn(position, rotation);
            }
            _health.Respawn();
            _lastEvent = "Respawned";
        }

        private static string Describe(Collider c)
        {
            BodyPart part = c != null && c.attachedRigidbody != null ? c.attachedRigidbody.GetComponent<BodyPart>() : null;
            return part != null ? $"{part.Character.name} ({part.Role})" : (c != null ? c.name : "nothing");
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10f, 10f, 480f, 400f), GUI.skin.box);
            GUILayout.Label($"Health: {_health.CurrentHealth:0}/{_health.MaxHealth:0}{(_health.IsAlive ? "" : "  (DEAD - press R)")}");
            GUILayout.Label("WASD move · mouse look · LMB shoot · RMB shove · F stab · G grenade");
            GUILayout.Label("V throw knife · hold Q twist / E yank the stuck knife you look at");
            GUILayout.Label("K kill all · R respawn all · T slow-mo · Esc cursor");
            GUILayout.Label(_lastEvent);
            foreach (ActiveRagdollCharacter c in ActiveRagdollCharacter.All)
            {
                NPCStateMachine m = c.GetComponent<NPCStateMachine>();
                string hp = c.Health != null ? $"{c.Health.CurrentHealth:0}" : "-";
                GUILayout.Label($"{c.name}: {(m != null ? m.CurrentState.ToString() : "")}  hp {hp}");
            }
            GUILayout.EndArea();

            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            GUI.Label(new Rect(cx - 5f, cy - 10f, 20f, 20f), "+");
        }

        private void OnDisable()
        {
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None;
        }
    }

    internal static class ListExtensions
    {
        public static T[] ToArrayCopy<T>(this IReadOnlyList<T> list)
        {
            var copy = new T[list.Count];
            for (int i = 0; i < list.Count; i++) copy[i] = list[i];
            return copy;
        }
    }
}
