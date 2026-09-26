using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Utils;
using MenuManager;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using CssMenuManager = CounterStrikeSharp.API.Modules.Menu.MenuManager;

namespace Duels;

[MinimumApiVersion(372)]
public class DuelsPlugin : BasePlugin, IPluginConfig<DuelsConfig>
{
    private readonly PluginCapability<IMenuApi> _menuApiCapability = new("menu:nfcore");
    private IMenuApi? _menuApi;
    private readonly List<DuelQueueEntry> _queue = new();
    private readonly Dictionary<ulong, Duel> _activeDuels = new();
    private readonly Dictionary<ulong, Duel> _pendingDuels = new();
    private readonly Dictionary<ulong, PlayerDuelStats> _stats = new();
    private MySqlConnectionStringBuilder? _database;
    private string _resetMarkerPath = string.Empty;
    private bool _lastPlayersDuelOffered;
    private string _statsPath = string.Empty;

    public DuelsConfig Config { get; set; } = new();
    public override string ModuleName => "Duels";
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
        _statsPath = Path.Combine(configDirectory, "duels_stats.json");
        _resetMarkerPath = Path.Combine(configDirectory, "quarterly_reset.txt");
        LoadStats();
        InitializeDatabase();
        ApplyQuarterlyReset();
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        AddCommand("css_duel", "Choose a duel mode and join the queue", OnDuelCommand);
        AddCommand("css_leaveduel", "Leave the duel queue", OnLeaveDuelCommand);
        AddCommand("css_duel_accept", "Accept a duel challenge", OnAcceptDuelCommand);
        AddCommand("css_duel_decline", "Decline a duel challenge", OnDeclineDuelCommand);
        AddCommand("css_duelstats", "Show duel statistics", OnStatsCommand);
        AddCommand("css_queue", "Show duel queue size", OnQueueCommand);
        AddCommand("css_duels_reset", "Reset Duels data (root only)", OnResetCommand);
    }

    public override void Unload(bool hotReload) => SaveStats();

    public void OnConfigParsed(DuelsConfig config) => Config = config;

    private void OnResetCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !AdminManager.PlayerHasPermissions(player, "@css/root"))
            return;

        if (!string.Equals(command.ArgString.Trim(), "confirm", StringComparison.OrdinalIgnoreCase))
        {
            player.PrintToChat(" \x04[Duels]\x01 Для очищення введіть: !duels_reset confirm");
            return;
        }

        ResetAllData();
        player.PrintToChat(" \x04[Duels]\x01 Статистику повністю очищено.");
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
        _stats.Clear();
        if (File.Exists(_statsPath))
            File.Delete(_statsPath);

        if (_database is not null)
        {
            try
            {
                using var connection = new MySqlConnection(_database.ConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM duels_player_stats";
                command.ExecuteNonQuery();
            }
            catch (Exception exception)
            {
                Logger.LogError(exception, "Could not reset Duels MySQL data.");
            }
        }

        File.WriteAllText(_resetMarkerPath, DateTime.UtcNow.ToString("O"));
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
            schemaCommand.CommandText = "CREATE TABLE IF NOT EXISTS duels_player_stats (steam_id BIGINT UNSIGNED PRIMARY KEY, wins INT NOT NULL DEFAULT 0, losses INT NOT NULL DEFAULT 0, rating INT NOT NULL DEFAULT 1000, updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP)";
            schemaCommand.ExecuteNonQuery();
            LoadStatsFromDatabase(schemaConnection);
            Logger.LogInformation("Duels MySQL persistence enabled.");
        }
        catch (Exception exception)
        {
            _database = null;
            Logger.LogError(exception, "Could not initialize Duels MySQL persistence; using JSON.");
        }
    }

    private void LoadStatsFromDatabase(MySqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT steam_id, wins, losses, rating FROM duels_player_stats";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var stats = new PlayerDuelStats
            {
                SteamId = reader.GetUInt64(0),
                Wins = reader.GetInt32(1),
                Losses = reader.GetInt32(2),
                Rating = reader.GetInt32(3)
            };
            _stats[stats.SteamId] = stats;
        }
    }

    private void SaveStatsToDatabase(PlayerDuelStats stats)
    {
        if (_database is null)
            return;

        try
        {
            using var connection = new MySqlConnection(_database.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO duels_player_stats (steam_id, wins, losses, rating) VALUES (@steam_id, @wins, @losses, @rating) ON DUPLICATE KEY UPDATE wins = @wins, losses = @losses, rating = @rating";
            command.Parameters.AddWithValue("@steam_id", stats.SteamId);
            command.Parameters.AddWithValue("@wins", stats.Wins);
            command.Parameters.AddWithValue("@losses", stats.Losses);
            command.Parameters.AddWithValue("@rating", stats.Rating);
            command.ExecuteNonQuery();
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Could not save Duel statistics to MySQL.");
        }
    }

    private void OnLeaveDuelCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player?.AuthorizedSteamID is null)
            return;

        if (_queue.RemoveAll(entry => entry.SteamId == player.AuthorizedSteamID.SteamId64) > 0)
            player.PrintToChat(" \x04[Duels]\x01 Ви вийшли з черги.");
        else
            player.PrintToChat(" \x04[Duels]\x01 Вас немає у черзі.");
    }

    private void OnDuelCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player?.AuthorizedSteamID is null || !player.PawnIsAlive)
            return;

        var steamId = player.AuthorizedSteamID.SteamId64;
        if (_queue.Any(entry => entry.SteamId == steamId) ||
            _pendingDuels.ContainsKey(steamId) || _activeDuels.ContainsKey(steamId))
        {
            player.PrintToChat(" \x04[Duels]\x01 Ви вже у черзі або в дуелі.");
            return;
        }

        OpenModeMenu(player);
    }

    private void OnAcceptDuelCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player?.AuthorizedSteamID is null || !_pendingDuels.TryGetValue(player.AuthorizedSteamID.SteamId64, out var duel))
            return;

        AcceptDuel(player, duel);
    }

    private void OnDeclineDuelCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player?.AuthorizedSteamID is null || !_pendingDuels.TryGetValue(player.AuthorizedSteamID.SteamId64, out var duel))
            return;

        CancelPendingDuel(duel, player.AuthorizedSteamID.SteamId64 == duel.FirstSteamId ? duel.SecondSteamId : duel.FirstSteamId);
    }

    private void OnStatsCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player?.AuthorizedSteamID is null)
            return;

        var stats = GetStats(player.AuthorizedSteamID.SteamId64);
        var total = stats.Wins + stats.Losses;
        var rate = total == 0 ? 0 : (double)stats.Wins / total * 100;
        player.PrintToChat($" \x04[Duels]\x01 Перемоги: \x0C{stats.Wins}\x01 | Поразки: \x0C{stats.Losses}\x01 | Рейтинг: \x0C{stats.Rating}\x01 | Winrate: \x0C{rate:F1}%");
    }

    private void OnQueueCommand(CCSPlayerController? player, CommandInfo command)
    {
        player?.PrintToChat($" \x04[Duels]\x01 У черзі: \x0C{_queue.Count}\x01 гравців.");
    }

    private void TryStartDuel()
    {
        _queue.RemoveAll(entry => FindPlayer(entry.SteamId) is null);
        while (_queue.Count >= 2)
        {
            var pair = DuelMatchmaker.FindPair(_queue);
            if (pair is null)
                break;

            var (firstIndex, secondIndex) = pair.Value;
            var firstEntry = _queue[firstIndex];
            var secondEntry = _queue[secondIndex];
            _queue.RemoveAt(secondIndex);
            _queue.RemoveAt(firstIndex);
            var firstId = firstEntry.SteamId;
            var secondId = secondEntry.SteamId;

            var first = FindPlayer(firstId);
            var second = FindPlayer(secondId);
            if (first?.AuthorizedSteamID is null || second?.AuthorizedSteamID is null)
                continue;

            var duel = new Duel(firstId, secondId, firstEntry.Mode);
            _pendingDuels[firstId] = duel;
            _pendingDuels[secondId] = duel;
            OpenChallengeMenu(first, duel, second.PlayerName);
            OpenChallengeMenu(second, duel, first.PlayerName);
            AddTimer(Config.DuelSettings.AcceptTimeout, () => CancelPendingDuel(duel, 0));
        }
    }

    private void OpenModeMenu(CCSPlayerController player)
    {
        if (_menuApi is not null)
        {
            OpenMenuManagerModeMenu(player);
            return;
        }

        var menu = new ChatMenu("Оберіть режим дуелі");
        foreach (var mode in Config.Modes)
        {
            var selectedMode = mode;
            menu.AddMenuOption(FormatModeLabel(selectedMode), (selectedPlayer, _) =>
            {
                CssMenuManager.CloseActiveMenu(selectedPlayer);
                QueuePlayer(selectedPlayer, selectedMode.Name);
            });
        }

        menu.Open(player);
    }

    private void OpenMenuManagerModeMenu(CCSPlayerController player)
    {
        if (_menuApi is null)
            return;

        var menu = CreateMenu(player, "Оберіть режим дуелі");
        if (menu is null)
            return;
        foreach (var configuredMode in Config.Modes)
        {
            var mode = configuredMode;
            menu.AddMenuOption(FormatModeLabel(mode), (selectedPlayer, _) =>
            {
                _menuApi.CloseMenu(selectedPlayer);
                QueuePlayer(selectedPlayer, mode.Name);
            });
        }

        menu.Open(player);
    }

    private static string FormatModeLabel(DuelMode mode)
    {
        var rules = new List<string>();
        if (!string.IsNullOrWhiteSpace(mode.Weapon))
            rules.Add(mode.Weapon.Replace("weapon_", "", StringComparison.OrdinalIgnoreCase));
        if (mode.NoZoom)
            rules.Add("nozoom");
        if (mode.OneBullet)
            rules.Add("1 ammo");
        if (mode.TurnBased)
            rules.Add("по черзі");

        return $"{mode.Name} ({mode.Duration:0} сек{(rules.Count == 0 ? string.Empty : $" | {string.Join(", ", rules)}")})";
    }

    private void QueuePlayer(CCSPlayerController player, string modeName)
    {
        if (player.AuthorizedSteamID is null || !player.PawnIsAlive ||
            _queue.Any(entry => entry.SteamId == player.AuthorizedSteamID.SteamId64) ||
            _pendingDuels.ContainsKey(player.AuthorizedSteamID.SteamId64) ||
            _activeDuels.ContainsKey(player.AuthorizedSteamID.SteamId64))
            return;

        var mode = Config.Modes.FirstOrDefault(item => item.Name.Equals(modeName, StringComparison.OrdinalIgnoreCase));
        if (mode is null)
            return;

        _queue.Add(new DuelQueueEntry(player.AuthorizedSteamID.SteamId64, mode.Name));
        player.PrintToChat($" \x04[Duels]\x01 Ви у черзі режиму \x0C{mode.Name}\x01.");
        TryStartDuel();
    }

    private void OpenChallengeMenu(CCSPlayerController player, Duel duel, string opponentName)
    {
        if (_menuApi is not null)
        {
            OpenMenuManagerChallengeMenu(player, duel, opponentName);
            return;
        }

        var menu = new ChatMenu($"Дуель: {duel.Mode}");
        menu.AddMenuOption($"Прийняти проти {opponentName}", (selectedPlayer, _) =>
        {
            if (selectedPlayer.AuthorizedSteamID is not null && _pendingDuels.TryGetValue(selectedPlayer.AuthorizedSteamID.SteamId64, out var pending) && ReferenceEquals(pending, duel))
                AcceptDuel(selectedPlayer, duel);
        });
        menu.AddMenuOption("Відхилити", (selectedPlayer, _) =>
        {
            if (selectedPlayer.AuthorizedSteamID is not null && _pendingDuels.TryGetValue(selectedPlayer.AuthorizedSteamID.SteamId64, out var pending) && ReferenceEquals(pending, duel))
                CancelPendingDuel(duel, selectedPlayer.AuthorizedSteamID.SteamId64);
        });
        menu.Open(player);
    }

    private void OpenMenuManagerChallengeMenu(CCSPlayerController player, Duel duel, string opponentName)
    {
        if (_menuApi is null)
            return;

        var menu = CreateMenu(player, $"Дуель: {duel.Mode}");
        if (menu is null)
            return;
        menu.AddMenuOption($"Прийняти проти {opponentName}", (selectedPlayer, _) =>
        {
            if (selectedPlayer.AuthorizedSteamID is not null && _pendingDuels.TryGetValue(selectedPlayer.AuthorizedSteamID.SteamId64, out var pending) && ReferenceEquals(pending, duel))
                AcceptDuel(selectedPlayer, duel);
        });
        menu.AddMenuOption("Відхилити", (selectedPlayer, _) =>
        {
            if (selectedPlayer.AuthorizedSteamID is not null && _pendingDuels.TryGetValue(selectedPlayer.AuthorizedSteamID.SteamId64, out var pending) && ReferenceEquals(pending, duel))
                CancelPendingDuel(duel, selectedPlayer.AuthorizedSteamID.SteamId64);
        });
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

    private void AcceptDuel(CCSPlayerController player, Duel duel)
    {
        if (player.AuthorizedSteamID is null ||
            !_pendingDuels.TryGetValue(player.AuthorizedSteamID.SteamId64, out var pending) ||
            !ReferenceEquals(pending, duel))
            return;

        if (_menuApi is not null)
            _menuApi.CloseMenu(player);
        else
            CssMenuManager.CloseActiveMenu(player);
        duel.Accepted.Add(player.AuthorizedSteamID.SteamId64);
        player.PrintToChat(" \x04[Duels]\x01 Ви прийняли дуель.");
        if (duel.Accepted.Count == 2)
            StartDuel(duel);
    }

    private void StartDuel(Duel duel)
    {
        _pendingDuels.Remove(duel.FirstSteamId);
        _pendingDuels.Remove(duel.SecondSteamId);
        _activeDuels[duel.FirstSteamId] = duel;
        _activeDuels[duel.SecondSteamId] = duel;
        var first = FindPlayer(duel.FirstSteamId);
        var second = FindPlayer(duel.SecondSteamId);
        if (first is null || second is null)
        {
            EndDuel(duel, null);
            return;
        }

        MoveToArena(first, second);
        ApplyDuelMode(first, second, duel.Mode);
        duel.ActiveTurnSteamId = duel.FirstSteamId;
        first.PrintToChat($" \x04[Duels]\x01 Дуель розпочато: режим \x0C{duel.Mode}");
        second.PrintToChat($" \x04[Duels]\x01 Дуель розпочато: режим \x0C{duel.Mode}");
        AddTimer(Config.Modes.FirstOrDefault(mode => mode.Name.Equals(duel.Mode, StringComparison.OrdinalIgnoreCase))?.Duration ?? Config.DuelSettings.DuelDuration, () => EndDuel(duel, null));
    }

    private void ApplyDuelMode(CCSPlayerController first, CCSPlayerController second, string modeName)
    {
        var mode = Config.Modes.FirstOrDefault(item => item.Name.Equals(modeName, StringComparison.OrdinalIgnoreCase));
        if (mode is null || string.IsNullOrWhiteSpace(mode.Weapon))
            return;

        first.GiveNamedItem(mode.Weapon);
        second.GiveNamedItem(mode.Weapon);
        var details = new List<string> { mode.Weapon };
        if (mode.NoZoom)
            details.Add("nozoom");
        if (mode.OneBullet)
            details.Add("1 ammo");
        if (mode.TurnBased)
            details.Add("по черзі");

        first.PrintToChat($" \x04[Duels]\x01 Налаштування: {string.Join(", ", details)}");
        second.PrintToChat($" \x04[Duels]\x01 Налаштування: {string.Join(", ", details)}");
    }

    private void CancelPendingDuel(Duel duel, ulong notifyId)
    {
        if (!_pendingDuels.TryGetValue(duel.FirstSteamId, out var first) || !ReferenceEquals(first, duel) ||
            !_pendingDuels.TryGetValue(duel.SecondSteamId, out var second) || !ReferenceEquals(second, duel))
            return;

        _pendingDuels.Remove(duel.FirstSteamId);
        _pendingDuels.Remove(duel.SecondSteamId);
        FindPlayer(duel.FirstSteamId)?.PrintToChat(" \x04[Duels]\x01 Виклик відхилено або час очікування минув.");
        FindPlayer(duel.SecondSteamId)?.PrintToChat(" \x04[Duels]\x01 Виклик відхилено або час очікування минув.");
        TryStartDuel();
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        if (victim?.AuthorizedSteamID is not null && _activeDuels.TryGetValue(victim.AuthorizedSteamID.SteamId64, out var duel))
        {
            var attackerId = @event.Attacker?.AuthorizedSteamID?.SteamId64;
            var winnerId = attackerId.HasValue && (attackerId == duel.FirstSteamId || attackerId == duel.SecondSteamId) && attackerId != victim.AuthorizedSteamID.SteamId64
                ? attackerId
                : duel.Other(victim.AuthorizedSteamID.SteamId64);

            EndDuel(duel, winnerId);
            return HookResult.Continue;
        }

        AddTimer(0.1f, TryOfferLastPlayersDuel);
        return HookResult.Continue;
    }

    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player?.AuthorizedSteamID is null || !_activeDuels.TryGetValue(player.AuthorizedSteamID.SteamId64, out var duel))
            return HookResult.Continue;

        var mode = Config.Modes.FirstOrDefault(item => item.Name.Equals(duel.Mode, StringComparison.OrdinalIgnoreCase));
        if (mode is null || !mode.TurnBased)
            return HookResult.Continue;

        if (duel.ActiveTurnSteamId != player.AuthorizedSteamID.SteamId64)
            return HookResult.Handled;

        duel.ActiveTurnSteamId = duel.Other(player.AuthorizedSteamID.SteamId64);
        FindPlayer(duel.ActiveTurnSteamId)?.PrintToChat(" \x04[Duels]\x01 Ваш хід.");
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _lastPlayersDuelOffered = false;
        _queue.Clear();
        _pendingDuels.Clear();
        _activeDuels.Clear();
        AddTimer(0.5f, TryOfferLastPlayersDuel);
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        AddTimer(0.5f, TryOfferLastPlayersDuel);
        return HookResult.Continue;
    }

    private void TryOfferLastPlayersDuel()
    {
        if (_lastPlayersDuelOffered || _queue.Count > 0 || _activeDuels.Count > 0 || _pendingDuels.Count > 0)
            return;

        var alivePlayers = Utilities.GetPlayers()
            .Where(player => player is { IsValid: true, PawnIsAlive: true, AuthorizedSteamID: not null })
            .ToList();
        var terrorists = alivePlayers.Where(player => player.TeamNum == 2).ToList();
        var counterTerrorists = alivePlayers.Where(player => player.TeamNum == 3).ToList();
        if (terrorists.Count != 1 || counterTerrorists.Count != 1)
            return;

        var mode = Config.Modes.FirstOrDefault();
        if (mode is null)
            return;

        var first = terrorists[0];
        var second = counterTerrorists[0];
        if (first.AuthorizedSteamID is null || second.AuthorizedSteamID is null)
            return;

        _lastPlayersDuelOffered = true;
        var duel = new Duel(first.AuthorizedSteamID.SteamId64, second.AuthorizedSteamID.SteamId64, mode.Name);
        _pendingDuels[duel.FirstSteamId] = duel;
        _pendingDuels[duel.SecondSteamId] = duel;
        OpenChallengeMenu(first, duel, second.PlayerName);
        OpenChallengeMenu(second, duel, first.PlayerName);
        AddTimer(Config.DuelSettings.AcceptTimeout, () => CancelPendingDuel(duel, 0));
        first.PrintToChat($" \x04[Duels]\x01 Залишилися останні гравці. Режим: \x0C{mode.Name}");
        second.PrintToChat($" \x04[Duels]\x01 Залишилися останні гравці. Режим: \x0C{mode.Name}");
    }

    private void EndDuel(Duel duel, ulong? winnerId)
    {
        if (duel.IsFinished || !_activeDuels.ContainsKey(duel.FirstSteamId))
            return;

        duel.IsFinished = true;
        _activeDuels.Remove(duel.FirstSteamId);
        _activeDuels.Remove(duel.SecondSteamId);

        var first = FindPlayer(duel.FirstSteamId);
        var second = FindPlayer(duel.SecondSteamId);
        if (!winnerId.HasValue)
        {
            first?.PrintToChat(" \x04[Duels]\x01 Час дуелі завершився — нічия.");
            second?.PrintToChat(" \x04[Duels]\x01 Час дуелі завершився — нічия.");
            return;
        }

        var loserId = duel.Other(winnerId.Value);
        var winnerStats = GetStats(winnerId.Value);
        var loserStats = GetStats(loserId);
        winnerStats.Wins++;
        winnerStats.Rating += Config.DuelSettings.WinReward;
        loserStats.Losses++;
        loserStats.Rating = Math.Max(0, loserStats.Rating + Config.DuelSettings.LossReward);
        SaveStatsToDatabase(winnerStats);
        SaveStatsToDatabase(loserStats);
        SaveStats();

        var winner = FindPlayer(winnerId.Value);
        var loser = FindPlayer(loserId);
        winner?.PrintToChat($" \x04[Duels]\x01 Перемога! \x10+\x0C{Config.DuelSettings.WinReward}\x01 рейтингу.");
        loser?.PrintToChat($" \x04[Duels]\x01 Поразка. \x10{Config.DuelSettings.LossReward}\x01 рейтингу.");
    }

    private void MoveToArena(CCSPlayerController first, CCSPlayerController second)
    {
        if (!Config.Arena.EnableTeleport || Config.Arena.TSpawnPoints.Count == 0 || Config.Arena.CTSpawnPoints.Count == 0)
            return;

        var t = Config.Arena.TSpawnPoints[Random.Shared.Next(Config.Arena.TSpawnPoints.Count)].ToVector();
        var ct = Config.Arena.CTSpawnPoints[Random.Shared.Next(Config.Arena.CTSpawnPoints.Count)].ToVector();
        first.PlayerPawn?.Value?.Teleport(t, new QAngle(0, 0, 0), new Vector(0, 0, 0));
        second.PlayerPawn?.Value?.Teleport(ct, new QAngle(0, 0, 0), new Vector(0, 0, 0));
    }

    private CCSPlayerController? FindPlayer(ulong steamId) => Utilities.GetPlayers().FirstOrDefault(player => player is { IsValid: true, AuthorizedSteamID: not null } && player.AuthorizedSteamID.SteamId64 == steamId);

    private PlayerDuelStats GetStats(ulong steamId)
    {
        if (!_stats.TryGetValue(steamId, out var stats))
        {
            stats = new PlayerDuelStats { SteamId = steamId, Rating = Config.DuelSettings.InitialRating };
            _stats[steamId] = stats;
        }

        return stats;
    }

    private void LoadStats()
    {
        if (!File.Exists(_statsPath))
            return;

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, PlayerDuelStats>>(File.ReadAllText(_statsPath));
            if (stored is null)
                return;
            foreach (var entry in stored)
                if (ulong.TryParse(entry.Key, out var steamId))
                    _stats[steamId] = entry.Value;
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Could not load duel statistics");
        }
    }

    private void SaveStats()
    {
        try
        {
            var stored = _stats.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value);
            var temp = _statsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _statsPath, true);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Could not save duel statistics");
        }
    }
}

public static class DuelMatchmaker
{
    public static (int First, int Second)? FindPair(IReadOnlyList<DuelQueueEntry> queue)
    {
        for (var first = 0; first < queue.Count; first++)
            for (var second = first + 1; second < queue.Count; second++)
                if (queue[first].Mode.Equals(queue[second].Mode, StringComparison.OrdinalIgnoreCase))
                    return (first, second);

        return null;
    }
}

public class Duel
{
    public Duel(ulong firstSteamId, ulong secondSteamId, string mode)
    {
        FirstSteamId = firstSteamId;
        SecondSteamId = secondSteamId;
        Mode = mode;
    }

    public ulong FirstSteamId { get; }
    public ulong SecondSteamId { get; }
    public bool IsFinished { get; set; }
    public string Mode { get; }
    public ulong ActiveTurnSteamId { get; set; }
    public HashSet<ulong> Accepted { get; } = new();
    public ulong Other(ulong steamId) => steamId == FirstSteamId ? SecondSteamId : FirstSteamId;
}

public class PlayerDuelStats
{
    public ulong SteamId { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Rating { get; set; } = 1000;
}

public class DuelsConfig : BasePluginConfig
{
    public override int Version { get; set; } = 1;
    public int MenuType { get; set; } = -1;
    public DuelDatabaseConfig Database { get; set; } = new();
    public DuelSettings DuelSettings { get; set; } = new();
    public ArenaConfig Arena { get; set; } = new();
    public List<DuelMode> Modes { get; set; } = new()
    {
        new() { Name = "Classic", Duration = 300 },
        new() { Name = "Quick", Duration = 120 }
    };
}

public class DuelDatabaseConfig
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string User { get; set; } = "root";
    public string Password { get; set; } = "";
    public string Name { get; set; } = "cs2plugins";
    public bool ResetEveryThreeMonths { get; set; } = true;
}

public class DuelQueueEntry
{
    public DuelQueueEntry(ulong steamId, string mode)
    {
        SteamId = steamId;
        Mode = mode;
    }

    public ulong SteamId { get; }
    public string Mode { get; }
}

public class DuelMode
{
    public string Name { get; set; } = "Classic";
    public float Duration { get; set; } = 300;
    public string Weapon { get; set; } = "";
    public bool NoZoom { get; set; }
    public bool OneBullet { get; set; }
    public bool TurnBased { get; set; }
}

public class DuelSettings
{
    public int DuelDuration { get; set; } = 300;
    public int WinReward { get; set; } = 10;
    public int LossReward { get; set; } = -5;
    public int InitialRating { get; set; } = 1000;
    public float AcceptTimeout { get; set; } = 15;
}

public class ArenaConfig
{
    public bool EnableTeleport { get; set; }
    public List<VectorConfig> TSpawnPoints { get; set; } = new();
    public List<VectorConfig> CTSpawnPoints { get; set; } = new();
}

public class VectorConfig
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public Vector ToVector() => new(X, Y, Z);
}
