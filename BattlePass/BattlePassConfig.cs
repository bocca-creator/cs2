using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace BattlePass;

public sealed class BattlePassConfig : BasePluginConfig
{
    [JsonPropertyName("SeasonId")] public string SeasonId { get; set; } = "season-1";
    [JsonPropertyName("XpPerLevel")] public int XpPerLevel { get; set; } = 100;
    [JsonPropertyName("MaxLevel")] public int MaxLevel { get; set; } = 50;
    [JsonPropertyName("XpPerKill")] public int XpPerKill { get; set; } = 10;
    [JsonPropertyName("HeadshotBonusXp")] public int HeadshotBonusXp { get; set; } = 5;
    [JsonPropertyName("KillCooldownSeconds")] public int KillCooldownSeconds { get; set; } = 60;
    [JsonPropertyName("DailyQuests")]
    public List<QuestDefinition> DailyQuests { get; set; } =
    [
        new() { Id = "daily_kills", Name = "Вбийте 5 суперників", Type = "kills", Target = 5, Xp = 50 }
    ];

    [JsonPropertyName("WeeklyQuests")]
    public List<QuestDefinition> WeeklyQuests { get; set; } =
    [
        new() { Id = "weekly_headshots", Name = "Зробіть 20 вбивств у голову", Type = "headshots", Target = 20, Xp = 200 }
    ];

    [JsonPropertyName("Rewards")]
    public List<RewardDefinition> Rewards { get; set; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SeasonId) ||
            SeasonId.Length > 50 ||
            SeasonId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("SeasonId may contain only ASCII letters, digits, '-' and '_'.");

        if (XpPerLevel <= 0 || MaxLevel is < 1 or > 1000 ||
            XpPerKill < 0 || HeadshotBonusXp < 0 || KillCooldownSeconds < 0)
            throw new ArgumentException("Invalid battle pass XP, level or cooldown setting.");

        ValidateQuests(DailyQuests);
        ValidateQuests(WeeklyQuests);
        if (Rewards is null || Rewards.Any(r =>
                r.Level < 1 || r.Level > MaxLevel ||
                string.IsNullOrWhiteSpace(r.Name) ||
                r.Command is null ||
                r.Command.Contains('\n') || r.Command.Contains('\r')) ||
            Rewards.GroupBy(r => (r.Level, r.Premium)).Any(g => g.Count() > 1))
            throw new ArgumentException("Rewards must have unique level/track pairs and valid names/commands.");
    }

    private static void ValidateQuests(List<QuestDefinition>? quests)
    {
        if (quests is null || quests.Any(q =>
                string.IsNullOrWhiteSpace(q.Id) || q.Id.Length > 50 ||
                q.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') ||
                string.IsNullOrWhiteSpace(q.Name) ||
                q.Type is not ("kills" or "headshots") || q.Target <= 0 || q.Xp < 0) ||
            quests.GroupBy(q => q.Id).Any(g => g.Count() > 1))
            throw new ArgumentException("Quest IDs must be unique per period; type must be kills or headshots.");
    }
}

public sealed class QuestDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "kills";
    public int Target { get; set; }
    public int Xp { get; set; }
}

public sealed class RewardDefinition
{
    public int Level { get; set; }
    public string Name { get; set; } = "";
    public bool Premium { get; set; }
    public string Command { get; set; } = "";
}
