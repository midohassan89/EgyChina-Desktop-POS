using EgyChina.Pos.Core;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class PinHasherTests
{
    [Fact]
    public void Hash_and_verify_roundtrip()
    {
        var (hash, salt) = PinHasher.Hash("246810");
        Assert.True(PinHasher.Verify("246810", hash, salt));
        Assert.False(PinHasher.Verify("246811", hash, salt));
        Assert.False(PinHasher.Verify("24681", hash, salt));
        Assert.False(PinHasher.Verify("246810", "pending-pin-sync", "local"));
    }

    [Fact]
    public void Rejects_short_or_non_digit_pins()
    {
        Assert.False(PinHasher.IsValidFormat("12345"));
        Assert.False(PinHasher.IsValidFormat("12ab56"));
        Assert.True(PinHasher.IsValidFormat("123456"));
        Assert.Throws<ArgumentException>(() => PinHasher.Hash("12345"));
    }
}
