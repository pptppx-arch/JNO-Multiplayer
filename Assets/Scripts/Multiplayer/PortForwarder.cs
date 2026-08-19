namespace Assets.Scripts.Multiplayer
{
    using Assets.Scripts;
    using Open.Nat;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public class PortForwarder
    {
        /// <summary>
        /// Creates matching TCP and UDP mappings. TCP is required for handshake/XML;
        /// UDP is required for telemetry. The same numeric port is valid because the
        /// protocols have separate transport namespaces.
        /// </summary>
        public static async Task<bool> ForwardPort(int port)
        {
            try
            {
                var discoverer = new NatDiscoverer();
                using (var cts = new CancellationTokenSource(5000))
                {
                    var device = await discoverer.DiscoverDeviceAsync(PortMapper.Upnp, cts);
                    await device.CreatePortMapAsync(new Mapping(Protocol.Tcp, port, port, "JNOMultiplayer TCP"));
                    await device.CreatePortMapAsync(new Mapping(Protocol.Udp, port, port, "JNOMultiplayer UDP"));
                }

                Mod.Log($"[PortForwarder] UPnP forwarded TCP and UDP port {port}.");
                return true;
            }
            catch (Exception ex)
            {
                Mod.LogWarning($"[PortForwarder] UPnP failed: {ex.Message}. Host may need both TCP and UDP manual forwarding.");
                return false;
            }
        }
    }
}
