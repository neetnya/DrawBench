#!/usr/bin/env python3
"""ComfyCollage 对比图生成脚本。

支持从命令行直接使用，也支持被 WPF 界面以参数方式调用。
所有排版参数均可配置，默认值与旧版本保持一致。

示例：
    python gen_sheets_cli.py "D:\\图片"
    python gen_sheets_cli.py "D:\\图片" --tile 640 --rows 4 --fit cover
    python gen_sheets_cli.py "D:\\图片" --models "wai17,zuki,boleromix" --only-common
"""

from __future__ import annotations

import argparse
import colorsys
import json
import os
import re
import sys
from functools import lru_cache
from pathlib import Path

# WPF 重定向标准输出时强制使用 UTF-8；-u 参数同时负责关闭缓冲。
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace", line_buffering=True, write_through=True)
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace", line_buffering=True, write_through=True)

from PIL import Image, ImageDraw, ImageFont, ImageOps  # noqa: E402  (必须在编码设置之后)

IMAGE_EXTENSIONS = {
    ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"
}

BACKGROUND = (245, 245, 245)
TILE_BACKGROUND = (232, 232, 232)
BORDER_COLOR = (190, 190, 190)
MISSING_COLOR = (220, 220, 220)
MISSING_TEXT = (120, 120, 120)
ERROR_COLOR = (255, 220, 220)
ERROR_TEXT = (180, 0, 0)
LABEL_TEXT = (25, 25, 25)

# 当输出图过大时关闭 PNG 的 optimize（该选项会显著拖慢编码）。
OPTIMIZE_PIXEL_LIMIT = 40_000_000

FORMAT_EXTENSIONS = {"png": ".png", "jpg": ".jpg", "webp": ".webp"}


def natural_key(value: str):
    """让 2.png 排在 10.png 前面；用类型标签避免数字与字母直接比较。"""
    return [
        (0, int(part)) if part.isdigit() else (1, part.casefold())
        for part in re.split(r"(\d+)", value)
    ]


def clamp(value: int, low: int, high: int, label: str) -> int:
    if value < low:
        print(f"[WARN] {label} {value} 过小，已调整为 {low}。")
        return low
    if value > high:
        print(f"[WARN] {label} {value} 过大，已调整为 {high}。")
        return high
    return value


@lru_cache(maxsize=64)
def load_font(size: int, bold: bool):
    """优先使用带中文字形的字体，避免中文文件名显示成方块。"""
    names = (
        ["msyhbd.ttc", "msyh.ttc", "segoeuib.ttf", "arialbd.ttf", "arial.ttf"]
        if bold
        else ["msyh.ttc", "segoeui.ttf", "arial.ttf"]
    )
    fonts_dir = Path(r"C:\Windows\Fonts")
    for name in names:
        candidate = fonts_dir / name
        if not candidate.is_file():
            continue
        try:
            return ImageFont.truetype(str(candidate), size)
        except OSError:
            continue
    return ImageFont.load_default()


def flatten(image: Image.Image, background) -> Image.Image:
    """把带透明通道的图片合成到单元格底色上，避免出现黑边。"""
    if image.mode in ("RGBA", "LA") or (image.mode == "P" and "transparency" in image.info):
        rgba = image.convert("RGBA")
        base = Image.new("RGBA", rgba.size, tuple(background) + (255,))
        base.alpha_composite(rgba)
        return base.convert("RGB")
    return image.convert("RGB")


def prepare_tile(path: Path, tile: int, fit: str) -> Image.Image:
    """读取并排版单张图片，返回 tile × tile 的方块。"""
    with Image.open(path) as source:
        image = ImageOps.exif_transpose(source)
        image = flatten(image, TILE_BACKGROUND)
        if image.width <= 0 or image.height <= 0:
            raise ValueError("图片尺寸无效")

        cover = fit == "cover"
        if cover:
            scale = max(tile / image.width, tile / image.height)
        else:
            # 保持旧行为：只缩小不放大，避免小图被插值放大后糊掉。
            scale = min(tile / image.width, tile / image.height, 1.0)

        new_w = max(1, int(round(image.width * scale)))
        new_h = max(1, int(round(image.height * scale)))
        if (new_w, new_h) != (image.width, image.height):
            image = image.resize((new_w, new_h), Image.Resampling.LANCZOS)

        if cover:
            left = max(0, (new_w - tile) // 2)
            top = max(0, (new_h - tile) // 2)
            return image.crop((left, top, left + min(tile, new_w), top + min(tile, new_h)))

        canvas = Image.new("RGB", (tile, tile), TILE_BACKGROUND)
        canvas.paste(image, ((tile - new_w) // 2, (tile - new_h) // 2))
        return canvas


def draw_centered(draw: ImageDraw.ImageDraw, text: str, font, box, fill) -> None:
    x0, y0, x1, y1 = box
    bbox = draw.textbbox((0, 0), text, font=font)
    width = bbox[2] - bbox[0]
    height = bbox[3] - bbox[1]
    draw.text(
        (x0 + (x1 - x0 - width) // 2 - bbox[0], y0 + (y1 - y0 - height) // 2 - bbox[1]),
        text,
        font=font,
        fill=fill,
    )


def fit_text(draw: ImageDraw.ImageDraw, text: str, font, max_width: int) -> str:
    """超长文件名截断并加省略号。"""
    if max_width <= 0:
        return text
    if draw.textlength(text, font=font) <= max_width:
        return text
    ellipsis = "…"
    low, high = 0, len(text)
    while low < high:
        mid = (low + high + 1) // 2
        if draw.textlength(text[:mid] + ellipsis, font=font) <= max_width:
            low = mid
        else:
            high = mid - 1
    return text[:low] + ellipsis


def model_color(index: int, total: int):
    hue = index / max(total, 1)
    r, g, b = colorsys.hls_to_rgb(hue, 0.42, 0.55)
    return (int(r * 255), int(g * 255), int(b * 255))


def build_index(models, match: str):
    """建立 文件名 -> {模型名: 路径} 的索引。"""
    entries = {}
    for model in models:
        try:
            items = sorted(model.iterdir(), key=lambda p: natural_key(p.name))
        except OSError as exc:
            print(f"[WARN] 无法读取模型目录：{model}（{exc}）")
            continue
        for item in items:
            if not item.is_file() or item.suffix.casefold() not in IMAGE_EXTENSIONS:
                continue
            key = item.stem if match == "stem" else item.name
            bucket = entries.setdefault(key, {})
            if model.name not in bucket:
                bucket[model.name] = item
    return entries


def parse_args(argv):
    parser = argparse.ArgumentParser(add_help=True, description="生成多模型对比拼图")
    parser.add_argument("base", nargs="?", default="", help="图片根目录")
    parser.add_argument("--models", default="",
                        help="模型文件夹名，逗号分隔；顺序即列顺序；留空表示自动扫描")
    parser.add_argument("--tile", type=int, default=500, help="单元格边长（默认 500）")
    parser.add_argument("--rows", type=int, default=3,
                        help="每张拼图的行数（默认 3；0 表示全部放一张）")
    parser.add_argument("--gap", type=int, default=8, help="间距（默认 8）")
    parser.add_argument("--fit", choices=("contain", "cover"), default="contain",
                        help="contain=完整显示；cover=填满并居中裁切")
    parser.add_argument("--format", choices=("png", "jpg", "webp"), default="png",
                        help="输出格式（默认 png）")
    parser.add_argument("--quality", type=int, default=92, help="JPG/WebP 画质（默认 92）")
    parser.add_argument("--match", choices=("full", "stem"), default="full",
                        help="full=按完整文件名匹配；stem=忽略扩展名匹配")
    parser.add_argument("--only-common", action="store_true",
                        help="只保留所有模型都存在的文件")
    parser.add_argument("--keep-single", action="store_true",
                        help="保留只在一个模型里出现的图片（默认跳过，因为没有可对比对象）")
    parser.add_argument("--no-label", action="store_true", help="不显示文件名标签")
    parser.add_argument("--no-header", action="store_true", help="不显示模型标题")
    parser.add_argument("--out", default="", help="输出目录；相对路径基于根目录，留空为 _output")
    parser.add_argument("--prefix", default="sheet", help="输出文件名前缀（默认 sheet）")
    parser.add_argument("--clean", action="store_true", help="删除本次未生成的旧拼图")
    parser.add_argument("--dry-run", action="store_true", help="只打印计划，不生成图片")
    return parser.parse_args(argv)


def resolve_output(base: Path, out_arg: str) -> Path:
    if not out_arg.strip():
        return base / "_output"
    candidate = Path(out_arg).expanduser()
    return candidate if candidate.is_absolute() else base / candidate


def main(argv=None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)

    base = Path(args.base).expanduser() if args.base.strip() else None
    if base is None:
        print("[ERROR] 未提供图片根目录。")
        return 1
    if not base.is_dir():
        print(f"[ERROR] 目录不存在或不是有效目录：{base}")
        return 1

    tile = clamp(args.tile, 64, 4096, "单元格边长")
    gap = clamp(args.gap, 0, 200, "间距")
    rows_per_sheet = clamp(args.rows, 0, 500, "每张行数")
    quality = clamp(args.quality, 1, 100, "画质")
    show_header = not args.no_header
    show_labels = not args.no_label
    prefix = re.sub(r"[^\w\-.]", "_", args.prefix.strip()) or "sheet"

    # ---------- 模型列表 ----------
    if args.models.strip():
        wanted = [name.strip() for name in args.models.split(",") if name.strip()]
        models = []
        seen = set()
        for name in wanted:
            if name in seen:
                print(f"[INFO] 模型重复，已忽略：{name}")
                continue
            seen.add(name)
            candidate = base / name
            if candidate.is_dir():
                models.append(candidate)
            else:
                print(f"[WARN] 模型目录不存在，已跳过：{name}")
        if not models:
            print("[ERROR] 指定的模型目录全部不存在。")
            return 1
    else:
        models = sorted(
            [
                item for item in base.iterdir()
                if item.is_dir() and not item.name.startswith((".", "_"))
            ],
            key=lambda item: natural_key(item.name),
        )
        if not models:
            print("[ERROR] 根目录下没有找到模型文件夹。")
            return 1

    print(f"[INFO] 识别到 {len(models)} 个模型：{', '.join(m.name for m in models)}")

    # ---------- 文件索引 ----------
    entries = build_index(models, args.match)
    keys = sorted(entries.keys(), key=natural_key)
    if not keys:
        print("[ERROR] 模型文件夹中没有找到支持的图片文件。")
        return 1

    if args.only_common:
        common = [key for key in keys if len(entries[key]) == len(models)]
        skipped = len(keys) - len(common)
        keys = common
        if skipped:
            print(f"[INFO] 已过滤掉 {skipped} 个并非所有模型都存在的文件名。")
        if not keys:
            print("[ERROR] 没有任何文件名同时存在于所有模型文件夹中。")
            return 1

    # 只存在于一个模型的图片没有可对比对象，默认整行跳过（仅一个模型时无意义，不启用）。
    if not args.keep_single and len(models) > 1:
        comparable = [key for key in keys if len(entries[key]) > 1]
        skipped = len(keys) - len(comparable)
        if skipped:
            print(f"[INFO] 已跳过 {skipped} 个没有可对比对象的图片（仅存在于单个模型）。")
        keys = comparable
        if not keys:
            print("[ERROR] 没有任何图片同时存在于两个及以上模型，无法生成对比图。")
            return 1

    total_cells = len(keys) * len(models)
    present_cells = sum(len(entries[key]) for key in keys)
    print(f"[INFO] 共识别到 {len(keys)} 个不同图片文件名，预计 {total_cells} 个格子"
          f"（缺失 {total_cells - present_cells} 个）。")

    output_dir = resolve_output(base, args.out)
    if not args.dry_run:
        try:
            output_dir.mkdir(parents=True, exist_ok=True)
        except OSError as exc:
            print(f"[ERROR] 无法创建输出目录：{output_dir}（{exc}）")
            print("[HINT] 常见原因：安全软件拦截 Python 写入、目录权限不足或磁盘只读。")
            print(f"[HINT] 当前用户：{os.environ.get('USERNAME', '未知')}；Python：{sys.executable}")
            print("[HINT] 可尝试：把 python.exe 加入安全软件白名单，或以管理员身份运行本程序。")
            return 1
        print(f"[INFO] 输出目录：{output_dir}")

    # ---------- 版面尺寸 ----------
    title_size = max(11, int(round(tile * 0.045)))
    label_size = max(10, int(round(tile * 0.032)))
    title_font = load_font(title_size, True)
    label_font = load_font(label_size, False)
    header_height = title_size + 14 if show_header else 0
    label_height = label_size + 10 if show_labels else 0

    width = gap * (len(models) + 1) + len(models) * tile

    if rows_per_sheet <= 0:
        groups = [keys]
        print(f"[INFO] 行数设为 0，全部 {len(keys)} 行将放在同一张长图中。")
    else:
        groups = [keys[i:i + rows_per_sheet] for i in range(0, len(keys), rows_per_sheet)]

    colors = {model.name: model_color(i, len(models)) for i, model in enumerate(models)}

    extension = FORMAT_EXTENSIONS[args.format]
    generated = set()
    missing_count = 0
    error_count = 0

    if args.dry_run:
        print("[INFO] 预览模式：只列出计划，不渲染、不写文件。")

    for group_index, group in enumerate(groups, start=1):
        height = header_height + len(group) * (label_height + tile + gap) + gap

        if args.dry_run:
            sheet_name = f"{prefix}_{group_index:02d}{extension}"
            print(f"[PROGRESS] {group_index-1}/{len(groups)}")
            print(f"[PLAN] 第 {group_index}/{len(groups)} 张 → {sheet_name}"
                  f"（{len(group)} 行 × {len(models)} 列，{width}×{height}）")
            for key in group:
                present = len(entries[key])
                flag = "" if present == len(models) else f"  [缺失 {len(models) - present}]"
                print(f"         - {key}{flag}")
            continue

        if width * height > 300_000_000:
            print(f"[WARN] 第 {group_index} 张尺寸达到 {width}×{height}，内存占用可能很高。")

        print(f"[PROGRESS] {group_index-1}/{len(groups)}")
        print(f"[PROCESS] 正在生成第 {group_index}/{len(groups)} 张对比图……")

        sheet = Image.new("RGB", (width, height), BACKGROUND)
        draw = ImageDraw.Draw(sheet)

        # 模型标题
        if show_header:
            for column, model in enumerate(models):
                x = gap + column * (tile + gap)
                color = colors[model.name]
                draw.rectangle((x, 0, x + tile, 4), fill=color)
                draw.text((x + 6, 7), fit_text(draw, model.name, title_font, tile - 12),
                          font=title_font, fill=color)

        y = header_height
        for key in group:
            if show_labels:
                draw.text((gap + 2, y + 3),
                          fit_text(draw, key, label_font, width - 2 * gap - 4),
                          font=label_font, fill=LABEL_TEXT)
                y += label_height

            for column, model in enumerate(models):
                x = gap + column * (tile + gap)
                image_path = entries[key].get(model.name)

                if image_path is not None:
                    try:
                        sheet.paste(prepare_tile(image_path, tile, args.fit), (x, y))
                    except Exception as exc:  # noqa: BLE001  (单张失败不影响整体)
                        error_count += 1
                        draw.rectangle((x, y, x + tile - 1, y + tile - 1), fill=ERROR_COLOR)
                        draw_centered(draw, "Error", title_font,
                                      (x, y, x + tile, y + tile), ERROR_TEXT)
                        print(f"[WARN] 图片读取失败：{image_path}：{exc}")
                else:
                    missing_count += 1
                    draw.rectangle((x, y, x + tile - 1, y + tile - 1), fill=MISSING_COLOR)
                    draw_centered(draw, "Missing", title_font,
                                  (x, y, x + tile, y + tile), MISSING_TEXT)

                draw.rectangle((x, y, x + tile - 1, y + tile - 1), outline=BORDER_COLOR)

            y += tile + gap

        output_path = output_dir / f"{prefix}_{group_index:02d}{extension}"
        try:
            if args.format == "png":
                sheet.save(output_path, format="PNG",
                           optimize=width * height <= OPTIMIZE_PIXEL_LIMIT)
            elif args.format == "jpg":
                sheet.save(output_path, format="JPEG", quality=quality, subsampling=0)
            else:
                sheet.save(output_path, format="WEBP", quality=quality, method=4)
        except OSError as exc:
            print(f"[ERROR] 保存失败：{output_path}（{exc}）")
            return 1

        generated.add(output_path.resolve())
        print(f"[SUCCESS] 已保存：{output_path}")

    if args.dry_run:
        print(f"[DONE] 预览完成：共 {len(groups)} 张对比图，未写入任何文件。")
        return 0

    print(f"[PROGRESS] {len(groups)}/{len(groups)}")

    if args.clean:
        pattern = re.compile(
            rf"^{re.escape(prefix)}_\d+\.(png|jpg|webp)$", re.IGNORECASE
        )
        removed = 0
        for item in output_dir.iterdir():
            if item.is_file() and pattern.match(item.name) and item.resolve() not in generated:
                try:
                    item.unlink()
                    removed += 1
                    print(f"[INFO] 已删除旧图：{item.name}")
                except OSError as exc:
                    print(f"[WARN] 无法删除旧图：{item.name}（{exc}）")
        if removed:
            print(f"[INFO] 共清理 {removed} 个旧文件。")

    print(
        f"[DONE] 全部完成。共生成 {len(groups)} 张对比图；"
        f"缺失图片 {missing_count} 张；读取失败 {error_count} 张。"
    )
    print("[SUMMARY] " + json.dumps({
        "sheets": len(groups),
        "models": [m.name for m in models],
        "rows": len(keys),
        "missing": missing_count,
        "errors": error_count,
        "output": str(output_dir),
        "format": args.format,
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\n[ERROR] 用户中断任务。")
        sys.exit(130)
    except Exception as exc:  # noqa: BLE001
        print(f"[ERROR] 程序发生未处理异常：{exc}")
        sys.exit(1)
