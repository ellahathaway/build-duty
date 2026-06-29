---
name: post-comment
description: Render a GitHub issue/PR comment for review and post it only after explicit confirmation. Guarantees the previewed text is byte-for-byte identical to what gets posted. Use this whenever build-duty is about to comment on an issue or pull request (status updates, escalation pings, cross-links, handoffs).
---

# Post Comment (Review Before Posting)

Whenever you are about to post a comment on a GitHub issue or pull request, you
**must** render the comment for review first and post only after the user
approves. The text you show in the preview must be exactly the text you post —
no rephrasing, reflow, added signatures, or summarizing between preview and post.

This protocol exists because an unreviewed comment was once posted with incorrect
and internally inconsistent claims (wrong feature bands, wrong fix PR). Reviewing
the rendered comment first catches those mistakes before they reach a public PR.

## When this applies

Follow this protocol any time you would add a comment to an issue or PR — via the
`github-mcp-server` MCP tools or `gh issue comment` / `gh pr comment` — as part of
triage, work-item review, escalation, handoff, or remediation. It applies to new
comments and to replies on existing threads.

It does **not** apply to reading comments, filing new issues (issue body), or
non-comment actions like retrying a build.

## Protocol

### 1. Verify the facts first

Before composing anything, confirm every claim you intend to make is correct.
Common mistakes to guard against:

- **Wrong scope** — which branches / feature bands were *actually* impacted vs
  assumed. Different bands can fail for different reasons; do not generalize one
  band's failure to another.
- **Wrong fix** — which PR or commit actually carries the fix, and whether it has
  merged / flowed yet.
- **Broken or wrong links** — every issue/PR number and URL must resolve to the
  intended target. Re-check each cross-link.
- **Internal consistency** — the comment must not contradict itself or earlier
  statements you made in the conversation.

If you cannot verify a claim, soften it or leave it out — do not guess.

### 2. Compose the final comment body

Write the complete comment as Markdown, exactly as it should appear on GitHub.

### 3. Render the preview

Show the comment to the user **verbatim inside a fenced code block**, preceded by
the exact target. The fenced block is the single source of truth for what will be
posted.

````
Target: dotnet/sdk PR #54506 — https://github.com/dotnet/sdk/pull/54506

```markdown
<the exact comment body, unmodified>
```
````

- One target + one fenced block **per comment**. When posting to multiple PRs or
  issues, render each comment separately with its own target header.
- Do not paraphrase or re-summarize the body outside the block.

### 4. Ask for confirmation

For each rendered comment, ask the user to choose: **Post / Edit / Cancel**.

- **Post** — proceed to step 5 using the exact approved text.
- **Edit** — apply the requested changes, then **re-render the full updated
  comment** in a fresh fenced block (step 3) and ask again. Repeat until approved.
- **Cancel** — do not post; move on.

### 5. Post the exact approved string

Only after explicit approval, post the comment using the **exact** approved text.

- To guarantee the posted bytes equal the previewed bytes, write the approved body
  to a temporary file and post from that file, e.g.:

  ```bash
  gh pr comment https://github.com/<owner>/<repo>/pull/<number> --body-file <file>
  # or
  gh issue comment https://github.com/<owner>/<repo>/issues/<number> --body-file <file>
  ```

  If you use the `github-mcp-server` add-comment tool instead, pass the approved
  string unchanged.
- Do **not** add a trailing "posted by", signature, timestamp, or any text that
  was not in the approved preview.

### 6. Report back

After posting, return the permalink to each created comment so the user can
verify it. Clean up any temporary files you created.

## Determinism rule (non-negotiable)

The string you post **must equal** the string in the approved fenced preview,
character-for-character — including links, line breaks, and Markdown. Do not
regenerate, rephrase, or reflow it between preview and post. If you posted to
multiple targets, each posted comment must match its own approved preview.

## Confirmation default and opt-out

- Confirmation is **required by default**, including in autopilot / `--yolo` mode.
  Never post an unreviewed comment by default.
- Skip the confirmation gate **only** when the user has explicitly opted out for
  this action (e.g. "post without review", "auto-post", "don't ask, just post").
  Even then, still render the preview (step 3) in your output so there is a record
  of exactly what was posted, and still verify the facts (step 1).

## Enforcement backstop (preToolUse hook)

This skill is the *visualize + verify* layer, but it relies on the agent
following the protocol. A `preToolUse` hook (`scripts/comment-review-gate`)
backs it up deterministically: it inspects every tool call and, when it detects a
GitHub issue/PR comment or review **post** (the GitHub MCP comment/review tools,
or `gh issue/pr comment`, `gh pr review`, or `gh api .../comments|/reviews` with a
body), it forces an interactive confirmation — even under autopilot / `--yolo`.
The gate never blocks outright; it only escalates to a human "ask".

To bypass the hook for a session where you genuinely want unreviewed posting, set
the environment variable `BUILD_DUTY_ALLOW_UNREVIEWED_COMMENTS=1` (also accepts
`true`, `yes`, or `on`). Even with the hook bypassed, still follow this skill and
render the preview for the record.

## Output

- The rendered comment(s) in fenced blocks with their targets.
- The action taken per comment (posted / edited / cancelled).
- The permalink(s) to any posted comment(s).
