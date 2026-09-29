using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace ActiveRagdoll
{
    /// <summary>
    /// Spawns a prefab at a spawn point. Hook <see cref="Spawn"/> up to any UnityEvent (a BIMOS button,
    /// an Interactable, a trigger): pick this object in the event slot, then <c>PrefabSpawner → Spawn ()</c>.
    /// </summary>
    [AddComponentMenu("Active Ragdoll/Prefab Spawner")]
    public sealed class PrefabSpawner : MonoBehaviour
    {
        [Tooltip("What to spawn.")]
        [SerializeField] private GameObject _prefab = null;

        [Tooltip("Where to spawn it (an empty GameObject). Uses its position and rotation. Empty = this object.")]
        [SerializeField] private Transform _spawnPoint = null;

        [Tooltip("Parent the spawned object to the spawn point.")]
        [SerializeField] private bool _parentToSpawnPoint = false;

        [Tooltip("Most spawned objects alive at once; the oldest is destroyed to make room. 0 = no limit.")]
        [SerializeField, Min(0)] private int _maxAlive = 0;

        [Tooltip("Minimum seconds between spawns (stops a button spamming).")]
        [SerializeField, Min(0f)] private float _cooldown = 0.25f;

        [SerializeField] private UnityEvent<GameObject> _onSpawned = new UnityEvent<GameObject>();

        private readonly List<GameObject> _spawned = new List<GameObject>();
        private float _lastSpawnTime = float.NegativeInfinity;

        public GameObject Prefab { get => _prefab; set => _prefab = value; }
        public Transform SpawnPoint { get => _spawnPoint; set => _spawnPoint = value; }

        /// <summary>Spawns the prefab at the spawn point. Use this from UnityEvents.</summary>
        public void Spawn() => SpawnAt(_spawnPoint != null ? _spawnPoint : transform);

        /// <summary>Spawns the prefab at another point (also selectable in UnityEvents).</summary>
        public void SpawnAt(Transform point)
        {
            if (_prefab == null)
            {
                Debug.LogWarning($"{ActiveRagdollCharacter.LogPrefix} PrefabSpawner '{name}' has no prefab assigned.", this);
                return;
            }
            if (Time.time - _lastSpawnTime < _cooldown)
                return;
            _lastSpawnTime = Time.time;

            if (point == null)
                point = transform;

            _spawned.RemoveAll(o => o == null);
            if (_maxAlive > 0)
                while (_spawned.Count >= _maxAlive)
                {
                    Destroy(_spawned[0]);
                    _spawned.RemoveAt(0);
                }

            GameObject instance = _parentToSpawnPoint
                ? Instantiate(_prefab, point.position, point.rotation, point)
                : Instantiate(_prefab, point.position, point.rotation);
            _spawned.Add(instance);
            _onSpawned?.Invoke(instance);
        }

        /// <summary>Destroys everything this spawner has spawned.</summary>
        public void DespawnAll()
        {
            foreach (GameObject o in _spawned)
                if (o != null)
                    Destroy(o);
            _spawned.Clear();
        }

        private void OnDrawGizmos()
        {
            Transform point = _spawnPoint != null ? _spawnPoint : transform;
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(point.position, 0.15f);
            Gizmos.DrawLine(point.position, point.position + point.forward * 0.5f);
        }
    }
}
