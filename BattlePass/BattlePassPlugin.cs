using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace BattlePass;

public sealed class BattlePassPlugin : BasePlugin, IPluginConfig<BattlePassConfig>
{
    private PassState _state = null!;
    private readonly Dictionary<(ulong Killer, ulong Victim), DateTime> _lastKill = [];

    public override string ModuleName => "BattlePass";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "bocca-creator";
    public override string ModuleDescription => "Seasonal XP, quests and free/premium rewards";
    public BattlePassConfig Config { get; set; } = new();

    public void OnConfigParsed(BattlePassConfig config)
    {
        config.Validate();
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        _state = new PassState(Config, Path.Combine(ModuleDirectory, "data"));
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        AddCommand("css_bp", "Show battle pass progress", OnProgress);
        AddCommand("css_bpquests", "Show daily and weekly quests", OnQuests);
        AddCommand("css_bpclaim", "Claim a battle pass reward", OnClaim);
        AddCommand("css_bppremium", "Set premium status for a SteamID64", OnPremium);
    }

    private static ulong SteamId(CCSPlayerController? player)
    {
        if (player is null || !player.IsValid || player.IsBot) return 0;
        return player.AuthorizedSteamID?.SteamId64 ?? 0;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;
        var killerId = SteamId(attacker);
        var victimId = SteamId(victim);
        if (killerId == 0 || victimId == 0 || killerId == victimId ||
            attacker!.TeamNum == victim!.TeamNum)
            return HookResult.Continue;

        var now = DateTime.UtcNow;
        var pair = (killerId, victimId);
        if (_lastKill.TryGetValue(pair, out var last) &&
            (now - last).TotalSeconds < Config.KillCooldownSeconds)
            return HookResult.Continue;

        try
        {
            var progress = _state.Get(killerId);
            var previousLevel = _state.Level(progress);
            var completed = _state.RecordKill(progress, @event.Headshot, now);
            _lastKill[pair] = now;
            if (_lastKill.Count > 1000)
            {
                foreach (var expired in _lastKill.Where(entry =>
                             (now - entry.Value).TotalSeconds >= Config.KillCooldownSeconds).Select(entry => entry.Key).ToList())
                    _lastKill.Remove(expired);
            }

            foreach (var quest in completed)
                attacker.PrintToChat($"[BP] Завдання виконано: {quest}");
            if (_state.Level(progress) > previousLevel)
                attacker.PrintToChat($"[BP] Рівень {_state.Level(progress)}! Нагорода: !bpclaim <рівень> <free|premium>");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not save battle pass kill progress");
        }
        return HookResult.Continue;
    }

    private bool TryGetPlayer(CCSPlayerController? player, CommandInfo info, out ulong steamId)
    {
        steamId = SteamId(player);
        if (steamId != 0) return true;
        info.ReplyToCommand("[BP] Команда доступна лише авторизованим гравцям.");
        return false;
    }

    private void OnProgress(CCSPlayerController? player, CommandInfo info)
    {
        if (!TryGetPlayer(player, info, out var steamId)) return;
        try
        {
            var progress = _state.Get(steamId);
            info.ReplyToCommand($"[BP] Сезон {Config.SeasonId}: рівень {_state.Level(progress)}/{Config.MaxLevel}, " +
                $"XP {progress.Xp}/{(long)Config.MaxLevel * Config.XpPerLevel}; преміум: {(progress.Premium ? "так" : "ні")}");
            var available = Config.Rewards.Where(reward =>
                reward.Level <= _state.Level(progress) &&
                (!reward.Premium || progress.Premium) &&
                !(reward.Premium ? progress.PremiumClaims : progress.FreeClaims).Contains(reward.Level)).ToList();
            info.ReplyToCommand(available.Count == 0
                ? "[BP] Доступних нагород немає. Завдання: !bpquests"
                : "[BP] Нагороди: " + string.Join(", ", available.Select(r =>
                    $"{r.Level} {(r.Premium ? "premium" : "free")}: {r.Name}")) +
                  ". Отримати: !bpclaim <рівень> <free|premium>");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not read battle pass progress");
            info.ReplyToCommand("[BP] Не вдалося завантажити прогрес.");
        }
    }

    private void OnQuests(CCSPlayerController? player, CommandInfo info)
    {
        if (!TryGetPlayer(player, info, out var steamId)) return;
        try
        {
            var progress = _state.Get(steamId);
            _state.Refresh(progress, DateTime.UtcNow);
            ShowQuests(info, "Щоденні", Config.DailyQuests, progress.Daily);
            ShowQuests(info, "Щотижневі", Config.WeeklyQuests, progress.Weekly);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not read battle pass quests");
            info.ReplyToCommand("[BP] Не вдалося завантажити завдання.");
        }
    }

    private static void ShowQuests(CommandInfo info, string label, List<QuestDefinition> quests, QuestPeriod period)
    {
        foreach (var quest in quests)
            info.ReplyToCommand($"[BP] {label}: {quest.Name} — " +
                $"{Math.Min(period.Counts.GetValueOrDefault(quest.Id), quest.Target)}/{quest.Target} " +
                $"({(period.Completed.Contains(quest.Id) ? "виконано" : $"+{quest.Xp} XP")})");
    }

    private void OnClaim(CCSPlayerController? player, CommandInfo info)
    {
        if (!TryGetPlayer(player, info, out var steamId)) return;
        if (info.ArgCount != 3 ||
            !int.TryParse(info.GetArg(1), out var level) ||
            info.GetArg(2) is not ("free" or "premium"))
        {
            info.ReplyToCommand("[BP] Використання: !bpclaim <рівень> <free|premium>");
            return;
        }

        var premium = info.GetArg(2) == "premium";
        var reward = Config.Rewards.FirstOrDefault(r => r.Level == level && r.Premium == premium);
        if (reward is null)
        {
            info.ReplyToCommand("[BP] Такої нагороди немає.");
            return;
        }
        try
        {
            if (!_state.Claim(_state.Get(steamId), reward))
            {
                info.ReplyToCommand("[BP] Нагорода недоступна або вже отримана.");
                return;
            }
            if (!string.IsNullOrWhiteSpace(reward.Command))
                Server.ExecuteCommand(reward.Command.Replace("{steamid}",
                    steamId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            info.ReplyToCommand($"[BP] Нагороду отримано: {reward.Name}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not claim battle pass reward for {SteamId}", steamId);
            info.ReplyToCommand("[BP] Не вдалося видати нагороду; перевірте журнал сервера.");
        }
    }

    private void OnPremium(CCSPlayerController? player, CommandInfo info)
    {
        if (player is not null && !AdminManager.PlayerHasPermissions(player, "@css/generic"))
        {
            info.ReplyToCommand("[BP] Немає прав адміністратора.");
            return;
        }
        if (info.ArgCount != 3 || !ulong.TryParse(info.GetArg(1), out var steamId) ||
            steamId == 0 || info.GetArg(2) is not ("on" or "off"))
        {
            info.ReplyToCommand("[BP] Використання: css_bppremium <SteamID64> <on|off>");
            return;
        }
        try
        {
            _state.SetPremium(_state.Get(steamId), info.GetArg(2) == "on");
            info.ReplyToCommand($"[BP] Преміум для {steamId}: {info.GetArg(2)}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not update premium status for {SteamId}", steamId);
            info.ReplyToCommand("[BP] Не вдалося зберегти преміум-статус.");
        }
    }
}
