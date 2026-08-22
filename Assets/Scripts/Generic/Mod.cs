namespace Assets.Scripts
{
    using Ui.Designer;
    using UnityEngine;
    using Assets.Scripts.Multiplayer;
    public class Mod : ModApi.Mods.GameMod
    {
        //Singleton instance of the mod
        private Mod() : base()
        {

        }

        public static Mod Instance { get; } = GetModInstance<Mod>();

        // Log methods
        public static void Log(string message, UnityEngine.Object context = null)
        {
            Debug.Log($"{Instance.ModInfo.Name}: {message}", context);
        }

        public static void LogError(string error, UnityEngine.Object context = null)
        {
            Debug.LogError($"{Instance.ModInfo.Name}: {error}", context);
        }

        public static void LogWarning(string warning, UnityEngine.Object context = null)
        {
            Debug.LogWarning($"{Instance.ModInfo.Name}: {warning}", context);
        }

        // Initialization
        protected override void OnModInitialized()
        {
            base.OnModInitialized();
            Log("Mod initalized.");

            MultiplayerJoinButton.Initialize();
            MultiplayerTelemetryRuntime.EnsureCreated();
            DevConsoleCommands.Register();
        }
    }
}
