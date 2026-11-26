using DevDoctor.Core;

namespace DevDoctor.Diagnostics;

public sealed class HistoryComparer : IHistoryComparer
{
    public HistoryComparison Compare(HistoryRecord before, HistoryRecord after)
    {
        var changes = new List<HistoryDifference>(); var notes = new List<string>();
        if (before.Kind != after.Kind || before.ScopeId != after.ScopeId || before.RuleVersion != after.RuleVersion)
        {
            notes.Add("记录的扫描类型、项目/机器范围或诊断规则版本不同，不能直接判断问题是否解决。");
            foreach (var item in before.Objects.Concat(after.Objects).DistinctBy(o => o.Id))
                changes.Add(new(DifferenceKind.NotComparable, item.Name, null, null, "范围/规则版本不同；未比较。"));
            return new(before, after, changes, notes);
        }
        var oldObjects = before.Objects.ToDictionary(o => o.Id); var newObjects = after.Objects.ToDictionary(o => o.Id);
        foreach (var objectId in oldObjects.Keys.Union(newObjects.Keys))
        {
            oldObjects.TryGetValue(objectId, out var oldObject); newObjects.TryGetValue(objectId, out var newObject);
            var name = newObject?.Name ?? oldObject!.Name;
            if (oldObject is null || newObject is null || oldObject.Coverage != CoverageState.Complete || newObject.Coverage != CoverageState.Complete)
            {
                changes.Add(new(DifferenceKind.NotComparable, name, null, null, oldObject is null || newObject is null ? "该对象仅在一次记录中受检；没有检查不等于已解决。" : "对象包含取消、失败、未知配置或扫描上限，缺少完整比较证据。"));
                continue;
            }
            if (oldObject.Presence == ToolPresence.Missing && newObject.Presence == ToolPresence.Present)
                changes.Add(new(DifferenceKind.ToolAdded, name, null, null, "两次均检查同一对象：此前未发现，本次确认存在。"));
            else if (oldObject.Presence == ToolPresence.Present && newObject.Presence == ToolPresence.Missing)
                changes.Add(new(DifferenceKind.ToolRemoved, name, null, null, "两次均检查同一对象：此前存在，本次限定范围内未发现。"));
            var oldRules = oldObject.Observations.ToDictionary(o => o.Id); var newRules = newObject.Observations.ToDictionary(o => o.Id);
            foreach (var id in oldRules.Keys.Union(newRules.Keys))
            {
                oldRules.TryGetValue(id, out var oldRule); newRules.TryGetValue(id, out var newRule);
                if (oldRule is null || newRule is null || oldRule.RuleId != newRule.RuleId)
                { if (oldRule?.IsProblem == true || newRule?.IsProblem == true) changes.Add(new(DifferenceKind.NotComparable, name, oldRule, newRule, "该规则/声明没有同时出现在两次结果中，未判断解决状态。")); continue; }
                if (!oldRule.IsProblem && !newRule.IsProblem) continue;
                var kind = !oldRule.IsProblem ? DifferenceKind.NewProblem : !newRule.IsProblem ? DifferenceKind.Resolved :
                    oldRule.Status == newRule.Status ? DifferenceKind.Persistent : DifferenceKind.StatusChanged;
                changes.Add(new(kind, name, oldRule, newRule, kind switch {
                    DifferenceKind.NewProblem => "同一稳定规则由明确正常/不适用变为需关注。",
                    DifferenceKind.Resolved => "同一稳定规则本次明确正常/不适用。",
                    DifferenceKind.Persistent => "规则与对象一致；证据措辞变化不改变问题身份。",
                    _ => "同一规则的状态改变；未知状态不等于已解决。" }));
            }
        }
        if (before.Completion != ScanCompletion.Completed || after.Completion != ScanCompletion.Completed) notes.Add("包含取消或失败的扫描；仅比较双方明确完整检查的对象。");
        return new(before, after, changes, notes);
    }
}
