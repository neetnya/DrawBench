# 第三方组件与许可

DrawBench 自己写的代码与下列第三方组件一起工作。第三方部分按其原许可分发，
原许可全文放在 `third-party\` 目录，随程序一起发布。

## MIT

### Comfyui-Anima-Wardrobe

- 用途：抽卡模块的抽取引擎（`engine\anima_cli.py`、`engine\wardrobe_rules.py`）与角色/服装词库（`engine\presets\`）
- 来源：https://github.com/lingdongbudong/Comfyui-Anima-Wardrobe
- 版权：Copyright (c) 2026 lingdongbudong
- 许可全文：`third-party\LICENSE-anima-wardrobe.txt`

### ComfyUI-WD14-Tagger

- 用途：反推模块的推理流程与标签处理（还原自 ComfyUI 的 WD14 Tagger 节点实现）
- 来源：https://github.com/pythongosssss/ComfyUI-WD14-Tagger
- 版权：Copyright (c) 2024 pythongosssss
- 许可全文：`third-party\LICENSE-wd14-tagger.txt`

## NuGet 包

### Microsoft.ML.OnnxRuntime

- 用途：反推模块的 ONNX 推理
- 许可：MIT
- https://github.com/microsoft/onnxruntime

## 不含在仓库里的东西

- **WD14 模型**（`xxx.onnx` + `xxx.csv`，约 360 MB）：版权归原作者，请自行从
  https://huggingface.co/SmilingWolf/wd-vit-tagger-v3/tree/main 下载后放进 `models\`。
- **ComfyUI 工作流 JSON**：来自用户自己的 ComfyUI，放在 `api\`。

## 运行期依赖

- .NET 9（发布包已自包含）
- Python 3.8+：抽卡与对比图两个功能需要；对比图还需要 Pillow（`python -m pip install --upgrade Pillow`）
