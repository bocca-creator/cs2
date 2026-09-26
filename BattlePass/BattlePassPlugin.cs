using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;

namespace BattlePass;

public sealed class BattlePassPlugin : BasePlugin
{
    public override string ModuleName => "BattlePass";
    public override string ModuleVersion => "0.1.0";

    public override void Load(bool hotReload)
    {
        AddCommand("css_bping", "Check if BattlePass is loaded", OnPing);
    }

    private static void OnPing(CCSPlayerController? player, CommandInfo info)
    {
        info.ReplyToCommand("[BP] Plugin loaded.");
    }
}
