#!/usr/bin/env bash
# Breaks one rule on purpose, builds, runs the tests that should notice, and puts the file back.
# If the tests still pass, nothing guards that rule. See docs/testing.md.
#
# Usage:  scripts/mutate.sh <name> <file> <sed-expression> <test-filter>
#   name           what is being broken, in words ("hash leaves out the detail")
#   file           the source file to change, relative to the repository root
#   sed-expression the change, as a sed script
#   test-filter    a `dotnet test --filter` expression naming EVERY test class that covers the rule
#
# Set REPO to run it on another copy of the repository (a copy is safer: nothing in your working tree is touched).
#
# Result, on one line:  Failed! ...   the rule is guarded (a test noticed)
#                       Passed! ...   NOT guarded: add a test, or the change was equivalent
#                       NOT APPLIED   the sed expression matched nothing
#                       DID NOT BUILD the change broke the build, so it proved nothing: make it compile and run again
#                       HUNG / TIMED OUT  a test hung, so the result is not confirmed
set -u

if [ "$#" -ne 4 ]; then
  sed -n '2,19p' "$0" | sed 's/^# \{0,1\}//'
  exit 2
fi

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${REPO:-$here}" || exit 2
name="$1"; file="$2"; expr="$3"; filter="$4"

backup="$(mktemp)"
out="$(mktemp)"
restore() { cp "$backup" "$file"; rm -f "$backup" "$out"; }
# The file is put back however this ends, including Ctrl-C.
trap restore EXIT
trap 'exit 130' INT TERM

cp "$file" "$backup"
sed -i -e "$expr" "$file"
if cmp -s "$file" "$backup"; then
  echo "MUTATION $name: NOT APPLIED (pattern did not match)"
  exit 3
fi

build="$(dotnet build CaseRegister.slnx -nologo -v q 2>&1)"
if ! grep -qE "^[[:space:]]+0 Error" <<< "$build"; then
  echo "MUTATION $name: DID NOT BUILD: $(grep -E ' error ' <<< "$build" | head -3)"
  exit 4
fi

# A test that hangs is reported after 5 minutes (the blame option names the test that was running, and no memory dump is
# written), and a hard limit of 15 minutes covers the build, discovery and everything else, so one mutation can never wait for hours.
timeout 900 dotnet test CaseRegister.slnx --no-build -nologo --blame-hang-timeout 5m --blame-hang-dump-type none --filter "$filter" > "$out" 2>&1
code=$?
result=$(grep -E "Passed!|Failed!" "$out" | head -1)
if [ "$code" -eq 124 ]; then
  result="TIMED OUT after 15 minutes (the test run was killed, so this mutation is NOT confirmed as caught)"
elif grep -q -i -E "blame-hang|test host process .* hung|The active test run was aborted" "$out"; then
  hung=$(grep -i -E "blame-hang|hung|aborted" "$out" | head -2 | tr '\n' ' ' | cut -c1-200)
  result="HUNG: ${hung} -- ${result}"
fi
echo "MUTATION $name: $result"
