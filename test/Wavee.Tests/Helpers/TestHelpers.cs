using System;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Extensions.Logging;
using Moq;

namespace Wavee.Tests.Helpers;

/// <summary>
/// Common test helper methods used across all test classes.
/// </summary>
public static class TestHelpers
{
    private static readonly Random _random = new Random(42); // Fixed seed for determinism

    /// <summary>
    /// Generates random bytes for test data.
    /// </summary>
    public static byte[] GenerateRandomBytes(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    /// <summary>
    /// Converts bytes to hex string for debugging output.
    /// </summary>
    public static string BytesToHex(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
            return string.Empty;

        return BitConverter.ToString(bytes).Replace("-", " ");
    }

    /// <summary>
    /// Creates a mock logger for testing.
    /// </summary>
    public static Mock<ILogger<T>> CreateMockLogger<T>()
    {
        return new Mock<ILogger<T>>();
    }

    /// <summary>
    /// Creates a non-generic mock logger.
    /// </summary>
    public static Mock<ILogger> CreateMockLogger()
    {
        return new Mock<ILogger>();
    }

    /// <summary>
    /// Generates a test device ID.
    /// </summary>
    public static string CreateDeviceId()
    {
        return $"test-device-{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Generates a test username.
    /// </summary>
    public static string CreateUsername()
    {
        return $"testuser{_random.Next(1000, 9999)}";
    }

    /// <summary>
    /// Creates a cancellation token that cancels after specified milliseconds.
    /// </summary>
    public static CancellationToken CreateCancellationToken(int milliseconds)
    {
        var cts = new CancellationTokenSource(milliseconds);
        return cts.Token;
    }
}
