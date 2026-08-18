namespace Assets.Scripts.Multiplayer
{
    using Assets.Scripts;
    using Open.Nat;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public class PortForwarder
    {
        public static async Task<bool> ForwardPort(int port)
        {
            try
            {
                var discoverer = new NatDiscoverer();
                var cts = new CancellationTokenSource(5000); // 5 sec timeout
                var device = await discoverer.DiscoverDeviceAsync(PortMapper.Upnp, cts);

                await device.CreatePortMapAsync(new Mapping(Protocol.Tcp, port, port, "JNOMultiplayer"));
                Mod.Log($"[ServerHost] UPnP automatically forwarded port {port}!");
                return true;
            }
            catch (Exception ex)
            {
                Mod.LogWarning($"[ServerHost] UPnP failed: {ex.Message}. Host may still need manual port forwarding.");
                return false;
            }
        }
    }
}