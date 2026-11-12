using System;
using DrawBench.Core;

namespace DrawBench.Modules.Gacha;

/// <summary>抽卡模块：抽「女性外观特征」和「服装/姿势/表情/场景」，拼成可直接出图的提示词。</summary>
public sealed class GachaModule : IToolModule
{
    private Views.GachaView? _view;

    public string Id => ModuleIds.Gacha;
    public string Title => "抽卡";
    public string Description => "抽角色特征与装扮，拼成可直接出图的提示词（需要本机 Python）";
    public string Glyph => "\uE8EC";   // Game

    public System.Windows.FrameworkElement CreateView() => _view ??= new Views.GachaView();
}
