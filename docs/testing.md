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

## Accessibility and size checks

Three layers, all run by CI and all failing the build:

| Check | What it does | Run it |
| --- | --- | --- |
| **axe in the screen tests** | Every test that renders a screen (`renderApp`) is checked, as the test left it, against WCAG 2.2 AA and axe's best practices, through `vitest-axe`. Colour contrast cannot be judged without a browser, so it is left to the next check. | `npm test` in `frontend` |
| **Playwright with axe** | The *built* site, in a real browser, with every call to the API answered by the test (`frontend/e2e/support.ts`). Every main screen, at a desktop width and at **320px wide** (what 400% zoom gives), with axe including colour contrast and target size, and a check that the page never scrolls sideways. A few things only a browser can show (focus, the error summary) are checked too. | `npm run build:e2e`, then `npm run e2e` |
| **Lighthouse CI** | The built site on a phone's settings (small screen, slow 4G, a CPU four times slower), on the screens anyone can reach. The accessibility score has to be **100**, and no more than **200 KB** of JavaScript may be sent (compressed). | `npm run build:e2e`, then `npm run lighthouse` |

The browser checks use a copy of the build in `dist-e2e` (with a Turnstile site key in it, which Cloudflare's script needs to draw; the tests replace that script), served by `frontend/scripts/serve-dist.mjs` under the production headers.

**Running them on this machine.** Playwright can use a browser already installed instead of downloading its own: `PLAYWRIGHT_CHANNEL=msedge npm run e2e` (or `chrome`). In CI the job runs `npx playwright install --with-deps chromium`. Lighthouse CI needs Chrome: set `CHROME_PATH` to `chrome.exe` (Edge does not start under Lighthouse CI's launcher here):

```bash
cd frontend
npm run build:e2e
PLAYWRIGHT_CHANNEL=msedge npm run e2e
CHROME_PATH="C:/Program Files/Google/Chrome/Application/chrome.exe" npm run lighthouse
```

Lighthouse CI is run with `npx` at a fixed version (see `package.json`); it is not a dependency of the site, so it adds nothing to `npm audit`. What these checks cannot judge (how it *sounds* in a screen reader, how it feels on a phone) is the manual list in [manual-accessibility-test.md](manual-accessibility-test.md).

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

## Running tests on Linux from Windows

CI runs on Linux; on a Windows machine the same tests can be run in a Linux container, which matters for anything that depends on the operating system (loading certificates, for one). From the repository folder, with a `.dockerignore` that leaves out `bin`, `obj`, `node_modules` and `.git`:

```powershell
docker build -f Dockerfile.linuxtest -t caseregister-linuxtest .
docker run --rm -v //var/run/docker.sock:/var/run/docker.sock -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal --add-host host.docker.internal:host-gateway caseregister-linuxtest --filter "FullyQualifiedName~CertificateSwap"
```

where `Dockerfile.linuxtest` is the SDK image (`mcr.microsoft.com/dotnet/sdk:10.0`) with the repository copied in, `dotnet build` of the test project, and `dotnet test --no-build` as the entry point. The tests that need PostgreSQL start it as a sibling container through the mounted Docker socket.

## A run where 13 tests timed out connecting to PostgreSQL

One full run on a Windows machine (16 October) failed 13 tests in a row (most of `SignInTests`, two in `SessionTests`, two in `SessionManagementTests`), each after exactly 15 seconds, with `Npgsql.NpgsqlException: The operation has timed out`. It has not come back in the runs since. What was found:

- **Not pool exhaustion.** The error was raised in `NpgsqlConnector.ConnectAsync`, while opening the first connection of a new pool (the TCP connection and handshake), not while waiting for a free connection. Exhaustion would have said "the connection pool has been exhausted" and failed at once. Each test database also has its own small pool, and the server allows 500 connections.
- **Not parallel test collections.** All 50 database test classes are in one xUnit collection (`PostgresTestGroup`), so they run one after the other, never together. The 13 were consecutive, which fits one stall that lasted a few minutes, not contention.
- **Not container start-up.** The first four minutes of the run passed; the failures began later and the tests after the 13 passed.
- **Not an IPv6/`localhost` delay.** The fixture connects to `127.0.0.1`. Opening 400 new connections in a row to the container afterwards took 27 ms at best, 32 ms median and 79 ms at worst, with no outlier.
- **Most likely a stall in Docker Desktop's port forwarding (or the host being starved) for a few minutes**: the only thing left that explains many consecutive, otherwise healthy tests each waiting out the default 15-second connect timeout and then recovering. It could not be reproduced, so this is the best explanation, not a proven one.

No retry was added to the fixture, because a retry would hide a stall rather than explain it. If it happens again: note the time, check whether Docker Desktop was restarting or the machine was under load (a second heavy job, a sleep/wake), and look at the Docker Desktop logs for that time. A failure like this in CI (Linux, a real Docker daemon) would be a different, and more worrying, thing.
