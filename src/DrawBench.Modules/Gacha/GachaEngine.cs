using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DrawBench.Modules.Gacha;

/// <summary>模块化抽卡配置（只针对模块与子维度开关，不涉及具体 tag）。</summary>
public sealed class GachaOptions
{
    public const string NoneChoice = "";
    public const string RandomChoice = "__random__";

    // 通用
    public int Repeat { get; set; } = 1;
    public bool ReplaceUnderscoreWithSpace { get; set; } = true;
    public bool IncludeNsfw { get; set; } = false;
    public bool AllowMultiPerson { get; set; } = false;
    public string PrefixTags { get; set; } = "1girl";
    public string SuffixTags { get; set; } = "";

    // 1. 头发 (hair)
    public bool HairColor { get; set; } = true;
    public string SelectedHairLength { get; set; } = RandomChoice;
    public bool HairStyles { get; set; } = true;
    public bool HairAccessories { get; set; } = false;

    // 2. 面部与五官 (face_and_eyes)
    public bool EyeColor { get; set; } = true;
    public bool Eyes { get; set; } = true;
    public bool FaceAndExpression { get; set; } = true;
    public bool Ears { get; set; } = false;

    // 3. 身体部位 (body_parts)
    public string SelectedBreastsSize { get; set; } = RandomChoice;
    public bool BreastsDetails { get; set; } = true;
    public bool Feet { get; set; } = false;

    // 4. 服装与配饰 (attire_and_accessories)
    public bool Clothing { get; set; } = true;
    public bool HeadwearAndNeckwear { get; set; } = false;
    public bool Legwear { get; set; } = true;
    public bool Footwear { get; set; } = false;
    public bool Accessories { get; set; } = false;

    // 5. 姿势与动作 (pose_and_action)
    public bool Posture { get; set; } = true;
    public bool Gestures { get; set; } = false;
    public bool VerbsAndActions { get; set; } = false;

    // 6. 场景与构图 (scene_and_composition)
    public bool Locations { get; set; } = true;
    public bool Backgrounds { get; set; } = false;

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

    /// <summary>需要多人互动才能成立的 tag 集合（关闭「多人」开关时过滤；开启时若抽到则自动屏蔽前置 1girl）。</summary>
    private static readonly HashSet<string> MultiPersonTags = new(StringComparer.OrdinalIgnoreCase)
    {
        // hair.hair_actions
        "hairdressing", "braiding_hair", "cutting_hair", "brushing_hair",
        "kissing_hair", "smelling_hair", "washing_hair",

        // body_parts.breasts_details
        "breast_contest", "breast_envy", "carried_breast_rest",
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
        "simulated_handjob", "simulated_paizuri", "ass-to-mouth", "licking_cum",
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
        new("face_and_eyes", "eye_color", "瞳色/瞳形", o => BoolChoice(o.EyeColor)),
        new("face_and_eyes", "eyes", "眼睛/视线", o => BoolChoice(o.Eyes)),
        new("face_and_eyes", "face_and_expression", "表情/面部", o => BoolChoice(o.FaceAndExpression)),
        new("face_and_eyes", "ears", "耳朵/兽耳", o => BoolChoice(o.Ears)),

        // 3. 身体部位
        new("body_parts", "breasts_size", "胸部尺寸", o => o.SelectedBreastsSize ?? GachaOptions.NoneChoice),
        new("body_parts", "breasts_details", "胸部细节", o => BoolChoice(o.BreastsDetails)),
        new("body_parts", "feet", "脚部", o => BoolChoice(o.Feet)),

        // 4. 服装与配饰
        new("attire_and_accessories", "clothing", "主服装", o => BoolChoice(o.Clothing)),
        new("attire_and_accessories", "headwear_and_neckwear", "头饰/颈饰", o => BoolChoice(o.HeadwearAndNeckwear)),
        new("attire_and_accessories", "legwear", "袜类/腿饰", o => BoolChoice(o.Legwear)),
        new("attire_and_accessories", "footwear", "鞋类", o => BoolChoice(o.Footwear)),
        new("attire_and_accessories", "accessories", "其他配饰", o => BoolChoice(o.Accessories)),

        // 5. 姿势与动作
        new("pose_and_action", "posture", "全身姿势", o => BoolChoice(o.Posture)),
        new("pose_and_action", "gestures", "手势/手持", o => BoolChoice(o.Gestures)),
        new("pose_and_action", "verbs_and_actions", "一般动作", o => BoolChoice(o.VerbsAndActions)),

        // 6. 场景与构图
        new("scene_and_composition", "locations", "地点环境", o => BoolChoice(o.Locations)),
        new("scene_and_composition", "backgrounds", "纯白背景", o => BoolChoice(o.Backgrounds)),
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

        // 提前按模式挑好每个槽位要用的池（或固定指定的 tag），避免连抽时反复合并
        var activeSlots = new List<(SlotSpec Slot, string? FixedTag, List<List<string>>? Pools, int Pick)>();
        foreach (var slot in Slots)
        {
            string choice = slot.GetChoice(options);
            if (string.IsNullOrEmpty(choice)) continue;

            if (!string.Equals(choice, GachaOptions.RandomChoice, StringComparison.Ordinal))
            {
                activeSlots.Add((slot, choice, null, 1));
                continue;
            }

            if (!bank.TryGetValue(slot.Module, out var subMap) ||
                !subMap.TryGetValue(slot.SubCategory, out var pool))
            {
                continue;
            }

            var picked = SelectPools(pool, options.IncludeNsfw, options.AllowMultiPerson);
            if (picked.Count > 0)
                activeSlots.Add((slot, null, picked, Math.Clamp(pool.Pick, 1, 4)));
        }

        if (activeSlots.Count == 0 &&
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
            bool hasMultiPersonTag = false;

            foreach (var (slot, fixedTag, pools, pick) in activeSlots)
            {
                if (fixedTag != null)
                {
                    if (MultiPersonTags.Contains(fixedTag))
                        hasMultiPersonTag = true;

                    string formatted = options.ReplaceUnderscoreWithSpace
                        ? fixedTag.Replace('_', ' ')
                        : fixedTag;

                    drawnTags.Add(formatted);
                    item.LogLines.Add($"[{slot.Module}.{slot.SubCategory}] {slot.Label}（指定）：{formatted}");
                    continue;
                }

                int totalDraws = pools!.Count == 1 ? pick : Math.Max(pick, pools.Count);
                for (int k = 0; k < totalDraws; k++)
                {
                    var pool = pools[k % pools.Count];
                    string rawTag = DrawRandom(pool, rng);
                    if (MultiPersonTags.Contains(rawTag))
                        hasMultiPersonTag = true;

                    string formatted = options.ReplaceUnderscoreWithSpace
                        ? rawTag.Replace('_', ' ')
                        : rawTag;

                    drawnTags.Add(formatted);
                    item.LogLines.Add($"[{slot.Module}.{slot.SubCategory}] {slot.Label}：{formatted}");
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

    /// <summary>
    /// 按 NSFW 开关与多人开关挑池：
    /// IncludeNsfw=false 只取 SFW 池；IncludeNsfw=true 等同原 ANY（SFW+NSFW 混合）；
    /// AllowMultiPerson=false 时过滤掉需要多人的 tag。
    /// </summary>
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

    /// <summary>等概率随机抽取一条 tag。</summary>
    private static string DrawRandom(List<string> pool, Random rng) =>
        pool.Count > 0 ? pool[rng.Next(pool.Count)] : string.Empty;

    private static void AddFixedTags(string? raw, List<string> target, bool suppressOneGirl)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            if (suppressOneGirl && string.Equals(part, "1girl", StringComparison.OrdinalIgnoreCase))
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
