# Changelog

## [Unreleased]

## [0.0.2] - 2026-06-03

### Fixed

- Pipelines with no `branches` configured and no `release` section are now queried without a branch filter (returns latest build across all branches) instead of being silently skipped
- Connection failures when creating the Azure DevOps build client are now reported as `CollectionFailure` instead of crashing
- Covered scopes are now reported unconditionally for all pipelines (not only when a failing build is found)

### Added

- Documentation for Azure DevOps pipeline configuration fields in README

## [0.0.1] - 2026-05-18

### Added

- Initial release of `ellahathaway.buildduty.mcp` MCP server
- `build_duty_collect_signals` tool: collects signals from Azure DevOps and GitHub based on a `.build-duty.yml` config
- `build_duty_get_config` tool: reads and returns the resolved build-duty configuration
