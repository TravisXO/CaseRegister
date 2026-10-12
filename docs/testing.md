# Testing

## Running the tests

```bash
dotnet test CaseRegister.slnx
```

The tests start a real PostgreSQL in Docker (Testcontainers), so Docker has to be running. They need no secrets and no
internet: the clock, Have I Been Pwned and Cloudflare are replaced, the Turnstile key is Cloudflare's published test key and
the certificates are generated for each run. The full suite takes about 15 to 20 minutes.

To run part of it, filter by class name: `dotnet test CaseRegister.slnx --filter "FullyQualifiedName~AuditChainTests"`.

### Coverage

```bash
dotnet test CaseRegister.slnx --settings coverage.runsettings --collect:"XPlat Code Coverage" --results-directory coverage
dotnet tool restore
dotnet reportgenerator -reports:"coverage/**/coverage.cobertura.xml" -targetdir:coverage/report -reporttypes:Html
```

Open `coverage/report/index.html`. CI does the same and keeps the report as a build artifact.

## Mutation checks

A passing test suite only shows that the tests agree with the code. A mutation check shows that they would notice if the code
were wrong: it breaks one rule on purpose and runs the tests. If they still pass, nothing guards that rule.

They are slow (each one builds and runs tests), so they run on your machine, not in CI. Run them on the rules that matter most
(security, integrity and compliance rules) whenever you add or change one.

```bash
scripts/mutate.sh <name> <file> <sed-expression> <test-filter>
```

For example, to check that the audit hash covers the detail field:

```bash
scripts/mutate.sh "hash leaves out the detail" \
  CaseRegister.Application/Auditing/AuditChainFormat.cs \
  's#\["detail"\] = fields.Detail,#["detail"] = null,#' \
  'FullyQualifiedName~AuditChainTests|FullyQualifiedName~AuditAnchorTests|FullyQualifiedName~CaseRegister.Tests.Auditing'
```

The result is one line:

| Result | Meaning |
| --- | --- |
| `Failed! ...` | A test noticed. The rule is guarded. |
| `Passed! ...` | Nothing noticed. Add a test, or check the change really did alter the behaviour (some changes are equivalent because a second layer enforces the same rule). |
| `NOT APPLIED` | The sed expression matched nothing. |
| `DID NOT BUILD` | The change broke the build, which proves nothing. Rewrite it so it compiles. |
| `HUNG` or `TIMED OUT` | A test hung. The result is not confirmed. |

Things that have caught people out:

- **The filter has to name every test class that covers the rule.** An early audit run used a filter that missed a whole
  group of tests and ran 93 of 210. Check the count in the result against what you expect
  (`dotnet test --list-tests --filter "<filter>"` lists what a filter matches).
- **The change must still compile.** The analysers treat warnings as errors, so an unused parameter or a constant condition
  stops the build. Break the rule with a variable-based condition instead of `true` or `false`.
- **Use a copy for a long run.** `REPO=/path/to/copy scripts/mutate.sh ...` runs on a copy, so you can carry on working in the
  real tree. Copy the repository without `bin`, `obj` and `.git`.
- **A hung test is reported, not waited for.** Each run has a 5 minute hang timeout and a 15 minute limit overall.
- **The file is put back** when the script ends, even if you press Ctrl-C. Check with `git status` after a run anyway.
