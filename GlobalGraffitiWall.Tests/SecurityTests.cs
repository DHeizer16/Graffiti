using FluentAssertions;
using GlobalGraffitiWall.API;
using Xunit;

namespace GlobalGraffitiWall.Tests;

public class SecurityTests
{
    [Theory]
    [InlineData("CorrectHorseBatteryStaple!")]
    [InlineData("Short1!")]
    [InlineData("A very long password with unicode: 🚀🎨✨ 2026!")]
    public void PasswordHashing_ShouldVerifyCorrectPassword(string password)
    {
        // Arrange & Act
        var (hash, salt) = PasswordSecurity.HashPassword(password);
        bool isValid = PasswordSecurity.VerifyPassword(password, hash, salt);

        // Assert
        isValid.Should().BeTrue();
    }

    [Fact]
    public void PasswordHashing_IncorrectPassword_ShouldReturnFalse()
    {
        // Arrange
        var (hash, salt) = PasswordSecurity.HashPassword("SuperSecret123!");

        // Act
        bool isValid = PasswordSecurity.VerifyPassword("WrongPassword!", hash, salt);

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public void PasswordHashing_CaseSensitivity_ShouldReturnFalseForDifferentCasing()
    {
        // Arrange
        var (hash, salt) = PasswordSecurity.HashPassword("PixelMaster2026");

        // Act
        bool isValid = PasswordSecurity.VerifyPassword("pixelmaster2026", hash, salt);

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public void PasswordHashing_UniqueSalts_ShouldProduceDistinctHashesForSamePassword()
    {
        // Arrange
        const string password = "IdenticalPassword!";

        // Act
        var (hash1, salt1) = PasswordSecurity.HashPassword(password);
        var (hash2, salt2) = PasswordSecurity.HashPassword(password);

        // Assert
        salt1.Should().NotBe(salt2, "cryptographic salt must be randomized per hash");
        hash1.Should().NotBe(hash2, "derived hashes with different salts must not match");

        PasswordSecurity.VerifyPassword(password, hash1, salt1).Should().BeTrue();
        PasswordSecurity.VerifyPassword(password, hash2, salt2).Should().BeTrue();
    }

    [Theory]
    [InlineData("", "invalid-salt")]
    [InlineData("invalid-hash", "")]
    [InlineData("not-base-64!!", "not-base-64!!")]
    [InlineData("AQID", "AQID")] // Valid base64 but wrong length
    public void VerifyPassword_MalformedInputs_ShouldReturnFalseSafelyWithoutThrowing(string badHash, string badSalt)
    {
        // Act
        bool result = PasswordSecurity.VerifyPassword("AnyPassword", badHash, badSalt);

        // Assert
        result.Should().BeFalse();
    }
}
