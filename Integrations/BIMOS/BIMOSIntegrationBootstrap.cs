using BIMOS;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ActiveRagdoll.BIMOSIntegration
{
    /// <summary>
    /// Zero-setup glue: gives every BIMOS <see cref="Player"/> a <see cref="BIMOSPlayerTarget"/> so NPCs can
    /// find it. Runs after each scene load, and again (rate limited) whenever an NPC finds no hostile targets,
    /// which covers players spawned at runtime by BIMOS spawn points.
    /// Set <see cref="Enabled"/> to false before the first scene loads to opt out, or define
    /// ACTIVE_RAGDOLL_NO_BIMOS_BOOTSTRAP to compile it out.
    /// </summary>
    public static class BIMOSIntegrationBootstrap
    {
        public static bool Enabled = true;

#if !ACTIVE_RAGDOLL_NO_BIMOS_BOOTSTRAP
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Enabled = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            CombatTarget.DiscoveryHandler = EnsurePlayerTargets;
            EnsurePlayerTargets();
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => EnsurePlayerTargets();
#endif

        /// <summary>Adds a <see cref="BIMOSPlayerTarget"/> to every BIMOS player that has no <see cref="CombatTarget"/>.</summary>
        public static void EnsurePlayerTargets()
        {
            if (!Enabled)
                return;
            Player[] players = Object.FindObjectsByType<Player>(FindObjectsSortMode.None);
            foreach (Player player in players)
            {
                if (player == null || player.GetComponentInParent<CombatTarget>() != null)
                    continue;
                player.gameObject.AddComponent<BIMOSPlayerTarget>();
            }
        }
    }
}
