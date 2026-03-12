using System;
using System.IO;
using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Agent;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Error = 2
}

[ModInitializer("Initialize")]
public static class Plugin
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "sts2agent.log");

    public static LogLevel CurrentLogLevel { get; set; } = LogLevel.Error;

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

            Server = new HttpServer(57541);
            Server.Start();

            Log("Plugin initialized. Patches applied. HTTP server started.");
        }
        catch (Exception e)
        {
            LogError($"Failed to initialize: {e}");
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
            LogError($"Error in OnRoomEntered: {e}");
        }
    }

    public static void Log(string message) => Log(LogLevel.Info, message);
    public static void LogDebug(string message) => Log(LogLevel.Debug, message);
    public static void LogError(string message) => Log(LogLevel.Error, message);

    public static void Log(LogLevel level, string message)
    {
        if (level < CurrentLogLevel) return;
        try
        {
            var prefix = level switch
            {
                LogLevel.Debug => "DEBUG",
                LogLevel.Error => "ERROR",
                _ => "INFO"
            };
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{prefix}] {message}\n";
            File.AppendAllText(LogPath, line, new UTF8Encoding(true));
        }
        catch
        {
            // Silently ignore logging failures
        }
    }
}
