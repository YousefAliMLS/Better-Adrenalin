## Summary
Describe the changes in this pull request.

## Testing Performed
- [ ] Built `RadeonGuard.AdlxBridge` (`Release|x64`) and `RadeonGuard.sln` (`Release`) with 0 errors
- [ ] Ran `dotnet test tests\RadeonGuard.Tests\RadeonGuard.Tests.csproj -c Release` (all tests pass)
- [ ] Tested live on AMD GPU hardware (if applicable; list GPU and driver version below)

## Checklist
- [ ] No personal file paths, secrets, or local `.db`/`.log` artifacts included
- [ ] ADLX bounds validation (`ProfileValidator`) preserved
- [ ] Documentation updated (`README.md`, `CHANGELOG.md`) if behavior changed
