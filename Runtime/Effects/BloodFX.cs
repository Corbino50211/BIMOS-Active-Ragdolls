using System.Collections.Generic;
using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Shared, asset-free blood renderer: one pooled world-space particle system for spray and drips, and a
    /// recycled pool of splat decals left where droplets land. Created on first use. Everything is emitted
    /// with <c>ParticleSystem.Emit</c>, so a gushing wound costs no allocations and no instantiation (Quest-safe).
    /// <para>
    /// Call <see cref="Spray"/> from your own code for custom effects. Assign <see cref="ParticleMaterial"/> and
    /// <see cref="SplatMaterial"/> before first use to replace the generated look.
    /// </para>
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class BloodFX : MonoBehaviour
    {
        private const int MaxParticles = 1200;

        /// <summary>Optional material for blood particles (alpha blended, uses vertex colour). Null = generated.</summary>
        public static Material ParticleMaterial;

        /// <summary>Optional material for floor/wall splats. Null = generated.</summary>
        public static Material SplatMaterial;

        /// <summary>Most splats alive at once; the oldest is reused.</summary>
        public static int MaxSplats = 80;

        /// <summary>Fraction of landing droplets that leave a splat.</summary>
        public static float SplatChance = 0.2f;

        /// <summary>Layers droplets collide with (and splat on). Defaults to everything except ragdolls and the BIMOS rig.</summary>
        public static LayerMask? CollisionMask;

        private static BloodFX s_instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_instance = null;

        private ParticleSystem _particles;
        private readonly List<ParticleCollisionEvent> _events = new List<ParticleCollisionEvent>(16);
        private readonly List<Transform> _splats = new List<Transform>();
        private int _nextSplat;
        private float _splatBudget;
        private Mesh _quad;
        private Material _splatMaterial;

        private static BloodFX Instance
        {
            get
            {
                if (s_instance == null)
                    s_instance = new GameObject("Active Ragdoll Blood FX").AddComponent<BloodFX>();
                return s_instance;
            }
        }

        /// <summary>
        /// Emits <paramref name="count"/> droplets from <paramref name="position"/> in a cone around
        /// <paramref name="direction"/> (degrees), at <paramref name="speed"/> m/s ±40%, plus <paramref name="inherit"/> velocity.
        /// </summary>
        public static void Spray(Vector3 position, Vector3 direction, int count, float speed, float cone,
            Color color, float size = 0.025f, Vector3 inherit = default)
        {
            if (count <= 0 || !RagdollMath.IsFinite(position))
                return;
            BloodFX fx = Instance;
            Vector3 dir = RagdollMath.SafeNormalize(direction, Vector3.up);
            var p = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            for (int i = 0; i < count; i++)
            {
                Vector3 d = Vector3.Slerp(dir, Random.onUnitSphere, cone / 180f * Random.value).normalized;
                p.position = position + Random.insideUnitSphere * 0.01f;
                p.velocity = d * (speed * Random.Range(0.6f, 1.4f)) + inherit;
                p.startSize = size * Random.Range(0.5f, 1.5f);
                p.startLifetime = Random.Range(0.8f, 1.6f);
                Color c = Color.Lerp(color, color * 0.6f, Random.value);
                c.a = color.a;
                p.startColor = c;
                fx._particles.Emit(p, 1);
            }
        }

        private void Awake()
        {
            _quad = BuildQuad();
            _splatMaterial = SplatMaterial != null ? SplatMaterial : GeneratedMaterial("Blood Splat", BuildSplatTexture());
            BuildParticles();
        }

        private void BuildParticles()
        {
            _particles = gameObject.AddComponent<ParticleSystem>();
            _particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = _particles.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 1f;
            main.maxParticles = MaxParticles;
            main.startSpeed = 0f;
            main.scalingMode = ParticleSystemScalingMode.Shape;

            ParticleSystem.EmissionModule emission = _particles.emission;
            emission.enabled = false;
            ParticleSystem.ShapeModule shape = _particles.shape;
            shape.enabled = false;

            // Droplets thin out as they fly.
            ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = _particles.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.4f));

            ParticleSystem.CollisionModule collision = _particles.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.World;
            collision.mode = ParticleSystemCollisionMode.Collision3D;
            collision.quality = ParticleSystemCollisionQuality.High;
            collision.collidesWith = CollisionMask ?? DefaultMask();
            collision.dampen = 1f;
            collision.bounce = 0f;
            collision.lifetimeLoss = 1f; // a droplet dies where it lands (and leaves a splat)
            collision.radiusScale = 0.5f;
            collision.sendCollisionMessages = true;
            collision.enableDynamicColliders = true;

            var renderer = GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.03f;
            renderer.lengthScale = 1.5f;
            renderer.sharedMaterial = ParticleMaterial != null ? ParticleMaterial : GeneratedMaterial("Blood Droplet", BuildDropTexture());
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            _particles.Play();
        }

        private static int DefaultMask()
        {
            int mask = ~0;
            int ragdoll = LayerMask.NameToLayer(RagdollLayers.DefaultRagdollLayer);
            int rig = LayerMask.NameToLayer(RagdollLayers.BIMOSRigLayer);
            if (ragdoll >= 0) mask &= ~(1 << ragdoll);
            if (rig >= 0) mask &= ~(1 << rig);
            return mask;
        }

        private void Update()
        {
            // At most ~30 new splats a second, however much is gushing.
            _splatBudget = Mathf.Min(8f, _splatBudget + Time.deltaTime * 30f);
        }

        private void OnParticleCollision(GameObject other)
        {
            if (other == null || _splatBudget < 1f)
                return;
            // Never paint the bodies themselves (the colliders are filtered by layer, but not everyone uses layers).
            Rigidbody rb = other.GetComponentInParent<Rigidbody>();
            if (rb != null && rb.GetComponent<BodyPart>() != null)
                return;

            int n = _particles.GetCollisionEvents(other, _events);
            for (int i = 0; i < n && _splatBudget >= 1f; i++)
            {
                if (Random.value > SplatChance)
                    continue;
                ParticleCollisionEvent e = _events[i];
                PlaceSplat(e.intersection, e.normal, e.colliderComponent as Collider);
                _splatBudget -= 1f;
            }
        }

        private void PlaceSplat(Vector3 point, Vector3 normal, Collider onto)
        {
            if (!RagdollMath.IsFinite(point) || normal.sqrMagnitude < 0.5f)
                return;

            Transform splat;
            if (_splats.Count < Mathf.Max(1, MaxSplats))
            {
                var go = new GameObject("Blood Splat");
                go.AddComponent<MeshFilter>().sharedMesh = _quad;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _splatMaterial;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                splat = go.transform;
                _splats.Add(splat);
            }
            else
            {
                _nextSplat %= _splats.Count;
                splat = _splats[_nextSplat++];
                if (splat == null)
                {
                    _splats.RemoveAll(t => t == null);
                    return;
                }
            }

            splat.SetParent(null, false);
            float size = Random.Range(0.06f, 0.2f);
            splat.SetPositionAndRotation(point + normal * 0.003f,
                Quaternion.LookRotation(-normal) * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f)));
            splat.localScale = new Vector3(size, size, size);
            // Follow moving props; stay put on static geometry.
            if (onto != null && onto.attachedRigidbody != null)
                splat.SetParent(onto.transform, true);
            else
                splat.SetParent(transform, true);
            splat.gameObject.SetActive(true);
        }

        private void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
            foreach (Transform t in _splats)
                if (t != null && t.parent != transform)
                    Destroy(t.gameObject);
        }

        // ------------------------------------------------------------------ Generated assets

        private static Material GeneratedMaterial(string name, Texture2D texture)
        {
            // Sprites/Default and UI/Default are always included in builds and render in both the built-in
            // pipeline and URP (untagged pass), alpha blended with vertex colour.
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("UI/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
            if (shader == null)
            {
                Debug.LogWarning($"{ActiveRagdollCharacter.LogPrefix} No particle shader found for blood; assign BloodFX.ParticleMaterial / SplatMaterial.");
                shader = Shader.Find("Hidden/InternalErrorShader");
            }
            var material = new Material(shader) { name = name, mainTexture = texture };
            material.color = Color.white;
            return material;
        }

        private static Texture2D BuildDropTexture()
        {
            const int n = 32;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "Blood Droplet", wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    pixels[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.SmoothStep(0f, 1f, a * 1.6f) * 255f));
                }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }

        private static Texture2D BuildSplatTexture()
        {
            const int n = 128;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { name = "Blood Splat", wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[n * n];
            float seed = Random.Range(0f, 100f);

            // A few satellite droplets around the main pool.
            var drops = new Vector3[7];
            for (int i = 0; i < drops.Length; i++)
            {
                float angle = Random.Range(0f, Mathf.PI * 2f);
                float dist = Random.Range(0.55f, 0.85f);
                drops[i] = new Vector3(Mathf.Cos(angle) * dist, Mathf.Sin(angle) * dist, Random.Range(0.04f, 0.1f));
            }

            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float angle = Mathf.Atan2(v, u);
                    float edge = 0.42f + 0.12f * Mathf.PerlinNoise(seed + Mathf.Cos(angle) * 1.5f, seed + Mathf.Sin(angle) * 1.5f);
                    float a = Mathf.Clamp01((edge - r) * 25f);
                    foreach (Vector3 d in drops)
                    {
                        float dd = Vector2.Distance(new Vector2(u, v), new Vector2(d.x, d.y));
                        a = Mathf.Max(a, Mathf.Clamp01((d.z - dd) * 40f));
                    }
                    // Darker, thicker middle.
                    float shade = Mathf.Lerp(0.55f, 1f, Mathf.Clamp01(r / 0.5f));
                    pixels[y * n + x] = new Color32((byte)(110 * shade), (byte)(4 * shade), (byte)(6 * shade), (byte)(a * 235f));
                }
            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return tex;
        }

        private static Mesh BuildQuad()
        {
            var mesh = new Mesh { name = "Blood Splat Quad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
