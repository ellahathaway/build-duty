# Dogfooding Guidelines

Dogfooding — using BuildDuty in our own day-to-day workflows — is the fastest
way to find real pain points, validate new features, and build shared confidence
in the tool before a broader rollout.

## Why Dogfood?

- Catch usability and functional issues early, in realistic conditions.
- Provide the team with direct experience to give informed feedback.

## Getting Started

Follow the [Quick start](../README.md#quick-start) instructions to install
BuildDuty and create a config file for a repository you already monitor. If you
maintain a pipeline or track GitHub issues as part of build duty, that is the
ideal starting point.

Try to run the build-duty tool in line with your usual build-duty workflow cadence.

## Reporting Feedback and Bugs

| What | How |
|---|---|
| Bug or unexpected behaviour | Open a [GitHub issue](https://github.com/ellahathaway/build-duty/issues/new) with the label **bug** and include the command you ran, any error output, and the relevant section of your `.build-duty.yml` (redact secrets). |
| Usability friction or missing feature | Open an issue with the label **enhancement** and describe the workflow you were trying to accomplish. |
| Quick question or discussion | Open a [GitHub issue](https://github.com/ellahathaway/build-duty/issues/new) with the label **question** so others can contribute and the answer is discoverable later. |

When filing an issue, please include:

1. A minimal reproduction of the config or command that triggered the problem (if relevant).
2. What you expected vs. what happened.
