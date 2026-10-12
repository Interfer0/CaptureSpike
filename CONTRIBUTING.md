# Contributing to CaptureSpike

Thanks for your interest in CaptureSpike! Contributions are
welcome. Please read this short guide before opening a pull
request.

## Code of conduct

Be respectful, focus on the code, assume good intent.
The full text is in [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md)
— it's the standard Contributor Covenant 2.1.

## Before you start

- **Open an issue first** for non-trivial changes (new
  features, refactors, breaking changes). Small bug fixes
  can go straight to a PR.
- **Check the AGENTS.md** at `.github/AGENTS.md` for
  project conventions if you're working with an AI
  coding agent. AI-generated PRs that don't follow the
  conventions will be rejected.

## Contributor License Agreement (CLA)

**All contributors must agree to the [CLA](CLA.md) before
their PR can be merged.** The CLA lets the project owner:

- Continue releasing CaptureSpike under GPL-3.0-or-later
  (the open source license), AND
- Offer commercial licenses to companies that want to
  embed CaptureSpike in a proprietary product

Without a CLA, the project owner cannot relicense your
contributions, which blocks both future relicensing and
any potential sale of the project. The CLA is
non-exclusive — you retain copyright and can use your
own contributions in any other project you work on.

### How to sign

On your first pull request, leave a comment:

> I have read and agree to the [CLA](CLA.md).
>
> GitHub: @your-username
> Name: Your Name
> Date: YYYY-MM-DD

For subsequent PRs from the same GitHub account, you're
already covered.

## Development setup

See the [README](README.md#development-setup) for build
instructions. The project uses .NET 10 (preview) and
Avalonia 12 (preview). Both have rough edges; expect to
hit issues.

## Testing

- All code changes should come with tests. The project
  uses xunit v3 on Microsoft Testing Platform.
- Run the test suite locally before opening a PR:
  ```
  dotnet build src/CaptureSpike.Core.Tests
  dotnet src/CaptureSpike.Core.Tests/bin/Debug/net10.0/CaptureSpike.Core.Tests.dll -parallel none
  ```
- The build server runs the same command on every push.

## Style

- C# 13, nullable enabled, implicit usings.
- 4-space indent, Allman braces, `_camelCase` for private
  fields, `PascalCase` for everything else.
- Keep methods short. If a method is over ~50 lines,
  consider splitting.
- XML doc comments on all public APIs. Private methods
  get a comment only when the intent isn't obvious from
  the code.
- Match the existing patterns in the file you're
  editing. Consistency wins over personal preference.

## Commit messages

- Short subject line (50 chars max), then a blank line,
  then a body explaining the "why".
- Reference the GitHub issue number in the body if
  applicable (`Fixes #123`, `Refs #456`).
- Don't worry about the exact format; the project
  doesn't enforce a style. The release-drafter bot reads
  the PR title and labels to build the changelog, so
  write the PR title in a way that makes sense in a
  changelog ("Fix: normalize pass bit rate" not "wip").

## Release process

See [`docs/RELEASING.md`](docs/RELEASING.md) for the
release checklist. Contributors don't cut releases;
the project owner does, but the checklist explains
what's involved.
