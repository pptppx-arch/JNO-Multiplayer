namespace Assets.Scripts.Ui.Designer
{
    using Assets.Scripts.Clock;
    using Assets.Scripts.Multiplayer;
    using ModApi.Common;
    using ModApi.Ui;
    using UnityEngine;

    public class MultiplayerJoinButtonDisplay : MonoBehaviour
    {
        private IXmlLayoutController _controller;
        public string craftXml;

        public void OnLayoutRebuilt(IXmlLayoutController xmlLayoutController)
        {
            this._controller = xmlLayoutController;
            var cancelButton = this._controller.XmlLayout.GetElementById("cancelButton");
            cancelButton?.AddOnClickEvent(Close);
            var connectButton = this._controller.XmlLayout.GetElementById("connectButton");
            connectButton?.AddOnClickEvent(OnConnectClicked);
            var hostButton = this._controller.XmlLayout.GetElementById("hostButton");
            hostButton?.AddOnClickEvent(OnHostClicked);
        }

        public void Close()
        {
            this._controller.XmlLayout.Hide(() => GameObject.Destroy(this.gameObject), true);
        }

        public void OnConnectClicked()
        {
            // 1. Get input values from XML elements
            var ipElement = this._controller.XmlLayout.GetElementById("ipInput");
            var portElement = this._controller.XmlLayout.GetElementById("portInput");

            string host = ipElement == null ? string.Empty : (ipElement.GetValue() ?? string.Empty).Trim();
            string portStr = portElement == null ? string.Empty : portElement.GetValue();
            if (string.IsNullOrWhiteSpace(host) || !TryReadPort(portStr, out int port))
            {
                Game.Instance.Designer.DesignerUi.ShowMessage(
                    "Enter a host address and a port from 1 to 65535.",
                    3f);
                return;
            }

            Mod.Log($"[UI] Queuing join to {host}:{port} after flight readiness.");
            TryConnect(host, port);
        }

        public void OnHostClicked()
        {
            var portElement = this._controller.XmlLayout.GetElementById("portInput");
            string portStr = portElement == null ? string.Empty : portElement.GetValue();
            if (!TryReadPort(portStr, out int port))
            {
                Game.Instance.Designer.DesignerUi.ShowMessage(
                    "Enter a port from 1 to 65535.",
                    3f);
                return;
            }

            Mod.Log($"[UI] Queuing host on port {port} after flight readiness.");
            TryConnect(null, port);
        }

        public void TryConnect(string host, int port)
        {
            Game.Instance.Designer.BeginFlight();
            if (host == null)
            {
                MultiplayerTelemetryRuntime.RequestHostWhenFlightReady(port);
                return;
            }

            MultiplayerTelemetryRuntime.RequestJoinWhenFlightReady(host, port);

#if JNO_MULTIPLAYER_DEV_REMOTE_DEBUGGER
            ModHelper.Connect(host, 4444);
#endif
        }

        private static bool TryReadPort(string value, out int port)
        {
            return int.TryParse(value, out port) && port >= 1 && port <= 65535;
        }
    }
}
