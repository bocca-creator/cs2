using BattlePass;
using Duels;

var december = new DateTime(2020, 12, 31, 23, 59, 0, DateTimeKind.Utc);
var january = new DateTime(2021, 1, 1, 0, 1, 0, DateTimeKind.Utc);
Assert(BattlePassPeriods.Key(BattlePassTaskPeriod.Weekly, december) == "2020-W53" &&
       BattlePassPeriods.Key(BattlePassTaskPeriod.Weekly, december) ==
       BattlePassPeriods.Key(BattlePassTaskPeriod.Weekly, january), "ISO week spans calendar years");
Assert(BattlePassPeriods.Key(BattlePassTaskPeriod.Weekly, january.AddDays(3)) == "2021-W01",
    "ISO week resets on Monday");

var daily = new BattlePassTask { Id = "daily", Period = BattlePassTaskPeriod.Daily };
var progress = new BattlePass.PlayerProgress
{
    Tasks = new Dictionary<string, int> { ["daily"] = 9 },
    TaskPeriods = new Dictionary<string, string> { ["daily"] = BattlePassPeriods.Key(daily.Period, december) }
};
Assert(BattlePassPeriods.CurrentCount(progress, daily, december) == 9 &&
       BattlePassPeriods.CurrentCount(progress, daily, january) == 0,
    "Expired task count is hidden before the next event");

var queue = new List<DuelQueueEntry>
{
    new(1, "awp"), new(2, "ak"), new(3, "AK"), new(4, "awp")
};
Assert(DuelMatchmaker.FindPair(queue) == (0, 3), "Oldest compatible pair is chosen");
queue.RemoveAt(0);
Assert(DuelMatchmaker.FindPair(queue) == (0, 1), "Matching modes ignore case");
Assert(DuelMatchmaker.FindPair([new(1, "awp"), new(2, "ak")]) is null,
    "Unmatched queue entries remain available");

Console.WriteLine("Plugin logic tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
