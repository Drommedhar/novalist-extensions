"""Compare original and MLX safetensors, allowing only Conv1d axis changes."""
import argparse
import json
from pathlib import Path
import struct

import numpy as np


def compare(original, converted):
    def header(file):
        size = struct.unpack("<Q", file.read(8))[0]
        values = json.loads(file.read(size))
        values.pop("__metadata__", None)
        return 8 + size, values

    with original.open("rb") as left, converted.open("rb") as right:
        left_base, left_header = header(left)
        right_base, right_header = header(right)
        failures = []
        transposed = 0
        verified = 0
        for name, a in left_header.items():
            b = right_header.get(name)
            if b is None or a["dtype"] != b["dtype"]:
                failures.append(name)
                continue
            left.seek(left_base + a["data_offsets"][0])
            right.seek(right_base + b["data_offsets"][0])
            original_bytes = left.read(a["data_offsets"][1] - a["data_offsets"][0])
            converted_bytes = right.read(b["data_offsets"][1] - b["data_offsets"][0])
            if a["shape"] == b["shape"] and original_bytes == converted_bytes:
                verified += 1
                continue
            shape = a["shape"]
            if len(shape) == 3 and [shape[0], shape[2], shape[1]] == b["shape"]:
                itemsize = len(original_bytes) // int(np.prod(shape))
                rearranged = np.frombuffer(original_bytes, dtype=f"u{itemsize}").reshape(shape).transpose(0, 2, 1).tobytes()
                if rearranged == converted_bytes:
                    verified += 1
                    transposed += 1
                    continue
            failures.append(name)
        return dict(verified=verified, transposed=transposed, failures=failures,
                    extra_keys=sorted(set(right_header) - set(left_header)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--original", type=Path, required=True)
    parser.add_argument("--mlx", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    rows = {name: compare(args.original/name, args.mlx/name) for name in
            ("model.safetensors", "speech_tokenizer/model.safetensors")}
    args.output.write_text(json.dumps(rows, indent=2) + "\n")
    print(json.dumps(rows), flush=True)
    assert all(not r["failures"] and not r["extra_keys"] for r in rows.values())


if __name__ == "__main__":
    main()
