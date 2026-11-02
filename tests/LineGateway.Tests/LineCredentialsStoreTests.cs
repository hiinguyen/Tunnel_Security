using LineGateway.Mqtt;

namespace LineGateway.Tests;

public class LineCredentialsStoreTests
{
    [Fact]
    public void Saves_loads_and_deletes_credentials_with_owner_only_permissions()
    {
        var dir = Directory.CreateTempSubdirectory();
        var store = new LineCredentialsStore(Path.Combine(dir.FullName, "sub", "state.json"));

        Assert.Null(store.Load());
        store.Save(new LineCredentials(4, "line-4", "secret"));

        Assert.Equal(new LineCredentials(4, "line-4", "secret"), store.Load());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(dir.FullName, "sub", "state.json")));
        }

        store.Delete();
        Assert.Null(store.Load());
    }
}
