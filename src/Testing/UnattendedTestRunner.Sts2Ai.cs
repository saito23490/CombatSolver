using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace CombatSolver;

/// <summary>
/// STS2AI arena extensions for the unattended test runner.
///
/// Everything the training-data pipeline needs beyond upstream capability
/// lives here: run-player defaults (gold, potion slots), the full
/// pre-combat state echo/dump the Python side reconciles against, extra
/// typed relic state members (string / ModelId / int[]), and the planned
/// native-acquisition card selector. Upstream files only carry the few
/// wiring lines that call into this partial.
/// </summary>
internal sealed partial class UnattendedTestRunner
{
    private static bool _sts2AiTeacherTraceEnabled;
    private static readonly List<JsonObject> Sts2AiTeacherTrace = [];

    private static void Sts2AiResetTeacherTrace(bool enabled)
    {
        _sts2AiTeacherTraceEnabled = enabled;
        Sts2AiTeacherTrace.Clear();
    }

    internal static JsonObject[] Sts2AiCaptureTeacherTrace()
        => _sts2AiTeacherTraceEnabled
            ? Sts2AiTeacherTrace.Select(row => (JsonObject)row.DeepClone()).ToArray()
            : [];

    internal static int Sts2AiBeginTeacherAction(
        CombatState state, Player player, PlanAction action, SolverResult result, int actionIndex)
    {
        if (!_sts2AiTeacherTraceEnabled)
            return -1;
        JsonObject row = new()
        {
            ["schemaVersion"] = 1,
            ["turn"] = action.Turn,
            ["actionIndex"] = actionIndex,
            ["action"] = JsonSerializer.SerializeToNode(new
            {
                kind = action.Kind.ToString(),
                cardId = action.CardId,
                cardOccurrence = action.CardOccurrence,
                cardUpgradeLevel = action.CardUpgradeLevel,
                cardEnchantmentId = action.CardEnchantmentId,
                potionId = action.PotionId,
                potionSlot = action.PotionSlot,
                targetIndex = action.TargetIndex,
                targetCombatId = action.TargetCombatId,
            }, UnattendedTestFiles.JsonOptions),
            ["search"] = JsonSerializer.SerializeToNode(new
            {
                score = result.BestNode.Score,
                boundary = result.BoundaryReason.ToString(),
                selectedExpanded = result.ExpandedNodes,
                totalExpanded = result.TotalExpandedNodes,
                onlyDeathRoutes = result.OnlyDeathRoutesFound,
            }, UnattendedTestFiles.JsonOptions),
            ["before"] = Sts2AiCaptureCombatObservation(state, player),
        };
        Sts2AiTeacherTrace.Add(row);
        return Sts2AiTeacherTrace.Count - 1;
    }

    internal static void Sts2AiCompleteTeacherAction(int index, CombatState state, Player player)
    {
        if (index < 0 || index >= Sts2AiTeacherTrace.Count)
            return;
        Sts2AiTeacherTrace[index]["after"] = Sts2AiCaptureCombatObservation(state, player);
    }

    private static JsonObject Sts2AiCaptureCombatObservation(CombatState state, Player player)
    {
        var pcs = player.PlayerCombatState!;
        object Cards(IEnumerable<CardModel> cards) => cards.Select((card, index) => new
        {
            index,
            id = card.Id.Entry,
            upgrade = card.CurrentUpgradeLevel,
            enchantment = card.Enchantment?.Id.Entry,
            energyCost = card.EnergyCost.GetAmountToSpend(),
            starCost = card.GetStarCostWithModifiers(),
        }).ToArray();
        List<object> legalActions = [];
        Dictionary<string, int> occurrences = new(StringComparer.Ordinal);
        foreach (CardModel card in pcs.Hand.Cards)
        {
            int occurrence = occurrences.GetValueOrDefault(card.Id.Entry);
            occurrences[card.Id.Entry] = occurrence + 1;
            Creature?[] targets = [null, player.Creature, .. state.Enemies];
            HashSet<uint?> emittedTargets = [];
            foreach (Creature? target in targets)
            {
                uint? combatId = target?.CombatId;
                if (!emittedTargets.Add(combatId) || !card.CanPlayTargeting(target))
                    continue;
                legalActions.Add(new
                {
                    kind = PlanActionKind.PlayCard.ToString(),
                    cardId = card.Id.Entry,
                    cardOccurrence = occurrence,
                    cardUpgradeLevel = card.CurrentUpgradeLevel,
                    cardEnchantmentId = card.Enchantment?.Id.Entry,
                    targetCombatId = combatId,
                });
            }
        }
        legalActions.Add(new { kind = PlanActionKind.EndTurn.ToString() });
        return JsonSerializer.SerializeToNode(new
        {
            player = new
            {
                hp = player.Creature.CurrentHp,
                maxHp = player.Creature.MaxHp,
                block = player.Creature.Block,
                energy = pcs.Energy,
                stars = pcs.Stars,
                powers = player.Creature.Powers.Select(power => new
                {
                    id = power.Id.Entry,
                    amount = power.Amount,
                    amountOnTurnStart = power.AmountOnTurnStart,
                }).ToArray(),
            },
            hand = Cards(pcs.Hand.Cards),
            draw = Cards(pcs.DrawPile.Cards),
            discard = Cards(pcs.DiscardPile.Cards),
            exhaust = Cards(pcs.ExhaustPile.Cards),
            potions = player.Potions.Select((potion, slot) => new
            {
                slot,
                id = potion?.Id.Entry,
            }).ToArray(),
            enemies = state.Enemies.Select((enemy, index) => new
            {
                index,
                combatId = enemy.CombatId,
                id = enemy.Monster?.Id.Entry,
                hp = enemy.CurrentHp,
                maxHp = enemy.MaxHp,
                block = enemy.Block,
                powers = enemy.Powers.Select(power => new
                {
                    id = power.Id.Entry,
                    amount = power.Amount,
                    amountOnTurnStart = power.AmountOnTurnStart,
                }).ToArray(),
            }).ToArray(),
            legalActions,
        }, UnattendedTestFiles.JsonOptions)!.AsObject();
    }

    private static async Task Sts2AiApplyRunPlayerDefaults(
        Player runPlayer, UnattendedTestRequest request)
    {
        if (request.InitialGold is { } initialGold)
            runPlayer.Gold = initialGold;
        if (request.InitialMaxPotionCount is { } initialSlots)
            await PlayerCmd.GainMaxPotionCount(initialSlots, runPlayer);
    }

    /// <summary>
    /// Pins a per-request node budget before the encounter is entered, so
    /// the initial automatic search, replans and audits all run under the
    /// same deterministic profile instead of the instance defaults.
    /// </summary>
    private static void Sts2AiApplySearchPolicy(UnattendedTestRequest request)
    {
        if (request.SearchMaxExpandedNodesForTest is not { } maxNodes)
            return;
        SolverSettingsData current = SolverSettings.Current;
        SolverSettings.ApplyForTesting(current with
        {
            PerformancePreset = SolverPerformancePreset.Custom,
            SearchMaxExpandedNodes = Math.Max(100, maxNodes),
            SearchTimeLimitSeconds = 600,
        });
        Entry.Logger.Info(
            $"[CombatSolver/Test] STS2AI_SEARCH_POLICY maxExpandedNodes={Math.Max(100, maxNodes)}");
    }

    private static void Sts2AiEchoInitialState(
        UnattendedTestRunner runner, RunState runState, Player runPlayer)
    {
        runner._completedChecks.Add(
            $"InitialStateEcho:hp={runPlayer.Creature.CurrentHp}/{runPlayer.Creature.MaxHp}"
            + $":gold={runPlayer.Gold}:slots={runPlayer.MaxPotionCount}"
            + $":deck={runPlayer.Deck.Cards.Count}"
            + $":act={runState.CurrentActIndex}:floor={runState.TotalFloor}"
            + $":relics={string.Join(",", runPlayer.Relics.Select(r => r.Id.Entry))}");
    }

    private static void Sts2AiWriteStateDump(RunState runState, Player player)
    {
        var dump = new Dictionary<string, object?>
        {
            ["act_index"] = runState.CurrentActIndex,
            ["floor"] = runState.TotalFloor,
            ["deck"] = player.Deck.Cards.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id.Entry,
                ["upgrade_level"] = c.IsUpgraded ? 1 : 0,
                ["enchantment_id"] = c.Enchantment?.Id.Entry,
                ["enchantment_amount"] = c.Enchantment is { } enchanted
                    ? decimal.ToInt32(enchanted.Amount)
                    : null,
            }).ToList(),
            ["hp"] = player.Creature.CurrentHp,
            ["max_hp"] = player.Creature.MaxHp,
            ["gold"] = player.Gold,
            ["max_potion_slots"] = player.MaxPotionCount,
            ["potions"] = player.Potions.Select(p => p?.Id.Entry).ToList(),
            ["relics"] = player.Relics.Select(r => (object)new Dictionary<string, object?>
            {
                ["id"] = r.Id.Entry,
                ["counter"] = r.DisplayAmount,
                ["saved"] = Sts2AiDumpSavedProperties(r),
            }).ToList(),
        };
        string path = UnattendedTestFiles.GlobalPath("user://combat_solver_state_dump.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(dump, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        File.Move(path + ".tmp", path, true);
    }

    private static Dictionary<string, object?> Sts2AiDumpSavedProperties(RelicModel relic)
    {
        var result = new Dictionary<string, object?>();
        for (Type? type = relic.GetType(); type != null; type = type.BaseType)
        {
            foreach (System.Reflection.PropertyInfo property in type.GetProperties(
                         System.Reflection.BindingFlags.Instance
                         | System.Reflection.BindingFlags.Public
                         | System.Reflection.BindingFlags.NonPublic
                         | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (!property.IsDefined(typeof(SavedPropertyAttribute), inherit: false)
                    || result.ContainsKey(property.Name))
                    continue;
                try
                {
                    object? value = property.GetValue(relic);
                    result[property.Name] = value switch
                    {
                        null => null,
                        int or bool or string or long => value,
                        _ => value.ToString(),
                    };
                }
                catch (Exception ex)
                {
                    result[property.Name] = $"<error: {ex.Message}>";
                }
            }
        }
        return result;
    }

    private static void Sts2AiInjectRelicExtras(RelicModel relic, UnattendedRelicInjection injection)
    {
        foreach ((string memberName, string value) in injection.StringMembers)
            SetRelicStateMember(relic, memberName, value);
        foreach ((string memberName, string value) in injection.ModelIdMembers)
        {
            int dot = value.IndexOf('.');
            if (dot <= 0 || dot == value.Length - 1)
                throw new InvalidOperationException(
                    $"遗物 {relic.Id.Entry} 成员 {memberName} 的 ModelId 值 '{value}' 必须是 CATEGORY.ENTRY。");
            SetRelicStateMember(relic, memberName, new ModelId(value[..dot], value[(dot + 1)..]));
        }
        foreach ((string memberName, int[] value) in injection.IntegerArrayMembers)
        {
            if (value is null)
                throw new InvalidOperationException(
                    $"遗物 {relic.Id.Entry} 成员 {memberName} 的整数数组为 null。");
            SetRelicStateMember(relic, memberName, value);
        }
    }
}

/// <summary>
/// Answers every native card-selection UI during a relic's acquisition by
/// consuming a per-round plan. Tokens locate exact card instances
/// (occurrence indexes the filtered candidates from 0); a plan that cannot
/// be satisfied fails loudly instead of falling back to "first same-id".
/// </summary>
internal sealed class SequentialPlannedCardSelector(
    UnattendedPlannedCardToken[][] rounds) : ICardSelector
{
    private int _round;

    public Task<IEnumerable<CardModel>> GetSelectedCards(
        IEnumerable<CardModel> options,
        int minSelect,
        int maxSelect)
    {
        if (_round >= rounds.Length)
            throw new InvalidOperationException(
                $"游戏请求了计划外的第 {_round + 1} 轮选牌。");
        UnattendedPlannedCardToken[] tokens = rounds[_round++];
        List<CardModel> available = options.ToList();
        List<CardModel> selected = [];
        foreach (UnattendedPlannedCardToken token in tokens)
        {
            IEnumerable<CardModel> matching = available.Where(item =>
                item.Id.Entry.Equals(token.CardId, StringComparison.Ordinal));
            if (token.UpgradeLevel.HasValue)
                matching = matching.Where(item =>
                    (token.UpgradeLevel.Value > 0) == item.IsUpgraded);
            if (token.EnchantmentId is { } enchantmentId)
                matching = matching.Where(item => item.Enchantment?.Id.Entry.Equals(
                    enchantmentId, StringComparison.Ordinal) == true);
            CardModel card = matching.Skip(token.Occurrence).FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"计划令牌 {token.CardId}+{token.UpgradeLevel}#{token.Occurrence} 找不到唯一合法实例。");
            selected.Add(card);
            available.Remove(card);
        }
        if (selected.Count < minSelect || selected.Count > maxSelect)
            throw new InvalidOperationException(
                $"计划选择 {selected.Count} 张牌, 但界面要求 {minSelect}..{maxSelect} 张。");
        Entry.Logger.Info(
            $"[CombatSolver/Test] ACQUISITION_CHOICE round={_round}/{rounds.Length} " +
            $"cards={string.Join(',', selected.Select(c => c.Id.Entry + (c.IsUpgraded ? "+" : "")))}");
        AppendChoiceLog(new
        {
            round = _round,
            kind = "card_selection",
            offered = options.Select(c => new
            {
                id = c.Id.Entry,
                upgraded = c.IsUpgraded,
                enchantment = c.Enchantment?.Id.Entry,
            }).ToList(),
            selected = selected.Select(c => c.Id.Entry).ToList(),
            min = minSelect,
            max = maxSelect,
        });
        return Task.FromResult<IEnumerable<CardModel>>(selected);
    }

    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
    {
        // The card reward screen shares the round plan with the selection
        // screen: consume the first token of the current round.
        if (_round == 0 || _round > rounds.Length)
            return default;
        UnattendedPlannedCardToken token = rounds[_round - 1].FirstOrDefault()
            ?? new UnattendedPlannedCardToken { CardId = "" };
        if (token.CardId.Length == 0)
            return default;
        CardModel selected = options.Select(option => option.Card)
            .FirstOrDefault(card => card.Id.Entry.Equals(token.CardId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"计划选择的奖励候选中找不到 {token.CardId}。");
        return new CardRewardSelection { card = selected };
    }

    private static void AppendChoiceLog(object entry)
    {
        try
        {
            string path = UnattendedTestFiles.GlobalPath("user://combat_solver_choice_log.jsonl");
            File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[CombatSolver/Test] CHOICE_LOG_WRITE_FAILED {ex.Message}");
        }
    }
}
