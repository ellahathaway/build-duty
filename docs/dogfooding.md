# Dogfooding Guidelines

Dogfooding — using BuildDuty in our own day-to-day workflows — is the fastest
way to find real pain points, validate new features, and build shared confidence
in the tool before a broader rollout.

## Why Dogfood?

- Catch usability and functional issues early, in realistic conditions.
- Provide the team with direct experience to give informed feedback.
- Build a culture of continuous improvement driven by firsthand use.

## Getting Started

Follow the [Quick start](../README.md#quick-start) instructions to install
BuildDuty and create a config file for a repository you already monitor. If you
maintain a pipeline or track GitHub issues as part of build duty, that is the
ideal starting point.

Aim to run `build-duty triage run` at least once per week as part of your normal
build-duty rotation and use `build-duty review` to work through the resulting
work items.

## Reporting Feedback and Bugs

| What | How |
|---|---|
| Bug or unexpected behaviour | Open a [GitHub issue](https://github.com/ellahathaway/build-duty/issues/new) with the label **bug** and include the command you ran, any error output, and the relevant section of your `.build-duty.yml` (redact secrets). |
| Usability friction or missing feature | Open an issue with the label **enhancement** and describe the workflow you were trying to accomplish. |
| Quick question or discussion | Start a [GitHub Discussion](https://github.com/ellahathaway/build-duty/discussions) so others can contribute and the answer is discoverable later. |

When filing an issue, please include:

1. BuildDuty version (`build-duty --version`) and .NET SDK version (`dotnet --version`).
2. Operating system and shell.
3. A minimal reproduction of the config or command that triggered the problem.
4. What you expected vs. what happened.

## Sharing Pain Points and Best Practices

Use [GitHub Discussions](https://github.com/ellahathaway/build-duty/discussions)
as the central forum for sharing:

- Config patterns that work well for specific pipeline topologies.
- Common error messages and how you resolved them.
- Workflow tips and shortcuts you have discovered.
- Signal or work item quality observations (e.g. noise, missed groupings).

Tag your post with the appropriate category (`Tips & Tricks`, `Show and Tell`,
`Q&A`) to keep the forum navigable.

## Regular Check-ins

The team holds a short dogfooding sync every two weeks. The goal of each
sync is to:

1. Share what each person ran and what they observed.
2. Review open issues and discussions filed since the last sync.
3. Agree on the top one or two pain points to address before the next sync.
4. Update the adoption tracker (see below).

If you cannot attend, please drop a short written summary in the associated
Discussion thread before the meeting.

## Tracking Adoption and Progress

Keep a running record of dogfooding activity in the
[GitHub Discussions](https://github.com/ellahathaway/build-duty/discussions)
dogfooding tracking thread:

| Metric | Target |
|---|---|
| Team members who have run at least one triage | All active members |
| Weekly triage runs across the team | ≥ 3 per week |
| Open bugs filed during dogfooding | Reviewed at each sync |
| Feedback items resolved before next sync | ≥ 1 per sync |

At the end of each sprint, briefly note:

- New pain points discovered.
- Improvements shipped in response to dogfooding feedback.
- Blockers that prevented team members from running the tool.

## Responsibilities

| Role | Responsibility |
|---|---|
| **Every team member** | Run the tool weekly, file issues and discussions promptly |
| **Sync facilitator** (rotates) | Schedule and run the biweekly check-in, update the tracking discussion |
| **On-call engineer** | Triage incoming dogfooding issues within one business day |
