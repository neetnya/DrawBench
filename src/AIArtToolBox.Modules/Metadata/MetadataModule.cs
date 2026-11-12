using System;
using AIArtToolBox.Core;
using AIArtToolBox.Modules.Metadata.Views;

namespace AIArtToolBox.Modules.Metadata;

/// <summary>参数读取模块：读 ComfyUI / WebUI / NovelAI 图片里的提示词与生成参数。</summary>
public sealed class MetadataModule : IToolModule
{
    private ImageView? _view;

    public string Id => ModuleIds.Metadata;
    public string Title => "参数读取";
    public string Description => "读取 AI 图片里嵌的提示词与生成参数（ComfyUI / WebUI / NovelAI）";
    public string Glyph => "\uE8B7";   // Photo

    public System.Windows.FrameworkElement CreateView() => _view ??= new ImageView();
}
