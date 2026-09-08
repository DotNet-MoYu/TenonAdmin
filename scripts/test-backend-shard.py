#!/usr/bin/env python3
"""按 VSTest 实际发现的方法名分片；Theory 的全部数据行留在同一片。"""

import argparse
import os
from pathlib import Path
import re
import subprocess


def select_tests(names, shard, count):
    if not 1 <= shard <= count:
        raise ValueError("分片编号必须在 1..count 内")
    names = sorted(set(names))
    # 精确匹配，拒绝过滤表达式字符；发现异常时不能退回无过滤的全量执行。
    if not names or any(not re.fullmatch(r"[\w.`+]+", name) for name in names):
        raise ValueError("未发现测试或测试名不能安全组成 VSTest 过滤器")
    selected = names[shard - 1::count]
    if not selected:
        raise ValueError("空分片，请减少分片数")
    return selected


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--shard", type=int, required=True)
    parser.add_argument("--count", type=int, required=True)
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("assemblies", nargs="+")
    args = parser.parse_args()
    args.results.mkdir(parents=True, exist_ok=True)
    discovered = args.results.resolve() / "discovered.txt"
    # 删除本次输出目标，避免发现失败后误用旧清单。
    discovered.unlink(missing_ok=True)
    command = ["dotnet", "vstest", *args.assemblies]
    discovery = command + ["/ListFullyQualifiedTests", f"/ListTestsTargetPath:{discovered}"]
    if original_filter := os.environ.get("TEST_FILTER"):
        discovery.append(f"/TestCaseFilter:{original_filter}")
    subprocess.run(discovery, check=True)
    selected = select_tests(discovered.read_text(encoding="utf-8-sig").splitlines(),
                            args.shard, args.count)
    (args.results / "selected.txt").write_text("\n".join(selected) + "\n", encoding="utf-8")
    print(f"Shard {args.shard}/{args.count}: {len(selected)} test methods", flush=True)
    subprocess.run(command + [
        "/TestCaseFilter:" + "|".join(f"FullyQualifiedName={name}" for name in selected),
        "/Logger:trx;LogFileName=tests.trx", f"/ResultsDirectory:{args.results.resolve()}",
    ], check=True)


if __name__ == "__main__":
    main()
