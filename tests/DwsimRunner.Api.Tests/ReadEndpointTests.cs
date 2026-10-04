// iskra spec 285 (ISK-485) — POST /flowsheets/read: open a DWSIM file with DWSIM's own loader.
//
// The API half is the cheap half: it sniffs the bytes (never the extension), refuses what is not a
// DWSIM file, refuses a zip it must not unpack, and hands the worker a temp file it deletes on
// every path. The FakeWorker's `read` branch echoes what it was given, so these tests can see which
// file the worker got, and prove a refusal never spawned one.

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class ReadEndpointTests
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private const string Xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<DWSIM_Simulation_Data></DWSIM_Simulation_Data>";

    private static RunnerHost Host(string readDir, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?> { ["READ_TEMP_PATH"] = readDir };
        foreach (var kv in extra ?? []) settings[kv.Key] = kv.Value;
        return new RunnerHost(settings);
    }

    private static string ReadDir() => Directory.CreateTempSubdirectory("dwsim-read-tests-").FullName;

    /// <summary>Upload files the route wrote and has not yet deleted (the FakeWorker's run markers excluded).</summary>
    private static string[] Uploads(string dir) =>
        Directory.GetFiles(dir).Where(f => !Path.GetFileName(f).StartsWith("run-")).ToArray();

    private static string[] StartMarkers(string dir) => Directory.GetFiles(dir, "run-*.start");

    private static async Task<HttpResponseMessage> Post(RunnerHost host, byte[] body, string? query = null,
        CancellationToken ct = default)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/flowsheets/read" + (query ?? "")) { Content = content };
        req.Headers.Add("X-File-Name", "anything.dwxmz");   // logged only; the extension decides nothing
        return await host.Client.SendAsync(req, ct);
    }

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static async Task<JsonElement> Body(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task AssertRefused(HttpResponseMessage resp, HttpStatusCode status, string code)
    {
        Assert.Equal(status, resp.StatusCode);
        Assert.Equal(code, (await Body(resp)).GetProperty("error").GetString());
    }

    // ── what the worker is handed ─────────────────────────────────────────

    [Fact]
    public async Task A_zip_reaches_the_worker_as_a_dwxmz_and_the_upload_is_deleted()
    {
        var dir = ReadDir();
        using var host = Host(dir);
        var bytes = Zip(("tmp1.xml", Utf8(Xml)));

        var resp = await Post(host, bytes);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await Body(resp);
        var fake = body.GetProperty("fake");
        Assert.Equal(".dwxmz", fake.GetProperty("extension").GetString());
        Assert.Equal(bytes.Length, fake.GetProperty("bytes").GetInt32());
        Assert.True(body.TryGetProperty("document", out _));
        Assert.Empty(Uploads(dir));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Xml_with_or_without_a_bom_reaches_the_worker_as_a_dwxml(bool bom)
    {
        var dir = ReadDir();
        using var host = Host(dir);
        var bytes = (bom ? Bom : []).Concat(Utf8(Xml)).ToArray();

        var resp = await Post(host, bytes);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(".dwxml", (await Body(resp)).GetProperty("fake").GetProperty("extension").GetString());
        Assert.Empty(Uploads(dir));
    }

    [Fact]
    public async Task Xml_without_a_prolog_is_recognised_by_its_root_element()
    {
        var dir = ReadDir();
        using var host = Host(dir);

        var resp = await Post(host, Utf8("<DWSIM_Simulation_Data><GeneralInfo/></DWSIM_Simulation_Data>"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── refusals the API makes without spawning a worker ──────────────────

    [Theory]
    [InlineData("just some text, not a flowsheet")]
    [InlineData("<html><body>not dwsim</body></html>")]
    [InlineData("")]
    public async Task Bytes_that_are_neither_zip_nor_xml_are_415_and_spawn_nothing(string text)
    {
        var dir = ReadDir();
        using var host = Host(dir);

        await AssertRefused(await Post(host, Utf8(text)), HttpStatusCode.UnsupportedMediaType, "NOT_A_DWSIM_FILE");
        Assert.Empty(StartMarkers(dir));
        Assert.Empty(Uploads(dir));
    }

    [Fact]
    public async Task A_zip_with_no_xml_entry_is_415()
    {
        var dir = ReadDir();
        using var host = Host(dir);

        await AssertRefused(await Post(host, Zip(("readme.txt", Utf8("hello")))),
            HttpStatusCode.UnsupportedMediaType, "NOT_A_DWSIM_FILE");
        Assert.Empty(StartMarkers(dir));
    }

    [Fact]
    public async Task A_truncated_zip_is_415()
    {
        var dir = ReadDir();
        using var host = Host(dir);
        var whole = Zip(("tmp1.xml", Utf8(Xml)));

        await AssertRefused(await Post(host, whole[..(whole.Length / 2)]),
            HttpStatusCode.UnsupportedMediaType, "NOT_A_DWSIM_FILE");
        Assert.Empty(StartMarkers(dir));
    }

    [Fact]
    public async Task An_encrypted_entry_is_422_password_protected()
    {
        var dir = ReadDir();
        using var host = Host(dir);
        var bytes = Zip(("tmp1.xml", Utf8(Xml)));
        SetEncryptedFlag(bytes);

        await AssertRefused(await Post(host, bytes), (HttpStatusCode)422, "PASSWORD_PROTECTED");
        Assert.Empty(StartMarkers(dir));
    }

    [Fact]
    public async Task Declared_unpacked_size_over_200_MB_is_413_before_extraction()
    {
        var dir = ReadDir();
        using var host = Host(dir);
        // 201 MB of zeros deflates to ~200 KB — a real bomb, not a patched header.
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = zip.CreateEntry("tmp1.xml", CompressionLevel.Optimal).Open();
            var chunk = new byte[1 << 20];
            for (var i = 0; i < 201; i++) s.Write(chunk);
        }

        await AssertRefused(await Post(host, ms.ToArray()), HttpStatusCode.RequestEntityTooLarge, "UNPACKED_TOO_LARGE");
        Assert.Empty(StartMarkers(dir));
    }

    [Fact]
    public async Task A_body_over_25_MB_is_413_file_too_large()
    {
        var dir = ReadDir();
        using var host = Host(dir);
        var bytes = new byte[25 * 1024 * 1024 + 1];
        Utf8(Xml).CopyTo(bytes, 0);

        await AssertRefused(await Post(host, bytes), HttpStatusCode.RequestEntityTooLarge, "FILE_TOO_LARGE");
        Assert.Empty(StartMarkers(dir));
        Assert.Empty(Uploads(dir));
    }

    [Fact]
    public async Task Exactly_25_MB_is_accepted()
    {
        var dir = ReadDir();
        using var host = Host(dir);
        var bytes = new byte[25 * 1024 * 1024];
        var prolog = Utf8(Xml);
        prolog.CopyTo(bytes, 0);
        Array.Fill(bytes, (byte)' ', prolog.Length, bytes.Length - prolog.Length);

        Assert.Equal(HttpStatusCode.OK, (await Post(host, bytes)).StatusCode);
    }

    // ── the worker's answers ──────────────────────────────────────────────

    [Fact]
    public async Task A_loader_failure_is_422_load_failed_with_the_workers_chain()
    {
        var dir = ReadDir();
        using var host = Host(dir);

        var resp = await Post(host, Utf8(Xml + "<!-- __load-fail -->"));

        Assert.Equal((HttpStatusCode)422, resp.StatusCode);
        var body = await Body(resp);
        Assert.Equal("LOAD_FAILED", body.GetProperty("error").GetString());
        Assert.Contains("TypeInitializationException", body.GetProperty("message").GetString());
        Assert.Contains("FileNotFoundException", body.GetProperty("message").GetString());
        Assert.Empty(Uploads(dir));
    }

    [Fact]
    public async Task Timeout_is_504_and_the_upload_is_deleted()
    {
        var dir = ReadDir();
        using var host = Host(dir);

        var resp = await Post(host, Utf8(Xml + "<!-- __sleep:10 -->"), "?timeoutSeconds=1");

        Assert.Equal(HttpStatusCode.GatewayTimeout, resp.StatusCode);
        Assert.Empty(Uploads(dir));
    }

    [Fact]
    public async Task Caller_abort_kills_the_worker_and_deletes_the_upload()
    {
        var dir = ReadDir();
        using var host = Host(dir);
        using var caller = new CancellationTokenSource();

        var request = Post(host, Utf8(Xml + "<!-- __sleep:30 -->"), ct: caller.Token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string? pidFile = null;
        while (pidFile is null && DateTime.UtcNow < deadline)
        {
            pidFile = Directory.GetFiles(dir, "run-*.pid").FirstOrDefault();
            await Task.Delay(25);
        }
        Assert.NotNull(pidFile);
        await Task.Delay(100);   // the pid file is written before its content is flushed on slow hosts
        var pid = int.Parse(File.ReadAllText(pidFile!));
        Assert.Single(Uploads(dir));   // the upload exists while the worker runs

        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        var gone = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < gone && (Alive(pid) || Uploads(dir).Length > 0)) await Task.Delay(25);
        Assert.False(Alive(pid), "the worker was still running after its caller hung up");
        Assert.Empty(Uploads(dir));
    }

    private static bool Alive(int pid)
    {
        try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task Queue_full_is_429_and_spawns_nothing()
    {
        var dir = ReadDir();
        // Cap 1 → 5 admitted. Five sleepers fill the queue; the sixth is refused.
        using var host = Host(dir, new() { ["MAX_CONCURRENT_SOLVES"] = "1" });
        var sleepers = Enumerable.Range(0, 5).Select(_ => Post(host, Utf8(Xml + "<!-- __sleep:3 -->"))).ToList();
        await Task.Delay(500);

        var resp = await Post(host, Utf8(Xml));

        await AssertRefused(resp, HttpStatusCode.TooManyRequests, "QUEUE_FULL");
        Assert.Equal("5", resp.Headers.RetryAfter?.ToString());
        await Task.WhenAll(sleepers);
        Assert.Empty(Uploads(dir));
    }

    [Fact]
    public async Task ReadResponse_names_every_top_level_field_the_worker_sends()
    {
        using var host = Host(ReadDir());
        var actual = await Body(await Post(host, Utf8(Xml)));

        var declared = typeof(DwsimRunner.Api.ReadResponse).GetProperties()
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToHashSet();
        var sent = actual.EnumerateObject().Select(p => p.Name).Where(n => n != "fake").ToHashSet();

        Assert.True(!sent.Except(declared).Any(), "undeclared: " + string.Join(", ", sent.Except(declared)));
    }

    /// <summary>Set general-purpose bit 0 (encrypted) on every local and central header.</summary>
    private static void SetEncryptedFlag(byte[] zip)
    {
        for (var i = 0; i + 4 <= zip.Length; i++)
        {
            if (zip[i] != 0x50 || zip[i + 1] != 0x4B) continue;
            if (zip[i + 2] == 0x03 && zip[i + 3] == 0x04) zip[i + 6] |= 1;        // local header: flags at +6
            else if (zip[i + 2] == 0x01 && zip[i + 3] == 0x02) zip[i + 8] |= 1;   // central header: flags at +8
        }
    }
}
