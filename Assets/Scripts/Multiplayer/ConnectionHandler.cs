namespace Assets.Scripts.Multiplayer
{
    using System.Xml.Linq;
    public class ConnectionHandler
    {
        public static bool IsHost { get; private set; }

        public static void TryConnect(string host, int port, bool isAttemptHostGame)
        {
            var designer = Game.Instance.Designer;

            // Get XML from Designer BEFORE transitioning scenes
            string craftXml = null;
            if (designer?.CraftScript?.Data != null)
            {
                XElement xmlElement = designer.CraftScript.Data.GenerateXml(designer.CraftScript.Transform, true, true);
                craftXml = xmlElement.ToString(SaveOptions.DisableFormatting);
            }

            designer.BeginFlight();

            IsHost = isAttemptHostGame;

            if (isAttemptHostGame)
            {
                ServerHost.Start(port, craftXml);
            }
            else
            {
                ClientConnection.Connect(host, port, craftXml);
            }
        }
    }
}