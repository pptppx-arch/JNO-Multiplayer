namespace Assets.Scripts.Multiplayer.CraftData
{
    using Assets.Scripts;
    using Assets.Scripts.Flight;
    using Assets.Scripts.Flight.Sim;
    using ModApi.Craft;
    using ModApi.State;
    using System;
    using System.Xml.Linq;
    using UnityEngine;

    public static class CraftSpawner
    {
        /// <summary>
        /// Spawns a kinematic remote proxy. This method must be called only by
        /// MultiplayerThread.Pump() from MultiplayerTelemetryRuntime.Update().
        /// </summary>
        public static CraftNode SpawnCraftOnGameThread(int clientId, string xmlString)
        {
            try
            {
                if (clientId < 0 || string.IsNullOrWhiteSpace(xmlString))
                {
                    Mod.LogWarning($"[CraftSpawner] Invalid craft spawn request for Client ID {clientId}.");
                    return null;
                }

                FlightSceneScript flightScene = FlightSceneScript.Instance;
                if (flightScene == null || flightScene.CraftNode == null)
                {
                    Mod.LogWarning($"[CraftSpawner] Flight scene or player craft is not ready for Client ID {clientId}.");
                    return null;
                }

                CraftNode localPlayerCraft = flightScene.CraftNode as CraftNode;
                if (localPlayerCraft == null)
                {
                    Mod.LogWarning($"[CraftSpawner] Local CraftNode is unavailable for Client ID {clientId}.");
                    return null;
                }

                XElement xml = XElement.Parse(xmlString);
                CraftData parsedCraftData = Game.Instance.CraftLoader.LoadCraftImmediate(xml);

                Vector3d spawnPosition = new Vector3d(0, 0, 0);
                LaunchLocation launchLocation = LaunchLocation.CreateLaunchLocation(
                    $"Client_{clientId}_Craft",
                    localPlayerCraft.Parent,
                    spawnPosition,
                    Vector3d.zero,
                    localPlayerCraft.Heading,
                    localPlayerCraft.ReferenceFrame,
                    LaunchLocationType.SurfaceLockedGround);

                CraftNode remoteCraftNode = flightScene.SpawnCraft(
                    $"Remote_Player_{clientId}",
                    parsedCraftData,
                    launchLocation,
                    xml);

                if (remoteCraftNode == null)
                {
                    Mod.LogError($"[CraftSpawner] Juno returned no craft for Client ID {clientId}.");
                    return null;
                }

                ICraftDebris remoteDebris = remoteCraftNode as ICraftDebris;
                Rigidbody remoteBody = remoteDebris == null ? null : remoteDebris.RigidBody;
                if (remoteBody != null)
                {
                    remoteBody.isKinematic = true;
                    remoteBody.velocity = Vector3.zero;
                    remoteBody.angularVelocity = Vector3.zero;
                }
                else
                {
                    Mod.LogWarning($"[CraftSpawner] Spawned remote craft {clientId} without an accessible Rigidbody.");
                }

                CraftRegistry.RegisterCraft(clientId, remoteCraftNode);
                Mod.Log($"[CraftSpawner] Spawned kinematic remote proxy for Client ID {clientId}.");
                return remoteCraftNode;
            }
            catch (Exception ex)
            {
                Mod.LogError($"[CraftSpawner] Error spawning craft for Client ID {clientId}: {ex.Message}");
                return null;
            }
        }
    }
}
