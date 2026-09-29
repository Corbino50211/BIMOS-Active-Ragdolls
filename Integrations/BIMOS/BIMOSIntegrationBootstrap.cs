using BIMOS;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ActiveRagdoll.BIMOSIntegration
{
    /// <summary>
    /// Zero-setup glue: gives every BIMOS <see cref="Player"/> a <see cref="BIMOSPlayerTarget"/> so NPCs can
    /// find it, and every grabbable <see cref="BladeWeapon"/> a <see cref="BIMOSBlade"/> (hand holding, haptics). Runs after each scene load, and again (rate limited) whenever an NPC finds no hostile targets,
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

        private static void OnBladeRegistered(BladeWeapon blade) => EnsureBladeWielder(blade);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            CombatTarget.DiscoveryHandler = EnsurePlayerTargets;
            EnsurePlayerTargets();

            BladeWeapon.Registered -= OnBladeRegistered;
            BladeWeapon.Registered += OnBladeRegistered;
            for (int i = 0; i < BladeWeapon.All.Count; i++)
                EnsureBladeWielder(BladeWeapon.All[i]);
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

        /// <summary>Adds a <see cref="BIMOSBlade"/> to a blade that BIMOS hands can grab and that has no wielder yet.</summary>
        public static void EnsureBladeWielder(BladeWeapon blade)
        {
            if (!Enabled || blade == null || blade.GetComponent<IBladeWielder>() != null)
                return;
            Transform root = blade.transform;
            ArticulationBody articulation = blade.GetComponentInParent<ArticulationBody>();
            if (articulation != null)
                foreach (ArticulationBody ab in blade.GetComponentsInParent<ArticulationBody>())
                    if (ab.isRoot)
                        root = ab.transform;
            if (root.GetComponentInChildren<Grab>(true) != null)
                blade.gameObject.AddComponent<BIMOSBlade>();
        }
    }
}
