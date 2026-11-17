using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DrawBench.Modules.Gacha;

/// <summary>模块化抽卡配置（只针对模块与子维度开关，不涉及具体 tag）。</summary>
public sealed class GachaOptions
{
    // 通用
    public int Repeat { get; set; } = 1;
    public bool ReplaceUnderscoreWithSpace { get; set; } = true;
    public string PrefixTags { get; set; } = "1girl, solo";
    public string SuffixTags { get; set; } = "";

    // 1. 头发 (hair)
    public bool HairColor { get; set; } = true;
    public bool HairStyles { get; set; } = true;
    public bool HairAccessories { get; set; } = false;
    public bool HairTraitsAndActions { get; set; } = false;

    // 2. 面部与五官 (face_and_eyes)
    public bool Eyes { get; set; } = true;
    public bool FaceAndExpression { get; set; } = true;
    public bool Ears { get; set; } = false;

    // 3. 身体部位 (body_parts)
    public bool Breasts { get; set; } = false;
    public bool Ass { get; set; } = false;
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
    public bool Lighting { get; set; } = true;
    public bool ImageComposition { get; set; } = true;

    // 7. 成人向 (nsfw)
    public bool EnableNsfw { get; set; } = false;
    public bool NsfwNudityAndCovering { get; set; } = false;
    public bool NsfwSexualAttire { get; set; } = false;
    public bool NsfwSexActs { get; set; } = false;
    public bool NsfwSexualPositions { get; set; } = false;
    public bool NsfwSexObjects { get; set; } = false;

    public GachaOptions Clone() => (GachaOptions)MemberwiseClone();
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
    private static Dictionary<string, Dictionary<string, List<string>>>? _cachedBank;

    private readonly record struct SlotSpec(string Module, string SubCategory, string Label, Func<GachaOptions, bool> IsEnabled);

    private static readonly SlotSpec[] Slots =
    {
        // 1. 头发
        new("hair", "hair_color", "发色", o => o.HairColor),
        new("hair", "hair_styles", "发型", o => o.HairStyles),
        new("hair", "hair_accessories", "发饰", o => o.HairAccessories),
        new("hair", "hair_traits_and_actions", "发质/动态", o => o.HairTraitsAndActions),

        // 2. 面部与五官
        new("face_and_eyes", "eyes", "眼睛", o => o.Eyes),
        new("face_and_eyes", "face_and_expression", "表情/面部", o => o.FaceAndExpression),
        new("face_and_eyes", "ears", "耳朵/兽耳", o => o.Ears),

        // 3. 身体部位
        new("body_parts", "breasts", "胸部", o => o.Breasts),
        new("body_parts", "ass", "臀部", o => o.Ass),
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
        new("attire_and_accessories", "accessories", "其他配饰", o => o.Accessories),

        // 5. 姿势与动作
        new("pose_and_action", "posture", "全身姿势", o => o.Posture),
        new("pose_and_action", "gestures", "手势", o => o.Gestures),
        new("pose_and_action", "dances", "舞蹈", o => o.Dances),
        new("pose_and_action", "holding", "手持物", o => o.Holding),
        new("pose_and_action", "verbs_and_actions", "一般动作", o => o.VerbsAndActions),

        // 6. 场景与构图
        new("scene_and_composition", "locations", "地点环境", o => o.Locations),
        new("scene_and_composition", "backgrounds", "背景", o => o.Backgrounds),
        new("scene_and_composition", "lighting", "光影", o => o.Lighting),
        new("scene_and_composition", "image_composition", "构图/视角", o => o.ImageComposition),

        // 7. 成人向 (NSFW)
        new("nsfw", "nudity_and_covering", "裸露/遮挡", o => o.EnableNsfw && o.NsfwNudityAndCovering),
        new("nsfw", "sexual_attire", "情趣服饰", o => o.EnableNsfw && o.NsfwSexualAttire),
        new("nsfw", "sex_acts", "性行为", o => o.EnableNsfw && o.NsfwSexActs),
        new("nsfw", "sexual_positions", "体位", o => o.EnableNsfw && o.NsfwSexualPositions),
        new("nsfw", "sex_objects", "器具/道具", o => o.EnableNsfw && o.NsfwSexObjects),
    };

    public static GachaRunOutcome Roll(GachaOptions options, string engineDir)
    {
        var outcome = new GachaRunOutcome();
        string bankPath = Path.Combine(engineDir, BankFileName);

        Dictionary<string, Dictionary<string, List<string>>> bank;
        try
        {
            bank = LoadBank(bankPath);
        }
        catch (Exception ex)
        {
            outcome.Failure = $"读取词库失败（{bankPath}）：{ex.Message}";
            return outcome;
        }

        var activeSlots = new List<SlotSpec>();
        foreach (var slot in Slots)
        {
            if (slot.IsEnabled(options))
                activeSlots.Add(slot);
        }

        if (activeSlots.Count == 0 &&
            string.IsNullOrWhiteSpace(options.PrefixTags) &&
            string.IsNullOrWhiteSpace(options.SuffixTags))
        {
            outcome.Failure = "请至少勾选一个要抽取的模块，或填写前置/后置提示词。";
            return outcome;
        }

        int repeat = Math.Clamp(options.Repeat, 1, 99);
        var rng = Random.Shared;

        for (int i = 0; i < repeat; i++)
        {
            var item = new GachaDrawItem();

            AddFixedTags(options.PrefixTags, item.Tags);

            bool pickLocation = !(options.Locations && options.Backgrounds) || rng.Next(2) == 0;
            foreach (var slot in activeSlots)
            {
                if (options.Locations && options.Backgrounds)
                {
                    if (slot.SubCategory == "locations" && !pickLocation) continue;
                    if (slot.SubCategory == "backgrounds" && pickLocation) continue;
                }
                if (!bank.TryGetValue(slot.Module, out var subMap) ||
                    !subMap.TryGetValue(slot.SubCategory, out var pool) ||
                    pool.Count == 0)
                {
                    continue;
                }

                string rawTag = pool[rng.Next(pool.Count)];
                string formatted = options.ReplaceUnderscoreWithSpace
                    ? rawTag.Replace('_', ' ')
                    : rawTag;

                item.Tags.Add(formatted);
                item.LogLines.Add($"[{slot.Module}.{slot.SubCategory}] {slot.Label}：{formatted}");
            }

            AddFixedTags(options.SuffixTags, item.Tags);
            item.PromptText = string.Join(", ", item.Tags);
            outcome.Items.Add(item);
        }

        return outcome;
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

    private static Dictionary<string, Dictionary<string, List<string>>> LoadBank(string path)
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
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<string>>>>(json)
                ?? throw new InvalidDataException("词库 JSON 内容为空。");

            _cachedPath = path;
            _cachedMtime = mtime;
            _cachedBank = parsed;
            return parsed;
        }
    }
}
