namespace Assets.Scripts.Multiplayer
{
    using Assets.Scripts.Flight.Sim;
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Maps multiplayer client IDs to locally spawned craft nodes.
    /// All methods must be called on the Juno/Unity game thread.
    /// </summary>
    public static class CraftRegistry
    {
        private static readonly Dictionary<int, CraftNode> _clientToCraft =
            new Dictionary<int, CraftNode>();

        private static readonly Dictionary<CraftNode, int> _craftToClient =
            new Dictionary<CraftNode, int>();

        public static void RegisterCraft(int clientId, CraftNode craftNode)
        {
            if (clientId < 0 || craftNode == null) return;

            if (_clientToCraft.TryGetValue(clientId, out CraftNode existingCraft))
            {
                if (existingCraft == craftNode) return;

                _craftToClient.Remove(existingCraft);
                _clientToCraft.Remove(clientId);

                // The local host/player craft is owned by Juno and must never be destroyed
                // by multiplayer registry replacement logic.
                if (!IsLocalClientId(clientId))
                {
                    DestroyProxy(existingCraft, clientId, "replaced");
                }
            }

            _clientToCraft[clientId] = craftNode;
            _craftToClient[craftNode] = clientId;
            Mod.Log($"[CraftRegistry] Bound Client ID {clientId} to Craft '{craftNode.Name}'.");
        }

        public static CraftNode GetCraft(int clientId)
        {
            _clientToCraft.TryGetValue(clientId, out CraftNode craft);
            return craft;
        }

        public static int GetOwnerId(CraftNode craftNode)
        {
            if (craftNode != null && _craftToClient.TryGetValue(craftNode, out int clientId))
            {
                return clientId;
            }

            return -1;
        }

        public static bool IsLocalPlayerCraft(CraftNode craftNode)
        {
            return IsLocalClientId(GetOwnerId(craftNode));
        }

        public static void UnregisterClient(int clientId)
        {
            if (_clientToCraft.TryGetValue(clientId, out CraftNode oldCraft))
            {
                _craftToClient.Remove(oldCraft);
                _clientToCraft.Remove(clientId);
                Mod.Log($"[CraftRegistry] Unregistered craft for Client ID {clientId}.");
            }
        }

        /// <summary>
        /// Unregisters and destroys a remote proxy. The local player/host craft is only
        /// unregistered, because it belongs to Juno rather than this multiplayer session.
        /// </summary>
        public static void DespawnCraft(int clientId)
        {
            if (!_clientToCraft.TryGetValue(clientId, out CraftNode craftNode)) return;

            _craftToClient.Remove(craftNode);
            _clientToCraft.Remove(clientId);

            if (!IsLocalClientId(clientId))
            {
                DestroyProxy(craftNode, clientId, "despawned");
            }
            else
            {
                Mod.Log($"[CraftRegistry] Unregistered local craft for Client ID {clientId}.");
            }
        }

        /// <summary>
        /// Clears the registry and destroys every proxy except the explicitly preserved
        /// local Juno craft. Capture the preserved ID before host/client state is reset.
        /// Call only on the game thread.
        /// </summary>
        public static void ClearAllExcept(int preservedClientId)
        {
            var entries = new List<KeyValuePair<int, CraftNode>>(_clientToCraft);
            _clientToCraft.Clear();
            _craftToClient.Clear();

            foreach (KeyValuePair<int, CraftNode> entry in entries)
            {
                if (entry.Key != preservedClientId)
                {
                    DestroyProxy(entry.Value, entry.Key, "cleared");
                }
            }
        }

        /// <summary>
        /// Clears the registry while preserving the currently local craft identity.
        /// Prefer ClearAllExcept when shutdown resets role state before queued work runs.
        /// </summary>
        public static void ClearAll()
        {
            int preservedClientId = ServerConnection.IsHosting
                ? ServerConnection.HostClientId
                : ClientConnection.LocalClientId;
            ClearAllExcept(preservedClientId);
        }

        private static bool IsLocalClientId(int clientId)
        {
            if (ServerConnection.IsHosting)
            {
                return clientId == ServerConnection.HostClientId;
            }

            return clientId >= 0 && clientId == ClientConnection.LocalClientId;
        }

        private static void DestroyProxy(CraftNode craftNode, int clientId, string reason)
        {
            if (craftNode == null) return;

            try
            {
                craftNode.DestroyCraft();
                Mod.Log($"[CraftRegistry] {reason} remote proxy for Client ID {clientId}.");
            }
            catch (Exception ex)
            {
                Mod.LogError($"[CraftRegistry] Error deleting proxy for Client ID {clientId}: {ex.Message}");
            }
        }
    }
}
