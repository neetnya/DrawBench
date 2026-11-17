using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DrawBench.Modules.Gacha;

/// <summary>抽卡模式：SFW 只用安全池，NSFW 只用成人池，ANY 两池混合。</summary>
public enum GachaMode
{
    Sfw,
    Nsfw,
    Any,
}

/// <summary>模块化抽卡配置（只针对模块与子维度开关，不涉及具体 tag）。</summary>
public sealed class GachaOptions
{
    // 通用
    public int Repeat { get; set; } = 1;
    public bool ReplaceUnderscoreWithSpace { get; set; } = true;
    public string PrefixTags { get; set; } = "1girl";
    public string SuffixTags { get; set; } = "";
    public GachaMode Mode { get; set; } = GachaMode.Sfw;

    // 1. 头发 (hair)
    public bool HairColor { get; set; } = true;
    public bool HairLength { get; set; } = true;
    public bool HairStyles { get; set; } = true;
    public bool HairAccessories { get; set; } = false;
    public bool HairTraits { get; set; } = false;
    public bool HairActions { get; set; } = false;

    // 2. 面部与五官 (face_and_eyes)
    public bool EyeColor { get; set; } = true;
    public bool Eyes { get; set; } = true;
    public bool FaceAndExpression { get; set; } = true;
    public bool Ears { get; set; } = false;

    // 3. 身体部位 (body_parts)
    public bool BreastsSize { get; set; } = true;
    public bool BreastsDetails { get; set; } = true;
    public bool AssSize { get; set; } = true;
    public bool AssDetails { get; set; } = true;
    public bool Hands { get; set; } = false;
    public bool Feet { get; set; } = false;
    public bool Shoulders { get; set; } = false;
    public bool OtherBodyParts { get; set; } = false;

    // 4. 服装与配饰 (attire_and_accessories)
    public bool FashionStyle { get; set; } = true;
    public bool Clothing { get; set; } = true;
    public bool Headwear { get; set; } = false;
    public bool Neckwear { get; set; } = false;
    public bool Sleeves { get; set; } = false;
    public bool Handwear { get; set; } = false;
    public bool Legwear { get; set; } = true;
    public bool Footwear { get; set; } = false;
    public bool Accessories { get; set; } = false;

    // 5. 姿势与动作 (pose_and_action)
    public bool Posture { get; set; } = true;
    public bool Gestures { get; set; } = false;
    public bool Dances { get; set; } = false;
    public bool Holding { get; set; } = false;
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

    /// <summary>NSFW / ANY 模式下是否两池各抽一条（用于「尺寸在 sfw、细节在 nsfw」的组）。</summary>
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
/// 词库为三级结构 {模块: {子组: {sfw: [...], nsfw: [...]}}}，抽卡模式决定从哪个池取词。
/// </summary>
public static class GachaEngine
{
    public const string BankFileName = "tagGroup_modular.json";

    private static readonly object CacheLock = new();
    private static string _cachedPath = "";
    private static DateTime _cachedMtime;
    private static Dictionary<string, Dictionary<string, RatingPool>>? _cachedBank;

    private readonly record struct SlotSpec(string Module, string SubCategory, string Label, Func<GachaOptions, bool> IsEnabled);

    private static readonly SlotSpec[] Slots =
    {
        // 1. 头发
        new("hair", "hair_color", "发色", o => o.HairColor),
        new("hair", "hair_length", "发长", o => o.HairLength),
        new("hair", "hair_styles", "发型", o => o.HairStyles),
        new("hair", "hair_accessories", "发饰", o => o.HairAccessories),
        new("hair", "hair_traits", "发质", o => o.HairTraits),
        new("hair", "hair_actions", "头发动作", o => o.HairActions),

        // 2. 面部与五官
        new("face_and_eyes", "eye_color", "瞳色/瞳形", o => o.EyeColor),
        new("face_and_eyes", "eyes", "眼睛/视线", o => o.Eyes),
        new("face_and_eyes", "face_and_expression", "表情/面部", o => o.FaceAndExpression),
        new("face_and_eyes", "ears", "耳朵/兽耳", o => o.Ears),

        // 3. 身体部位
        new("body_parts", "breasts_size", "胸部尺寸", o => o.BreastsSize),
        new("body_parts", "breasts_details", "胸部细节", o => o.BreastsDetails),
        new("body_parts", "ass_size", "臀部尺寸", o => o.AssSize),
        new("body_parts", "ass_details", "臀部细节", o => o.AssDetails),
        new("body_parts", "hands", "手部", o => o.Hands),
        new("body_parts", "feet", "脚部", o => o.Feet),
        new("body_parts", "shoulders", "肩部", o => o.Shoulders),
        new("body_parts", "other_body_parts", "其他部位", o => o.OtherBodyParts),

        // 4. 服装与配饰
        new("attire_and_accessories", "fashion_style", "穿搭风格", o => o.FashionStyle),
        new("attire_and_accessories", "clothing", "主服装", o => o.Clothing),
        new("attire_and_accessories", "headwear", "头饰/帽子", o => o.Headwear),
        new("attire_and_accessories", "neckwear", "颈饰/领饰", o => o.Neckwear),
        new("attire_and_accessories", "sleeves", "袖型", o => o.Sleeves),
        new("attire_and_accessories", "handwear", "手套/手饰", o => o.Handwear),
        new("attire_and_accessories", "legwear", "袜类/腿饰", o => o.Legwear),
        new("attire_and_accessories", "footwear", "鞋类", o => o.Footwear),
        new("attire_and_accessories", "accessories", "其他配饰", o => o.Accessories),

        // 5. 姿势与动作
        new("pose_and_action", "posture", "全身姿势", o => o.Posture),
        new("pose_and_action", "gestures", "手势", o => o.Gestures),
        new("pose_and_action", "dances", "舞蹈", o => o.Dances),
        new("pose_and_action", "holding", "手持物", o => o.Holding),
        new("pose_and_action", "verbs_and_actions", "一般动作", o => o.VerbsAndActions),

        // 6. 场景与构图
        new("scene_and_composition", "locations", "地点环境", o => o.Locations),
        new("scene_and_composition", "backgrounds", "纯白背景", o => o.Backgrounds),
    };

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

        // 提前按模式挑好每个槽位要用的池，避免连抽时反复合并
        var activePools = new List<(SlotSpec Slot, List<List<string>> Pools, int Pick)>();
        foreach (var slot in Slots)
        {
            if (!slot.IsEnabled(options)) continue;
            if (!bank.TryGetValue(slot.Module, out var subMap) ||
                !subMap.TryGetValue(slot.SubCategory, out var pool))
            {
                continue;
            }

            var picked = SelectPools(pool, options.Mode);
            if (picked.Count > 0)
                activePools.Add((slot, picked, Math.Clamp(pool.Pick, 1, 4)));
        }

        if (activePools.Count == 0 &&
            string.IsNullOrWhiteSpace(options.PrefixTags) &&
            string.IsNullOrWhiteSpace(options.SuffixTags))
        {
            outcome.Failure = "请至少勾选一个要抽取的模块，或填写前置/后置提示词。";
            return outcome;
        }

        if (activePools.Count == 0 && options.Mode == GachaMode.Nsfw)
        {
            outcome.Failure = "NSFW 模式下选中的模块没有可用的成人 tag，请多勾几个模块或改用 ANY。";
            return outcome;
        }

        int repeat = Math.Clamp(options.Repeat, 1, 99);
        var rng = Random.Shared;

        for (int i = 0; i < repeat; i++)
        {
            var item = new GachaDrawItem();

            AddFixedTags(options.PrefixTags, item.Tags);

            bool pickLocation = !(options.Locations && options.Backgrounds) || rng.Next(2) == 0;
            // 全身姿势与舞蹈互斥：同时勾选时每次只抽其中一个
            bool pickPosture = !(options.Posture && options.Dances) || rng.Next(2) == 0;
            foreach (var (slot, pools, pick) in activePools)
            {
                if (options.Locations && options.Backgrounds)
                {
                    if (slot.SubCategory == "locations" && !pickLocation) continue;
                    if (slot.SubCategory == "backgrounds" && pickLocation) continue;
                }
                if (options.Posture && options.Dances)
                {
                    if (slot.SubCategory == "posture" && !pickPosture) continue;
                    if (slot.SubCategory == "dances" && pickPosture) continue;
                }

                var drawn = new List<string>(pick);
                int totalDraws = pools.Count == 1 ? pick : Math.Max(pick, pools.Count);
                for (int k = 0; k < totalDraws; k++)
                {
                    var pool = pools[k % pools.Count];
                    string rawTag = DrawUnique(pool, drawn, rng);
                    drawn.Add(rawTag);

                    string formatted = options.ReplaceUnderscoreWithSpace
                        ? rawTag.Replace('_', ' ')
                        : rawTag;

                    item.Tags.Add(formatted);
                    item.LogLines.Add($"[{slot.Module}.{slot.SubCategory}] {slot.Label}：{formatted}");
                }
            }

            AddFixedTags(options.SuffixTags, item.Tags);
            item.PromptText = string.Join(", ", item.Tags);
            outcome.Items.Add(item);
        }

        return outcome;
    }

    /// <summary>
    /// 按模式挑池：SFW 只取安全池；NSFW 优先成人池（没有则回退安全池）；
    /// 标了 both 的组在 NSFW / ANY 下额外带上安全池，让「尺寸/形状」和「细节」各抽一条。
    /// </summary>
    private static List<List<string>> SelectPools(RatingPool pool, GachaMode mode)
    {
        var result = new List<List<string>>(2);
        var sfw = pool.Sfw;
        var nsfw = pool.Nsfw;
        bool hasSfw = sfw is { Count: > 0 };
        bool hasNsfw = nsfw is { Count: > 0 };

        switch (mode)
        {
            case GachaMode.Sfw:
                if (hasSfw) result.Add(sfw!);
                break;

            case GachaMode.Nsfw:
                if (hasNsfw) result.Add(nsfw!);
                else if (hasSfw) result.Add(sfw!);
                if (pool.Both && hasSfw && hasNsfw) result.Add(sfw!);
                break;

            default:
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
                break;
        }

        return result;
    }

    /// <summary>从池里抽一条，尽量不与本次已抽的 tag 重复。</summary>
    private static string DrawUnique(List<string> pool, List<string> drawn, Random rng)
    {
        if (pool.Count == 1) return pool[0];
        string tag = pool[rng.Next(pool.Count)];
        for (int i = 0; i < 5 && drawn.Contains(tag); i++)
            tag = pool[rng.Next(pool.Count)];
        return tag;
    }

    private static void AddFixedTags(string? raw, List<string> target)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(part))
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
