namespace CombatSolver;

/// <summary>
/// E：跨回合估值的外部回调。为空（默认）时搜索行为完全不变。
/// 在 <c>CombatBeamSolver.Snapshot</c> 计算完启发式分数之后、生成快照之前调用；打赢和死亡这类
/// 确定的结局不经过它（仍由规则判定），只有未结束的节点会被评估。
/// 实现必须线程安全（搜索可能并行扩展），并且对同一输入给出同一输出（否则破坏可复现性）。
/// 回调拿到的是搜索里的模拟状态，含抽牌顺序和 RNG：只用可观测特征训练的网络，
/// 特征提取也必须只读可观测部分。
/// </summary>
internal interface ISearchValueEvaluator
{
    /// 返回用于束内排序的新分数；原样返回 <paramref name="heuristicScore"/> 即不改变任何行为。
    double Evaluate(in ExternalValueQuery query, double heuristicScore);
}

internal readonly record struct ExternalValueQuery(
    SimulatedCombatState Combat,
    MegaCrit.Sts2.Core.Entities.Players.Player Player,
    int Turn,
    int ActionCount,
    SearchBoundaryReason Boundary,
    int ProjectedPlayerHp,
    // 这次评估的节点是否刚跨过回合（重放的动作里含结束回合、并已推进到下一回合）。
    // 回合边界节点是求解器必须"猜之后会怎样"的地方，最适合交给网络估值。
    bool TurnBoundary = false);
