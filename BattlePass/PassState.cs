using System.Globalization;
using System.Text.Json;

namespace BattlePass;

public sealed class PlayerProgress
{
    public long Xp { get; set; }
    public bool Premium { get; set; }
    public HashSet<int> FreeClaims { get; set; } = [];
    public HashSet<int> PremiumClaims { get; set; } = [];
    public QuestPeriod Daily { get; set; } = new();
    public QuestPeriod Weekly { get; set; } = new();
}

public sealed class QuestPeriod
{
    public string Key { get; set; } = "";
    public Dictionary<string, int> Counts { get; set; } = [];
    public HashSet<string> Completed { get; set; } = [];
}

public sealed class PassState
{
    private readonly BattlePassConfig _config;
    private readonly string _path;
    private readonly Dictionary<string, PlayerProgress> _players;

    public PassState(BattlePassConfig config, string directory)
    {
        _config = config;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"progress-{config.SeasonId}.json");
        _players = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, PlayerProgress>>(File.ReadAllText(_path))
              ?? throw new InvalidDataException("Battle pass progress file has no players.")
            : [];
    }

    public PlayerProgress Get(ulong steamId) =>
        _players.TryGetValue(steamId.ToString(CultureInfo.InvariantCulture), out var progress)
            ? progress
            : _players[steamId.ToString(CultureInfo.InvariantCulture)] = new PlayerProgress();

    public int Level(PlayerProgress progress) =>
        (int)Math.Clamp(progress.Xp / _config.XpPerLevel, 0, _config.MaxLevel);

    public static string PeriodKey(DateTime utcNow, bool weekly) =>
        weekly
            ? $"{ISOWeek.GetYear(utcNow)}-W{ISOWeek.GetWeekOfYear(utcNow):D2}"
            : utcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public bool Refresh(PlayerProgress progress, DateTime utcNow)
    {
        var previousDaily = progress.Daily;
        var previousWeekly = progress.Weekly;
        if (!ResetPeriods(progress, utcNow)) return false;
        try
        {
            Save();
        }
        catch
        {
            progress.Daily = previousDaily;
            progress.Weekly = previousWeekly;
            throw;
        }
        return true;
    }

    private static bool ResetPeriods(PlayerProgress progress, DateTime utcNow)
    {
        var changed = false;
        var daily = PeriodKey(utcNow, false);
        var weekly = PeriodKey(utcNow, true);
        if (progress.Daily.Key != daily)
        {
            progress.Daily = new QuestPeriod { Key = daily };
            changed = true;
        }
        if (progress.Weekly.Key != weekly)
        {
            progress.Weekly = new QuestPeriod { Key = weekly };
            changed = true;
        }
        return changed;
    }

    public List<string> RecordKill(PlayerProgress progress, bool headshot, DateTime utcNow)
    {
        var previousXp = progress.Xp;
        var previousDaily = progress.Daily;
        var previousWeekly = progress.Weekly;
        var completed = new List<string>();
        try
        {
            ResetPeriods(progress, utcNow);
            progress.Daily = CopyPeriod(progress.Daily);
            progress.Weekly = CopyPeriod(progress.Weekly);
            AwardXp(progress, (long)_config.XpPerKill + (headshot ? _config.HeadshotBonusXp : 0));
            UpdateQuests(progress, progress.Daily, _config.DailyQuests, headshot, completed);
            UpdateQuests(progress, progress.Weekly, _config.WeeklyQuests, headshot, completed);
            Save();
        }
        catch
        {
            progress.Xp = previousXp;
            progress.Daily = previousDaily;
            progress.Weekly = previousWeekly;
            throw;
        }
        return completed;
    }

    private static QuestPeriod CopyPeriod(QuestPeriod period) => new()
    {
        Key = period.Key,
        Counts = new Dictionary<string, int>(period.Counts),
        Completed = new HashSet<string>(period.Completed)
    };

    private void UpdateQuests(PlayerProgress player, QuestPeriod period, List<QuestDefinition> quests,
        bool headshot, List<string> completed)
    {
        foreach (var quest in quests)
        {
            if (period.Completed.Contains(quest.Id) || (quest.Type == "headshots" && !headshot))
                continue;

            var count = period.Counts.GetValueOrDefault(quest.Id);
            if (count < quest.Target) count++;
            period.Counts[quest.Id] = count;
            if (count < quest.Target) continue;

            period.Completed.Add(quest.Id);
            AwardXp(player, quest.Xp);
            completed.Add($"{quest.Name} (+{quest.Xp} XP)");
        }
    }

    private void AwardXp(PlayerProgress progress, long xp)
    {
        var maximum = (long)_config.MaxLevel * _config.XpPerLevel;
        progress.Xp = Math.Clamp(progress.Xp, 0, maximum);
        progress.Xp += Math.Min(maximum - progress.Xp, xp);
    }

    public bool Claim(PlayerProgress progress, RewardDefinition reward)
    {
        if (Level(progress) < reward.Level || (reward.Premium && !progress.Premium))
            return false;

        var claims = reward.Premium ? progress.PremiumClaims : progress.FreeClaims;
        if (!claims.Add(reward.Level)) return false;
        try
        {
            Save();
        }
        catch
        {
            claims.Remove(reward.Level);
            throw;
        }
        return true;
    }

    public void SetPremium(PlayerProgress progress, bool enabled)
    {
        var previous = progress.Premium;
        progress.Premium = enabled;
        try
        {
            Save();
        }
        catch
        {
            progress.Premium = previous;
            throw;
        }
    }

    public void Save()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_players));
        File.Move(temp, _path, true);
    }
}
