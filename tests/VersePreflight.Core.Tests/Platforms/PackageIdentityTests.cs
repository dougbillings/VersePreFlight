using VersePreflight.Core.Platforms;

namespace VersePreflight.Core.Tests.Platforms;

public class PackageIdentityTests
{
    // The test host is never an MSIX package; the packaged path is checked by installing the Store build
    [Fact]
    public void UnpackagedProcess_IsNotAStoreInstall()
    {
        Assert.False(PackageIdentity.IsPackaged);
        Assert.Null(PackageIdentity.FamilyName);
    }
}
