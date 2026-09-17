using System.Text;
using DDLink.Core;

namespace DDLink.Core.Tests;

public class SignerTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"id\":\"abc\"}");

    [Fact]
    public void Matches_an_independently_computed_hmac()
    {
        // printf '%s' '1789700000.{"id":"abc"}' | openssl dgst -sha256 -hmac 'test-secret'
        Assert.Equal(
            "v1=8f334d7b9e33b7c66e9d89b95e86abb6c78caf9caceb70a2dc99804ddf324ea0",
            Signer.Sign("test-secret", 1789700000, Body));
    }

    [Fact]
    public void Verify_accepts_the_right_signature_and_rejects_any_change()
    {
        var signature = Signer.Sign("test-secret", 1789700000, Body);

        Assert.True(Signer.Verify("test-secret", 1789700000, Body, signature));
        Assert.False(Signer.Verify("other-secret", 1789700000, Body, signature));
        Assert.False(Signer.Verify("test-secret", 1789700001, Body, signature));
        Assert.False(Signer.Verify("test-secret", 1789700000, Encoding.UTF8.GetBytes("{\"id\":\"abd\"}"), signature));
        Assert.False(Signer.Verify("test-secret", 1789700000, Body, "v1=00"));
    }
}
