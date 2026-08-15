namespace Assets.Scripts.Ui.Designer
{
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

            string host = ipElement != null ? ipElement.GetValue() : "";
            string portStr = portElement != null ? portElement.GetValue() : "25555";
            int.TryParse(portStr, out int port);

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(portStr))
            {
                Game.Instance.Designer.DesignerUi.ShowMessage("Please enter a valid IP address and port.", 3f);
                return;
            }   

            Mod.Log($"[UI] Connecting to {host}:{port}");

            TryConnect(host, port);
        }

        public void OnHostClicked()
        {
            var portElement = this._controller.XmlLayout.GetElementById("portInput");
            string portStr = portElement != null ? portElement.GetValue() : "25555";
            int.TryParse(portStr, out int port);

            if (string.IsNullOrEmpty(portStr))
            {
                Game.Instance.Designer.DesignerUi.ShowMessage("Please enter a valid port.", 3f);
                return;
            }

            Mod.Log($"[UI] Hosting on port {port}");

            TryConnect(null, port);
        }

        public void TryConnect(string host, int port)
        {
            var designer = Game.Instance.Designer;
            designer.BeginFlight();
            if (host == null) ServerConnection.Start(port); else ClientConnection.Connect(host, port);
            ModHelper.Connect(host, 4444);
        }
    }
}