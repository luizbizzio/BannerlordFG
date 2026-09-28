"""Catch block/brace regressions missed by older string-only tests.
Pygments tokenization skips comments, regex literals, verbatim C# strings.
"""
from pathlib import Path
from pygments import lex
from pygments.lexers import CSharpLexer
from pygments.token import Keyword, Punctuation

ROOT = Path(__file__).resolve().parents[1]


def test_balanced_csharp_blocks_and_catch_context():
    for path in (ROOT / 'src').rglob('*.cs'):
        blocks = []
        just_closed = None
        pending = None
        for kind, chunk in lex(path.read_text(encoding='utf-8-sig'), CSharpLexer()):
            if kind in Keyword and chunk in ('try', 'catch', 'finally'):
                if chunk in ('catch', 'finally'):
                    assert just_closed in ('try', 'catch'), (path.name, chunk, just_closed)
                pending = chunk
                just_closed = None
            if kind in Punctuation:
                for ch in chunk:
                    if ch == '{':
                        blocks.append(pending or 'block')
                        pending = None
                        just_closed = None
                    elif ch == '}':
                        assert blocks, (path.name, 'extra closing brace')
                        just_closed = blocks.pop()
        assert not blocks, (path.name, 'unclosed blocks', blocks)
