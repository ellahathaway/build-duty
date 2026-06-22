# Section 3 Addendum: Open PR with 0 files changed

Use this rule only when the PR is open.

- If `files changed` is 0, do not recommend closing the PR only because it has no file diff.
- In this case, recommend merge (or force merge if failing checks) so the merge commit is preserved in history.
- Recommend a merge commit strategy. Do not squash or rebase.
