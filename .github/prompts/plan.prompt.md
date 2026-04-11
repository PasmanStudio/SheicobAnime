# Prompt: Plan today's work session
# Usage: /plan in Copilot chat

Plan a focused coding session for SheicobAnime based on the current phase and what's already done.

## Steps I will take:

### 1. Check current phase status
I'll look at the open GitHub issues and PRs to determine:
- Which phase are we currently in?
- What tasks are In Progress?
- What's blocking progress?

### 2. Suggest a session goal (2–4 hour block)
I'll recommend ONE main goal for this session that:
- Is the highest-priority unblocked task in the current phase
- Can realistically be completed in the session
- Has clear acceptance criteria we can verify

### 3. Break it into steps (max 30 min each)
Each step will have:
- Exact files to create or modify
- Clear definition of done
- Which chatmode to use (backend-engineer / frontend-engineer / etc.)

### 4. Identify what to check first
Before starting, I'll flag:
- Any env vars that need to be set
- Any DB migrations that need to run
- Any dependencies that must be installed
- Any architectural contracts to be aware of

### 5. End-of-session checklist
At the end, we should be able to verify:
- [ ] Code compiles / TypeScript passes
- [ ] Local dev server runs without errors
- [ ] The specific feature works manually
- [ ] No architectural contract violations introduced
- [ ] Relevant cache invalidation is in place (for backend work)

---
Start by reading the open issues and recent commits, then give me today's plan.
