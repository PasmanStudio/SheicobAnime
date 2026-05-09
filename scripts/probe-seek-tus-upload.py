"""
Proof-of-concept: download MP4 from mp4upload and upload to SeekStreaming via tus.

Flow:
  1. Resolve mp4upload embed → direct .mp4 URL
  2. GET /api/v1/video/upload → tus endpoint + accessToken
  3. Stream-download the MP4 while uploading via tus (no temp file)

Usage: python scripts/probe-seek-tus-upload.py
"""
import re, base64, os, requests

SEEK_KEY  = "9db0b40302002d160ce3172e"
EMBED_URL = "https://www.mp4upload.com/embed-u655v52bzp7e.html"
UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36"

# ── Step 1: resolve mp4upload → direct MP4 URL ──────────────────────────────
print("Step 1: resolving mp4upload embed...")
r = requests.get(EMBED_URL, headers={"User-Agent": UA, "Referer": "https://www.mp4upload.com/"}, timeout=15)
r.raise_for_status()

# Direct pattern (no packed JS on mp4upload)
m = re.search(r'player\.src\s*\(\s*\{[^}]*?src\s*:\s*["\']( ?https?://[^"\']+\.mp4[^"\']*)["\']', r.text, re.DOTALL)
mp4_url = m.group(1).strip() if m else None

# Fallback: any a*.mp4upload.com URL
if not mp4_url:
    m = re.search(r'(https?://[a-z0-9]+\.mp4upload\.com[^"\'<\s]+\.mp4[^"\'<\s]*)', r.text)
    mp4_url = m.group(1).strip() if m else None

if not mp4_url:
    print("ERROR: could not extract MP4 URL from embed page")
    print("HTML snippet:", r.text[:500])
    exit(1)

print(f"  MP4 URL: {mp4_url[:100]}...")

# ── Step 2: get tus credentials from SeekStreaming ──────────────────────────
print("Step 2: getting tus credentials...")
creds = requests.get(
    "https://seekstreaming.com/api/v1/video/upload",
    headers={"api-token": SEEK_KEY},
    timeout=10
).json()
tus_url     = creds["tusUrl"]
access_token = creds["accessToken"]
print(f"  tusUrl: {tus_url}")

# ── Step 3: probe MP4 size via HEAD ─────────────────────────────────────────
print("Step 3: checking MP4 file size...")
head = requests.head(mp4_url, headers={"User-Agent": UA, "Referer": "https://www.mp4upload.com/"}, timeout=10, allow_redirects=True)
file_size = int(head.headers.get("Content-Length", 0))
print(f"  File size: {file_size:,} bytes ({file_size/1024/1024:.1f} MB)")
if file_size == 0:
    print("WARNING: Content-Length not returned — will stream without known size")

# ── Step 4: create tus upload slot ──────────────────────────────────────────
print("Step 4: creating tus upload slot...")
filename  = "test-anime-ep.mp4"
filetype  = "video/mp4"

def b64(s): return base64.b64encode(s.encode()).decode()
metadata = f"accessToken {b64(access_token)},filename {b64(filename)},filetype {b64(filetype)}"

create_resp = requests.post(
    tus_url,
    headers={
        "Tus-Resumable": "1.0.0",
        "Upload-Length": str(file_size),
        "Upload-Metadata": metadata,
        "Content-Length": "0",
    },
    timeout=15
)
print(f"  Create status: {create_resp.status_code}")
if create_resp.status_code not in (201, 200):
    print("  Response:", create_resp.text[:300])
    exit(1)

upload_url = create_resp.headers.get("Location")
print(f"  Upload URL: {upload_url}")

# ── Step 5: stream-upload first 10 MB as a sanity check ─────────────────────
CHUNK = 5 * 1024 * 1024  # 5 MB chunks (SeekStreaming wants 50 MB but let's test)
print(f"Step 5: uploading first chunk ({CHUNK/1024/1024:.0f} MB) to verify...")

with requests.get(mp4_url, headers={"User-Agent": UA, "Referer": "https://www.mp4upload.com/"}, stream=True, timeout=30) as dl:
    dl.raise_for_status()
    chunk_data = dl.raw.read(CHUNK)

patch_resp = requests.patch(
    upload_url,
    headers={
        "Tus-Resumable": "1.0.0",
        "Content-Type": "application/offset+octet-stream",
        "Upload-Offset": "0",
        "Content-Length": str(len(chunk_data)),
    },
    data=chunk_data,
    timeout=60
)
print(f"  Patch status: {patch_resp.status_code}")
print(f"  Upload-Offset after patch: {patch_resp.headers.get('Upload-Offset', 'N/A')}")
if patch_resp.status_code not in (204, 200):
    print("  Response:", patch_resp.text[:300])
else:
    print("  SUCCESS: tus upload working! First chunk accepted.")
    print(f"\nTo do a full upload, implement streaming all {file_size/1024/1024:.1f} MB in chunks.")
