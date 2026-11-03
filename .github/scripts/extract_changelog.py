"""从 CHANGELOG.md 抽取指定版本的正文，供 GitHub Release 使用。

用法：python3 extract_changelog.py v2.2.0
输出 release-body.md，并把版本描述写入 $GITHUB_OUTPUT 的 desc 字段。
"""

import os
import pathlib
import re
import sys


def main() -> int:
    tag = sys.argv[1] if len(sys.argv) > 1 else os.environ.get("GITHUB_REF_NAME", "")
    if not tag:
        print("缺少 tag 参数", file=sys.stderr)
        return 1

    ver = tag[1:] if tag.startswith("v") else tag
    text = pathlib.Path("CHANGELOG.md").read_text(encoding="utf-8")

    pattern = r"## \[v" + re.escape(ver) + r"\][^\n]*\n(.*?)(?=\n---\n|\n## \[|\Z)"
    match = re.search(pattern, text, re.S)
    if match:
        body = match.group(1).strip()
    else:
        body = f"{tag}：该版本未在 CHANGELOG.md 中记录。"
        print(f"警告：CHANGELOG.md 中找不到 v{ver} 章节", file=sys.stderr)

    pathlib.Path("release-body.md").write_text(body + "\n", encoding="utf-8")
    print(f"版本 {ver}：正文 {len(body)} 字符")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
