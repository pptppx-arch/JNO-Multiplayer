namespace Assets.Scripts.Multiplayer
{
    using Assets.Scripts.Flight.Sim;
    using System;
    using System.Collections.Generic;

    public static class CraftRegistry
    {
        // Tracks ClientId -> CraftNode
        private static readonly Dictionary<int, CraftNode> _clientToCraft = new Dictionary<int, CraftNode>();

        // Tracks CraftNode -> ClientId
        private static readonly Dictionary<CraftNode, int> _craftToClient = new Dictionary<CraftNode, int>();

        public static void RegisterCraft(int clientId, CraftNode craftNode)
        {
            if (craftNode == null) return;

            // Remove existing registration if client re-spawned
            UnregisterClient(clientId);

            _clientToCraft[clientId] = craftNode;
            _craftToClient[craftNode] = clientId;

            Mod.Log($"[CraftRegistry] Bound Client ID {clientId} to Craft '{craftNode.Name}'.");
        }

        public static CraftNode GetCraft(int clientId)
        {
            _clientToCraft.TryGetValue(clientId, out var craft);
            return craft;
        }

        public static int GetOwnerId(CraftNode craftNode)
        {
            if (craftNode != null && _craftToClient.TryGetValue(craftNode, out int clientId))
            {
                return clientId;
            }
            return -1; // Unowned or local environment craft
        }

        public static bool IsLocalPlayerCraft(CraftNode craftNode)
        {
            return GetOwnerId(craftNode) == ClientConnection.LocalClientId;
        }

        public static void UnregisterClient(int clientId)
        {
            if (_clientToCraft.TryGetValue(clientId, out var oldCraft))
            {
                _craftToClient.Remove(oldCraft);
                _clientToCraft.Remove(clientId);
                Mod.Log($"[CraftRegistry] Unregistered craft for Client ID {clientId}.");
            }
        }

        // Unregisters a client and deletes their CraftNode from the scene.
        public static void DespawnCraft(int clientId)
        {
            if (_clientToCraft.TryGetValue(clientId, out var craftNode))
            {
                UnregisterClient(clientId);
                if (craftNode != null)
                {
                    try
                    {
                        craftNode.DestroyCraft();
                        Mod.Log($"[CraftRegistry] Despawned craft for Client ID {clientId}.");
                    }
                    catch (Exception ex)
                    {
                        Mod.LogError($"[CraftRegistry] Error deleting craft for Client ID {clientId}: {ex.Message}");
                    }
                }
            }
        }

        public static void ClearAll()
        {
            _clientToCraft.Clear();
            _craftToClient.Clear();
        }
    }
}