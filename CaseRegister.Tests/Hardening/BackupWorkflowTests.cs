using System.Text.RegularExpressions;
using CaseRegister.Tests.Architecture;

namespace CaseRegister.Tests.Hardening;

/// <summary>What the backup and restore-check workflows promise, checked as text so a later edit cannot quietly loosen it.</summary>
public sealed partial class BackupWorkflowTests
{
    private static readonly string _root = SolutionGraph.SolutionRoot;

    public static TheoryData<string> Workflows => new() { "backup.yml", "restore-check.yml" };

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([_root, .. path])).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Workflow(string name) => Read(".github", "workflows", name);

    /// <summary>Without comment lines: only what a workflow does counts, not what it says about itself.</summary>
    private static string Code(string name) => string.Join('\n', Workflow(name).Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    private static string Steps(string name)
    {
        var text = Code(name);

        return text[text.IndexOf("    steps:", StringComparison.Ordinal)..];
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void Every_action_is_pinned_to_a_full_commit_with_the_version_in_a_comment(string name)
    {
        var uses = Workflow(name).Split('\n').Where(line => UsesLine().IsMatch(line)).ToList();

        Assert.NotEmpty(uses);
        Assert.All(uses, line => Assert.Matches(@"uses:\s+[\w./-]+@[0-9a-f]{40}\s+#\s+v\d+(\.\d+)*\s*$", line.TrimEnd()));
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void Every_job_has_a_timeout_and_the_token_can_only_read(string name)
    {
        var text = Workflow(name);

        Assert.Matches(@"(?m)^    timeout-minutes:\s+\d+\s*$", text);
        Assert.Matches(@"(?m)^permissions:\n  contents: read\s*\n", text);
        Assert.DoesNotMatch(@"(?m)^\s+[\w-]+:\s+write\s*$", text);
        Assert.DoesNotContain("pull_request", Code(name), StringComparison.Ordinal); // runs on a schedule or by hand, never for someone's pull request
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void The_checkout_leaves_no_token_behind(string name)
    {
        Assert.Matches(@"uses: actions/checkout@[^\n]*\n\s+with:\n\s+persist-credentials: false", Workflow(name));
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void Runs_on_a_schedule_and_by_hand_and_two_runs_never_overlap(string name)
    {
        var text = Workflow(name);

        Assert.Matches(@"(?m)^on:\n  schedule:\n    - cron: ""[^""]+""\n  workflow_dispatch:\n", text);
        Assert.Matches(@"(?m)^concurrency:\n  group: \$\{\{ github\.workflow \}\}\n  cancel-in-progress: false\s*$", text);
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void A_failing_step_fails_the_job(string name)
    {
        var code = Code(name);

        Assert.DoesNotContain("continue-on-error", code, StringComparison.Ordinal);
        Assert.DoesNotContain("|| true", code, StringComparison.Ordinal);
        Assert.Contains("set -euo pipefail", code, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void Without_its_secrets_the_workflow_skips_with_a_notice_and_every_later_step_waits_for_that_check(string name)
    {
        var steps = Steps(name);

        Assert.Contains("::notice title=", steps, StringComparison.Ordinal);
        Assert.Contains("echo \"ready=false\" >> \"$GITHUB_OUTPUT\"", steps, StringComparison.Ordinal);

        // Every step after the check is conditional on it. Steps start at "      - name:" (six spaces in).
        var all = Regex.Split(steps, @"(?m)^      - name: ").Skip(1).ToList();
        var after = all.SkipWhile(step => !step.StartsWith("Check the ", StringComparison.Ordinal)).Skip(1).ToList();
        Assert.NotEmpty(after);
        Assert.All(after, step => Assert.Contains("if: steps.setup.outputs.ready == 'true'", step, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void No_secret_is_ever_written_to_the_log(string name)
    {
        // Lines that go into the job's own environment file are not the log (and a secret put there is masked anyway).
        var code = Regex.Replace(Code(name), @"\{\n(?:.*\n)*?\s*\} >> ""\$GITHUB_ENV""", string.Empty);

        Assert.DoesNotMatch(@"echo[^\n]*\$\{?(BACKUP_DATABASE_URL|BACKUP_R2_SECRET_ACCESS_KEY|BACKUP_R2_READ_SECRET_ACCESS_KEY|BACKUP_AGE_RESTORE_KEY|RESTORE_CHECK_SETTINGS|AWS_SECRET_ACCESS_KEY)", code);
        Assert.DoesNotContain("set -x", code, StringComparison.Ordinal);
        Assert.DoesNotContain("--verbose", code, StringComparison.Ordinal);
    }

    // --- The nightly backup --------------------------------------------------------------------------------------

    [Fact]
    public void The_backup_runs_every_day_at_two_in_the_morning_utc()
    {
        Assert.Contains("- cron: \"0 2 * * *\"", Workflow("backup.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_dump_is_in_custom_format_without_owners_and_the_connection_string_stays_out_of_the_command_line()
    {
        var steps = Steps("backup.yml");

        Assert.Contains("pg_dump \"$BACKUP_DATABASE_URL\" --format=custom --no-owner --no-privileges", steps, StringComparison.Ordinal);
        Assert.Contains("--env BACKUP_DATABASE_URL", steps, StringComparison.Ordinal); // passed by name; the value never appears in a command
        Assert.Contains("pg_restore --list", steps, StringComparison.Ordinal); // a dump that cannot be listed is not a backup
        Assert.Matches(@"POSTGRES_IMAGE: postgres:\d+", Workflow("backup.yml"));
    }

    [Fact]
    public void The_dump_is_encrypted_to_the_committed_public_keys_and_the_plain_dump_is_removed_before_the_upload()
    {
        var steps = Steps("backup.yml");

        Assert.Contains("age --recipients-file backup/age-recipients.txt --output backup.dump.age backup.dump", steps, StringComparison.Ordinal);
        Assert.True(steps.IndexOf("rm -f backup.dump", StringComparison.Ordinal) < steps.IndexOf("aws s3 cp backup.dump.age", StringComparison.Ordinal), "the plain dump is deleted before anything is uploaded");
        Assert.DoesNotMatch(@"aws s3 cp backup\.dump(?!\.age)", steps); // the plain dump is never uploaded
        Assert.DoesNotContain("upload-artifact", steps, StringComparison.Ordinal); // and never kept as a workflow artifact either
    }

    [Fact]
    public void The_upload_goes_to_the_backup_bucket_with_the_read_and_write_token_and_is_checked_afterwards()
    {
        var steps = Steps("backup.yml");

        Assert.Contains("secrets.BACKUP_R2_BUCKET", Workflow("backup.yml"), StringComparison.Ordinal);
        Assert.Contains("s3://${BACKUP_R2_BUCKET}/${key}", steps, StringComparison.Ordinal);
        Assert.Contains("--endpoint-url \"$BACKUP_R2_ENDPOINT\"", steps, StringComparison.Ordinal);
        Assert.Contains("aws s3api head-object", steps, StringComparison.Ordinal);
        Assert.Matches(@"key=""dumps/caseregister-\$\(date -u \+%Y%m%dT%H%M%SZ\)\.dump\.age""", steps);
        Assert.DoesNotContain("AUDIT", Workflow("backup.yml"), StringComparison.Ordinal); // nothing to do with the audit anchors' bucket
    }

    [Fact]
    public void The_committed_recipients_file_holds_only_public_keys()
    {
        var lines = Read("backup", "age-recipients.txt").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')).ToList();

        Assert.All(lines, line => Assert.Matches(@"^age1[0-9a-z]{50,}$", line));
    }

    [Fact]
    public void No_age_private_key_is_anywhere_in_the_workflows_the_backup_folder_or_the_docs()
    {
        var folders = new[] { Path.Combine(_root, ".github"), Path.Combine(_root, "backup"), Path.Combine(_root, "docs") };
        var files = folders.SelectMany(folder => Directory.GetFiles(folder, "*", SearchOption.AllDirectories)).ToList();

        Assert.NotEmpty(files);
        Assert.All(files, file => Assert.DoesNotMatch(@"AGE-SECRET-KEY-1[0-9A-Z]{20,}", File.ReadAllText(file)));
    }

    // --- The monthly restore check -------------------------------------------------------------------------------

    [Fact]
    public void The_restore_check_runs_once_a_month()
    {
        var cron = Regex.Match(Workflow("restore-check.yml"), @"cron: ""([^""]+)""").Groups[1].Value.Split(' ');

        Assert.Equal(5, cron.Length);
        Assert.Matches(@"^([1-9]|[12]\d)$", cron[2]); // one day of the month
        Assert.Equal("*", cron[3]);
        Assert.Equal("*", cron[4]);
    }

    [Fact]
    public void The_restore_check_restores_into_a_throwaway_container_and_never_touches_production()
    {
        var text = Workflow("restore-check.yml");
        var code = Code("restore-check.yml");

        Assert.Matches(@"services:\n      postgres:\n        image: postgres:\d+", text);
        Assert.Contains("pg_restore --host localhost", code, StringComparison.Ordinal);
        Assert.Contains("--exit-on-error", code, StringComparison.Ordinal);
        Assert.Contains("Database=restored", code, StringComparison.Ordinal);
        Assert.DoesNotContain("BACKUP_DATABASE_URL", code, StringComparison.Ordinal); // the production database is not reachable from it
        Assert.DoesNotContain("BACKUP_R2_ACCESS_KEY_ID", code, StringComparison.Ordinal); // and neither is the token that can write to the bucket
        Assert.DoesNotContain("BACKUP_R2_SECRET_ACCESS_KEY", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_restore_check_reads_the_newest_backup_with_the_read_only_token()
    {
        var code = Code("restore-check.yml");

        Assert.Contains("secrets.BACKUP_R2_READ_ACCESS_KEY_ID", code, StringComparison.Ordinal);
        Assert.Contains("secrets.BACKUP_R2_READ_SECRET_ACCESS_KEY", code, StringComparison.Ordinal);
        Assert.Contains("list-objects-v2", code, StringComparison.Ordinal);
        Assert.Contains("sort | tail -n 1", code, StringComparison.Ordinal);
        Assert.Contains("The backup bucket holds no backups at all", code, StringComparison.Ordinal); // an empty bucket is a failure, not a pass
        Assert.Matches(@"Audit__R2__AccessKeyId=\$BACKUP_R2_READ_ACCESS_KEY_ID", code); // the audit anchors are read with the read-only token too
    }

    [Fact]
    public void The_restore_check_runs_verify_audit_on_the_restored_copy_and_the_job_fails_if_it_does()
    {
        var steps = Steps("restore-check.yml");

        Assert.Contains("-- verify-audit --restored-backup", steps, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__Default=Host=localhost;Port=5432;Database=restored", steps, StringComparison.Ordinal);
        // The restore comes before the verification, and the verification is the last step.
        Assert.True(steps.IndexOf("pg_restore", StringComparison.Ordinal) < steps.IndexOf("verify-audit", StringComparison.Ordinal));
        Assert.EndsWith("verify-audit --restored-backup", steps.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_database_and_the_token_the_check_uses_are_set_after_the_settings_secret_so_it_cannot_override_them()
    {
        var steps = Steps("restore-check.yml");

        Assert.True(steps.IndexOf("done <<< \"$RESTORE_CHECK_SETTINGS\"", StringComparison.Ordinal) < steps.IndexOf("ConnectionStrings__Default=", StringComparison.Ordinal));
        Assert.Contains("::add-mask::", steps, StringComparison.Ordinal);
    }

    // --- The set-up is written down ---------------------------------------------------------------------------------

    [Fact]
    public void The_runbook_covers_the_role_the_keys_the_bucket_and_the_sixty_day_rule()
    {
        var runbook = Read("docs", "runbooks", "backup-and-restore.md");

        Assert.Contains("CREATE ROLE backup_reader", runbook, StringComparison.Ordinal);
        Assert.Contains("age-keygen", runbook, StringComparison.Ordinal);
        Assert.Contains("35 days", runbook, StringComparison.Ordinal);
        Assert.Contains("60 days", runbook, StringComparison.Ordinal);
        Assert.Contains("Enable workflow", runbook, StringComparison.Ordinal);
        Assert.Contains("verify-audit --restored-backup", runbook, StringComparison.Ordinal);

        foreach (var secret in new[] { "BACKUP_DATABASE_URL", "BACKUP_R2_ENDPOINT", "BACKUP_R2_BUCKET", "BACKUP_R2_ACCESS_KEY_ID", "BACKUP_R2_SECRET_ACCESS_KEY", "BACKUP_R2_READ_ACCESS_KEY_ID", "BACKUP_R2_READ_SECRET_ACCESS_KEY", "BACKUP_AGE_RESTORE_KEY", "RESTORE_CHECK_SETTINGS" })
        {
            Assert.Contains(secret, runbook, StringComparison.Ordinal);
            Assert.Contains(secret, Workflow(secret.StartsWith("BACKUP_R2_READ", StringComparison.Ordinal) || secret is "BACKUP_AGE_RESTORE_KEY" or "RESTORE_CHECK_SETTINGS" ? "restore-check.yml" : "backup.yml"), StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(@"^\s*-?\s*uses:\s")]
    private static partial Regex UsesLine();
}
