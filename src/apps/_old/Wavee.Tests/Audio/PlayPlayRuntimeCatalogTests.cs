using System.Text.Json;
using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public sealed class PlayPlayRuntimeCatalogTests
{
    [Fact]
    public void Deserialize_WaveeCatalogSchema_Parses129300667Packs()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "packs": [
                {
                  "packId": "129300667-arm64",
                  "spotifyVersion": "1.2.93.667",
                  "clientVersion": "1.2.93.667.g7b5cc0ce",
                  "appVersion": "129300667",
                  "playPlayRequestVersion": 5,
                  "arch": "Arm64",
                  "algorithmVersion": "129300667-native-cdn-v2",
                  "dllSha256": "cf95a21911e932cb3a53ae5732c5f8f416b885d79e8cf793107d79e329e9b387",
                  "playPlayTokenHex": "025614bf92a6c95e922e466523da4f96",
                  "config": {
                    "analysisBaseHex": "180000000",
                    "vmRuntimeInitVaHex": "1804B6DD8",
                    "vmObjectTransformVaHex": "1804B9038",
                    "runtimeContextVaHex": "18192D398",
                    "runtimeContextSecondaryVaHex": "0",
                    "initVtableLabsHex": [],
                    "transformFourthArgTemplateVaHex": "1822ECD38",
                    "transformFourthArgBuildVaHex": "181579FF0",
                    "fillRandomBytesVaHex": "180434B04",
                    "vmInitValueHex": "00000000000000000000000000000000",
                    "aesKey": { "strategy": "buffer_slice", "offsetBytes": 0, "lengthBytes": 16 },
                    "vmObjectSize": 144,
                    "rtContextSize": 16,
                    "derivedKeySize": 24,
                    "obfuscatedKeySize": 16,
                    "initValueSize": 16,
                    "contentIdSize": 16,
                    "keySize": 16
                  },
                  "urls": ["https://cproducts.dev/r/runtime-pack-2.bin"],
                  "compression": "br"
                }
              ]
            }
            """;

        var catalog = JsonSerializer.Deserialize(json, PlayPlayCatalogJson.Default.PlayPlayRuntimeCatalog);
        Assert.NotNull(catalog);
        Assert.Null(catalog!.Validate());
        Assert.Single(catalog.Packs);
        Assert.Null(catalog.Packs[0].Validate());
        Assert.True(catalog.Packs[0].IsBrotli);
    }
}
