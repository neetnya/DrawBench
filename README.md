# DrawBench

把五个围绕 ComfyUI 出图的小工具装进一个窗口：**抽卡 → 批量提交 → 参数读取 → 反推标签 → 对比图**。

原来它们是五个独立程序（PromptGacha / ComfyBatchTool / ImageTagReader / WD14Tagger / ComfyCollage），
各自带一份界面、一份配置、一份 Python 探测逻辑，互不相通。现在是一个程序、一套设置、一条流水线，
五页界面统一到同一套配色与控件样式（`src/DrawBench.App/Themes/Theme.xaml`）。

## 界面

左侧竖排导航，右侧是当前模块：

| 模块 | 做什么 | 需要 |
| --- | --- | --- |
| 抽卡 | 抽角色特征 + 服装/姿势/表情/场景，拼成提示词 | Python 3.8+ |
| 批量提交 | 把提示词队列写进 ComfyUI 工作流并批量排队 | ComfyUI 在跑 |
| 参数读取 | 读图片里嵌的提示词与生成参数（ComfyUI / WebUI / NovelAI） | — |
| 反推标签 | WD14 本地 ONNX 反推标签 | 自备 onnx + csv 模型 |
| 对比图 | 多模型同名出图横向拼成对比图 | Python 3.8+ 与 Pillow |

底部状态栏常驻显示 Python 与 ComfyUI 的状态。

### 模块之间能互通

- 抽卡结果 → 「发送到队列」直接进批量提交的正向队列
- 参数读取到的正/负向提示词 → 一键送进队列（复现别人的图）
- 反推出来的标签 → 「复制标签」旁边的「发送到队列」送进同一个队列
- 任一处选的图片目录会共享给其他模块（参数读取、反推、对比图都认它）
- Python 解释器在「设置」里全局统一；ComfyUI 地址只属于批量提交，在它自己页面顶部改

## 目录约定（便携）

整个文件夹复制走即可换机器，删掉 `data\` 就是恢复出厂：

```
DrawBench.exe
api\         ComfyUI 工作流 JSON（批量提交从这里读）
engine\      抽卡引擎（anima_cli.py + presets）
scripts\     拼图脚本（gen_sheets_cli.py）
models\      反推模型（xxx.onnx + 同名 xxx.csv）
data\        全部配置与结果
  settings.json   统一配置（按模块分段）
  results.csv     反推结果
  app.log         运行日志
```

开发期程序在 `build\` 下跑，会向上找到仓库里带 `api\`/`engine\` 的目录当数据根，
所以源码树里的脚本和发布包行为一致。

## 编译

需要 .NET 9 SDK。

```bat
dotnet build
```

调试运行产物：`build\DrawBench.App\Debug\net9.0-windows\win-x64\DrawBench.exe`

发布（x64 自包含单目录）：

```bat
dotnet publish src\DrawBench.App\DrawBench.App.csproj -c Release -r win-x64 --self-contained true -o dist
```

发布后自己补两样东西（仓库里不含，版权与体积都归原作者）：

- `dist\models\`：把 `xxx.onnx` 与同名 `xxx.csv` 放进去（[wd-vit-tagger-v3](https://huggingface.co/SmilingWolf/wd-vit-tagger-v3/tree/main)）
- `dist\api\`：把 ComfyUI 导出的 API 格式工作流 JSON 放进去

`engine\` 和 `scripts\` 会随生成自动拷过去。

## 命令行

保留原来 WD14 的批量模式：

```bat
DrawBench.exe tagger --cli D:\图片 --threshold 0.35 --char-threshold 0.85
DrawBench.exe --cli D:\图片                      旧写法，等同 tagger
```

## 开发期自检

```bat
DrawBench.exe --selftest D:\图片   解析元数据 + 验证跨模块投递
DrawBench.exe --render shots        依次切模块截图后退出
```

`--render` 会在无人值守时把五个模块各截一张图，用来检查界面是否正常渲染。

## 工程结构

```
src/
  DrawBench.Core/       公共层：路径、配置、Python、ComfyUI 客户端、图片扫描、日志
  DrawBench.Modules/    五个模块，各占一个文件夹；只写布局，颜色与控件样式统一取自全局主题
  DrawBench.App/        外壳：左侧导航、内容区、状态栏、设置页、命令行入口，以及 Themes/Theme.xaml（唯一主题）
```

模块通过 `Core.IToolModule` 契约被外壳反射加载，加模块只需往 Modules 里放一个类。

## 第三方许可

- 抽卡模块的引擎与角色档案来自 [Comfyui-Anima-Wardrobe](https://github.com/lingdongbudong/Comfyui-Anima-Wardrobe)（MIT）
- 反推模块由 [ComfyUI-WD14-Tagger](https://github.com/pythongosssss/ComfyUI-WD14-Tagger) 改造（MIT）

详见 `THIRD-PARTY-NOTICES.md`。
