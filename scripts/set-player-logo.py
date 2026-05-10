#!/usr/bin/env python3
"""
One-time script to set the SheicobAnime watermark logo on the SeekStreaming player.

Usage:
    python scripts/set-player-logo.py --logo path/to/logo.png [--position top-left]

What it does:
  1. Opens the logo PNG and removes white background → transparent PNG
  2. Resizes to 200px wide (player-friendly size)
  3. Uploads processed PNG to SeekStreaming asset storage (PUT /api/v1/user/file)
  4. GETs /api/v1/video/player to find the active player ID
  5. PATCHes the player with logo.url + logo.position

Requires: pip install pillow requests
"""

import argparse
import io
import sys

import requests
from PIL import Image

API_KEY   = "9db0b40302002d160ce3172e"
BASE_URL  = "https://seekstreaming.com"
JSON_HEADERS = {"api-token": API_KEY, "Content-Type": "application/json"}
UPLOAD_HEADERS = {"api-token": API_KEY}


# ── 1. Process logo ───────────────────────────────────────────────────────────

def process_logo(path: str, width: int = 200) -> bytes:
    """Opens logo, removes white BG, resizes, returns PNG bytes."""
    img = Image.open(path).convert("RGBA")
    data = img.getdata()
    new_data = [
        (r, g, b, 0) if r >= 240 and g >= 240 and b >= 240 else (r, g, b, a)
        for r, g, b, a in data
    ]
    img.putdata(new_data)

    ratio = width / img.width
    img = img.resize((width, int(img.height * ratio)), Image.LANCZOS)

    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()


# ── 2. Upload image to SeekStreaming asset storage ────────────────────────────

def upload_logo(png_bytes: bytes) -> str:
    """Uploads PNG bytes and returns the hosted asset URL."""
    resp = requests.put(
        f"{BASE_URL}/api/v1/user/file",
        headers=UPLOAD_HEADERS,
        files={"file": ("sheicob-logo.png", png_bytes, "image/png")},
        timeout=30,
    )
    if resp.status_code not in (200, 201):
        print(f"  ERROR uploading file {resp.status_code}: {resp.text}")
        sys.exit(1)
    url = resp.json().get("url", "")
    if not url:
        print(f"  ERROR: no URL in upload response: {resp.text}")
        sys.exit(1)
    return url


# ── 3. Find player ────────────────────────────────────────────────────────────

def get_player_id() -> str:
    """Returns the default player ID (or first available)."""
    resp = requests.get(f"{BASE_URL}/api/v1/video/player", headers=JSON_HEADERS, timeout=15)
    resp.raise_for_status()
    players = resp.json().get("data", [])
    if not players:
        raise RuntimeError("No players found in SeekStreaming account")
    default = next((p for p in players if p.get("isDefault")), players[0])
    print(f"  Found player: id={default['id']}  name={default.get('name', '(unnamed)')}")
    return default["id"]


# ── 4. Patch player logo ──────────────────────────────────────────────────────

def set_player_logo(player_id: str, logo_url: str, position: str) -> None:
    resp = requests.patch(
        f"{BASE_URL}/api/v1/video/player/{player_id}",
        headers=JSON_HEADERS,
        json={"logo": {"position": position, "url": logo_url}},
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

    print(f"[1/4] Processing logo: {args.logo}")
    png_bytes = process_logo(args.logo, width=args.width)
    print(f"      White background removed, resized to {args.width}px wide ({len(png_bytes)} bytes)")

    print("[2/4] Uploading logo to SeekStreaming assets...")
    logo_url = upload_logo(png_bytes)
    print(f"      Uploaded: {logo_url}")

    print("[3/4] Finding player...")
    player_id = get_player_id()

    print(f"[4/4] Setting logo at position '{args.position}'...")
    set_player_logo(player_id, logo_url, args.position)

    print("\nDone! The watermark will appear on all videos in this player automatically.")
    print("To move it: python scripts/set-player-logo.py --logo scripts/sheicob-logo.png --position <pos>")
    print("To hide it: python scripts/set-player-logo.py --logo scripts/sheicob-logo.png --position hidden")


if __name__ == "__main__":
    main()
