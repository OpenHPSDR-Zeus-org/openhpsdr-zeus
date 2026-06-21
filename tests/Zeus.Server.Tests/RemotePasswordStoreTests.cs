using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.Hosting.Remote;

namespace Zeus.Server.Tests;

public sealed class RemotePasswordStoreTests
{
    [Fact]
    public void Set_Get_Clear_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zeus-rps-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new RemotePasswordStore(NullLogger<RemotePasswordStore>.Instance, path);

            Assert.False(store.HasPassword());
            Assert.Null(store.GetVerifier());

            store.Set("super-secret-pw");
            Assert.True(store.HasPassword());

            var v = store.GetVerifier();
            Assert.NotNull(v);
            Assert.Equal(RemoteAuthConstants.SaltBytes, v!.Salt.Length);

            store.Clear();
            Assert.False(store.HasPassword());
            Assert.Null(store.GetVerifier());
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Set_ReplacesPreviousPassword()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zeus-rps-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new RemotePasswordStore(NullLogger<RemotePasswordStore>.Instance, path);
            store.Set("first");
            var first = store.GetVerifier()!;
            store.Set("second");
            var second = store.GetVerifier()!;

            // New salt → different stored verifier; exactly one row remains.
            Assert.NotEqual(Convert.ToHexString(first.Salt), Convert.ToHexString(second.Salt));
            Assert.True(store.HasPassword());
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
