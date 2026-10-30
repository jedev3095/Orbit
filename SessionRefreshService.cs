namespace Orbit;

public sealed class SessionRefreshService(AppService app, TimeSpan? accountTimeout = null) : IDisposable
{
    private CancellationTokenSource? batch, current;
    public bool IsRunning { get; private set; }
    public Guid? CurrentAccountId { get; private set; }
    public int Total { get; private set; }
    public int Processed { get; private set; }
    public int Healthy { get; private set; }
    public int Refreshed { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public string Phase { get; private set; } = "";
    internal Task Completion { get; private set; } = Task.CompletedTask;
    public object State => new { running = IsRunning, currentAccountId = CurrentAccountId, total = Total, processed = Processed, healthy = Healthy, refreshed = Refreshed, skipped = Skipped, failed = Failed, phase = Phase };
    public bool Start(Func<Account, SavedLogin, CancellationToken, Task<bool>> signIn)
    {
        if (IsRunning) return false;
        if (app.IsDemo) throw new InvalidOperationException("Session refresh is unavailable in demo mode.");
        if (!app.Credentials.Available) throw new InvalidOperationException("Recover the local password vault before refreshing expired accounts.");
        var ids = app.Vault.Data.Accounts.Select(a => a.Id).ToArray();
        batch?.Dispose(); batch = new(); IsRunning = true;
        Total = ids.Length; Processed = Healthy = Refreshed = Skipped = Failed = 0;
        Phase = "Checking sessions"; app.Notify();
        Completion = Run(ids, signIn, batch.Token);
        return true;
    }
    public void SkipCurrent() => current?.Cancel();
    public void Stop() { if (IsRunning) { Phase = "Stopping"; batch?.Cancel(); app.Notify(); } }
    private async Task Run(Guid[] ids, Func<Account, SavedLogin, CancellationToken, Task<bool>> signIn, CancellationToken stop)
    {
        await Task.Yield(); bool rateLimited = false;
        try
        {
            foreach (var id in ids)
            {
                stop.ThrowIfCancellationRequested();
                var account = app.Vault.Data.Accounts.FirstOrDefault(a => a.Id == id);
                if (account is null) { Skipped++; Processed++; continue; }
                using var accountLifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
                current = accountLifetime; current.CancelAfter(accountTimeout ?? TimeSpan.FromMinutes(5));
                var token = current.Token;
                CurrentAccountId = id; Phase = "Checking session"; app.Notify();
                try
                {
                    bool expired = account.Status == "Needs login";
                    if (!expired)
                    {
                        try
                        {
                            var identity = await app.Api.Identify(account.SecurityToken).WaitAsync(token);
                            token.ThrowIfCancellationRequested();
                            expired = identity.UserId != account.UserId;
                        }
                        catch (RobloxApiException ex) when (ex.RequiresLogin) { expired = true; }
                    }
                    token.ThrowIfCancellationRequested();
                    if (!expired)
                    {
                        if (account.Status == "Unchecked") { account.Status = "Offline"; account.LastError = ""; }
                        Healthy++; continue;
                    }
                    account.Status = "Needs login"; account.LastError = "Session expired. Sign in again to refresh this account.";
                    var login = app.Credentials.ForAccount(account);
                    if (login is null)
                    {
                        Skipped++; app.Log($"Skipped session refresh for @{account.Username}: no unambiguous saved login. Choose Refresh sign-in to select or add one.", "warning");
                        continue;
                    }
                    Phase = "Waiting for sign-in"; app.Notify();
                    bool saved = await signIn(account, login, token);
                    if (saved) Refreshed++;
                    else { token.ThrowIfCancellationRequested(); Skipped++; }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { Skipped++; throw; }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Skipped++; app.Log($"Skipped session refresh for @{account.Username}; it was skipped or reached the five-minute time limit.", "warning");
                }
                catch (RobloxApiException ex) when ((int)ex.StatusCode == 429)
                {
                    Failed++; rateLimited = true; app.Log("Session refresh stopped because Roblox is rate limiting requests. Try again later.", "warning"); break;
                }
                catch (Exception)
                {
                    Failed++; app.Log($"Could not refresh @{account.Username}. Its saved details were kept; check the session or saved login and retry.", "warning");
                }
                finally { Processed++; current = null; app.Notify(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch { Failed++; app.Log("The session refresh queue stopped unexpectedly. Try again from Accounts.", "error"); }
        finally
        {
            current = null; CurrentAccountId = null; IsRunning = false;
            Phase = stop.IsCancellationRequested ? "Stopped" : rateLimited ? "Rate limited — try again later" : "Finished";
            app.Log($"Session refresh {Phase.ToLowerInvariant()}: {Refreshed} refreshed, {Healthy} valid, {Skipped} skipped, {Failed} failed.");
        }
    }
    public void Dispose() { batch?.Cancel(); }
}
