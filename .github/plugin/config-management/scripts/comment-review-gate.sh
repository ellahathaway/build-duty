#!/usr/bin/env bash
set -euo pipefail

# build-duty preToolUse gate: review GitHub comments before they are posted (issue #73).
#
# Runs as a Copilot CLI 'preToolUse' command hook. Reads the tool-call payload from
# stdin and, when the call would post a comment or review on a GitHub issue or pull
# request, prints a JSON decision of "ask" so the agent must surface the rendered
# comment for confirmation first -- even under --yolo / autopilot. Deterministic
# backstop for the 'post-comment' skill.
#
# The hook NEVER denies and always exits 0: a bug here can only ever escalate a comment
# post to a human "ask", never block an unrelated tool call or the session.
#
# Opt out by setting BUILD_DUTY_ALLOW_UNREVIEWED_COMMENTS=1.

case "${BUILD_DUTY_ALLOW_UNREVIEWED_COMMENTS:-}" in
  1 | true | TRUE | yes | YES | on | ON)
    exit 0
    ;;
esac

payload="$(cat || true)"
[ -z "$payload" ] && exit 0

is_comment=0

# GitHub MCP server tools that create issue/PR comments or reviews.
for tool in \
  add_issue_comment \
  add_comment_to_pending_review \
  create_and_submit_pull_request_review \
  submit_pending_pull_request_review \
  create_pull_request_review \
  add_pull_request_review_comment; do
  case "$payload" in
    *"$tool"*)
      is_comment=1
      break
      ;;
  esac
done

# gh CLI invoked through the shell tool.
if [ "$is_comment" -eq 0 ]; then
  if printf '%s' "$payload" | grep -Eq 'gh[[:space:]]+issue[[:space:]]+comment|gh[[:space:]]+pr[[:space:]]+comment|gh[[:space:]]+pr[[:space:]]+review'; then
    is_comment=1
  elif printf '%s' "$payload" | grep -Eq 'gh[[:space:]]+api' \
    && printf '%s' "$payload" | grep -Eq '/(comments|reviews)' \
    && printf '%s' "$payload" | grep -q 'body'; then
    is_comment=1
  fi
fi

[ "$is_comment" -eq 0 ] && exit 0

reason='build-duty: review the rendered comment before posting (issue #73). Confirm the scope and fix/PR links are correct and that the text matches the previewed comment. Set BUILD_DUTY_ALLOW_UNREVIEWED_COMMENTS=1 to opt out.'
printf '{"permissionDecision":"ask","permissionDecisionReason":"%s"}\n' "$reason"
exit 0
