"""Full tus upload probe — downloads a small test video and uploads via tus to SeekStreaming.
Polls /api/v1/video/manage to discover the video ID returned after upload."""

import requests
import base64
import time
import json

SEEK_KEY = "9db0b40302002d160ce3172e"
BASE = "https://seekstreaming.com"
TEST_URL = "https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/1080/Big_Buck_Bunny_1080_10s_5MB.mp4"

api_headers = {"api-token": SEEK_KEY}

def b64(s: str) -> str:
    return base64.b64encode(s.encode()).decode()

print("Step 1: downloading test video...")
dl = requests.get(TEST_URL, timeout=60)
dl.raise_for_status()
video_data = dl.content
file_size = len(video_data)
print(f"  Downloaded {file_size:,} bytes")

print("Step 2: getting tus credentials...")
r = requests.get(f"{BASE}/api/v1/video/upload", headers=api_headers, timeout=10)
r.raise_for_status()
creds = r.json()
tus_url = creds["tusUrl"]
access_token = creds["accessToken"]
print(f"  tusUrl: {tus_url}")

print("Step 3: creating tus upload slot...")
filename = "sa_probe_fulltest.mp4"
metadata = f"accessToken {b64(access_token)},filename {b64(filename)},filetype {b64('video/mp4')}"
create_resp = requests.post(
    tus_url,
    headers={
        "Tus-Resumable": "1.0.0",
        "Upload-Length": str(file_size),
        "Upload-Metadata": metadata,
        "Content-Length": "0",
    },
    timeout=30,
)
print(f"  Create status: {create_resp.status_code}")
upload_url = create_resp.headers.get("Location")
print(f"  Upload URL: {upload_url}")

print("Step 4: uploading all data (one PATCH)...")
patch_resp = requests.patch(
    upload_url,
    headers={
        "Tus-Resumable": "1.0.0",
        "Upload-Offset": "0",
        "Content-Type": "application/offset+octet-stream",
        "Content-Length": str(file_size),
    },
    data=video_data,
    timeout=120,
)
print(f"  Patch status: {patch_resp.status_code}")
print(f"  Upload-Offset: {patch_resp.headers.get('Upload-Offset')}")

if patch_resp.status_code not in (200, 201, 204):
    print(f"  ERROR: {patch_resp.text[:200]}")
    exit(1)

print("Step 5: polling /api/v1/video/manage for new video...")
for i in range(20):
    time.sleep(5)
    r2 = requests.get(f"{BASE}/api/v1/video/manage", headers=api_headers, timeout=10)
    data = r2.json()
    total = data["metadata"]["total"]
    print(f"  Poll {i+1}: total={total} videos")
    if total > 0:
        for v in data["data"]:
            print(json.dumps(v, indent=2))
        break

print("Done.")
