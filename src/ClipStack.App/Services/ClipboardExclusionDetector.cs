using System.Windows;
using ClipStack.Core.Utilities;

namespace ClipStack.Services;

/// <summary>
/// Checks a clipboard data object for the opt-out formats described in
/// <see cref="ClipboardExclusionFormats"/>, before any content is read.
/// </summary>
internal static class ClipboardExclusionDetector
{
    /// <summary>
    /// Returns <see langword="true"/> when the source application asked clipboard
    /// monitors to skip this clip. <paramref name="marker"/> names the format that
    /// triggered exclusion, for logging — the clip's contents are never logged.
    /// </summary>
    public static bool IsExcluded(IDataObject data, FileLogger logger, out string? marker)
    {
        marker = null;

        foreach (var name in ClipboardExclusionFormats.PresenceMarkers)
        {
            if (IsPresent(data, name, logger))
            {
                marker = name;
                return true;
            }
        }

        foreach (var name in ClipboardExclusionFormats.PolicyMarkers)
        {
            if (!IsPresent(data, name, logger))
                continue;

            object? value = null;
            try
            {
                // autoConvert: false — we want the raw DWORD payload, not a coerced string.
                value = data.GetData(name, autoConvert: false);
            }
            catch (Exception ex)
            {
                logger.Error("ExclusionRead", ex, name);
            }

            if (!ClipboardExclusionFormats.PolicyValueAllowsCapture(value))
            {
                marker = name;
                return true;
            }
        }

        return false;
    }

    private static bool IsPresent(IDataObject data, string format, FileLogger logger)
    {
        try
        {
            return data.GetDataPresent(format, autoConvert: false);
        }
        catch (Exception ex)
        {
            // A probe that throws tells us nothing either way; treat it as absent so a
            // single misbehaving format cannot disable capture entirely.
            logger.Error("ExclusionProbe", ex, format);
            return false;
        }
    }
}
