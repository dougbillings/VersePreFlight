using System.Runtime.InteropServices;

namespace VersePreflight.Core.Platforms;

/// <summary>
/// Whether the app runs as an MSIX package (the Microsoft Store version) and, if so, its package
/// family name. Store installs are updated by the Store and keep their files in the package's own folder.
/// </summary>
public static partial class PackageIdentity
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122; // a package exists; APPMODEL_ERROR_NO_PACKAGE (15700) if not

    private static readonly Lazy<string?> s_familyName = new(ReadFamilyName);

    /// <summary>The package family name (e.g. "Publisher.App_8wekyb3d8bbwe"), or null when not packaged.</summary>
    public static string? FamilyName => s_familyName.Value;

    public static bool IsPackaged => FamilyName is not null;

    private static string? ReadFamilyName()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2)) return null; // the API exists from Windows 8

        uint length = 0;
        if (GetCurrentPackageFamilyName(ref length, null) != ErrorInsufficientBuffer) return null;
        var buffer = new char[length];
        return GetCurrentPackageFamilyName(ref length, buffer) == ErrorSuccess
            ? new string(buffer, 0, (int)length - 1) // length includes the terminating null
            : null;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, [Out] char[]? packageFamilyName);
}
