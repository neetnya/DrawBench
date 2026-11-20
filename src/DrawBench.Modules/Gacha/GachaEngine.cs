using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using DrawBench.Core.Logging;

namespace DrawBench.Modules.Gacha;

/// <summary>模块化抽卡配置。</summary>
public sealed class GachaOptions
{
    public const string NoneChoice = "";
    public const string RandomChoice = "__random__";
    public const string OutfitsChoice = "outfits";
    public const string TopsBottomsChoice = "tops_bottoms";
    public const string LocationsChoice = "locations";
    public const string BackgroundsChoice = "backgrounds";
    public const string PostureChoice = "posture";
    public const string GesturesChoice = "gestures";
    public const string EarsRareChoice = "rare";
    public const string EarsAlwaysChoice = "always";

    // 通用
    public int Repeat { get; set; } = 1;
    public bool ReplaceUnderscoreWithSpace { get; set; } = true;
    public bool IncludeNsfw { get; set; } = false;
    public bool AllowMultiPerson { get; set; } = false;
    public string PrefixTags { get; set; } = "1girl, solo";
    public string SuffixTags { get; set; } = "";

    // 外貌
    public bool HairColor { get; set; } = true;
    public string SelectedHairLength { get; set; } = RandomChoice;
    public bool HairStyles { get; set; } = true;
    public bool HairAccessories { get; set; } = false;
    public bool Eyes { get; set; } = true;
    public bool FaceAndExpression { get; set; } = true;

    /// <summary>兽耳模式：不指定 / 15% 概率(rare) / 100% 必出(always)。</summary>
    public string EarsMode { get; set; } = EarsRareChoice;

    public string SelectedBreastsSize { get; set; } = NoneChoice;
    public bool BodyDetails { get; set; } = false;

    // 穿搭
    public string AttireMode { get; set; } = RandomChoice;
    public bool Legwear { get; set; } = true;
    public bool Footwear { get; set; } = true;
    public bool Accessories { get; set; } = false;

    // 动作
    public string ActionMode { get; set; } = RandomChoice;

    // 场景
    public string SceneMode { get; set; } = LocationsChoice;

    public GachaOptions Clone() => (GachaOptions)MemberwiseClone();
}

/// <summary>子组内的两档词池与可选元数据（JSON 中缺省字段使用默认值）。</summary>
public sealed class RatingPool
{
    public List<string>? Sfw { get; set; }
    public List<string>? Nsfw { get; set; }

    /// <summary>每次抽几条，默认 1。</summary>
    public int Pick { get; set; } = 1;

    /// <summary>可选最小/最大抽取条数（如细节 2~3 条）。</summary>
    public int? PickMin { get; set; }
    public int? PickMax { get; set; }

    /// <summary>NSFW 开启时是否两池各抽一条。</summary>
    public bool Both { get; set; }

    /// <summary>可选触发概率（0~1，未设置表示 100% 触发）。</summary>
    public double? TriggerProbability { get; set; }

    /// <summary>可选稀有触发概率（如兽耳 15% 模式）。</summary>
    public double? RareProbability { get; set; }

    /// <summary>可选相对权重（未列出默认 1），随机抽取时生效。</summary>
    public Dictionary<string, int>? Weights { get; set; }

    /// <summary>需要多人互动才能成立的 tag 列表。</summary>
    public List<string>? MultiPerson { get; set; }

    /// <summary>抽中集合内任意 tag 时，额外再抽一条不在此集合内的 tag（如呆毛系发型）。</summary>
    public List<string>? ExtraDrawOn { get; set; }
}

public sealed class GachaDrawItem
{
    public List<string> Tags { get; } = new();
    public List<string> LogLines { get; } = new();
    public string PromptText { get; set; } = "";
}

public sealed class GachaRunOutcome
{
    public string? Failure { get; set; }
    public List<GachaDrawItem> Items { get; } = new();
    public bool Ok => Failure == null && Items.Count > 0;

    public string AllText
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var item in Items)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(item.PromptText);
            }
            return sb.ToString();
        }
    }

    public string AllReport
    {
        get
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Items.Count; i++)
            {
                if (i > 0) sb.AppendLine();
                if (Items.Count > 1) sb.AppendLine($"—— 第 {i + 1} 条 ——");
                foreach (var line in Items[i].LogLines)
                    sb.AppendLine(line);
            }
            return sb.ToString().TrimEnd();
        }
    }
}

/// <summary>
/// 原生模块化抽卡引擎：直接读取 engine\tagGroup_modular.json，按选中的模块与子维度随机抽取 tag。
/// </summary>
public static class GachaEngine
{
    public const string BankFileName = "tagGroup_modular.json";

    private const double DefaultEarsRareProbability = 0.15;
    private const int RejectionSampleAttempts = 8;

    private static readonly JsonSerializerOptions BankJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly object CacheLock = new();
    private static string _cachedPath = "";
    private static DateTime _cachedMtime;
    private static CompiledBank? _cachedBank;

    private sealed class CompiledPool
    {
        public RatingPool Raw { get; }
        public IReadOnlyList<string> SfwAll { get; }
        public IReadOnlyList<string> SfwSingle { get; }
        public IReadOnlyList<string> NsfwAll { get; }
        public IReadOnlyList<string> NsfwSingle { get; }
        public IReadOnlyList<string> MergedAll { get; }
        public IReadOnlyList<string> MergedSingle { get; }
        public HashSet<string>? ExtraDrawOnSet { get; }

        public CompiledPool(RatingPool raw, HashSet<string> poolMultiPerson)
        {
            Raw = raw;
            SfwAll = raw.Sfw is { Count: > 0 } ? raw.Sfw : Array.Empty<string>();
            NsfwAll = raw.Nsfw is { Count: > 0 } ? raw.Nsfw : Array.Empty<string>();

            SfwSingle = FilterSingle(SfwAll, poolMultiPerson);
            NsfwSingle = FilterSingle(NsfwAll, poolMultiPerson);

            MergedAll = MergeIfBothNonEmpty(SfwAll, NsfwAll);
            MergedSingle = MergeIfBothNonEmpty(SfwSingle, NsfwSingle);

            if (raw.ExtraDrawOn is { Count: > 0 })
                ExtraDrawOnSet = new HashSet<string>(raw.ExtraDrawOn, StringComparer.OrdinalIgnoreCase);
        }

        public List<IReadOnlyList<string>> SelectPools(bool includeNsfw, bool allowMultiPerson)
        {
            var result = new List<IReadOnlyList<string>>(2);
            var sfw = allowMultiPerson ? SfwAll : SfwSingle;
            var nsfw = includeNsfw ? (allowMultiPerson ? NsfwAll : NsfwSingle) : Array.Empty<string>();

            bool hasSfw = sfw.Count > 0;
            bool hasNsfw = nsfw.Count > 0;

            if (!includeNsfw)
            {
                if (hasSfw) result.Add(sfw);
                return result;
            }

            if (Raw.Both && hasSfw && hasNsfw)
            {
                result.Add(sfw);
                result.Add(nsfw);
            }
            else if (hasSfw && hasNsfw)
            {
                result.Add(allowMultiPerson ? MergedAll : MergedSingle);
            }
            else if (hasSfw)
            {
                result.Add(sfw);
            }
            else if (hasNsfw)
            {
                result.Add(nsfw);
            }

            return result;
        }

        private static IReadOnlyList<string> FilterSingle(IReadOnlyList<string> source, HashSet<string> multiPerson)
        {
            if (source.Count == 0 || multiPerson.Count == 0) return source;
            var filtered = new List<string>(source.Count);
            foreach (string tag in source)
            {
                if (!multiPerson.Contains(tag))
                    filtered.Add(tag);
            }
            return filtered.Count == source.Count ? source : filtered;
        }

        private static IReadOnlyList<string> MergeIfBothNonEmpty(IReadOnlyList<string> sfw, IReadOnlyList<string> nsfw)
        {
            if (sfw.Count == 0 || nsfw.Count == 0) return Array.Empty<string>();
            var merged = new List<string>(sfw.Count + nsfw.Count);
            merged.AddRange(sfw);
            merged.AddRange(nsfw);
            return merged;
        }
    }

    private sealed class CompiledBank
    {
        public Dictionary<string, Dictionary<string, CompiledPool>> Modules { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> MultiPersonTags { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool TryGetPool(string module, string subCategory, out CompiledPool pool)
        {
            if (Modules.TryGetValue(module, out var subMap) && subMap.TryGetValue(subCategory, out pool!))
                return true;
            pool = null!;
            return false;
        }
    }

    private readonly record struct SlotSpec(
        string Module,
        string SubCategory,
        string Label,
        Func<GachaOptions, string> GetChoice,
        Func<GachaOptions, CompiledPool, double>? GetTriggerProbability = null);

    private static string BoolChoice(bool enabled) =>
        enabled ? GachaOptions.RandomChoice : GachaOptions.NoneChoice;

    private static readonly SlotSpec[] Slots =
    {
        // 1. 头发
        new("hair", "hair_color", "发色", o => BoolChoice(o.HairColor)),
        new("hair", "hair_length", "发长", o => o.SelectedHairLength ?? GachaOptions.NoneChoice),
        new("hair", "hair_styles", "发型", o => BoolChoice(o.HairStyles)),
        new("hair", "hair_accessories", "发饰", o => BoolChoice(o.HairAccessories)),

        // 2. 面部与五官
        new("face_and_eyes", "eyes", "瞳色/眼型", o => BoolChoice(o.Eyes)),
        new("face_and_eyes", "face_and_expression", "表情", o => BoolChoice(o.FaceAndExpression)),
        new("face_and_eyes", "ears", "兽耳",
            o => string.IsNullOrEmpty(o.EarsMode) ? GachaOptions.NoneChoice : GachaOptions.RandomChoice,
            (o, pool) => o.EarsMode == GachaOptions.EarsRareChoice
                ? (pool.Raw.RareProbability ?? DefaultEarsRareProbability)
                : 1.0),

        // 3. 身体部位
        new("body_parts", "breasts_size", "胸部尺寸", o => o.SelectedBreastsSize ?? GachaOptions.NoneChoice),
        new("body_parts", "details", "细节", o => BoolChoice(o.BodyDetails)),

        // 4. 服装与配饰（outfits/tops/bottoms 由 AttireMode 控制，在 Roll 中按需展开）
        new("attire_and_accessories", "accessories", "头饰/配饰", o => BoolChoice(o.Accessories)),
        new("attire_and_accessories", "legwear", "袜类/腿饰", o => BoolChoice(o.Legwear)),
        new("attire_and_accessories", "footwear", "鞋类", o => BoolChoice(o.Footwear)),
    };

    /// <summary>读取指定子组的 SFW tag 列表，用于下拉框选项填充。</summary>
    public static List<string> GetSlotSfwTags(string engineDir, string module, string subCategory)
    {
        try
        {
            string bankPath = Path.Combine(engineDir, BankFileName);
            var bank = LoadBank(bankPath);
            if (bank.TryGetPool(module, subCategory, out var pool) && pool.SfwAll.Count > 0)
                return new List<string>(pool.SfwAll);
        }
        catch (Exception ex)
        {
            AppLog.Error($"读取词库子组失败（{module}.{subCategory}）", ex);
        }
        return new List<string>();
    }

    public static GachaRunOutcome Roll(GachaOptions options, string engineDir)
    {
        var outcome = new GachaRunOutcome();
        string bankPath = Path.Combine(engineDir, BankFileName);

        CompiledBank bank;
        try
        {
            bank = LoadBank(bankPath);
        }
        catch (Exception ex)
        {
            outcome.Failure = $"读取词库失败（{bankPath}）：{ex.Message}";
            return outcome;
        }

        // 1. 普通槽位
        var activeSlots = new List<(SlotSpec Slot, string? FixedTag, CompiledPool? Compiled, List<IReadOnlyList<string>>? Pools)>();
        foreach (var slot in Slots)
        {
            string choice = slot.GetChoice(options);
            if (string.IsNullOrEmpty(choice)) continue;

            if (!string.Equals(choice, GachaOptions.RandomChoice, StringComparison.Ordinal))
            {
                activeSlots.Add((slot, choice, null, null));
                continue;
            }

            if (!bank.TryGetPool(slot.Module, slot.SubCategory, out var compiled))
                continue;

            var picked = compiled.SelectPools(options.IncludeNsfw, options.AllowMultiPerson);
            if (picked.Count > 0)
                activeSlots.Add((slot, null, compiled, picked));
        }

        // 2. 下拉维度按需取池（未启用时不遍历）
        var (outfitsPool, outfitsPools) = options.AttireMode is GachaOptions.RandomChoice or GachaOptions.OutfitsChoice
            ? GetPoolAndLists(bank, "attire_and_accessories", "outfits", options)
            : (null, null);
        var (topsPool, topsPools) = options.AttireMode is GachaOptions.RandomChoice or GachaOptions.TopsBottomsChoice
            ? GetPoolAndLists(bank, "attire_and_accessories", "tops", options)
            : (null, null);
        var (bottomsPool, bottomsPools) = options.AttireMode is GachaOptions.RandomChoice or GachaOptions.TopsBottomsChoice
            ? GetPoolAndLists(bank, "attire_and_accessories", "bottoms", options)
            : (null, null);

        var (posturePool, posturePools) = options.ActionMode is GachaOptions.RandomChoice or GachaOptions.PostureChoice
            ? GetPoolAndLists(bank, "pose_and_action", "posture", options)
            : (null, null);
        var (gesturesPool, gesturesPools) = options.ActionMode is GachaOptions.RandomChoice or GachaOptions.GesturesChoice
            ? GetPoolAndLists(bank, "pose_and_action", "gestures", options)
            : (null, null);

        var (locationsPool, locationsPools) = options.SceneMode is GachaOptions.RandomChoice or GachaOptions.LocationsChoice
            ? GetPoolAndLists(bank, "scene_and_composition", "locations", options)
            : (null, null);
        var (backgroundsPool, backgroundsPools) = options.SceneMode is GachaOptions.RandomChoice or GachaOptions.BackgroundsChoice
            ? GetPoolAndLists(bank, "scene_and_composition", "backgrounds", options)
            : (null, null);

        bool hasAttire = !string.IsNullOrEmpty(options.AttireMode) &&
                         (HasAny(outfitsPools) || HasAny(topsPools) || HasAny(bottomsPools));
        bool hasAction = !string.IsNullOrEmpty(options.ActionMode) &&
                         (HasAny(posturePools) || HasAny(gesturesPools));
        bool hasScene = !string.IsNullOrEmpty(options.SceneMode) &&
                        (HasAny(locationsPools) || HasAny(backgroundsPools));

        if (activeSlots.Count == 0 && !hasAttire && !hasAction && !hasScene &&
            string.IsNullOrWhiteSpace(options.PrefixTags) &&
            string.IsNullOrWhiteSpace(options.SuffixTags))
        {
            outcome.Failure = "请至少勾选或选择一个要抽取的模块，或填写前置/后置提示词。";
            return outcome;
        }

        // 预先解析前置/后置 tag，便于写入每条结果的去重集合
        var prefixList = SplitFixedTags(options.PrefixTags);
        var suffixList = SplitFixedTags(options.SuffixTags);

        int repeat = Math.Clamp(options.Repeat, 1, 99);
        var rng = Random.Shared;

        for (int i = 0; i < repeat; i++)
        {
            var item = new GachaDrawItem();
            var drawnTags = new List<string>();
            var usedRawTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasMultiPersonTag = false;

            // 将前置/后置手写 tag 预先写入去重集合，避免重复抽到
            foreach (string tag in prefixList)
                AddUsedTag(usedRawTags, tag);
            foreach (string tag in suffixList)
                AddUsedTag(usedRawTags, tag);

            void RecordTag(string module, string subCategory, string label, string rawTag)
            {
                if (string.IsNullOrEmpty(rawTag)) return;
                AddUsedTag(usedRawTags, rawTag);
                if (bank.MultiPersonTags.Contains(rawTag))
                    hasMultiPersonTag = true;

                string formatted = FormatTag(rawTag, options.ReplaceUnderscoreWithSpace);
                drawnTags.Add(formatted);
                item.LogLines.Add($"[{module}.{subCategory}] {label}：{formatted}");
            }

            foreach (var (slot, fixedTag, compiled, pools) in activeSlots)
            {
                if (fixedTag != null)
                {
                    RecordTag(slot.Module, slot.SubCategory, $"{slot.Label}（指定）", fixedTag);
                    continue;
                }

                double triggerProb = slot.GetTriggerProbability != null
                    ? slot.GetTriggerProbability(options, compiled!)
                    : (compiled!.Raw.TriggerProbability ?? 1.0);
                if (triggerProb < 1.0 && rng.NextDouble() >= triggerProb)
                    continue;

                int effectivePick = ResolvePickCount(compiled!.Raw, rng);
                int totalDraws = pools!.Count == 1 ? effectivePick : Math.Max(effectivePick, pools.Count);
                var extraDrawOn = compiled.ExtraDrawOnSet;

                for (int k = 0; k < totalDraws; k++)
                {
                    var pool = pools[k % pools.Count];
                    string rawTag = DrawUniqueInItem(pool, usedRawTags, rng, compiled.Raw.Weights);
                    RecordTag(slot.Module, slot.SubCategory, slot.Label, rawTag);

                    if (extraDrawOn != null && extraDrawOn.Contains(rawTag))
                    {
                        string extraTag = DrawUniqueInItem(pool, usedRawTags, rng, compiled.Raw.Weights, extraDrawOn);
                        RecordTag(slot.Module, slot.SubCategory, $"{slot.Label}（额外）", extraTag);
                    }
                }
            }

            // 服装下拉：不指定 / 随机 / 套装 / 上下装
            if (hasAttire)
            {
                string mode = options.AttireMode;
                if (mode == GachaOptions.RandomChoice)
                    mode = rng.Next(2) == 0 ? GachaOptions.OutfitsChoice : GachaOptions.TopsBottomsChoice;

                if (mode == GachaOptions.OutfitsChoice && HasAny(outfitsPools))
                {
                    var pool = outfitsPools![rng.Next(outfitsPools.Count)];
                    RecordTag("attire_and_accessories", "outfits", "套装",
                        DrawUniqueInItem(pool, usedRawTags, rng, outfitsPool?.Raw.Weights));
                }
                else if (mode == GachaOptions.TopsBottomsChoice)
                {
                    if (HasAny(topsPools))
                    {
                        var pool = topsPools![rng.Next(topsPools.Count)];
                        RecordTag("attire_and_accessories", "tops", "上衣",
                            DrawUniqueInItem(pool, usedRawTags, rng, topsPool?.Raw.Weights));
                    }
                    if (HasAny(bottomsPools))
                    {
                        var pool = bottomsPools![rng.Next(bottomsPools.Count)];
                        RecordTag("attire_and_accessories", "bottoms", "下装",
                            DrawUniqueInItem(pool, usedRawTags, rng, bottomsPool?.Raw.Weights));
                    }
                }
            }

            // 动作下拉：不指定 / 随机 / 全身姿势 / 手势
            if (hasAction)
            {
                string mode = options.ActionMode;
                if (mode == GachaOptions.RandomChoice)
                    mode = rng.Next(2) == 0 ? GachaOptions.PostureChoice : GachaOptions.GesturesChoice;

                if (mode == GachaOptions.PostureChoice && HasAny(posturePools))
                {
                    var pool = posturePools![rng.Next(posturePools.Count)];
                    RecordTag("pose_and_action", "posture", "全身姿势",
                        DrawUniqueInItem(pool, usedRawTags, rng, posturePool?.Raw.Weights));
                }
                else if (mode == GachaOptions.GesturesChoice && HasAny(gesturesPools))
                {
                    var pool = gesturesPools![rng.Next(gesturesPools.Count)];
                    RecordTag("pose_and_action", "gestures", "手势",
                        DrawUniqueInItem(pool, usedRawTags, rng, gesturesPool?.Raw.Weights));
                }
            }

            // 场景下拉：不指定 / 随机 / 地点环境 / 简单背景
            if (hasScene)
            {
                string mode = options.SceneMode;
                if (mode == GachaOptions.RandomChoice)
                    mode = rng.Next(2) == 0 ? GachaOptions.LocationsChoice : GachaOptions.BackgroundsChoice;

                if (mode == GachaOptions.LocationsChoice && HasAny(locationsPools))
                {
                    var pool = locationsPools![rng.Next(locationsPools.Count)];
                    RecordTag("scene_and_composition", "locations", "地点环境",
                        DrawUniqueInItem(pool, usedRawTags, rng, locationsPool?.Raw.Weights));
                }
                else if (mode == GachaOptions.BackgroundsChoice && HasAny(backgroundsPools))
                {
                    var pool = backgroundsPools![rng.Next(backgroundsPools.Count)];
                    RecordTag("scene_and_composition", "backgrounds", "简单背景",
                        DrawUniqueInItem(pool, usedRawTags, rng, backgroundsPool?.Raw.Weights));
                }
            }

            // 多人开关开启且本次抽到了多人 tag 时，自动屏蔽前置的 1girl / solo；未抽到多人 tag 则保留
            bool suppressOneGirl = options.AllowMultiPerson && hasMultiPersonTag;
            AppendFixedTags(prefixList, item.Tags, suppressOneGirl);
            item.Tags.AddRange(drawnTags);
            AppendFixedTags(suffixList, item.Tags, suppressOneGirl: false);

            item.PromptText = string.Join(", ", item.Tags);
            outcome.Items.Add(item);
        }

        return outcome;
    }

    private static (CompiledPool? Pool, List<IReadOnlyList<string>>? Lists) GetPoolAndLists(
        CompiledBank bank, string module, string subCategory, GachaOptions options)
    {
        if (bank.TryGetPool(module, subCategory, out var compiled))
            return (compiled, compiled.SelectPools(options.IncludeNsfw, options.AllowMultiPerson));
        return (null, null);
    }

    private static bool HasAny(List<IReadOnlyList<string>>? lists) => lists is { Count: > 0 };

    private static int ResolvePickCount(RatingPool pool, Random rng)
    {
        if (pool.PickMin.HasValue && pool.PickMax.HasValue)
        {
            int min = Math.Clamp(pool.PickMin.Value, 1, 8);
            int max = Math.Clamp(pool.PickMax.Value, min, 8);
            return min == max ? min : rng.Next(min, max + 1);
        }
        return Math.Clamp(pool.Pick, 1, 4);
    }

    /// <summary>单条结果内随机抽取不重复 tag（无权重时先拒绝采样减少分配，有权重时加权随机）。</summary>
    private static string DrawUniqueInItem(
        IReadOnlyList<string> pool,
        HashSet<string> usedRawTags,
        Random rng,
        Dictionary<string, int>? weights = null,
        HashSet<string>? excludeSet = null)
    {
        int count = pool.Count;
        if (count == 0) return string.Empty;

        // 无权重快速路径：拒绝采样，常见情况下零分配直接命中
        if (weights == null || weights.Count == 0)
        {
            int attempts = Math.Min(RejectionSampleAttempts, count);
            for (int a = 0; a < attempts; a++)
            {
                string candidate = pool[rng.Next(count)];
                if (IsTagUsed(usedRawTags, candidate)) continue;
                if (excludeSet != null && excludeSet.Contains(candidate)) continue;
                return candidate;
            }
        }

        // 回退全池扫描（处理权重或高冲突场景）
        var candidates = new List<(string Tag, int Weight)>(count);
        int totalWeight = 0;
        for (int i = 0; i < count; i++)
        {
            string tag = pool[i];
            if (IsTagUsed(usedRawTags, tag)) continue;
            if (excludeSet != null && excludeSet.Contains(tag)) continue;

            int w = 1;
            if (weights != null && weights.TryGetValue(tag, out int configured))
                w = configured;
            if (w <= 0) continue;

            candidates.Add((tag, w));
            totalWeight += w;
        }

        if (candidates.Count == 0)
            return pool[rng.Next(count)];
        if (candidates.Count == 1 || totalWeight <= 0)
            return candidates[0].Tag;

        if (weights == null || weights.Count == 0)
            return candidates[rng.Next(candidates.Count)].Tag;

        int roll = rng.Next(totalWeight);
        foreach (var (tag, weight) in candidates)
        {
            if (roll < weight) return tag;
            roll -= weight;
        }
        return candidates[^1].Tag;
    }

    private static void AddUsedTag(HashSet<string> usedTags, string rawTag)
    {
        if (string.IsNullOrWhiteSpace(rawTag)) return;

        if (!rawTag.Contains(','))
        {
            string trimmed = rawTag.Trim();
            usedTags.Add(trimmed);
            usedTags.Add(NormalizeKey(trimmed));
            return;
        }

        usedTags.Add(rawTag.Trim());
        foreach (string part in rawTag.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            usedTags.Add(part);
            usedTags.Add(NormalizeKey(part));
        }
    }

    private static bool IsTagUsed(HashSet<string> usedTags, string rawTag)
    {
        if (usedTags.Count == 0 || string.IsNullOrEmpty(rawTag)) return false;
        if (usedTags.Contains(rawTag)) return true;

        if (!rawTag.Contains(','))
            return usedTags.Contains(NormalizeKey(rawTag));

        foreach (string part in rawTag.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (usedTags.Contains(part) || usedTags.Contains(NormalizeKey(part)))
                return true;
        }
        return false;
    }

    private static string NormalizeKey(string tag) =>
        tag.Contains(' ') ? tag.Replace(' ', '_') : tag;

    private static string FormatTag(string rawTag, bool replaceUnderscore)
    {
        if (!replaceUnderscore || string.IsNullOrEmpty(rawTag)) return rawTag;
        // 颜文字/符号表情保留下划线（如 ^_^, @_@, o_o, >_<, double_\m/）
        if (IsKaomojiOrSymbol(rawTag)) return rawTag;
        return rawTag.Replace('_', ' ');
    }

    private static bool IsKaomojiOrSymbol(string tag)
    {
        if (tag is "3_3" or "0_0" or "o_o" or "x_x" or "t_t") return true;
        foreach (char c in tag)
        {
            if (c is '^' or '@' or '=' or '+' or '.' or '>' or '<' or ';' or ':' or '\\' or '/')
                return true;
        }
        return false;
    }

    private static List<string> SplitFixedTags(string? raw)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(part))
                list.Add(part);
        }
        return list;
    }

    private static void AppendFixedTags(List<string> source, List<string> target, bool suppressOneGirl)
    {
        foreach (string part in source)
        {
            if (suppressOneGirl &&
                (string.Equals(part, "1girl", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(part, "solo", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            target.Add(part);
        }
    }

    private static CompiledBank LoadBank(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到模块化词库文件。", path);

        lock (CacheLock)
        {
            DateTime mtime = File.GetLastWriteTimeUtc(path);
            if (_cachedBank != null &&
                string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) &&
                _cachedMtime == mtime)
            {
                return _cachedBank;
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, RatingPool>>>(json, BankJsonOptions)
                ?? throw new InvalidDataException("词库 JSON 内容为空。");

            var compiled = new CompiledBank();
            foreach (var (moduleName, subMap) in parsed)
            {
                var compiledSubs = new Dictionary<string, CompiledPool>(StringComparer.OrdinalIgnoreCase);
                foreach (var (subName, rawPool) in subMap)
                {
                    var poolMp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (rawPool.MultiPerson is { Count: > 0 })
                    {
                        foreach (string mp in rawPool.MultiPerson)
                        {
                            poolMp.Add(mp);
                            compiled.MultiPersonTags.Add(mp);
                        }
                    }
                    compiledSubs[subName] = new CompiledPool(rawPool, poolMp);
                }
                compiled.Modules[moduleName] = compiledSubs;
            }

            _cachedPath = path;
            _cachedMtime = mtime;
            _cachedBank = compiled;
            return compiled;
        }
    }
}
