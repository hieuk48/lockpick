r"""Static cross-mapping of addresses between two builds of the same game (research tool; needs capstone and pefile).

For every instruction in build A that uses a target address (as a memory displacement or an immediate), take the
surrounding instructions, mask absolute addresses and relative branch offsets, and look for exactly that code in
build B. A unique match gives the address build B uses at the same spot. Each code site is one vote; a mapping is
only evidence when the sites agree.

    python tools/re1/xmap.py A.exe B.exe targets.txt      # lines "name 0xADDR [0xLENGTH]"
    python tools/re1/xmap.py --code A.exe B.exe code.txt  # lines "name 0xADDR": instruction addresses (anchors)
"""
from __future__ import annotations

import re
import sys
from collections import Counter
from dataclasses import dataclass, field

import capstone
import pefile
from capstone import x86


@dataclass
class Image:
    path: str
    text_start: int
    text: bytes
    low: int
    high: int
    instructions: list = field(default_factory=list)
    index: dict = field(default_factory=dict)


def load(path: str, disassemble: bool = True) -> Image:
    pe = pefile.PE(path)
    base = pe.OPTIONAL_HEADER.ImageBase
    section = next(s for s in pe.sections if s.Characteristics & 0x20000000)
    data = section.get_data()[: section.Misc_VirtualSize]
    image = Image(path, base + section.VirtualAddress, data, base, base + pe.OPTIONAL_HEADER.SizeOfImage)
    if disassemble:
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
        md.detail = True
        md.skipdata = True
        image.instructions = list(md.disasm(data, image.text_start))
        image.index = {ins.address: i for i, ins in enumerate(image.instructions)}
    return image


def address_operands(ins, low: int, high: int) -> list[tuple[int, int]]:
    """(byte offset in the instruction, value) of each absolute address operand inside [low, high)."""
    if ins.id == 0:
        return []
    out = []
    raw = bytes(ins.bytes)
    for op in ins.operands:
        if op.type == x86.X86_OP_MEM and op.mem.disp:
            value = op.mem.disp & 0xFFFFFFFF
        elif op.type == x86.X86_OP_IMM:
            value = op.imm & 0xFFFFFFFF
        else:
            continue
        if low <= value < high and (at := raw.find(value.to_bytes(4, "little"))) >= 0:
            out.append((at, value))
    return out


def _relative_branch(ins) -> bool:
    return ins.id != 0 and (ins.group(x86.X86_GRP_JUMP) or ins.group(x86.X86_GRP_CALL)) \
        and bool(ins.operands) and ins.operands[0].type == x86.X86_OP_IMM


def pattern(window, low: int, high: int) -> bytes:
    parts = []
    for ins in window:
        raw = bytes(ins.bytes)
        keep = [True] * len(raw)
        if _relative_branch(ins):
            n = 4 if len(raw) >= 5 else 1
            keep[len(raw) - n:] = [False] * n
        for at, _ in address_operands(ins, low, high):
            keep[at:at + 4] = [False] * 4
        parts.append(b"".join(re.escape(bytes([b])) if k else b"." for b, k in zip(raw, keep)))
    return b"".join(parts)


def _unique(a: Image, b: Image, i: int, sizes=(6, 10, 16)):
    """(start of the matched window in B's text, A's window start index) for the smallest unique window, or None."""
    for n in sizes:
        lo, hi = max(0, i - n), min(len(a.instructions), i + n + 1)
        matches = [m.start() for m in re.finditer(pattern(a.instructions[lo:hi], a.low, a.high), b.text, re.DOTALL)]
        if len(matches) == 1:
            return matches[0], lo
        if not matches:
            return None
    return None


@dataclass
class Mapping:
    name: str
    address: int
    sites: int
    votes: Counter

    @property
    def delta(self) -> int | None:
        return self.votes.most_common(1)[0][0] if self.votes else None

    @property
    def agreeing(self) -> int:
        return self.votes.most_common(1)[0][1] if self.votes else 0

    @property
    def disagreeing(self) -> int:
        return sum(self.votes.values()) - self.agreeing

    @property
    def target(self) -> int | None:
        return None if self.delta is None else (self.address + self.delta) & 0xFFFFFFFF


def map_data(a: Image, b: Image, targets: list[tuple[str, int, int]]) -> list[Mapping]:
    """Votes for every target (name, address, length): references into [address, address+length) count."""
    refs: dict[str, list[tuple[int, int, int]]] = {name: [] for name, _, _ in targets}
    for i, ins in enumerate(a.instructions):
        for at, value in address_operands(ins, a.low, a.high):
            for name, address, length in targets:
                if address <= value < address + length:
                    refs[name].append((i, at, value))
    result = []
    for name, address, _ in targets:
        votes = Counter()
        for i, at, value in refs[name]:
            if (found := _unique(a, b, i)) is None:
                continue
            start, lo = found
            offset = sum(len(w.bytes) for w in a.instructions[lo:i]) + at
            mapped = int.from_bytes(b.text[start + offset:start + offset + 4], "little")
            votes[mapped - value] += 1
        result.append(Mapping(name, address, len(refs[name]), votes))
    return result


def map_code(a: Image, b: Image, address: int) -> tuple[int, bytes] | None:
    """The instruction in B matching A's instruction at <address>: (address, bytes), or None."""
    i = a.index.get(address)
    if i is None or (found := _unique(a, b, i, sizes=(8, 14, 22, 4, 2))) is None:
        return None
    start, lo = found
    offset = sum(len(w.bytes) for w in a.instructions[lo:i])
    size = len(a.instructions[i].bytes)
    return b.text_start + start + offset, b.text[start + offset:start + offset + size]


def _main(argv: list[str]) -> int:
    code = argv[:1] == ["--code"]
    a_path, b_path, list_path = argv[1:4] if code else argv[:3]
    a, b = load(a_path), load(b_path)
    rows = []
    for line in open(list_path, encoding="utf-8"):
        parts = line.split("#")[0].split()
        if len(parts) >= 2:
            rows.append((parts[0], int(parts[1], 16), int(parts[2], 16) if len(parts) > 2 else 1))
    if code:
        for name, address, _ in rows:
            found = map_code(a, b, address)
            print(f"{name}: 0x{address:X} -> " + (f"0x{found[0]:X} {found[1].hex(' ').upper()}" if found else "no unique match"))
        return 0
    for m in map_data(a, b, rows):
        if m.delta is None:
            print(f"{m.name:22} 0x{m.address:X}  sites {m.sites:3}  no unique match")
        else:
            print(f"{m.name:22} 0x{m.address:X} -> 0x{m.target:X}  delta {m.delta:+#x}  {m.agreeing}/{m.sites} sites agree"
                  + (f", {m.disagreeing} disagree {dict(m.votes.most_common()[1:4])}" if m.disagreeing else ""))
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
