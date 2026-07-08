// T057 — US6 Tier B: the live engine renders a flash-drum document to a real
// PNG (magic bytes, sane dimensions) through POST /flowsheets/pfd, and the
// same flowsheet saved as a template renders via GET /templates/{id}/pfd.png.

using System.Net;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "Pfd")]
public class PfdTests
{
    private static (int Width, int Height) PngDimensions(byte[] png)
    {
        // IHDR is always the first chunk: width/height are big-endian ints at 16/20.
        int Be(int offset) => png[offset] << 24 | png[offset + 1] << 16 | png[offset + 2] << 8 | png[offset + 3];
        return (Be(16), Be(20));
    }

    [SkippableFact]
    public async Task Document_pfd_renders_a_real_png_with_sane_dimensions()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var body = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["document"] = JsonSerializer.Deserialize<JsonElement>(BuildSolveTests.FlashDrumDoc),
        }), System.Text.Encoding.UTF8, "application/json");
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/pfd", body);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/png", resp.Content.Headers.ContentType?.MediaType);

        var png = await resp.Content.ReadAsByteArrayAsync();
        Assert.True(png.Length > 1000, $"suspiciously small PNG ({png.Length} bytes)");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);

        var (width, height) = PngDimensions(png);
        Assert.True(width >= 200 && height >= 200,
            $"PFD must be at least 200×200 px, got {width}×{height}");
    }

    [SkippableFact]
    public async Task Template_pfd_renders_via_the_get_route()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        const string id = "it-pfd-template";

        var save = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["document"] = JsonSerializer.Deserialize<JsonElement>(BuildSolveTests.FlashDrumDoc),
            ["timeoutSeconds"] = 180,
            ["saveAsTemplate"] = new Dictionary<string, object?> { ["id"] = id, ["overwrite"] = true },
        }), System.Text.Encoding.UTF8, "application/json");
        (await RunnerConnection.Client.PostAsync("/flowsheets/build-solve", save)).EnsureSuccessStatusCode();

        try
        {
            var resp = await RunnerConnection.Client.GetAsync($"/templates/{id}/pfd.png");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("image/png", resp.Content.Headers.ContentType?.MediaType);
            var png = await resp.Content.ReadAsByteArrayAsync();
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        }
        finally
        {
            await RunnerConnection.Client.DeleteAsync($"/templates/{id}");
        }
    }
}
