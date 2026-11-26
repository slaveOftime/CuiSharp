#!/usr/bin/env python3
"""Build the browser gallery's OFL text fallbacks from Noto Sans CJK SC or Noto Emoji.

Requires fonttools==4.60.1. See the browser gallery README for sources and checksums.
"""

import argparse
from pathlib import Path

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont


def gb2312() -> set[int]:
    # GB2312 includes the common simplified Han characters and their punctuation.
    characters = set()
    for lead in range(0xA1, 0xF8):
        for trail in range(0xA1, 0xFF):
            try:
                characters.add(ord(bytes([lead, trail]).decode("gb2312")))
            except UnicodeDecodeError:
                continue

    return characters


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--emoji", action="store_true", help="build the static Noto Emoji text fallback")
    args = parser.parse_args()

    with TTFont(args.source, recalcTimestamp=False) as font:
        if args.emoji:
            instantiateVariableFont(font, {"wght": 400}, inplace=True)
            names = {1: "Gallery Emoji", 3: "GalleryEmoji-Regular-3.002",
                     4: "Gallery Emoji Regular", 6: "GalleryEmoji-Regular"}
            # The browser build's CUI text renderer uses mapped glyph outlines, not ligatures.
            codepoints = font.getBestCmap()
        else:
            names = {1: "Gallery Sans SC", 3: "GallerySansSC-Regular-2.004",
                     4: "Gallery Sans SC", 6: "GallerySansSC-Regular"}
            codepoints = gb2312()
            cff = font["CFF "].cff
            cff.fontNames[0] = names[6]
            cff[0].FullName = cff[0].FamilyName = names[1]

        for record in font["name"].names:
            if record.nameID in names:
                record.string = names[record.nameID]

        options = subset.Options(layout_features=[], name_IDs=[0, 1, 2, 3, 4, 5, 6, 13, 14])
        subsetter = subset.Subsetter(options=options)
        subsetter.populate(unicodes=codepoints)
        subsetter.subset(font)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        font.save(args.output)


if __name__ == "__main__":
    main()
