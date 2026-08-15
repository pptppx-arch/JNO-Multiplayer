namespace Assets.Scripts.Multiplayer.CraftData
{
    using Assets.Scripts;
    using Assets.Scripts.Flight;
    using Assets.Scripts.Flight.Sim;
    using ModApi.Craft;
    using ModApi.State;
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Threading.Tasks;
    using System.Xml.Linq;
    using UnityEngine;

    public class CraftSpawner
    {
        public static async Task<CraftNode> SpawnCraft(int clientId, string craftData)
        {
            try
            {
                // 1. Decompress Base64 / GZip string if compressed, or use directly if raw XML
                string xmlString = craftData;
                if (!string.IsNullOrEmpty(craftData) && !craftData.TrimStart().StartsWith("<"))
                {
                    try
                    {
                        byte[] compressedBytes = Convert.FromBase64String(craftData);
                        using (MemoryStream ms = new MemoryStream(compressedBytes))
                        using (GZipStream gzip = new GZipStream(ms, CompressionMode.Decompress))
                        using (StreamReader reader = new StreamReader(gzip, System.Text.Encoding.UTF8))
                        {
                            xmlString = reader.ReadToEnd();
                        }
                    }
                    catch (Exception ex)
                    {
                        Mod.LogError($"[CraftSpawner] Failed to decompress craft XML for Client {clientId}: {ex.Message}");
                        return null;
                    }
                }

                // 2. Ensure FlightScene and local host/player craft exist before spawning
                var flightScene = FlightSceneScript.Instance;
                if (flightScene == null || flightScene.CraftNode == null)
                {
                    Mod.LogWarning($"[CraftSpawner] Flight scene or player craft not ready. Skipping spawn for Client {clientId}.");
                    return null;
                }

                CraftNode localPlayerCraft = flightScene.CraftNode as CraftNode;

                // 3. Parse XML & Load CraftData via Juno API
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
                    LaunchLocationType.SurfaceLockedGround
                );

                // 4. Spawn craft into the game world
                CraftNode remoteCraftNode = flightScene.SpawnCraft(
                    $"Remote_Player_{clientId}",
                    parsedCraftData,
                    launchLocation,
                    xml
                );

                CraftRegistry.RegisterCraft(clientId, remoteCraftNode);

                Mod.Log($"[CraftSpawner] Successfully spawned and registered craft for Client ID {clientId}.");
                return remoteCraftNode;
            }
            catch (Exception ex)
            {
                Mod.LogError($"[CraftSpawner] Error spawning craft for Client {clientId}: {ex.Message}");
                return null;
            }
        }
    }
}