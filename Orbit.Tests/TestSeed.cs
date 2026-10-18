namespace Orbit;

internal static class TestSeed
{
    public static void Fill(AppService app)
    {
        string[] names = ["asterism", "northbound", "evergreen", "moonlit", "solstice", "horizon", "afterglow"];
        for (int i = 0; i < names.Length; i++) app.Vault.Data.Accounts.Add(new Account { Username = names[i], DisplayName = char.ToUpper(names[i][0]) + names[i][1..], UserId = 1000 + i, Alias = i == 0 ? "Main account" : "", Group = i < 2 ? "Personal" : i < 5 ? "Play together" : "Studio", Status = i == 0 ? "In game" : i == 2 ? "Online" : "Offline", Robux = i == 0 ? 2450 : i == 2 ? 720 : 0, LastUse = DateTime.UtcNow.AddHours(-i), SecurityToken = "SAMPLE_ACCOUNT_NO_REAL_CREDENTIALS" });
        app.Vault.Data.Games.AddRange([new Game { PlaceId = 920587237, Name = "Adopt Me!", Creator = "Uplift Games", Favorite = true }, new Game { PlaceId = 4924922222, Name = "Brookhaven RP", Creator = "Brookhaven", Favorite = true }, new Game { PlaceId = 2753915549, Name = "Blox Fruits", Creator = "Gamer Robot Inc" }]);
        app.Log("Preview workspace loaded. All accounts shown are sample data.");
    }
}
