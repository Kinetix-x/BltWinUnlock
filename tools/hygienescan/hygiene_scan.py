"""发包前卫生扫描：包里不能有构建机的痕迹（docs/技术说明.md 第 8 节）。

要扫的串**不写死在脚本里** —— 那份名单本身就会成为泄密源（第一版就是这样：扫描器里写着作者的
真实账号、机器名与本地路径，然后它被提交进了公开仓库）。现在默认只给占位符样例，真实名单放在
同目录的 `patterns.txt`（**已在 .gitignore 里**），或用 `--patterns FILE` 指定。

patterns.txt 格式：每行 `待查文本 = 说明`，`#` 开头是注释。

    myaccount@example.com = 作者的账户地址
    DESKTOP-A1B2C3        = 构建机机器名

用法：

    python tools\\hygienescan\\hygiene_scan.py dist\\Btwu-1.0.0-win-x64
    python tools\\hygienescan\\hygiene_scan.py . --patterns my-patterns.txt
    # 退出码 0 = 干净；1 = 有命中（拿来当发布门禁就行）

规矩（每条都付过代价）：
  · **逐文件扫、绝不 `| head`** —— 第一版扫描就是这样“通过”的，命中行被 head 截掉了；
  · **UTF-8 与 UTF-16LE 各扫一遍** —— dll 里的字符串是 UTF-16，`grep` 对它完全是瞎的
    （`Btwu.Core.dll` 里那条 RSDS 构建路径就是这么漏出去的）；
  · **允许清单必须写明理由** —— 例如作者署名本来就该出现在「关于」页里。

⚠️ 检查器自己会骗人：先在**已知会失败的输入**上跑一次（造个含真实串的文件），确认它真的报错退出 ——
上面那条「扫描器自己带着账号」的教训就是从这里来的。
"""
import io
import os
import sys

DEFAULT_PATTERNS = [
    ("CHANGE-ME@example.com", "作者的账户地址 —— 换成你自己的"),
    ("YOUR-MACHINE-NAME", "构建机机器名 —— 换成你自己的"),
    ("YOUR-PROJECT-DIR", "构建机项目目录名 —— 换成你自己的"),
    (r"C:\Users\YOUR-NAME", "构建机用户目录 —— 换成你自己的"),
]

# 允许清单：相对路径 -> 理由。这些文件里出现命中不算失败。
ALLOWED = {
    "src/Btwu.Agent/Assets/alipay.jpg": "作者自愿公开的收款码图片",
}


def load_patterns(script_dir, cli_path):
    path = cli_path or os.path.join(script_dir, "patterns.txt")
    if not path or not os.path.exists(path):
        return list(DEFAULT_PATTERNS), f"（没有 {path or 'patterns.txt'}，用占位符名单，基本扫不出东西）"

    patterns = []
    for line in io.open(path, encoding="utf-8"):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        text, _, why = line.partition("=")
        patterns.append((text.strip(), why.strip() or "本地名单"))
    return patterns, f"（名单: {path}，{len(patterns)} 条）"


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else "."
    cli = None
    if "--patterns" in sys.argv and sys.argv.index("--patterns") + 1 < len(sys.argv):
        cli = sys.argv[sys.argv.index("--patterns") + 1]

    script_dir = os.path.dirname(os.path.abspath(__file__))
    patterns, source = load_patterns(script_dir, cli)
    print(f"扫描 {os.path.abspath(root)}{source}")

    files = 0
    total = 0
    hits = 0

    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in (".git", "node_modules")]
        for name in filenames:
            path = os.path.join(dirpath, name)
            rel = os.path.relpath(path, root).replace(os.sep, "/")
            files += 1
            try:
                data = io.open(path, "rb").read()
            except OSError as exc:
                print(f"  !! 读不了 {rel}: {exc}")
                hits += 1
                continue
            total += len(data)

            for text, why in patterns:
                for encoding, label in (("utf-8", "UTF-8"), ("utf-16-le", "UTF-16LE")):
                    needle = text.encode(encoding)
                    start = 0
                    while True:
                        at = data.find(needle, start)
                        if at < 0:
                            break
                        start = at + 1
                        if rel in ALLOWED:
                            print(f"  [允许] {rel} @0x{at:X} {label} {text}（{ALLOWED[rel]}）")
                            continue
                        hits += 1
                        lo, hi = max(0, at - 48), min(len(data), at + len(needle) + 48)
                        ctx = "".join(chr(b) if 32 <= b < 127 else "·" for b in data[lo:hi])
                        print(f"  [命中] {rel} @0x{at:X} {label} {text!r}（{why}）")
                        print(f"         …{ctx}…")

    print()
    print(f"扫描完成: {files} 个文件 / {total / 1024 / 1024:.1f} MB / 命中 {hits} 处")
    return 1 if hits else 0


if __name__ == "__main__":
    sys.exit(main())
