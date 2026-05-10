#!/usr/bin/env python3
"""
One-time script to set the SheicobAnime watermark logo on the SeekStreaming player.

Usage:
    python scripts/set-player-logo.py --logo path/to/logo.png [--position top-left]

What it does:
  1. Opens the logo PNG and removes white background → transparent PNG
  2. Resizes to 200px wide (player-friendly size)
  3. Converts to base64
  4. GETs /api/v1/video/player to find the active player ID
  5. PATCHes the player with logo.asset + logo.position

Requires: pip install pillow requests
"""

import argparse
import base64
import io
import sys

import requests
from PIL import Image

API_KEY   = "9db0b40302002d160ce3172e"
BASE_URL  = "https://seekstreaming.com"
HEADERS   = {"api-token": API_KEY, "Content-Type": "application/json"}


# ── 1. Remove white background ────────────────────────────────────────────────

def remove_white_background(img: Image.Image, threshold: int = 240) -> Image.Image:
    """
    Converts near-white pixels to transparent.
    Works well for logos exported on a clean white background.
    """
    img = img.convert("RGBA")
    data = img.getdata()

    new_data = []
    for r, g, b, a in data:
        # If all channels are above threshold → transparent
        if r >= threshold and g >= threshold and b >= threshold:
            new_data.append((r, g, b, 0))
        else:
            new_data.append((r, g, b, a))

    img.putdata(new_data)
    return img


def prepare_logo(path: str, width: int = 200) -> str:
    """Opens logo, removes white BG, resizes, returns base64-encoded PNG."""
    img = Image.open(path)
    img = remove_white_background(img)

    # Keep aspect ratio
    ratio = width / img.width
    height = int(img.height * ratio)
    img = img.resize((width, height), Image.LANCZOS)

    buf = io.BytesIO()
    img.save(buf, format="PNG")
    buf.seek(0)
    return base64.b64encode(buf.read()).decode("utf-8")


# ── 2. Find player ────────────────────────────────────────────────────────────

def get_player_id() -> str:
    """Returns the default player ID (or first available)."""
    resp = requests.get(f"{BASE_URL}/api/v1/video/player", headers=HEADERS, timeout=15)
    resp.raise_for_status()
    body = resp.json()

    players = body.get("data", [])
    if not players:
        raise RuntimeError("No players found in SeekStreaming account")

    # Prefer the default player
    default = next((p for p in players if p.get("isDefault")), players[0])
    print(f"  Found player: id={default['id']}  name={default.get('name', '(unnamed)')}  default={default.get('isDefault')}")
    return default["id"]


# ── 3. Patch player logo ──────────────────────────────────────────────────────

def set_player_logo(player_id: str, asset_b64: str, position: str) -> None:
    payload = {
        "logo": {
            "position": position,
            "asset": asset_b64,
        }
    }
    resp = requests.patch(
        f"{BASE_URL}/api/v1/video/player/{player_id}",
        headers=HEADERS,
        json=payload,
        timeout=30,
    )
    if resp.status_code == 204:
        print(f"  Player {player_id} updated — logo set at '{position}'")
    else:
        print(f"  ERROR {resp.status_code}: {resp.text}")
        sys.exit(1)


# ── Main ──────────────────────────────────────────────────────────────────────

def main() -> None:
    parser = argparse.ArgumentParser(description="Set SheicobAnime logo on SeekStreaming player")
    parser.add_argument("--logo", required=True, help="Path to the logo PNG file")
    parser.add_argument("--position", default="top-left",
                        choices=["top-left", "top-right", "bottom-left", "bottom-right", "control-bar", "hidden"],
                        help="Watermark position (default: top-left)")
    parser.add_argument("--width", type=int, default=200,
                        help="Logo width in pixels after resize (default: 200)")
    args = parser.parse_args()

    print(f"[1/3] Processing logo: {args.logo}")
    b64 = prepare_logo(args.logo, width=args.width)
    print(f"      White background removed, resized to {args.width}px wide, base64 length={len(b64)}")

    print("[2/3] Finding player...")
    player_id = get_player_id()

    print(f"[3/3] Setting logo at position '{args.position}'...")
    set_player_logo(player_id, b64, args.position)

    print("\nDone! The watermark will appear on all videos in this player automatically.")
    print("To move it later, run this script again with --position <new-position>")
    print("To hide it, run with --position hidden")


if __name__ == "__main__":
    main()
