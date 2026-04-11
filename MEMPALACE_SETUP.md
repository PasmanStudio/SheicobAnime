# MemPalace MCP Configuration for SheicobAnime
#
# MemPalace (github.com/milla-jovovich/mempalace) solves the context loss problem:
# every time you start a new Copilot session, all architectural decisions,
# debugging history, and "why we did X" reasoning is gone.
#
# This file is a SETUP GUIDE — not a config file that goes in the repo.
# Run these commands ONCE on your machine, then Copilot remembers across sessions.

# ─── STEP 1: Install MemPalace ────────────────────────────────────────────────
# pip install mempalace

# ─── STEP 2: Initialize your project palace ──────────────────────────────────
# mempalace init ~/path/to/sheicobanime
#
# This creates ~/.mempalace/ with rooms for:
# - technical decisions (why .NET 8 instead of Node, etc.)
# - debugging sessions (what errors you hit and how you fixed them)
# - architecture changes (when and why contracts changed)
# - preferences (your coding style, shortcuts, preferred patterns)

# ─── STEP 3: Mine this repo ──────────────────────────────────────────────────
# mempalace mine ~/path/to/sheicobanime --mode projects
#
# This indexes all code, docs, and notes into searchable memory.

# ─── STEP 4: Configure as MCP server for Copilot ─────────────────────────────
# Add to VS Code settings.json (or user MCP config):
#
# "mcp": {
#   "servers": {
#     "mempalace": {
#       "command": "python",
#       "args": ["-m", "mempalace.mcp_server"]
#     }
#   }
# }
#
# OR via Copilot CLI:
# copilot plugin marketplace add milla-jovovich/mempalace
# copilot plugin install mempalace

# ─── WHAT TO STORE IN MEMPALACE ──────────────────────────────────────────────
# After every important decision in your Copilot session, say:
# "Save this to MemPalace: [the decision and why]"
#
# Examples of things worth saving:
# - "We chose IScrapeStrategy interface over direct class usage because..."
# - "Railway free tier sleeps — solution is Cloudflare Worker keepalive"
# - "Supabase needs PgBouncer Session mode for Hangfire, direct for migrations"
# - "Episode player must be 'use client' — iframe breaks SSR/SEO if in Server Component"
# - "Source1 uses network interception not DOM scraping because site is JS-rendered"
# - "[Bug fixed] Mirror probe was timing out at 5s — changed to 10s for slow embeds"

# ─── WHAT MEMPALACE DOES IN EVERY SESSION ────────────────────────────────────
# When you ask Copilot anything about SheicobAnime:
# 1. Copilot calls mempalace_search automatically
# 2. Gets back the relevant past decisions verbatim
# 3. Answers you with that context already loaded
# 4. You don't have to re-explain architecture every session

# ─── MINING YOUR CHAT EXPORTS ────────────────────────────────────────────────
# Export your Claude/ChatGPT/Copilot chat history and mine it:
# mempalace mine ~/Downloads/chats/ --mode convos
#
# This captures all the debugging sessions and decisions from past conversations
# into searchable memory — even the "we tried X and it failed because Y" moments.
