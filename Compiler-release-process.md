For each release, some steps to check:

- declare the new Language Versions when a common branch becomes available (this has a checklist of its own)
- review public API changes
- notify partners of preview and RTM packages being published to NuGet
- remove any unused Language Version

For each language feature:

- work with LDM to define the feature
- update the [Language Feature Status](https://github.com/dotnet/roslyn/blob/master/docs/Language%20Feature%20Status.md) page as we start working on features
- identify a designated reviewer
- notify Jared and Neal on PRs with public API changes
- breaking changes need to be approved by the compat council and [documented](https://github.com/dotnet/roslyn/tree/master/docs/compilers/CSharp)
- designated reviewer should document and validate a test plan
- blocking issues should be identified and resolved before merging feature back to `master` (that includes resolving `PROTOTYPE` comments)
