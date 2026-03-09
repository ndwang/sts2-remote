using System;
using System.IO;
using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Agent;

[ModInitializer("Initialize")]
public static class Plugin
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "sts2agent.log");

    private static Harmony? _harmony;
    public static HttpServer? Server { get; private set; }

    public static void Initialize()
    {
        try
        {
            _harmony = new Harmony("sts2agent");
            _harmony.PatchAll(typeof(Plugin).Assembly);

            GameStabilityDetector.Initialize();
            GameStabilityDetector.OnBecameStable += () => Server?.SignalDecisionPoint();

            RunManager.Instance.RoomEntered += OnRoomEntered;

            Server = new HttpServer(8080);
            Server.Start();

            Log("Plugin initialized. Patches applied. HTTP server started.");
        }
        catch (Exception e)
        {
            Log($"Failed to initialize: {e}");
        }
    }

    private static void OnRoomEntered()
    {
        try
        {
            GameStabilityDetector.OnRoomEntered();
        }
        catch (Exception e)
        {
            Plugin.Log($"Error in OnRoomEntered: {e}");
        }
    }

    public static void Log(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n";
            File.AppendAllText(LogPath, line, new UTF8Encoding(true));
        }
        catch
        {
            // Silently ignore logging failures
        }
    }
}
