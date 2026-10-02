"""Token ids upstream's text encoder feeds FLAN-T5 for any prompt: unimate/models/text_encoder/t5.py tokenizes with
``T5Tokenizer.from_pretrained('google/flan-t5-base')`` (normalize_text off), so the port's tokenizer must give the
same ids for whatever a user types. Prompts: every caption in UniML3D (Mixamo, Objaverse, Truebones) plus
user-style and adversarial text (case, digits, punctuation, quotes, whitespace, accents, symbols, emoji, CJK).

Run: D:\\99-scratch\\unimate\\.venv\\Scripts\\python.exe make_tokenizer_fixture.py <repo root> <captions dir>
(<captions dir> holds caps_{mixamo,objaverse,truebones}.json: UniML3D export/<dataset>/motion_captions.json)
"""
import json
import os
import sys

import transformers
from transformers import T5Tokenizer

ROOT, CAPS = sys.argv[1], sys.argv[2]
SNAPSHOT = r"D:\99-scratch\hf-cache\hub\models--google--flan-t5-base\snapshots\7bcac572ce56db69c1ea7c8af255c5d7c9672fc2"
tok = T5Tokenizer.from_pretrained(SNAPSHOT)

prompts = []
for d in ("mixamo", "objaverse", "truebones"):
    prompts += json.load(open(os.path.join(CAPS, f"caps_{d}.json"), encoding="utf-8")).values()
prompts += [
    "a person does a frontflip", "a person runs and then performs a front flip", "walk forward", "WALK FORWARD!!!",
    "a tired man slowly walk home", "jump 3 times", "spin 360 degrees", "walk 2.5 meters, then stop",
    "a person's arm waves", "don't move", "\"punch\" forward", "kick (high) - left leg", "walk/run", "a_b c-d",
    "  leading and trailing spaces  ", "multiple   inner    spaces", "tab\tseparated", "line\nbreak", "crlf\r\nline",
    "café dancer", "naïve résumé jump", "Übermensch läuft", "ﬁne ligature", "full-width ｗａｌｋ", "１２３",
    "emoji 🕺 dance", "🔥🔥", "日本語で歩く", "привет мир", "a—b – c", "ellipsis… then", "“curly quotes”", "‘single’",
    "zero\u200bwidth", "non\u00a0breaking space", "math × ÷ ± °", "~!@#$%^&*()_+{}|:<>?`-=[]\\;',./",
    "", " ", ".", "a", "An object walks forward.", "an object WALKS forward", "x" * 300,
    "a very long prompt " * 40, "Spider-Man swings; then lands.", "2x speed run", "hello\u0000world", "tab\t\tdouble",
    # combining marks, other scripts, emoji sequences, invisible and odd whitespace, compatibility forms
    "café è ñ", "한국어 걷기", "مرحبا يمشي", "שלום", "हिन्दी चलना", "ภาษาไทย", "Ελληνικά", "İstanbul ß ẞ",
    "family 👨‍👩‍👧 wave", "flag 🇺🇸 run", "thumbs 👍🏽 up", "keycap 1️⃣", "soft­hyphen", "﻿bom start",
    "en em thin hair ", "ideographic　space", "line sep para", "nel\u0085next", "v\u000btab f\u000cfeed",
    "ctrl\u0001\u0002\u001f chars", "del\u007fchar", "𝐁𝐨𝐥𝐝 𝑖𝑡𝑎𝑙𝑖𝑐", "① ② ⑩", "x² y³ ½ ¼ ⅞", "Ⅻ ⅷ", "ｆｕｌｌ，ｗｉｄｔｈ！", "ﾊﾝｶｸ ｶﾀｶﾅ",
    "…‥", "™ © ®", "℃ ℉ Å", "ﬀ ﬃ ﬆ", "⁠word⁠joiner", "‌non‍joiner", "‮right to left‬",
    "tabs\t\t\tand  spaces \t mixed", "\r\n\r\n", "\t", "a\u0000", "\u0000", "é́́́́ stacked",
    "z̵̴̵̶̡̢̧̨ zalgo", "🏳️‍🌈", "👩🏽‍🚀", "ǅ ǈ ǋ", "ŉ ſ", "Ⓐⓑ", "㍿ ㌀", "ﷺ",
    "a▁b", "▁x", "▁▁x", "x▁", "▁", "▁ ▁", "a b᠎c d e", "x  y",
]
seen, cases = set(), []
for p in prompts:
    if p in seen:
        continue
    seen.add(p)
    cases.append([p, tok(p)["input_ids"]])
out = os.path.join(ROOT, "dev/Tests/fixtures/upstream_text/t5_token_ids.json")
os.makedirs(os.path.dirname(out), exist_ok=True)
json.dump({"tokenizer": f"{type(tok).__name__} (transformers {transformers.__version__})", "cases": cases},
          open(out, "w", encoding="utf-8"), ensure_ascii=False, separators=(",", ":"))
print(f"{len(cases)} prompts, {type(tok).__name__}, transformers {transformers.__version__}")
