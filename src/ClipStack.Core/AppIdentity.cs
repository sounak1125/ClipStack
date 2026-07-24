using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipStack.Core;

/// <summary>
/// Central application identity. Change these values in one place to rebrand.
/// </summary>
public static class AppIdentity
{
    public const string ProductName = "ClipStack";
    public const string ExecutableName = "ClipStack.exe";
    public const string ApplicationId = "ClipStack.Desktop";
    public const string Publisher = "ClipStack";
    public const string DefaultVersion = "1.0.1";
    public const string DataFolderName = "ClipStack";
    public const string StartupRegistryValueName = "ClipStack";
    public const string MutexNamePrefix = "Local\\ClipStack.Desktop.SingleInstance";
    public const string SignalEventNamePrefix = "Local\\ClipStack.Desktop.ShowHistory";

    public static string GetDataDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DataFolderName);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}
