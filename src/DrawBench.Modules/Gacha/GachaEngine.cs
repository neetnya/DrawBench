using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

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
    public bool EyeColor { get; set; } = true;
    public bool Eyes { get; set; } = true;
    public bool FaceAndExpression { get; set; } = true;
    public bool EarsRare { get; set; } = true;
    public bool Ears { get; set; } = false;
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

/// <summary>子组内的两档词池（JSON 中缺省的池视为空）。</summary>
public sealed class RatingPool
{
    public List<string>? Sfw { get; set; }
    public List<string>? Nsfw { get; set; }

    /// <summary>每次抽几条，默认 1。</summary>
    public int Pick { get; set; } = 1;

    /// <summary>NSFW 开启时是否两池各抽一条。</summary>
    public bool Both { get; set; }

    /// <summary>可选相对权重（未列出默认 1），随机抽取时生效。</summary>
    public Dictionary<string, int>? Weights { get; set; }
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

    private static readonly object CacheLock = new();
    private static string _cachedPath = "";
    private static DateTime _cachedMtime;
    private static Dictionary<string, Dictionary<string, RatingPool>>? _cachedBank;

    /// <summary>兽耳(15%)启用时的内置触发概率。</summary>
    private const double EarsTriggerProbability = 0.15;

    /// <summary>瞳色与眼型/瞳型启用时的内置触发概率（30%）。</summary>
    private const double EyeTriggerProbability = 0.30;

    /// <summary>呆毛系发型：抽中时额外再抽一条发型。</summary>
    private static readonly HashSet<string> AhogeTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "ahoge", "heart_ahoge", "huge_ahoge", "antenna_hair", "heart_antenna_hair"
    };

    /// <summary>需要多人互动才能成立的 tag 集合（关闭「多人」开关时过滤；开启时若抽到则自动屏蔽前置 1girl）。</summary>
    private static readonly HashSet<string> MultiPersonTags = new(StringComparer.OrdinalIgnoreCase)
    {
        // hair.hair_actions (merged into verbs_and_actions)
        "hairdressing", "braiding_hair", "cutting_hair", "brushing_hair",
        "kissing_hair", "smelling_hair", "washing_hair",

        // body_parts.details & breasts_details
        "breast_contest", "breast_envy", "flat_envy", "bust_measuring", "carried_breast_rest",
        "biting_nipple", "licking_nipple", "mutual_breast_sucking",
        "nipple-to-nipple", "penis_to_breast", "reverse_paizuri",

        // pose_and_action.posture (sfw)
        "cheek-to-breast", "head_on_chest", "hug", "arm_hug", "carrying",

        // pose_and_action.posture (nsfw)
        "sex", "69", "sex_from_behind", "doggystyle", "prone_bone", "standing_sex",
        "top-down_bottom-up", "spitroast", "reverse_spitroast", "spooning",
        "girl_on_top", "amazon_position", "cowgirl_position", "squatting_cowgirl_position",
        "reverse_cowgirl_position", "reverse_squatting_cowgirl_position",
        "reverse_upright_straddle", "upright_straddle", "boy_on_top",
        "missionary", "mating_press", "suspended_congress", "reverse_suspended_congress",
        "full_nelson", "piledriver_(sex)", "mounting", "standing_missionary", "belly_riding",

        // pose_and_action.gestures (sfw + nsfw)
        "noogie", "pinky_swear", "holding_hands", "hands_on_feet",
        "headpat", "cheek_pinching", "cheek_poking", "cheek_squash", "patting_back",
        "grabbing_another's_ass", "grabbing_another's_breast", "guided_breast_grab",

        // pose_and_action.verbs_and_actions (sfw)
        "comforting", "cuddling", "elbowing", "feeding", "flirting", "hitting",
        "kicking", "kneeing", "licking_breast", "patting", "pinching", "poking",
        "punching", "pushing", "sharing", "slapping", "tickling", "touching",
        "whispering", "mind_control", "taunting", "teasing",
        "toe_sucking", "kissing_foot", "trampling",

        // pose_and_action.verbs_and_actions (nsfw)
        "ass_smack", "biting_ass", "smelling_ass", "head_on_ass",
        "footjob", "double_footjob", "implied_footjob", "cooperative_footjob",
        "footjob_with_footwear", "footjob_with_shoes", "footjob_with_boots",
        "footjob_with_legwear", "two-footed_footjob", "reverse_footjob",
        "footjob_from_behind", "female_footjob", "footjob_under_table",
        "simulated_footjob", "after_footjob", "licking_foot", "foot_worship", "smelling_feet",
        "frottage", "armpit_sex", "pussyjob", "buttjob", "cooperative_pussyjob", "kneepit_sex",
        "paizuri", "assisted_paizuri", "cooperative_paizuri", "handsfree_paizuri", "naizuri",
        "paizuri_over_clothes", "paizuri_on_lap", "paizuri_under_clothes", "pecjob",
        "perpendicular_paizuri", "straddling_paizuri", "sideways_perpendicular_paizuri",
        "thigh_sex", "glansjob", "groping", "pectoral_grab", "hairjob",
        "handjob", "caressing_testicles", "double_handjob", "cooperative_handjob",
        "nursing_handjob", "cuddling_handjob", "reverse_nursing_handjob",
        "reach-around", "two-handed_handjob", "mutual_masturbation", "tailjob",
        "oral", "anilingus", "breast_sucking", "cunnilingus", "implied_cunnilingus",
        "fellatio", "assisted_fellatio", "cum_swap", "deepthroat", "implied_fellatio",
        "irrumatio", "cooperative_fellatio", "upright_69", "hug_and_suck",
        "licking_testicle", "sitting_on_face", "testicle_sucking", "licking_armpit",
        "covered_penetration", "deep_penetration",
        "after_sex", "after_anal", "after_fellatio", "after_paizuri", "after_vaginal", "clothed_after_sex",
        "anal", "imminent_anal", "clothed_sex", "guided_penetration", "happy_sex",
        "imminent_penetration", "implied_sex", "rough_sex", "ear_sex", "underwater_sex",
        "vaginal", "imminent_vaginal",
        "breast_smother", "breastfeeding", "bound_together",
        "dominator_(bdsm)", "femdom", "assertive_female", "clothed_female_nude_male",
        "dominatrix", "neglect_play", "public_use", "sadism", "spanked", "nipple_pull", "tickle_torture",
        "ejaculation", "ejaculating_while_penetrated", "pull_out", "facial",
        "voyeurism", "caught", "peeking", "walk-in", "stealth_sex", "prostitution",
        "rape", "assisted_rape", "imminent_rape", "molestation", "sleep_molestation", "chikan",
        "smother", "internal_cumshot", "josou_seme", "power_bottom", "futasub", "defloration",
        "hand_milking", "humping", "dry_humping", "penis_measuring", "spanking", "pantsing",
        "simulated_fellatio", "simulated_bukkake", "simulated_facial",
        "simulated_handjob", "simulated_paizuri",
        "footjob_with_sandals", "stealth_paizuri", "guided_pectoral_grab", "legjob",
        "simulated_cunnilingus", "simulated_thigh_sex", "ass-to-mouth", "licking_cum",
    };

    private readonly record struct SlotSpec(string Module, string SubCategory, string Label, Func<GachaOptions, string> GetChoice);

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
        new("face_and_eyes", "eye_color", "瞳色", o => BoolChoice(o.EyeColor)),
        new("face_and_eyes", "eyes", "眼型/瞳型", o => BoolChoice(o.Eyes)),
        new("face_and_eyes", "face_and_expression", "表情", o => BoolChoice(o.FaceAndExpression)),
        new("face_and_eyes", "ears", "兽耳", o => BoolChoice(o.Ears || o.EarsRare)),

        // 3. 身体部位
        new("body_parts", "breasts_size", "胸部尺寸", o => o.SelectedBreastsSize ?? GachaOptions.NoneChoice),
        new("body_parts", "details", "细节", o => BoolChoice(o.BodyDetails)),

        // 4. 服装与配饰（outfits/tops/bottoms 由 AttireMode 控制，在 Roll 中动态展开）
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
            if (bank.TryGetValue(module, out var subMap) &&
                subMap.TryGetValue(subCategory, out var pool) &&
                pool.Sfw is { Count: > 0 })
            {
                return new List<string>(pool.Sfw);
            }
        }
        catch
        {
            // 读取失败时返回空列表，由调用方回退
        }
        return new List<string>();
    }

    public static GachaRunOutcome Roll(GachaOptions options, string engineDir)
    {
        var outcome = new GachaRunOutcome();
        string bankPath = Path.Combine(engineDir, BankFileName);

        Dictionary<string, Dictionary<string, RatingPool>> bank;
        try
        {
            bank = LoadBank(bankPath);
        }
        catch (Exception ex)
        {
            outcome.Failure = $"读取词库失败（{bankPath}）：{ex.Message}";
            return outcome;
        }

        // 普通槽位
        var activeSlots = new List<(SlotSpec Slot, string? FixedTag, List<List<string>>? Pools, int Pick, Dictionary<string, int>? Weights)>();
        foreach (var slot in Slots)
        {
            string choice = slot.GetChoice(options);
            if (string.IsNullOrEmpty(choice)) continue;

            if (!string.Equals(choice, GachaOptions.RandomChoice, StringComparison.Ordinal))
            {
                activeSlots.Add((slot, choice, null, 1, null));
                continue;
            }

            if (!bank.TryGetValue(slot.Module, out var subMap) ||
                !subMap.TryGetValue(slot.SubCategory, out var pool))
            {
                continue;
            }

            var picked = SelectPools(pool, options.IncludeNsfw, options.AllowMultiPerson);
            if (picked.Count > 0)
                activeSlots.Add((slot, null, picked, Math.Clamp(pool.Pick, 1, 4), pool.Weights));
        }

        // 服装下拉与场景下拉的可用池
        var outfitsPools = GetPools(bank, "attire_and_accessories", "outfits", options);
        var topsPools = GetPools(bank, "attire_and_accessories", "tops", options);
        var bottomsPools = GetPools(bank, "attire_and_accessories", "bottoms", options);
        var posturePools = GetPools(bank, "pose_and_action", "posture", options);
        var gesturesPools = GetPools(bank, "pose_and_action", "gestures", options);
        var locationsPools = GetPools(bank, "scene_and_composition", "locations", options);
        var backgroundsPools = GetPools(bank, "scene_and_composition", "backgrounds", options);
        Dictionary<string, int>? backgroundsWeights = bank.TryGetValue("scene_and_composition", out var scMap) && scMap.TryGetValue("backgrounds", out var bgPool) ? bgPool.Weights : null;

        bool hasAttire = !string.IsNullOrEmpty(options.AttireMode) &&
                         (outfitsPools.Count > 0 || topsPools.Count > 0 || bottomsPools.Count > 0);
        bool hasAction = !string.IsNullOrEmpty(options.ActionMode) &&
                         (posturePools.Count > 0 || gesturesPools.Count > 0);
        bool hasScene = !string.IsNullOrEmpty(options.SceneMode) &&
                        (locationsPools.Count > 0 || backgroundsPools.Count > 0);

        if (activeSlots.Count == 0 && !hasAttire && !hasAction && !hasScene &&
            string.IsNullOrWhiteSpace(options.PrefixTags) &&
            string.IsNullOrWhiteSpace(options.SuffixTags))
        {
            outcome.Failure = "请至少勾选或选择一个要抽取的模块，或填写前置/后置提示词。";
            return outcome;
        }

        int repeat = Math.Clamp(options.Repeat, 1, 99);
        var rng = Random.Shared;

        for (int i = 0; i < repeat; i++)
        {
            var item = new GachaDrawItem();
            var drawnTags = new List<string>();
            var usedRawTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasMultiPersonTag = false;

            void RecordTag(string module, string subCategory, string label, string rawTag)
            {
                if (string.IsNullOrEmpty(rawTag)) return;
                usedRawTags.Add(rawTag);
                if (MultiPersonTags.Contains(rawTag))
                    hasMultiPersonTag = true;

                string formatted = FormatTag(rawTag, options.ReplaceUnderscoreWithSpace);

                drawnTags.Add(formatted);
                item.LogLines.Add($"[{module}.{subCategory}] {label}：{formatted}");
            }

            foreach (var (slot, fixedTag, pools, pick, weights) in activeSlots)
            {
                if (fixedTag != null)
                {
                    RecordTag(slot.Module, slot.SubCategory, $"{slot.Label}（指定）", fixedTag);
                    continue;
                }

                // 「兽耳(15%)」开启时仅按内置 15% 概率触发抽取
                if (slot.SubCategory == "ears" && options.EarsRare && !options.Ears && rng.NextDouble() >= EarsTriggerProbability)
                    continue;

                // 「瞳色(30%)」与「眼型/瞳型(30%)」开启时按内置 30% 概率触发抽取
                if ((slot.SubCategory == "eye_color" || slot.SubCategory == "eyes") && rng.NextDouble() >= EyeTriggerProbability)
                    continue;

                // 「细节」分组每次随机抽取 2~3 个
                int effectivePick = slot.SubCategory == "details" ? rng.Next(2, 4) : pick;
                int totalDraws = pools!.Count == 1 ? effectivePick : Math.Max(effectivePick, pools.Count);
                for (int k = 0; k < totalDraws; k++)
                {
                    var pool = pools[k % pools.Count];
                    string rawTag = DrawUniqueInItem(pool, usedRawTags, rng, weights);
                    RecordTag(slot.Module, slot.SubCategory, slot.Label, rawTag);

                    // 发型抽到呆毛系时，额外再抽一次发型
                    if (slot.SubCategory == "hair_styles" && AhogeTags.Contains(rawTag))
                    {
                        string extraTag = DrawUniqueInItem(pool, usedRawTags, rng, weights, excludeAhoge: true);
                        RecordTag(slot.Module, slot.SubCategory, $"{slot.Label}（呆毛额外）", extraTag);
                    }
                }
            }

            // 服装下拉：不指定 / 随机 / 套装 / 上下装
            if (hasAttire)
            {
                string mode = options.AttireMode;
                if (mode == GachaOptions.RandomChoice)
                    mode = rng.Next(2) == 0 ? GachaOptions.OutfitsChoice : GachaOptions.TopsBottomsChoice;

                if (mode == GachaOptions.OutfitsChoice && outfitsPools.Count > 0)
                {
                    var pool = outfitsPools[rng.Next(outfitsPools.Count)];
                    RecordTag("attire_and_accessories", "outfits", "套装", DrawRandom(pool, rng));
                }
                else if (mode == GachaOptions.TopsBottomsChoice)
                {
                    if (topsPools.Count > 0)
                    {
                        var pool = topsPools[rng.Next(topsPools.Count)];
                        RecordTag("attire_and_accessories", "tops", "上衣", DrawRandom(pool, rng));
                    }
                    if (bottomsPools.Count > 0)
                    {
                        var pool = bottomsPools[rng.Next(bottomsPools.Count)];
                        RecordTag("attire_and_accessories", "bottoms", "下装", DrawRandom(pool, rng));
                    }
                }
            }

            // 动作下拉：不指定 / 随机 / 全身姿势 / 手势
            if (hasAction)
            {
                string mode = options.ActionMode;
                if (mode == GachaOptions.RandomChoice)
                    mode = rng.Next(2) == 0 ? GachaOptions.PostureChoice : GachaOptions.GesturesChoice;

                if (mode == GachaOptions.PostureChoice && posturePools.Count > 0)
                {
                    var pool = posturePools[rng.Next(posturePools.Count)];
                    RecordTag("pose_and_action", "posture", "全身姿势", DrawRandom(pool, rng));
                }
                else if (mode == GachaOptions.GesturesChoice && gesturesPools.Count > 0)
                {
                    var pool = gesturesPools[rng.Next(gesturesPools.Count)];
                    RecordTag("pose_and_action", "gestures", "手势", DrawRandom(pool, rng));
                }
            }

            // 场景下拉：不指定 / 随机 / 地点环境 / 简单背景
            if (hasScene)
            {
                string mode = options.SceneMode;
                if (mode == GachaOptions.RandomChoice)
                    mode = rng.Next(2) == 0 ? GachaOptions.LocationsChoice : GachaOptions.BackgroundsChoice;

                if (mode == GachaOptions.LocationsChoice && locationsPools.Count > 0)
                {
                    var pool = locationsPools[rng.Next(locationsPools.Count)];
                    RecordTag("scene_and_composition", "locations", "地点环境", DrawRandom(pool, rng));
                }
                else if (mode == GachaOptions.BackgroundsChoice && backgroundsPools.Count > 0)
                {
                    var pool = backgroundsPools[rng.Next(backgroundsPools.Count)];
                    RecordTag("scene_and_composition", "backgrounds", "简单背景", DrawUniqueInItem(pool, usedRawTags, rng, backgroundsWeights));
                }
            }

            // 多人开关开启且本次抽到了多人 tag 时，自动屏蔽前置的 1girl；未抽到多人 tag 则保留 1girl
            bool suppressOneGirl = options.AllowMultiPerson && hasMultiPersonTag;
            AddFixedTags(options.PrefixTags, item.Tags, suppressOneGirl);
            item.Tags.AddRange(drawnTags);
            AddFixedTags(options.SuffixTags, item.Tags, suppressOneGirl: false);

            item.PromptText = string.Join(", ", item.Tags);
            outcome.Items.Add(item);
        }

        return outcome;
    }

    private static List<List<string>> GetPools(
        Dictionary<string, Dictionary<string, RatingPool>> bank,
        string module, string subCategory, GachaOptions options)
    {
        if (bank.TryGetValue(module, out var subMap) &&
            subMap.TryGetValue(subCategory, out var pool))
        {
            return SelectPools(pool, options.IncludeNsfw, options.AllowMultiPerson);
        }
        return new List<List<string>>();
    }

    private static List<List<string>> SelectPools(RatingPool pool, bool includeNsfw, bool allowMultiPerson)
    {
        var result = new List<List<string>>(2);
        var sfw = FilterPool(pool.Sfw, allowMultiPerson);
        var nsfw = includeNsfw ? FilterPool(pool.Nsfw, allowMultiPerson) : null;
        bool hasSfw = sfw is { Count: > 0 };
        bool hasNsfw = nsfw is { Count: > 0 };

        if (!includeNsfw)
        {
            if (hasSfw) result.Add(sfw!);
            return result;
        }

        if (pool.Both && hasSfw && hasNsfw)
        {
            result.Add(sfw!);
            result.Add(nsfw!);
        }
        else if (hasSfw && hasNsfw)
        {
            var merged = new List<string>(sfw!.Count + nsfw!.Count);
            merged.AddRange(sfw);
            merged.AddRange(nsfw);
            result.Add(merged);
        }
        else if (hasSfw) result.Add(sfw!);
        else if (hasNsfw) result.Add(nsfw!);

        return result;
    }

    private static List<string>? FilterPool(List<string>? source, bool allowMultiPerson)
    {
        if (source is not { Count: > 0 }) return null;
        if (allowMultiPerson) return source;

        var filtered = new List<string>(source.Count);
        foreach (string tag in source)
        {
            if (!MultiPersonTags.Contains(tag))
                filtered.Add(tag);
        }
        return filtered;
    }

    /// <summary>单条结果内随机抽取不重复 tag（若配置了 Weights 则按权重加权随机，否则等概率）。</summary>
    private static string DrawUniqueInItem(
        List<string> pool,
        HashSet<string> usedRawTags,
        Random rng,
        Dictionary<string, int>? weights = null,
        bool excludeAhoge = false)
    {
        if (pool.Count == 0) return string.Empty;

        var candidates = new List<(string Tag, int Weight)>(pool.Count);
        int totalWeight = 0;
        foreach (string tag in pool)
        {
            if (usedRawTags.Contains(tag)) continue;
            if (excludeAhoge && AhogeTags.Contains(tag)) continue;

            int w = 1;
            if (weights != null && weights.TryGetValue(tag, out int configured))
                w = configured;
            if (w <= 0) continue;

            candidates.Add((tag, w));
            totalWeight += w;
        }

        if (candidates.Count == 0)
            return pool[rng.Next(pool.Count)];
        if (candidates.Count == 1 || totalWeight <= 0)
            return candidates[0].Tag;

        if (weights == null)
            return candidates[rng.Next(candidates.Count)].Tag;

        int roll = rng.Next(totalWeight);
        foreach (var (tag, weight) in candidates)
        {
            if (roll < weight) return tag;
            roll -= weight;
        }
        return candidates[^1].Tag;
    }

    /// <summary>等概率随机抽取一条 tag。</summary>
    private static string DrawRandom(List<string> pool, Random rng) =>
        pool.Count > 0 ? pool[rng.Next(pool.Count)] : string.Empty;

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

    private static void AddFixedTags(string? raw, List<string> target, bool suppressOneGirl)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            if (suppressOneGirl && (string.Equals(part, "1girl", StringComparison.OrdinalIgnoreCase) || string.Equals(part, "solo", StringComparison.OrdinalIgnoreCase)))
                continue;
            target.Add(part);
        }
    }

    private static Dictionary<string, Dictionary<string, RatingPool>> LoadBank(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到模块化词库文件。", path);

        DateTime mtime = File.GetLastWriteTimeUtc(path);
        lock (CacheLock)
        {
            if (_cachedBank != null &&
                string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) &&
                _cachedMtime == mtime)
            {
                return _cachedBank;
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, RatingPool>>>(json, jsonOptions)
                ?? throw new InvalidDataException("词库 JSON 内容为空。");

            _cachedPath = path;
            _cachedMtime = mtime;
            _cachedBank = parsed;
            return parsed;
        }
    }
}
