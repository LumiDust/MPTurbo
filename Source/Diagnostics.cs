using System.Text;
using HarmonyLib;
using RimWorld;
using TurboTick.Simulation;
using Verse;
using Verse.AI;

namespace TurboTick;

/// <summary>
/// Human-readable snapshot of what the mod believes is true. Every line comes from state this mod owns,
/// so a player can report a precise failure instead of "it doesn't work".
/// </summary>
public static class Diagnostics
{
    public static string Build()
    {
        var policy = TurboState.Authority.Current;
        var session = SharedColony.Session;
        var text = new StringBuilder();
        text.AppendLine("版本 " + (typeof(Diagnostics).Assembly.GetName().Version?.ToString(3) ?? "?"));
        text.AppendLine("补丁 " + string.Join(" ", new[]
        {
            Patched(typeof(DesignationManager), nameof(DesignationManager.AddDesignation), "标记"),
            Patched(typeof(GenConstruct), nameof(GenConstruct.PlaceBlueprintForBuild), "建造"),
            Patched(typeof(Pawn_DraftController), "set_Drafted", "征召"),
            Patched(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob), "指令"),
            Patched(typeof(TickManager), nameof(TickManager.TickManagerUpdate), "锁步"),
            Patched(typeof(TickManager), nameof(TickManager.DoSingleTick), "应用")
        }));
        text.AppendLine("房间 " + RoomConnection.Status);
        text.AppendLine("模式 " + (TurboState.Authority.SessionRequested
            ? (TurboState.Authority.SessionConnected ? "联机" : "联机（等待中）")
            : "单机"));
        text.AppendLine($"倍率 ×{TurboState.EffectiveMultiplier:0.0}（开启={policy.Enabled} 上限={policy.MaxRate:0} 修订={policy.Revision}）");
        text.AppendLine($"会话 {session.Phase} epoch={session.Epoch} 服务器tick={session.Barrier.ServerTick} 本地tick={session.Barrier.AppliedTick} 待执行={session.Commands.Count}");
        text.AppendLine($"TPS 实测 {TurboState.MeasuredTps:0} / 目标 {TurboState.TargetTps:0}{(TurboState.Behind ? "（偏低）" : "")}");
        if (TurboState.Authority.SessionConnected)
            text.AppendLine("检查点所有者 " + (RoomConnection.IsCheckpointOwner ? "是" : "否"));
        if (session.Phase == SharedPhase.Faulted) text.AppendLine("错误 " + session.LastError);
        return text.ToString().TrimEnd();
    }

    private static string Patched(Type type, string method, string label)
    {
        var target = AccessTools.Method(type, method);
        if (target == null) return label + "缺失";
        var info = Harmony.GetPatchInfo(target);
        return info != null && info.Owners.Contains("yg.turbotick") ? label + "✓" : label + "✗";
    }
}
