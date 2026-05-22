using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Analysis.Bootstrap;
using RayTagger.Core.Configuration;
using RayTagger.Core.IO;

namespace RayTagger.Analysis.Tests.Bootstrap;

public sealed class NativeToolBootstrapperTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "tagger-bootstrap-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDataDirs _dataDirs;
    private readonly List<IDisposable> _disposables = [];

    public NativeToolBootstrapperTests()
    {
        Directory.CreateDirectory(_cacheRoot);
        _dataDirs = new FakeDataDirs(_cacheRoot);
    }

    public void Dispose()
    {
        foreach (var d in _disposables) { try { d.Dispose(); } catch (ObjectDisposedException) { } }
        try { if (Directory.Exists(_cacheRoot)) Directory.Delete(_cacheRoot, recursive: true); }
        catch (IOException) { }
    }

    private HttpClient TrackedClient(StubHandler handler)
    {
        _disposables.Add(handler);
        var client = new HttpClient(handler);
        _disposables.Add(client);
        return client;
    }

    [Fact]
    public async Task Downloads_raw_binary_and_writes_it_to_cache()
    {
        var payload = "#!/bin/sh\necho hi"u8.ToArray();
        var hash = ToHex(SHA256.HashData(payload));

        var manifest = ManifestWith("foo-tool", NativeToolArchiveFormat.None, payload: payload, hash: hash, binaryPath: "");
        var http = TrackedClient(new StubHandler(new Dictionary<string, byte[]> { ["https://example.invalid/foo"] = payload }));

        var sut = new NativeToolBootstrapper(manifest, _dataDirs, http, NullLogger<NativeToolBootstrapper>.Instance,
            runtimeIdentifier: "osx-arm64");

        var path = await sut.EnsureAsync("foo-tool");

        File.Exists(path).Should().BeTrue();
        File.ReadAllBytes(path).Should().BeEquivalentTo(payload);
        path.Should().StartWith(_cacheRoot);
    }

    [Fact]
    public async Task Hash_mismatch_aborts_and_leaves_no_partial_binary()
    {
        var payload = new byte[] { 1, 2, 3 };
        var wrongHash = new string('0', 64);
        var manifest = ManifestWith("foo-tool", NativeToolArchiveFormat.None, payload: payload, hash: wrongHash, binaryPath: "");
        var http = TrackedClient(new StubHandler(new Dictionary<string, byte[]> { ["https://example.invalid/foo"] = payload }));

        var sut = new NativeToolBootstrapper(manifest, _dataDirs, http, NullLogger<NativeToolBootstrapper>.Instance,
            runtimeIdentifier: "osx-arm64");

        var act = () => sut.EnsureAsync("foo-tool");

        await act.Should().ThrowAsync<NativeToolBootstrapException>().Where(ex => ex.Message.Contains("SHA-256", StringComparison.OrdinalIgnoreCase));

        sut.TryResolveCached("foo-tool").Should().BeNull();
    }

    [Fact]
    public async Task Already_cached_binary_skips_the_download()
    {
        var payload = new byte[] { 9, 9, 9 };
        var hash = ToHex(SHA256.HashData(payload));
        var manifest = ManifestWith("foo-tool", NativeToolArchiveFormat.None, payload: payload, hash: hash, binaryPath: "");

        // Pre-populate the expected cache slot so the bootstrapper short-circuits.
        var expectedDir = Path.Combine(_cacheRoot, "tools", "foo-tool", "1.0", "osx-arm64");
        Directory.CreateDirectory(expectedDir);
        var expectedFile = Path.Combine(expectedDir, "foo-tool");
        File.WriteAllBytes(expectedFile, payload);

        var stub = new StubHandler(new Dictionary<string, byte[]>());  // would 404 if called
        var http = TrackedClient(stub);

        var sut = new NativeToolBootstrapper(manifest, _dataDirs, http, NullLogger<NativeToolBootstrapper>.Instance,
            runtimeIdentifier: "osx-arm64");

        var path = await sut.EnsureAsync("foo-tool");

        path.Should().Be(expectedFile);
        stub.RequestCount.Should().Be(0, "the cached binary must short-circuit any network call");
    }

    [Fact]
    public async Task Extracts_zip_archive_and_finds_binary_at_given_path()
    {
        var (zipBytes, binaryPayload) = BuildZipWith("nested/foo-tool", binary: [4, 5, 6]);
        var hash = ToHex(SHA256.HashData(zipBytes));

        var manifest = ManifestWith("foo-tool", NativeToolArchiveFormat.Zip, payload: zipBytes, hash: hash, binaryPath: "nested/foo-tool");
        var http = TrackedClient(new StubHandler(new Dictionary<string, byte[]> { ["https://example.invalid/foo.zip"] = zipBytes }));

        var sut = new NativeToolBootstrapper(manifest, _dataDirs, http, NullLogger<NativeToolBootstrapper>.Instance,
            runtimeIdentifier: "osx-arm64");

        var path = await sut.EnsureAsync("foo-tool");

        File.Exists(path).Should().BeTrue();
        File.ReadAllBytes(path).Should().BeEquivalentTo(binaryPayload);
    }

    [Fact]
    public async Task Extracts_targz_archive_and_finds_binary_at_given_path()
    {
        var (tarGzBytes, binaryPayload) = BuildTarGzWith("nested/foo-tool", binary: [7, 8, 9]);
        var hash = ToHex(SHA256.HashData(tarGzBytes));

        var manifest = ManifestWith("foo-tool", NativeToolArchiveFormat.TarGz, payload: tarGzBytes, hash: hash, binaryPath: "nested/foo-tool");
        var http = TrackedClient(new StubHandler(new Dictionary<string, byte[]> { ["https://example.invalid/foo.tar.gz"] = tarGzBytes }));

        var sut = new NativeToolBootstrapper(manifest, _dataDirs, http, NullLogger<NativeToolBootstrapper>.Instance,
            runtimeIdentifier: "osx-arm64");

        var path = await sut.EnsureAsync("foo-tool");

        File.Exists(path).Should().BeTrue();
        File.ReadAllBytes(path).Should().BeEquivalentTo(binaryPayload);
    }

    [Fact]
    public async Task Missing_rid_in_manifest_throws_with_known_rids_in_message()
    {
        var manifest = new NativeToolsManifest
        {
            Tools =
            {
                ["foo-tool"] = new NativeToolEntry
                {
                    Version = "1.0",
                    Sources =
                    {
                        ["linux-x64"] = new NativeToolSource
                        {
                            Url = "https://example.invalid/foo",
                            Sha256 = new string('0', 64),
                            ArchiveFormat = NativeToolArchiveFormat.None,
                        },
                    },
                },
            },
        };
        var http = TrackedClient(new StubHandler(new Dictionary<string, byte[]>()));

        var sut = new NativeToolBootstrapper(manifest, _dataDirs, http, NullLogger<NativeToolBootstrapper>.Instance,
            runtimeIdentifier: "osx-arm64");

        var act = () => sut.EnsureAsync("foo-tool");

        await act.Should().ThrowAsync<NativeToolBootstrapException>().Where(ex => ex.Message.Contains("linux-x64", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Parallel_calls_share_a_single_download()
    {
        var payload = new byte[] { 1 };
        var hash = ToHex(SHA256.HashData(payload));
        var manifest = ManifestWith("foo-tool", NativeToolArchiveFormat.None, payload: payload, hash: hash, binaryPath: "");
        var stub = new StubHandler(new Dictionary<string, byte[]> { ["https://example.invalid/foo"] = payload });
        var http = TrackedClient(stub);

        var sut = new NativeToolBootstrapper(manifest, _dataDirs, http, NullLogger<NativeToolBootstrapper>.Instance,
            runtimeIdentifier: "osx-arm64");

        var paths = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => sut.EnsureAsync("foo-tool")));

        paths.Should().AllBeEquivalentTo(paths[0]);
        stub.RequestCount.Should().Be(1);
    }

    // --- helpers --------------------------------------------------------------------------

    private static NativeToolsManifest ManifestWith(
        string toolName,
        NativeToolArchiveFormat fmt,
        byte[] payload,
        string hash,
        string binaryPath)
    {
        var ext = fmt switch
        {
            NativeToolArchiveFormat.Zip => ".zip",
            NativeToolArchiveFormat.TarGz => ".tar.gz",
            _ => "",
        };
        return new NativeToolsManifest
        {
            Tools =
            {
                [toolName] = new NativeToolEntry
                {
                    Version = "1.0",
                    Sources =
                    {
                        ["osx-arm64"] = new NativeToolSource
                        {
                            Url = $"https://example.invalid/foo{ext}",
                            Sha256 = hash,
                            ArchiveFormat = fmt,
                            BinaryPath = binaryPath,
                        },
                    },
                },
            },
        };
    }

    private static (byte[] Archive, byte[] BinaryPayload) BuildZipWith(string entryPath, byte[] binary)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryPath);
            using var es = entry.Open();
            es.Write(binary);
        }
        return (ms.ToArray(), binary);
    }

    private static (byte[] Archive, byte[] BinaryPayload) BuildTarGzWith(string entryPath, byte[] binary)
    {
        using var tarMs = new MemoryStream();
        using (var writer = new TarWriter(tarMs, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, entryPath)
            {
                DataStream = new MemoryStream(binary),
            };
            writer.WriteEntry(entry);
        }
        tarMs.Position = 0;

        using var gzMs = new MemoryStream();
        using (var gz = new GZipStream(gzMs, CompressionLevel.Fastest, leaveOpen: true))
        {
            tarMs.CopyTo(gz);
        }
        return (gzMs.ToArray(), binary);
    }

    // -- Model bootstrap tests --------------------------------------------------------------

    [Fact]
    public async Task Downloads_all_model_files_and_writes_version_sentinel()
    {
        var pb = new byte[] { 1, 2, 3, 4 };
        var json = "{\"classes\": [\"a\",\"b\"]}"u8.ToArray();
        var pbHash = ToHex(SHA256.HashData(pb));
        var jsonHash = ToHex(SHA256.HashData(json));

        var manifest = ModelManifest("electronic", "1",
            ("https://example.invalid/head.pb", pbHash, ""),
            ("https://example.invalid/metadata.json", jsonHash, "labels.json"));

        var http = TrackedClient(new StubHandler(new Dictionary<string, byte[]>
        {
            ["https://example.invalid/head.pb"] = pb,
            ["https://example.invalid/metadata.json"] = json,
        }));

        var sut = new NativeToolBootstrapper(manifest, _dataDirs, http, NullLogger<NativeToolBootstrapper>.Instance,
            runtimeIdentifier: "osx-arm64");

        var dir = await sut.EnsureModelAsync("electronic");

        dir.Should().StartWith(_cacheRoot);
        Directory.Exists(dir).Should().BeTrue();
        File.ReadAllBytes(Path.Combine(dir, "head.pb")).Should().BeEquivalentTo(pb);
        File.ReadAllBytes(Path.Combine(dir, "labels.json")).Should().BeEquivalentTo(json);
        File.ReadAllText(Path.Combine(dir, ".version")).Trim().Should().Be("1");
    }

    [Fact]
    public async Task Model_already_cached_at_matching_version_skips_downloads()
    {
        var pb = new byte[] { 7, 7, 7 };
        var manifest = ModelManifest("electronic", "5",
            ("https://example.invalid/head.pb", ToHex(SHA256.HashData(pb)), ""));

        var dir = Path.Combine(_cacheRoot, "models", "electronic");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "head.pb"), pb);
        File.WriteAllText(Path.Combine(dir, ".version"), "5");

        var stub = new StubHandler(new Dictionary<string, byte[]>());   // would 404 if asked
        var sut = new NativeToolBootstrapper(manifest, _dataDirs, TrackedClient(stub),
            NullLogger<NativeToolBootstrapper>.Instance,
            runtimeIdentifier: "osx-arm64");

        var result = await sut.EnsureModelAsync("electronic");

        result.Should().Be(dir);
        stub.RequestCount.Should().Be(0, "matching .version + all files present must short-circuit any network call");
    }

    [Fact]
    public async Task Version_sentinel_mismatch_invalidates_cache_and_re_downloads()
    {
        var newPb = new byte[] { 4, 4, 4 };
        var manifest = ModelManifest("electronic", "2",
            ("https://example.invalid/head.pb", ToHex(SHA256.HashData(newPb)), ""));

        // Pre-populate with stale content + stale version sentinel.
        var dir = Path.Combine(_cacheRoot, "models", "electronic");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "head.pb"), [99, 99, 99]);   // stale bytes
        File.WriteAllText(Path.Combine(dir, ".version"), "1");            // stale version

        var http = TrackedClient(new StubHandler(new Dictionary<string, byte[]>
        {
            ["https://example.invalid/head.pb"] = newPb,
        }));

        var sut = new NativeToolBootstrapper(manifest, _dataDirs, http, NullLogger<NativeToolBootstrapper>.Instance,
            runtimeIdentifier: "osx-arm64");

        var result = await sut.EnsureModelAsync("electronic");

        File.ReadAllBytes(Path.Combine(result, "head.pb")).Should().BeEquivalentTo(newPb);
        File.ReadAllText(Path.Combine(result, ".version")).Trim().Should().Be("2");
    }

    [Fact]
    public async Task Missing_version_sentinel_invalidates_cache()
    {
        var pb = new byte[] { 8, 8, 8 };
        var manifest = ModelManifest("electronic", "1",
            ("https://example.invalid/head.pb", ToHex(SHA256.HashData(pb)), ""));

        // Files present but no .version sentinel → cache must re-validate via re-download.
        var dir = Path.Combine(_cacheRoot, "models", "electronic");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "head.pb"), pb);

        var http = TrackedClient(new StubHandler(new Dictionary<string, byte[]>
        {
            ["https://example.invalid/head.pb"] = pb,
        }));
        var sut = new NativeToolBootstrapper(manifest, _dataDirs, http, NullLogger<NativeToolBootstrapper>.Instance,
            runtimeIdentifier: "osx-arm64");

        await sut.EnsureModelAsync("electronic");

        File.Exists(Path.Combine(dir, ".version")).Should().BeTrue();
    }

    [Fact]
    public async Task Model_hash_mismatch_aborts_and_leaves_cache_pristine()
    {
        var manifest = ModelManifest("electronic", "1",
            ("https://example.invalid/head.pb", new string('0', 64), ""));   // wrong hash

        var http = TrackedClient(new StubHandler(new Dictionary<string, byte[]>
        {
            ["https://example.invalid/head.pb"] = [1, 2, 3],
        }));
        var sut = new NativeToolBootstrapper(manifest, _dataDirs, http, NullLogger<NativeToolBootstrapper>.Instance,
            runtimeIdentifier: "osx-arm64");

        var act = () => sut.EnsureModelAsync("electronic");

        await act.Should().ThrowAsync<NativeToolBootstrapException>()
            .Where(ex => ex.Message.Contains("SHA-256", StringComparison.OrdinalIgnoreCase));

        sut.TryResolveCachedModel("electronic").Should().BeNull();
    }

    [Fact]
    public async Task Unknown_model_key_throws_descriptive_error()
    {
        var manifest = ModelManifest("electronic", "1",
            ("https://example.invalid/head.pb", new string('a', 64), ""));

        var sut = new NativeToolBootstrapper(manifest, _dataDirs,
            TrackedClient(new StubHandler(new Dictionary<string, byte[]>())),
            NullLogger<NativeToolBootstrapper>.Instance,
            runtimeIdentifier: "osx-arm64");

        var act = () => sut.EnsureModelAsync("does-not-exist");

        await act.Should().ThrowAsync<NativeToolBootstrapException>()
            .Where(ex => ex.Message.Contains("not declared", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void KnownModels_lists_all_declared_keys()
    {
        var manifest = new NativeToolsManifest
        {
            Models =
            {
                ["electronic"] = new NativeModelEntry { Version = "1" },
                ["jamendo"] = new NativeModelEntry { Version = "1" },
            },
        };
        var sut = new NativeToolBootstrapper(manifest, _dataDirs,
            TrackedClient(new StubHandler(new Dictionary<string, byte[]>())),
            NullLogger<NativeToolBootstrapper>.Instance,
            runtimeIdentifier: "osx-arm64");

        sut.KnownModels.Should().BeEquivalentTo(["electronic", "jamendo"]);
    }

    private static NativeToolsManifest ModelManifest(
        string modelKey,
        string version,
        params (string Url, string Sha256, string RenameTo)[] files)
    {
        return new NativeToolsManifest
        {
            Models =
            {
                [modelKey] = new NativeModelEntry
                {
                    Version = version,
                    Files = [.. files.Select(f => new NativeModelFile
                    {
                        Url = f.Url,
                        Sha256 = f.Sha256,
                        RenameTo = f.RenameTo,
                    })],
                },
            },
        };
    }

    // -- Shared helpers ---------------------------------------------------------------------

    private static string ToHex(byte[] hash) => Convert.ToHexStringLower(hash);

    private sealed class FakeDataDirs(string root) : IUserDataDirectoryProvider
    {
        public string GetDataDirectory() => root;
        public string GetCacheDirectory()
        {
            var dir = Path.Combine(root, "cache");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private sealed class StubHandler(Dictionary<string, byte[]> responses) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var url = request.RequestUri?.ToString() ?? "";
            if (responses.TryGetValue(url, out var bytes))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
