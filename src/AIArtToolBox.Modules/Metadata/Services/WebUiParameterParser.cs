using System.Text.RegularExpressions;
using System.Globalization;
using AIArtToolBox.Modules.Metadata.Models;

namespace AIArtToolBox.Modules.Metadata.Services;

/// <summary>
/// Stable Diffusion WebUI / Forge / 各类衍生 UI 的 parameters 文本解析：
/// <code>
/// 正向提示词
/// Negative prompt: 负向提示词
/// Steps: 20, Sampler: Euler a, CFG scale: 7, Seed: 123, Size: 512x768, Model hash: abcd1234, Model: xxx
/// </code>
/// </summary>
public static class WebUiParameterParser
{
    private static readonly Regex StepLineRegex = new(@"^\s*Steps:\s*\d+", RegexOptions.Compiled);
    private static readonly Regex SizeRegex = new(@"(\d+)\s*[x×]\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ParamSplitRegex = new(@",\s*(?=[A-Za-z][A-Za-z0-9 _\-/]*:\s)", RegexOptions.Compiled);

    public static bool TryParse(string raw, GenerationParameters p)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');

        int stepLineIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (StepLineRegex.IsMatch(lines[i])) stepLineIndex = i;
        }

        if (stepLineIndex < 0 &&
            !text.Contains("Negative prompt:", StringComparison.Ordinal) &&
            !text.Contains("Model hash:", StringComparison.Ordinal) &&
            !text.Contains("CFG scale:", StringComparison.Ordinal))
        {
            return false;
        }

        string promptPart;
        string paramLine;
        if (stepLineIndex >= 0)
        {
            promptPart = string.Join("\n", lines.Take(stepLineIndex));
            paramLine = string.Join(" ", lines.Skip(stepLineIndex));
        }
        else
        {
            promptPart = text;
            paramLine = "";
        }

        int negativeIndex = promptPart.IndexOf("Negative prompt:", StringComparison.Ordinal);
        if (negativeIndex >= 0)
        {
            p.PositivePrompt = promptPart[..negativeIndex].Trim();
            p.NegativePrompt = promptPart[(negativeIndex + "Negative prompt:".Length)..].Trim();
        }
        else
        {
            p.PositivePrompt = promptPart.Trim();
        }

        foreach (var segment in ParamSplitRegex.Split(paramLine))
        {
            int colon = segment.IndexOf(':');
            if (colon <= 0) continue;

            string key = segment[..colon].Trim();
            string value = segment[(colon + 1)..].Trim().Trim('"');

            if (key.Length == 0 || value.Length == 0) continue;

            switch (key.ToLowerInvariant())
            {
                case "steps":
                    p.Steps = ParseInt(value);
                    break;
                case "sampler":
                    p.Sampler = value;
                    break;
                case "schedule type":
                case "scheduler":
                    p.Scheduler = value;
                    break;
                case "cfg scale":
                case "cfg":
                    p.CfgScale = ParseDouble(value);
                    break;
                case "seed":
                    if (long.TryParse(value, out var seed)) p.Seed = seed;
                    else p.Extra["Seed"] = value;
                    break;
                case "size":
                {
                    var m = SizeRegex.Match(value);
                    if (m.Success)
                    {
                        p.Width = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        p.Height = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    }
                    break;
                }
                case "model":
                    p.Model = value;
                    break;
                case "model hash":
                    p.ModelHash = value;
                    break;
                case "vae":
                    p.Vae = value;
                    break;
                case "clip skip":
                    p.ClipSkip = value;
                    break;
                case "denoising strength":
                    p.Denoise = ParseDouble(value);
                    break;
                default:
                    p.Extra[key] = value;
                    break;
            }
        }

        p.Source = "Stable Diffusion WebUI";
        return true;
    }

    private static int? ParseInt(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static double? ParseDouble(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
}
