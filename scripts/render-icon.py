#!/usr/bin/env python3
"""Regenerate the embedded PNG from assets/rowfinch.svg (developer tool only)."""
from pathlib import Path
import gi
import warnings

gi.require_version("Rsvg", "2.0")
from gi.repository import Rsvg

root = Path(__file__).resolve().parents[1]
image = Rsvg.Handle.new_from_file(str(root / "assets/rowfinch.svg"))
destination = root / "src/Rowfinch.Desktop/Assets/rowfinch.png"
destination.parent.mkdir(parents=True, exist_ok=True)
# The source has a fixed 256px viewport. This API avoids requiring the optional
# Python Cairo bridge just to regenerate one checked-in development asset.
with warnings.catch_warnings():
    warnings.simplefilter("ignore", DeprecationWarning)
    image.get_pixbuf().savev(str(destination), "png", [], [])
print(destination)
