using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Orbit;

public sealed class CoreTests
{
    private static string Temp() { string path = Path.Combine(Path.GetTempPath(), "Orbit.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static Account Account() => new() { UserId = 42, Username = "synthetic", SecurityToken = "synthetic_cookie_for_tests_only_123456" };
    [Fact] public void PreviousRegressionSuite()
    {
        string path = Path.Combine(Temp(), "legacy-results.json"); int result = SelfTests.Run(["--output", path]);
        Assert.True(result == 0, File.ReadAllText(path));
    }
    [Theory]
    [InlineData(91, "RobloxPlayerBeta", 0, true)]
    [InlineData(92, "RobloxPlayerBeta", 0, false)]
    [InlineData(91, "RobloxPlayerBeta", 1, false)]
    [InlineData(91, "other", 0, false)]
    public void ProcessIdentityRequiresPidStartAndRobloxName(int pid, string name, int seconds, bool expected)
    {
        DateTime start = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc); var account = Account();
        ProcessTracker.Assign(account, new(91, start, "RobloxPlayerBeta"));
        Assert.Equal(expected, ProcessTracker.Matches(account, new(pid, start.AddSeconds(seconds), name)));
    }
    [Fact] public void StopNeverKillsAnUnrelatedCurrentProcess()
    {
        using var current = Process.GetCurrentProcess(); var account = Account();
        ProcessTracker.Assign(account, new(current.Id, current.StartTime.ToUniversalTime(), "RobloxPlayerBeta"));
        Assert.False(ProcessTracker.Stop(account)); Assert.False(current.HasExited);
    }
    [Theory]
    [InlineData(0, 42, "follow", true)]
    [InlineData(42, 0, "follow", false)]
    [InlineData(1, 42, "follow", false)]
    [InlineData(1, 0, "game", true)]
    [InlineData(1, 42, "game", false)]
    [InlineData(0, 0, "game", false)]
    public void FollowHasItsOwnRequiredUserId(long place, long user, string mode, bool valid)
    {
        var target = new LaunchTarget(place, Mode: mode, UserId: user);
        if (valid) target.Validate(); else Assert.Throws<ArgumentException>(target.Validate);
        if (valid && mode == "follow") { Assert.Contains("userId=42", target.Query("tracker")); Assert.DoesNotContain("placeId", target.Query("tracker")); }
    }
    [Fact] public void PasswordArtifactsAreDiscardedOnLoadAndImport()
    {
        const string secret = "synthetic-old-password";
        var account = Json.Read<Account>("{\"userId\":42,\"username\":\"test\",\"securityToken\":\"synthetic_cookie_for_tests_only_123456\",\"password\":\"" + secret + "\"}");
        Assert.DoesNotContain(secret, Json.Write(account));
        var imported = LegacyImport.Read(Encoding.UTF8.GetBytes("[{\"UserID\":42,\"Username\":\"test\",\"SecurityToken\":\"synthetic_cookie_for_tests_only_123456\",\"Password\":\"" + secret + "\"}]"), "");
        Assert.DoesNotContain(secret, Json.Write(imported)); Assert.Null(typeof(Account).GetProperty("Password"));
    }
    [Fact] public async Task ActivityWritesAreDebouncedAndFlushKeepsLatestSnapshot()
    {
        string path = Path.Combine(Temp(), "activity.json"); using var store = new ActivityStore(path);
        store.Queue([new(DateTime.UtcNow, "info", "old")]); store.Queue([new(DateTime.UtcNow, "info", "latest")]);
        Assert.False(File.Exists(path)); await store.FlushAsync();
        Assert.Contains("latest", File.ReadAllText(path)); Assert.DoesNotContain("old", File.ReadAllText(path));
    }
    [Fact] public void VaultRecoveryValidatesBeforeReplacingAndPreservesBothCopies()
    {
        var vault = new Vault(Temp()); vault.Merge([Account()]); vault.Save();
        byte[] backup = File.ReadAllBytes(vault.FilePath + ".backup"); File.WriteAllText(vault.FilePath, "corrupt");
        vault.RestorePrevious(); Assert.Single(vault.Data.Accounts);
        Assert.Equal(backup, File.ReadAllBytes(vault.FilePath + ".backup"));
        Assert.Equal("corrupt", File.ReadAllText(Directory.GetFiles(vault.DirectoryPath, "vault.dat.unreadable-*").Single()));
    }
    [Fact] public void CorruptBackupCannotOverwriteTheCurrentVault()
    {
        var vault = new Vault(Temp()); vault.Merge([Account()]); byte[] before = File.ReadAllBytes(vault.FilePath);
        File.WriteAllText(vault.FilePath + ".backup", "corrupt"); Assert.ThrowsAny<Exception>(vault.RestorePrevious);
        Assert.Equal(before, File.ReadAllBytes(vault.FilePath));
    }
    [Fact] public async Task DailyBackupsAreOptInEncryptedAndRetainSeven()
    {
        var vault = new Vault(Temp()); vault.Merge([Account()]); vault.Data.Games.Add(new() { PlaceId = 123, Name = "sample" });
        using var app = new AppService(vault); string folder = Temp();
        await app.Backups.RunNow(); Assert.Empty(Directory.GetFiles(folder));
        const string password = "synthetic-backup-password"; app.Backups.Configure(folder, password);
        using (var reopened = new ScheduledBackups(app)) Assert.True(JsonSerializer.SerializeToElement(reopened.State, Json.Options).GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain(password, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(vault.DirectoryPath, "backup-policy.dat"))));
        for (int i = 0; i < 9; i++) await app.Backups.RunNow();
        var files = Directory.GetFiles(folder, "*.orbit"); Assert.Equal(7, files.Length);
        var restored = Vault.DecodeBackup(File.ReadAllBytes(files[0]), password); Assert.Single(restored.Accounts); Assert.Single(restored.Games);
        app.Backups.Disable(); Assert.False(File.Exists(Path.Combine(vault.DirectoryPath, "backup-policy.dat")));
    }
    private static int Port() { using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
    [Fact] public async Task ListenerSurvivesBadRequestsRotatesTokensAndReportsFailure()
    {
        var vault = new Vault(Temp()); vault.Data.Settings.ApiEnabled = true; vault.Data.Settings.ApiPort = Port();
        using var app = new AppService(vault); app.ApplySettings(); var server = app.Server!; Assert.True(server.IsRunning);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}"), Timeout = TimeSpan.FromSeconds(5) };
        string original = vault.Data.Settings.ApiToken; http.DefaultRequestHeaders.Add("Authorization", "Bearer " + original);
        using var bad = await http.PostAsync("/v1/launch", new StringContent("{invalid", Encoding.UTF8, "application/json")); Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var valid = await http.GetAsync("/v1/state"); Assert.Equal(HttpStatusCode.OK, valid.StatusCode); Assert.True(server.IsRunning);
        using var hostRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/state"); hostRequest.Headers.Host = "attacker.invalid";
        using var host = await http.SendAsync(hostRequest); Assert.False(host.IsSuccessStatusCode);
        await app.Command("rotateApiToken", JsonSerializer.SerializeToElement(new { }));
        Assert.NotEqual(original, vault.Data.Settings.ApiToken);
        using var rejected = await http.GetAsync("/v1/state"); Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        http.DefaultRequestHeaders.Remove("Authorization"); http.DefaultRequestHeaders.Add("Authorization", "Bearer " + vault.Data.Settings.ApiToken);
        using var newToken = await http.GetAsync("/v1/state"); Assert.Equal(HttpStatusCode.OK, newToken.StatusCode);
        server.InterruptListener();
        for (int i = 0; i < 100 && app.Server is not null; i++) await Task.Delay(10);
        Assert.Null(app.Server); Assert.False(server.IsRunning); Assert.Contains(app.Activity, item => item.Message.Contains("stopped unexpectedly"));
    }
    private sealed class RejectApi : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") }); }
    }
    private sealed class FakePlatform : ILaunchPlatform
    {
        public void Start(string protocol) => throw new Exception("No process should start for rejected auth");
        public ProcessIdentity? Find(string tracker) => null;
        public bool IsRunning(Account account) => false;
        public bool Stop(Account account) => false;
    }
    [Fact] public async Task RelaunchDelayForOneAccountDoesNotBlockOthersAndRetriesStopAtThree()
    {
        var vault = new Vault(Temp()); var a = Account(); a.AutoRelaunch = true; a.PlaceId = 123; a.RelaunchSeconds = 60;
        var b = Account(); b.UserId = 43; b.AutoRelaunch = true; b.PlaceId = 123; b.RelaunchSeconds = 1; vault.Merge([a, b]);
        using var api = new RejectApi(); using var app = new AppService(vault, api: new RobloxApi(api));
        var never = new TaskCompletionSource(); var waits = new List<double>();
        using var launches = new LaunchService(app, new FakePlatform(), (time, token) => { waits.Add(time.TotalSeconds); return time.TotalSeconds >= 60 ? never.Task.WaitAsync(token) : Task.CompletedTask; });
        launches.Schedule(a); launches.Schedule(b);
        for (int i = 0; i < 100 && b.Status != "Relaunch gave up"; i++) await Task.Delay(10);
        Assert.Equal(3, b.RelaunchAttempts); Assert.Equal("Relaunch gave up", b.Status); Assert.Equal(3, api.Requests);
        Assert.Contains(60d, waits); Assert.Contains(1d, waits); Assert.Contains(2d, waits); Assert.Contains(4d, waits);
        launches.Schedule(b); Assert.Equal(3, api.Requests);
    }
    [Fact] public void ProductionAssemblyContainsNoTestRunner()
    {
        Assert.Null(typeof(AppService).Assembly.GetType("Orbit.SelfTests"));
        Assert.DoesNotContain(typeof(AppService).Assembly.GetManifestResourceNames(), name => name.Contains("testdata"));
        Assert.DoesNotContain(typeof(MainWindow).GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance), method => method.Name == "Smoke");
    }
    [Fact] public async Task PlaceSearchUsesRootPlaceIdsAndSupportsPaginationWithoutCookies()
    {
        using var api = new RobloxApi(new SearchFixture());
        var page = await api.SearchPlaces("Natural Disaster");
        Assert.Equal(189707, Assert.Single(page.Games).PlaceId); Assert.Equal(65241, page.Games[0].UniverseId);
        Assert.Equal("synthetic-next-page", page.NextPageToken); Assert.True(Guid.TryParse(page.SessionId, out _));
        var next = await api.SearchPlaces("Natural Disaster", page.NextPageToken, page.SessionId); Assert.Equal(page.SessionId, next.SessionId);
        await Assert.ThrowsAsync<ArgumentException>(() => api.SearchPlaces("x"));
    }
    [OptionalFact("ORBIT_SEARCH_TEST")]
    public async Task LivePublicPlaceSearch()
    {
        using var api = new RobloxApi(); var page = await api.SearchPlaces("Natural Disaster Survival");
        Assert.Contains(page.Games, game => game.PlaceId == 189707); Assert.NotEmpty(page.Games);
    }
}
