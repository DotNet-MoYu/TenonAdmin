"""分片不能漏测、重测，或因错误输入退化成全量测试。"""

import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location(
    "shard", Path(__file__).with_name("test-backend-shard.py"))
shard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shard)


class ShardTests(unittest.TestCase):
    def test_partition_is_complete_disjoint_and_stable_with_theory_duplicates(self):
        names = [f"Tests.Class{i // 3}.Method{i}" for i in range(23)]
        inputs = names + [names[0], names[3]]
        parts = [shard.select_tests(inputs, i, 4) for i in range(1, 5)]
        flat = [name for part in parts for name in part]
        self.assertCountEqual(names, flat)
        self.assertEqual(len(flat), len(set(flat)))
        self.assertLessEqual(max(map(len, parts)) - min(map(len, parts)), 1)
        self.assertEqual(parts, [shard.select_tests(reversed(inputs), i, 4) for i in range(1, 5)])

    def test_invalid_or_empty_input_fails_closed(self):
        for names, index, count in [([], 1, 4), (["Tests.A"], 2, 4),
                                     (["Tests.A"], 0, 4), (["Tests.A"], 1, 0),
                                     (["Tests.A|FullyQualifiedName~"], 1, 1), ([""], 1, 1)]:
            with self.subTest(names=names, index=index, count=count):
                with self.assertRaises(ValueError):
                    shard.select_tests(names, index, count)


if __name__ == "__main__":
    unittest.main()
