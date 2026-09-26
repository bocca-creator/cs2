using BattlePass;

var directory = Path.Combine(Path.GetTempPath(), "BattlePassTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var config = new BattlePassConfig
    {
        SeasonId = "test-1",
        DailyQuests = [new() { Id = "kills", Name = "Kills", Type = "kills", Target = 2, Xp = 50 }],
        WeeklyQuests = [new() { Id = "heads", Name = "Heads", Type = "headshots", Target = 2, Xp = 80 }]
    };
    config.Validate();
    var state = new PassState(config, directory);
    var player = state.Get(76561198000000001);
    var day = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
    Assert(state.RecordKill(player, false, day).Count == 0, "Quest completes too early");
    Assert(state.RecordKill(player, true, day).SequenceEqual(["Kills (+50 XP)"]), "Daily completion");
    Assert(player.Xp == 75 && player.Daily.Completed.Contains("kills"), "XP and daily completion");
    Assert(state.RecordKill(player, true, day).SequenceEqual(["Heads (+80 XP)"]), "Weekly completion");
    Assert(player.Xp == 170 && state.Level(player) == 1, "Weekly XP and level");
    Assert(state.RecordKill(player, false, day).Count == 0 && player.Xp == 180, "Quests awarded once");

    var free = new RewardDefinition { Level = 1, Name = "Free" };
    var premium = new RewardDefinition { Level = 1, Name = "Premium", Premium = true };
    Assert(!state.Claim(player, premium), "Premium cannot be claimed without access");
    Assert(state.Claim(player, free) && !state.Claim(player, free), "Free reward once");
    state.SetPremium(player, true);
    Assert(state.Claim(player, premium) && !state.Claim(player, premium), "Premium reward once");

    var reloaded = new PassState(config, directory);
    var persisted = reloaded.Get(76561198000000001);
    Assert(persisted.Xp == 180 && persisted.Premium && !reloaded.Claim(persisted, free) &&
           !reloaded.Claim(persisted, premium), "Progress and claims survive restart");

    var tomorrow = day.AddDays(1);
    reloaded.Refresh(persisted, tomorrow);
    Assert(persisted.Daily.Counts.Count == 0 && persisted.Weekly.Completed.Contains("heads"),
        "Daily reset preserves weekly progress");
    reloaded.Refresh(persisted, tomorrow.AddDays(8));
    Assert(persisted.Weekly.Counts.Count == 0 && persisted.Weekly.Completed.Count == 0, "Weekly reset");
    Assert(PassState.PeriodKey(new DateTime(2020, 12, 31), true) ==
           PassState.PeriodKey(new DateTime(2021, 1, 1), true), "ISO year boundary");

    config.SeasonId = "test-2";
    var newSeason = new PassState(config, directory).Get(76561198000000001);
    Assert(newSeason.Xp == 0 && !newSeason.Premium && newSeason.FreeClaims.Count == 0,
        "Season isolation");
    config.SeasonId = "../unsafe";
    AssertThrows<ArgumentException>(config.Validate, "Unsafe season ID rejected");
    Console.WriteLine("BattlePass tests passed.");
}
finally
{
    Directory.Delete(directory, true);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void AssertThrows<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception(message);
}
