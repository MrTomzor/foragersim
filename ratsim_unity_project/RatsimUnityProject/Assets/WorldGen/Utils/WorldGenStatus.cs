using UnityEngine;

/// <summary>
/// Helper for publishing world-generation status to the Python client AND mirroring
/// it to Unity's log. Use this when something goes wrong (or noteworthy) during
/// generation so users running training in a terminal see it.
///
/// Topic: /sim_control/worldgen_status (WorldGenStatusMessage)
/// </summary>
public static class WorldGenStatus {
    public const string Topic = "/sim_control/worldgen_status";

    public static void Info(string source, string message) {
        Debug.Log($"[{source}] {message}");
        Publish("info", source, message);
    }

    public static void Warning(string source, string message) {
        Debug.LogWarning($"[{source}] {message}");
        Publish("warning", source, message);
    }

    public static void Error(string source, string message) {
        Debug.LogError($"[{source}] {message}");
        Publish("error", source, message);
    }

    private static void Publish(string severity, string source, string message) {
        RoslikeTCPServer conn = RoslikeTCPServer.GetInstance();
        if (conn == null) return;
        conn.Publish(Topic, new WorldGenStatusMessage {
            severity = severity,
            source   = source,
            message  = message,
        });
    }
}
