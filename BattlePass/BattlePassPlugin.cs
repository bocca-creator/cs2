using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Menu;
using MenuManager;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace BattlePass;

[MinimumApiVersion(372)]
public class BattlePassPlugin : BasePlugin, IPluginConfig<BattlePassConfig>
{
    private readonly PluginCapability<IMenuApi> _menuApiCapability = new("menu:nfcore");
    private IMenuApi? _menuApi;
    private readonly Dictionary<ulong, PlayerProgress> _progress = new();
    private MySqlConnectionStringBuilder? _database;
    private readonly HashSet<ulong> _dirtyPlayers = new();
    private string _progressPath = string.Empty;
    private string _resetMarkerPath = string.Empty;

    public BattlePassConfig Config { get; set; } = new();
    public override string ModuleName => "Battle Pass";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "DevTeam";

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try
        {
            _menuApi = _menuApiCapability.Get();
        }
        catch (KeyNotFoundException)
        {
            _menuApi = null;
        }

        if (_menuApi is null)
            Logger.LogWarning("MenuManagerCS2 is not available; using CounterStrikeSharp menus.");
    }

    public override void Load(bool hotReload)
    {
        var configDirectory = Path.GetDirectoryName(Config.GetConfigPath())!;
        Directory.CreateDirectory(configDirectory);
        EnsureConfigFile(Config.GetConfigPath());
        _progressPath = Path.Combine(configDirectory, "progress.json");
        _resetMarkerPath = Path.Combine(configDirectory, "quarterly_reset.txt");
        LoadProgress();
        InitializeDatabase();
        ApplyQuarterlyReset();
        ScheduleProgressSave();
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventPlayerJump>(OnPlayerJump);
        RegisterEventHandler<EventPlayerFootstep>(OnPlayerFootstep);
        AddCommand("css_bp", "Shows your Battle Pass progress", OnProgressCommand);
        AddCommand("css_battlepass", "Shows your Battle Pass progress", OnProgressCommand);
        AddCommand("css_bptasks", "Shows your Battle Pass tasks", OnTasksCommand);
        AddCommand("css_bp_reset", "Reset Battle Pass data (root only)", OnResetCommand);
    }

    private void OnResetCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !AdminManager.PlayerHasPermissions(player, "@css/root"))
            return;

        if (!string.Equals(command.ArgString.Trim(), "confirm", StringComparison.OrdinalIgnoreCase))
        {
            player.PrintToChat(" \x04[Battle Pass]\x01 Для очищення введіть: !bp_reset confirm");
            return;
        }

        ResetAllData();
        player.PrintToChat(" \x04[Battle Pass]\x01 Дані повністю очищено.");
    }

    private void ApplyQuarterlyReset()
    {
        if (!Config.Database.ResetEveryThreeMonths)
            return;

        if (!File.Exists(_resetMarkerPath))
        {
            File.WriteAllText(_resetMarkerPath, DateTime.UtcNow.ToString("O"));
            return;
        }

        if (!DateTime.TryParse(File.ReadAllText(_resetMarkerPath), out var lastReset) || DateTime.UtcNow >= lastReset.ToUniversalTime().AddMonths(3))
            ResetAllData();
    }

    private void ResetAllData()
    {
        _progress.Clear();
        _dirtyPlayers.Clear();
        if (File.Exists(_progressPath))
            File.Delete(_progressPath);

        if (_database is not null)
        {
            try
            {
                using var connection = new MySqlConnection(_database.ConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM battlepass_progress";
                command.ExecuteNonQuery();
            }
            catch (Exception exception)
            {
                Logger.LogError(exception, "Could not reset Battle Pass MySQL data.");
            }
        }

        File.WriteAllText(_resetMarkerPath, DateTime.UtcNow.ToString("O"));
    }

    public override void Unload(bool hotReload)
    {
        FlushDatabase();
        SaveProgress();
    }

    public void OnConfigParsed(BattlePassConfig config)
    {
        Config = config;
        var configPath = Config.GetConfigPath();
        var configDirectory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(configDirectory))
            Directory.CreateDirectory(configDirectory);
        EnsureConfigFile(configPath);
    }

    private void InitializeDatabase()
    {
        if (!Config.Database.Enabled)
            return;

        try
        {
            _database = new MySqlConnectionStringBuilder
            {
                Server = Config.Database.Host,
                Port = (uint)Config.Database.Port,
                UserID = Config.Database.User,
                Password = Config.Database.Password,
                Database = Config.Database.Name,
                SslMode = MySqlSslMode.Preferred
            };
            var databaseName = _database.Database;
            _database.Database = string.Empty;
            using var connection = new MySqlConnection(_database.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE IF NOT EXISTS `{databaseName.Replace("`", "``")}`";
            command.ExecuteNonQuery();
            _database.Database = databaseName;
            using var schemaConnection = new MySqlConnection(_database.ConnectionString);
            schemaConnection.Open();
            using var schemaCommand = schemaConnection.CreateCommand();
            schemaCommand.CommandText = "CREATE TABLE IF NOT EXISTS battlepass_progress (steam_id BIGINT UNSIGNED PRIMARY KEY, level INT NOT NULL, experience INT NOT NULL, stars INT NOT NULL, tasks_json LONGTEXT NOT NULL, periods_json LONGTEXT NOT NULL, updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP)";
            schemaCommand.ExecuteNonQuery();
            LoadProgressFromDatabase(schemaConnection);
            Logger.LogInformation("Battle Pass MySQL persistence enabled.");
        }
        catch (Exception exception)
        {
            _database = null;
            Logger.LogError(exception, "Could not initialize Battle Pass MySQL persistence; using JSON.");
        }
    }

    private void LoadProgressFromDatabase(MySqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT steam_id, level, experience, stars, tasks_json, periods_json FROM battlepass_progress";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var progress = new PlayerProgress
            {
                Level = reader.GetInt32(1),
                Exp = reader.GetInt32(2),
                Stars = reader.GetInt32(3),
                Tasks = JsonSerializer.Deserialize<Dictionary<string, int>>(reader.GetString(4)) ?? new(),
                TaskPeriods = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(5)) ?? new()
            };
            _progress[reader.GetUInt64(0)] = progress;
        }
    }

    private void FlushDatabase()
    {
        if (_database is null || _dirtyPlayers.Count == 0)
            return;

        foreach (var steamId in _dirtyPlayers.ToArray())
        {
            if (!_progress.TryGetValue(steamId, out var progress) || SavePlayerToDatabase(steamId, progress))
                _dirtyPlayers.Remove(steamId);
        }
    }

    private void ScheduleProgressSave()
    {
        AddTimer(Math.Max(30, Config.Settings.SaveIntervalSeconds), () =>
        {
            FlushDatabase();
            SaveProgress();
            ScheduleProgressSave();
        });
    }

    private bool SavePlayerToDatabase(ulong steamId, PlayerProgress progress)
    {
        if (_database is null)
            return false;

        try
        {
            using var connection = new MySqlConnection(_database.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO battlepass_progress (steam_id, level, experience, stars, tasks_json, periods_json) VALUES (@steam_id, @level, @experience, @stars, @tasks, @periods) ON DUPLICATE KEY UPDATE level = @level, experience = @experience, stars = @stars, tasks_json = @tasks, periods_json = @periods";
            command.Parameters.AddWithValue("@steam_id", steamId);
            command.Parameters.AddWithValue("@level", progress.Level);
            command.Parameters.AddWithValue("@experience", progress.Exp);
            command.Parameters.AddWithValue("@stars", progress.Stars);
            command.Parameters.AddWithValue("@tasks", JsonSerializer.Serialize(progress.Tasks));
            command.Parameters.AddWithValue("@periods", JsonSerializer.Serialize(progress.TaskPeriods));
            command.ExecuteNonQuery();
            return true;
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Could not save Battle Pass progress to MySQL for {SteamId}.", steamId);
            return false;
        }
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (attacker is null || !attacker.IsValid || attacker.IsBot || attacker.AuthorizedSteamID is null ||
            victim is null || !victim.IsValid || victim.IsBot || victim.AuthorizedSteamID is null ||
            attacker == victim || attacker.TeamNum == victim.TeamNum)
            return HookResult.Continue;

        AwardExperience(attacker, Config.Settings.ExpPerKill, "за вбивство");
        AdvanceTasks(attacker, BattlePassTaskType.Kills);
        return HookResult.Continue;
    }

    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player?.AuthorizedSteamID is null)
            return HookResult.Continue;

        AdvanceTasks(player, BattlePassTaskType.Shots);
        var weapon = @event.Weapon ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(weapon))
            AdvanceTasks(player, BattlePassTaskType.WeaponShots, weapon);

        if (IsNoZoomWeapon(weapon) && player.PlayerPawn?.Value?.IsScoped != true)
            AdvanceTasks(player, BattlePassTaskType.NoZoomShots, weapon);

        return HookResult.Continue;
    }

    private HookResult OnPlayerJump(EventPlayerJump @event, GameEventInfo info)
    {
        if (@event.Userid?.AuthorizedSteamID is not null)
            AdvanceTasks(@event.Userid, BattlePassTaskType.Jumps);

        return HookResult.Continue;
    }

    private HookResult OnPlayerFootstep(EventPlayerFootstep @event, GameEventInfo info)
    {
        if (@event.Userid?.AuthorizedSteamID is not null)
            AdvanceTasks(@event.Userid, BattlePassTaskType.Steps);

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        foreach (var player in Utilities.GetPlayers().Where(player => player is { IsValid: true, AuthorizedSteamID: not null }))
        {
            AwardExperience(player, Config.Settings.ExpPerRound, "за раунд", false);
            AdvanceTasks(player, BattlePassTaskType.Rounds);
        }

        return HookResult.Continue;
    }

    private void OnProgressCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player?.AuthorizedSteamID is null)
            return;

        OpenBattlePassMenu(player);
    }

    private void OnTasksCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player?.AuthorizedSteamID is null)
            return;

        OpenTaskMenu(player);
    }

    private void OpenBattlePassMenu(CCSPlayerController player)
    {
        EnsureMenuApi();
        if (_menuApi is not null)
        {
            OpenMenuManagerBattlePass(player);
            return;
        }

        var progress = GetProgress(player.AuthorizedSteamID!.SteamId64);
        var menu = new ChatMenu("Battle Pass");
        menu.ExitButton = true;
        menu.AddMenuOption($"Рівень: {progress.Level} | XP: {progress.Exp}/{GetRequiredExperience(progress.Level)} | Stars: {progress.Stars}", (_, _) => { }, true);
        menu.AddMenuOption("Переглянути завдання", (selectedPlayer, _) => OpenTaskMenu(selectedPlayer));
        menu.AddMenuOption("Магазин", (selectedPlayer, _) => OpenShopMenu(selectedPlayer));
        menu.Open(player);
    }

    private void OpenMenuManagerBattlePass(CCSPlayerController player)
    {
        if (_menuApi is null || player.AuthorizedSteamID is null)
            return;

        var progress = GetProgress(player.AuthorizedSteamID.SteamId64);
        var menu = CreateMenu(player, "BattlePass");
        if (menu is null)
        {
            OpenBattlePassMenu(player);
            return;
        }
        menu.AddMenuOption($"Рівень: {progress.Level} | XP: {progress.Exp}/{GetRequiredExperience(progress.Level)} | Stars: {progress.Stars}", (_, _) => { }, true);
        menu.AddMenuOption("Мої квести", (selectedPlayer, _) => OpenTaskMenu(selectedPlayer));
        menu.AddMenuOption("Прогрес", (selectedPlayer, _) => OpenProgressMenu(selectedPlayer));
        menu.AddMenuOption("Забрати нагороди", (selectedPlayer, _) => OpenShopMenu(selectedPlayer));
        menu.Open(player);
    }

    private IMenu? CreateMenu(CCSPlayerController player, string title)
    {
        if (_menuApi is null)
            return null;

        return Config.MenuType switch
        {
            -1 => _menuApi.GetMenu(title),
            0 => _menuApi.GetMenuForcetype(title, MenuManager.MenuType.ChatMenu),
            1 => _menuApi.GetMenuForcetype(title, MenuManager.MenuType.ConsoleMenu),
            2 => _menuApi.GetMenuForcetype(title, MenuManager.MenuType.CenterMenu),
            3 => _menuApi.GetMenuForcetype(title, MenuManager.MenuType.ButtonMenu),
            4 => _menuApi.GetMenuForcetype(title, MenuManager.MenuType.MetamodMenu),
            _ => _menuApi.GetMenu(title)
        };
    }

    private void OpenProgressMenu(CCSPlayerController player)
    {
        var progress = GetProgress(player.AuthorizedSteamID!.SteamId64);
        player.PrintToChat($" \x04[Battle Pass]\x01 Рівень: \x0C{progress.Level}\x01 | XP: \x0C{progress.Exp}/{GetRequiredExperience(progress.Level)}\x01 | Stars: \x0C{progress.Stars}");
    }

    private void OpenShopMenu(CCSPlayerController player)
    {
        if (player.AuthorizedSteamID is null)
            return;

        EnsureMenuApi();
        var progress = GetProgress(player.AuthorizedSteamID.SteamId64);
        if (_menuApi is null)
        {
            var fallback = new ChatMenu("Магазин Battle Pass") { ExitButton = true };
            fallback.AddMenuOption($"Ваш баланс: {progress.Stars} Stars", (_, _) => { }, true);
            foreach (var item in Config.Shop.Where(item => item.Enabled))
            {
                var selectedItem = item;
                fallback.AddMenuOption($"{item.Title} - {item.Price} Stars", (selectedPlayer, _) => PurchaseShopItem(selectedPlayer, selectedItem));
            }
            fallback.Open(player);
            return;
        }

        var menu = CreateMenu(player, "Магазин Battle Pass");
        if (menu is null)
            return;
        menu.ExitButton = true;
        menu.AddMenuOption($"Ваш баланс: {progress.Stars} Stars", (_, _) => { }, true);

        foreach (var configuredItem in Config.Shop.Where(item => item.Enabled))
        {
            var item = configuredItem;
            menu.AddMenuOption($"{item.Title} - {item.Price} Stars", (selectedPlayer, _) => PurchaseShopItem(selectedPlayer, item));
        }

        if (!Config.Shop.Any(item => item.Enabled))
            menu.AddMenuOption("Магазин порожній", (_, _) => { }, true);

        menu.Open(player);
    }

    private void PurchaseShopItem(CCSPlayerController player, BattlePassShopItem item)
    {
        if (player.AuthorizedSteamID is null)
            return;

        var progress = GetProgress(player.AuthorizedSteamID.SteamId64);
        if (!item.Enabled || item.Price < 0 || item.RewardExp < 0)
            return;

        if (progress.Stars < item.Price)
        {
            player.PrintToChat($" \x04[Battle Pass]\x01 Недостатньо Stars. Потрібно: {item.Price}, у вас: {progress.Stars}.");
            return;
        }

        progress.Stars -= item.Price;
        AwardExperience(player, item.RewardExp, $"за покупку «{item.Title}»");
        player.PrintToChat($" \x04[Battle Pass]\x01 Придбано: \x0C{item.Title}\x01. Залишок: \x0C{progress.Stars} Stars.");
        OpenShopMenu(player);
    }

    private void OpenTaskMenu(CCSPlayerController player)
    {
        EnsureMenuApi();
        var progress = GetProgress(player.AuthorizedSteamID!.SteamId64);
        if (_menuApi is null)
        {
            var fallback = new ChatMenu("Завдання Battle Pass") { ExitButton = true };
            foreach (var task in GetActiveTasks())
            {
                var value = CurrentTaskCount(progress, task);
                var status = value >= task.Target ? "виконано" : $"{value}/{task.Target}";
                fallback.AddMenuOption($"[{task.Period}] {task.Title} | {status} | +{task.RewardExp} XP / +{task.RewardStars} Stars", (_, _) => { }, value >= task.Target);
            }
            fallback.Open(player);
            return;
        }

        var menu = CreateMenu(player, "Завдання Battle Pass");
        if (menu is null)
            return;
        menu.ExitButton = true;

        foreach (var task in GetActiveTasks())
        {
            var currentTask = task;
            var value = CurrentTaskCount(progress, currentTask);
            var status = value >= currentTask.Target ? "виконано" : $"{value}/{currentTask.Target}";
            menu.AddMenuOption($"[{currentTask.Period}] {currentTask.Title} | {status} | +{currentTask.RewardExp} XP / +{currentTask.RewardStars} Stars", (_, _) => { }, value >= currentTask.Target);
        }

        menu.Open(player);
    }

    private void EnsureMenuApi()
    {
        if (_menuApi is not null)
            return;

        try
        {
            _menuApi = _menuApiCapability.Get();
        }
        catch (KeyNotFoundException)
        {
            _menuApi = null;
        }
    }

    private void AdvanceTasks(CCSPlayerController player, BattlePassTaskType type, string? weapon = null, int amount = 1)
    {
        if (player.AuthorizedSteamID is null)
            return;

        var progress = GetProgress(player.AuthorizedSteamID.SteamId64);
        foreach (var task in GetActiveTasks().Where(task => task.Type == type && (string.IsNullOrWhiteSpace(task.Weapon) || task.Weapon.Equals(weapon, StringComparison.OrdinalIgnoreCase))))
        {
            var periodKey = GetPeriodKey(task.Period);
            var current = CurrentTaskCount(progress, task);
            progress.TaskPeriods[task.Id] = periodKey;
            progress.Tasks[task.Id] = current;
            if (current >= task.Target)
                continue;

            current = Math.Min(task.Target, current + Math.Max(1, amount));
            progress.Tasks[task.Id] = current;
            if (current == task.Target)
            {
                progress.Stars += task.RewardStars;
                AwardExperience(player, task.RewardExp, $"за завдання «{task.Title}»");
                player.PrintToChat($" \x04[Battle Pass]\x01 Завдання виконано: \x0C{task.Title}\x01 (+{task.RewardStars} Stars)");
            }
        }
    }

    private static string GetPeriodKey(BattlePassTaskPeriod period) => BattlePassPeriods.Key(period, DateTime.UtcNow);

    private static int CurrentTaskCount(PlayerProgress progress, BattlePassTask task) =>
        BattlePassPeriods.CurrentCount(progress, task, DateTime.UtcNow);

    private static bool IsNoZoomWeapon(string weapon) => weapon.Contains("awp", StringComparison.OrdinalIgnoreCase) || weapon.Contains("ssg08", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<BattlePassTask> GetActiveTasks()
    {
        var enabledTasks = Config.Tasks.Where(task => task.Enabled);
        if (!Config.Rotation.Enabled)
            return enabledTasks;

        return enabledTasks
            .GroupBy(task => task.Period)
            .SelectMany(group => group
                .OrderBy(task => StableScore($"{GetPeriodKey(group.Key)}:{task.Id}"))
                .Take(GetRotationCount(group.Key)));
    }

    private int GetRotationCount(BattlePassTaskPeriod period) => period switch
    {
        BattlePassTaskPeriod.Daily => Math.Max(1, Config.Rotation.DailyTasks),
        BattlePassTaskPeriod.Weekly => Math.Max(1, Config.Rotation.WeeklyTasks),
        BattlePassTaskPeriod.Monthly => Math.Max(1, Config.Rotation.MonthlyTasks),
        _ => Math.Max(1, Config.Rotation.GlobalTasks)
    };

    private static int StableScore(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in value)
                hash = hash * 31 + character;
            return hash & int.MaxValue;
        }
    }

    private void AwardExperience(CCSPlayerController player, int experience, string reason, bool notify = true)
    {
        if (experience <= 0 || player.AuthorizedSteamID is null)
            return;

        var progress = GetProgress(player.AuthorizedSteamID.SteamId64);
        progress.Exp += experience;

        if (notify)
            player.PrintToChat($" \x04[Battle Pass]\x01 +\x0C{experience}\x01 XP {reason}");

        while (progress.Exp >= GetRequiredExperience(progress.Level))
        {
            progress.Exp -= GetRequiredExperience(progress.Level);
            progress.Level++;
            player.PrintToChat($" \x04[Battle Pass]\x01 Ви досягли рівня \x0C{progress.Level}\x01!");
        }
    }

    private PlayerProgress GetProgress(ulong steamId)
    {
        if (!_progress.TryGetValue(steamId, out var progress))
        {
            progress = new PlayerProgress();
            _progress[steamId] = progress;
        }

        _dirtyPlayers.Add(steamId);

        return progress;
    }

    private int GetRequiredExperience(int level) => Math.Max(1, Config.LevelSystem.BaseExp + ((level - 1) * Config.LevelSystem.ExpMultiplier));

    private void LoadProgress()
    {
        if (!File.Exists(_progressPath))
            return;

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, PlayerProgress>>(File.ReadAllText(_progressPath));
            if (stored is null)
                return;

            foreach (var entry in stored)
                if (ulong.TryParse(entry.Key, out var steamId))
                    _progress[steamId] = entry.Value;
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Could not load Battle Pass progress");
        }
    }

    private void SaveProgress()
    {
        try
        {
            var stored = _progress.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value);
            var temp = _progressPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _progressPath, true);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Could not save Battle Pass progress");
        }
    }

    private void EnsureConfigFile(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath) || File.Exists(configPath))
            return;

        try
        {
            var directory = Path.GetDirectoryName(configPath);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            Directory.CreateDirectory(directory);
            File.WriteAllText(configPath, JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true }));
            Logger.LogInformation("Created default Battle Pass config at {ConfigPath}", configPath);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Could not create Battle Pass config at {ConfigPath}", configPath);
        }
    }
}

public static class BattlePassPeriods
{
    public static string Key(BattlePassTaskPeriod period, DateTime utcNow) => period switch
    {
        BattlePassTaskPeriod.Daily => utcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        BattlePassTaskPeriod.Weekly => $"{System.Globalization.ISOWeek.GetYear(utcNow)}-W{System.Globalization.ISOWeek.GetWeekOfYear(utcNow):00}",
        BattlePassTaskPeriod.Monthly => utcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
        _ => "global"
    };

    public static int CurrentCount(PlayerProgress progress, BattlePassTask task, DateTime utcNow) =>
        progress.TaskPeriods.TryGetValue(task.Id, out var period) && period == Key(task.Period, utcNow)
            ? progress.Tasks.GetValueOrDefault(task.Id)
            : 0;
}

public class PlayerProgress
{
    public int Level { get; set; } = 1;
    public int Exp { get; set; }
    public int Stars { get; set; }
    public Dictionary<string, int> Tasks { get; set; } = new();
    public Dictionary<string, string> TaskPeriods { get; set; } = new();
}

public class BattlePassConfig : BasePluginConfig
{
    public override int Version { get; set; } = 1;
    public int MenuType { get; set; } = -1;
    public BattlePassDatabaseConfig Database { get; set; } = new();
    public BattlePassRotationConfig Rotation { get; set; } = new();
    public BattlePassSettings Settings { get; set; } = new();
    public LevelSystemConfig LevelSystem { get; set; } = new();
    public List<BattlePassShopItem> Shop { get; set; } = new()
    {
        new() { Id = "xp_100", Title = "100 XP", Description = "Отримати 100 XP", Price = 10, RewardExp = 100 },
        new() { Id = "xp_500", Title = "500 XP", Description = "Отримати 500 XP", Price = 40, RewardExp = 500 },
        new() { Id = "xp_1500", Title = "1500 XP", Description = "Отримати 1500 XP", Price = 100, RewardExp = 1500 }
    };
    public List<BattlePassTask> Tasks { get; set; } = new()
    {
        new() { Id = "kills_25", Title = "Зробіть 25 вбивств", Type = BattlePassTaskType.Kills, Target = 25, RewardExp = 100 },
        new() { Id = "rounds_10", Title = "Зіграйте 10 раундів", Type = BattlePassTaskType.Rounds, Target = 10, RewardExp = 75 }
    };
}

public class BattlePassRotationConfig
{
    public bool Enabled { get; set; } = true;
    public int DailyTasks { get; set; } = 4;
    public int WeeklyTasks { get; set; } = 4;
    public int MonthlyTasks { get; set; } = 4;
    public int GlobalTasks { get; set; } = 4;
}

public enum BattlePassTaskType
{
    Kills,
    Rounds,
    Shots,
    WeaponShots,
    Jumps,
    Steps,
    NoZoomShots
}

public enum BattlePassTaskPeriod
{
    Daily,
    Weekly,
    Monthly,
    Global
}

public class BattlePassTask
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    [JsonConverter(typeof(BattlePassTaskTypeConverter))]
    public BattlePassTaskType Type { get; set; }
    [JsonConverter(typeof(BattlePassTaskPeriodConverter))]
    public BattlePassTaskPeriod Period { get; set; } = BattlePassTaskPeriod.Global;
    public string Weapon { get; set; } = string.Empty;
    public int Target { get; set; } = 1;
    public int RewardExp { get; set; } = 25;
    public int RewardStars { get; set; } = 1;
    public bool Enabled { get; set; } = true;
}

public sealed class BattlePassTaskPeriodConverter : JsonConverter<BattlePassTaskPeriod>
{
    public override BattlePassTaskPeriod Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numericValue) && numericValue is >= 0 and <= 3)
            return (BattlePassTaskPeriod)numericValue;

        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<BattlePassTaskPeriod>(reader.GetString(), true, out var period))
            return period;

        throw new JsonException("BattlePass task Period must be Daily, Weekly, Monthly, Global, 0, 1, 2, or 3.");
    }

    public override void Write(Utf8JsonWriter writer, BattlePassTaskPeriod value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

public sealed class BattlePassTaskTypeConverter : JsonConverter<BattlePassTaskType>
{
    public override BattlePassTaskType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numericValue))
            return numericValue switch
            {
                0 => BattlePassTaskType.Kills,
                1 => BattlePassTaskType.Rounds,
                2 => BattlePassTaskType.Shots,
                3 => BattlePassTaskType.WeaponShots,
                4 => BattlePassTaskType.Jumps,
                5 => BattlePassTaskType.Steps,
                6 => BattlePassTaskType.NoZoomShots,
                _ => throw new JsonException($"Unknown BattlePass task type value: {numericValue}")
            };

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.Equals(value, "Kills", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Kill", StringComparison.OrdinalIgnoreCase))
                return BattlePassTaskType.Kills;
            if (string.Equals(value, "Rounds", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Round", StringComparison.OrdinalIgnoreCase))
                return BattlePassTaskType.Rounds;
            if (string.Equals(value, "Shots", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Shot", StringComparison.OrdinalIgnoreCase))
                return BattlePassTaskType.Shots;
            if (string.Equals(value, "WeaponShots", StringComparison.OrdinalIgnoreCase))
                return BattlePassTaskType.WeaponShots;
            if (string.Equals(value, "Jumps", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Jump", StringComparison.OrdinalIgnoreCase))
                return BattlePassTaskType.Jumps;
            if (string.Equals(value, "Steps", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Step", StringComparison.OrdinalIgnoreCase))
                return BattlePassTaskType.Steps;
            if (string.Equals(value, "NoZoomShots", StringComparison.OrdinalIgnoreCase))
                return BattlePassTaskType.NoZoomShots;
        }

        throw new JsonException("BattlePass task Type must be Kills, Rounds, 0, or 1.");
    }

    public override void Write(Utf8JsonWriter writer, BattlePassTaskType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

public class BattlePassShopItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Price { get; set; }
    public int RewardExp { get; set; }
    public bool Enabled { get; set; } = true;
}

public class BattlePassSettings
{
    public int ExpPerKill { get; set; } = 10;
    public int ExpPerRound { get; set; } = 5;
    public int SaveIntervalSeconds { get; set; } = 60;
}

public class BattlePassDatabaseConfig
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string User { get; set; } = "root";
    public string Password { get; set; } = "";
    public string Name { get; set; } = "cs2plugins";
    public bool ResetEveryThreeMonths { get; set; } = true;
}

public class LevelSystemConfig
{
    public int BaseExp { get; set; } = 100;
    public int ExpMultiplier { get; set; } = 20;
}
