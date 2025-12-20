using System;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Wavee.WinUI.Models.Dto;

namespace Wavee.WinUI.Services;

/// <summary>
/// Mock service that provides sample Spotify home feed data from embedded JSON file
/// </summary>
public class MockSpotifyService
{
    /// <summary>
    /// Gets the home feed data asynchronously from embedded Home.json file
    /// </summary>
    public async Task<HomeResponseDto?> GetHomeDataAsync()
    {
        // Simulate network delay
        await Task.Delay(500);

        try
        {
            // Load embedded resource
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "Wavee.WinUI.Assets.Mock.Home.json";

            await using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                System.Diagnostics.Debug.WriteLine($"Error: Could not find embedded resource: {resourceName}");
                return null;
            }

            // Deserialize using source-generated context for AOT compatibility
            var rootDto = await JsonSerializer.DeserializeAsync(stream, HomeJsonSerializerContext.Default.HomeRootDto);

            // Navigate through the wrapper structure
            return rootDto?.Data?.Home;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading home data: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
            return null;
        }
    }
}
