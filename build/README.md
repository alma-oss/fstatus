# fbuild

This repository uses the bootstrap Alma.Build build infrastructure.

## Entry point

`./build.sh <Target>` restores local tools and packages, then runs `build/Build.fs`.

## Common options

- `no-clean` disables cleaning directories in the first step. This is useful on CI when intermediate outputs need to remain available.
- `no-lint` skips the `Lint` step entirely.

## Updating bootstrap files

`build.sh`, this README, and the other bootstrap support files are bundled inside the pinned `Alma.Build` package. After bumping the pinned version, run the `Bootstrap` target to redeploy them:

```bash
./build.sh Bootstrap
```
