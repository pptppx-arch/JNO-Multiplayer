namespace Assets.Scripts.Ui.Designer
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using System.Xml.Linq;
    using ModApi.Common;
    using ModApi.Ui;

    public static class MultiplayerJoinButton
    {
        private const string _buttonId = "multiplayer-join-button";

        private static MultiplayerJoinButtonDisplay _displayScript;

        public static void Initialize()
        {
            var userInterface = Game.Instance.UserInterface;
            userInterface.AddBuildUserInterfaceXmlAction(
                UserInterfaceIds.Design.DesignerUi,
                OnBuildDesignerUI);

            Game.Instance.SceneManager.SceneTransitionStarted += (s, e) => _displayScript = null;
        }

        private static void OnBuildDesignerUI(BuildUserInterfaceXmlRequest request)
        {
            var ns = XmlLayoutConstants.XmlNamespace;
            var viewButton = request.XmlDocument
                .Descendants(ns + "Panel")
                .First(x => (string)x.Attribute("internalId") == "flyout-view");

            viewButton.Parent.Add(
                new XElement(
                    ns + "Panel",
                    new XAttribute("id", _buttonId),
                    new XAttribute("class", "toggle-button audio-btn-click"),
                    new XAttribute("name", "ButtonPanel.MultiplayerJoinButton"),
                    new XAttribute("tooltip", "Join Multiplayer Game"),
                    new XElement(
                        ns + "Image",
                        new XAttribute("class", "toggle-button-icon"),
                        new XAttribute("sprite", "JNO Multiplayer/Sprites/ToolbarButton"))));

            request.AddOnLayoutRebuiltAction(xmlLayoutController =>
            {
                var button = xmlLayoutController.XmlLayout.GetElementById(_buttonId);
                button.AddOnClickEvent(OnMultiplayerJoinButtonClicked);
            });
        }

        private static void OnMultiplayerJoinButtonClicked()
        {
            if (_displayScript != null)
            {
                _displayScript.Close();
                _displayScript = null;
            }
            else
            {
                var ui = Game.Instance.UserInterface;
                _displayScript = ui.BuildUserInterfaceFromResource<MultiplayerJoinButtonDisplay>(
                    "JNO Multiplayer/Designer/MultiplayerJoinUI",
                    (script, controller) => script.OnLayoutRebuilt(controller));

                Mod.Log("Multiplayer Join Dialog opened.");
            }
        }
    }
}