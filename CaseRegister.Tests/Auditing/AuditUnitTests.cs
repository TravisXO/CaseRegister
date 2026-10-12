using System.Text.Json.Nodes;
using CaseRegister.Application.Auditing;

namespace CaseRegister.Tests.Auditing;

/// <summary>RFC 8785 canonical JSON: the same data always gives the same bytes.</summary>
public class JcsTests
{
    private static string Canonical(string json) => Jcs.Canonicalise(JsonNode.Parse(json));

    // The number examples are from RFC 8785, section 3.2.2.3 and Appendix B.
    [Theory]
    [InlineData(333333333.33333329, "333333333.3333333")]
    [InlineData(1E30, "1e+30")]
    [InlineData(4.50, "4.5")]
    [InlineData(2e-3, "0.002")]
    [InlineData(0.000000000000000000000000001, "1e-27")]
    [InlineData(0.0, "0")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    [InlineData(9007199254740992.0, "9007199254740992")]
    [InlineData(1e21, "1e+21")]
    [InlineData(1e20, "100000000000000000000")]
    [InlineData(0.000001, "0.000001")]
    [InlineData(0.0000001, "1e-7")]
    [InlineData(-1.5, "-1.5")]
    [InlineData(1.5e300, "1.5e+300")]
    [InlineData(100.0, "100")]
    [InlineData(123456789012345680000.0, "123456789012345680000")]
    public void Numbers_are_written_the_way_ECMAScript_writes_them(double number, string expected)
    {
        Assert.Equal(expected, Jcs.FormatNumber(number));
    }

    [Fact]
    public void Minus_zero_is_just_zero()
    {
        Assert.Equal("0", Jcs.FormatNumber(double.NegativeZero));
    }

    [Fact]
    public void There_is_no_JSON_for_not_a_number_or_infinity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Jcs.FormatNumber(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Jcs.FormatNumber(double.PositiveInfinity));
    }

    [Fact]
    public void Keys_are_sorted_by_UTF16_code_units_as_in_the_RFC_example()
    {
        // RFC 8785 section 3.2.3: carriage return, "1", U+0080, o-umlaut, euro sign, the emoji (a surrogate pair), a Hebrew letter.
        var input = "{\"\\u20ac\":\"Euro Sign\",\"\\r\":\"Carriage Return\",\"\\ufb33\":\"Hebrew Letter Dalet With Dagesh\",\"1\":\"One\","
            + "\"\\ud83d\\ude00\":\"Emoji: Grinning Face\",\"\\u0080\":\"Control\",\"\\u00f6\":\"Latin Small Letter O With Diaeresis\"}";

        var canonical = Canonical(input);

        var order = JsonNode.Parse(canonical)!.AsObject().Select(property => property.Value!.GetValue<string>()).ToArray();
        Assert.Equal(
            ["Carriage Return", "One", "Control", "Latin Small Letter O With Diaeresis", "Euro Sign", "Emoji: Grinning Face", "Hebrew Letter Dalet With Dagesh"],
            order);
        Assert.StartsWith("{\"\\r\":\"Carriage Return\",\"1\":\"One\",", canonical, StringComparison.Ordinal);
        Assert.Contains("\"\u0080\":\"Control\"", canonical, StringComparison.Ordinal); // not escaped
    }

    [Fact]
    public void There_is_no_whitespace_and_the_RFC_example_comes_out_as_written()
    {
        // RFC 8785 section 3.2.2.
        var canonical = Canonical("{\n  \"numbers\": [333333333.33333329, 1E30, 4.50, 2e-3, 0.000000000000000000000000001],\n  \"string\": \"\\u20ac$\\u000F\\u000aA'\\u0042\\u0022\\u005c\\\\\\\"\\/\",\n  \"literals\": [null, true, false]\n}");

        Assert.Equal(
            "{\"literals\":[null,true,false],\"numbers\":[333333333.3333333,1e+30,4.5,0.002,1e-27],\"string\":\"\u20ac$\\u000f\\nA'B\\\"\\\\\\\\\\\"/\"}",
            canonical);
    }

    [Fact]
    public void Strings_are_escaped_the_one_minimal_way()
    {
        Assert.Equal("\"\\u0000\\u001f\"", Canonical("\"\\u0000\\u001f\""));
        Assert.Equal("\"\\b\\f\\n\\r\\t\"", Canonical("\"\\b\\f\\n\\r\\t\""));
        Assert.Equal("\"a/b\"", Canonical("\"a\\/b\"")); // a slash is not escaped
        Assert.Equal("\"caf\u00e9\"", Canonical("\"caf\\u00e9\"")); // nor are letters outside ASCII
        Assert.Equal("\"\u007f\"", Canonical("\"\\u007f\"")); // 127 is not a control character here
    }

    [Fact]
    public void The_order_data_arrives_in_makes_no_difference_but_array_order_does()
    {
        Assert.Equal(Canonical("{\"b\":1,\"a\":{\"y\":2,\"x\":[3,1,2]}}"), Canonical("{\"a\":{\"x\":[3,1,2],\"y\":2},\"b\":1}"));
        Assert.NotEqual(Canonical("[1,2,3]"), Canonical("[3,2,1]"));
    }

    [Fact]
    public void Null_and_empty_values_have_their_canonical_forms()
    {
        Assert.Equal("null", Jcs.Canonicalise(null));
        Assert.Equal("{}", Canonical("{ }"));
        Assert.Equal("[]", Canonical("[ ]"));
        Assert.Equal("{\"a\":null}", Canonical("{\"a\" : null}"));
    }

    [Fact]
    public void Whole_numbers_beyond_what_a_double_holds_exactly_go_through_double_as_the_RFC_says()
    {
        Assert.Equal("9007199254740992", Canonical("9007199254740992"));
        Assert.Equal("1e+21", Canonical("1000000000000000000000"));
    }

    [Fact]
    public void The_bytes_are_UTF8()
    {
        Assert.Equal([0x22, 0xC3, 0xA9, 0x22], Jcs.CanonicaliseToUtf8(JsonNode.Parse("\"\u00e9\"")));
    }
}

public class AuditChainFormatTests
{
    private static readonly byte[] _key = Enumerable.Range(1, 32).Select(index => (byte)index).ToArray();
    private static readonly byte[] _otherKey = Enumerable.Range(2, 32).Select(index => (byte)index).ToArray();

    private static AuditFields Fields() => new(
        7,
        "2026-10-12T08:00:00.123456Z",
        "user",
        "0199aaaa-0000-7000-8000-000000000001",
        "Admin",
        "UserSuspended",
        "User",
        "0199aaaa-0000-7000-8000-000000000002",
        "0199aaaa-0000-7000-8000-000000000003",
        "0HN-trace",
        "203.0.113.5",
        "a note",
        "{\"state\":\"Approved\"}",
        "{\"state\":\"Suspended\"}",
        new string('a', 64),
        "k1");

    [Fact]
    public void The_hash_is_64_lower_case_hex_characters_and_the_same_every_time()
    {
        var hash = AuditChainFormat.Hash(_key, Fields());

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, AuditChainFormat.Hash(_key, Fields()));
    }

    [Fact]
    public void The_first_entry_follows_sixty_four_zeros()
    {
        Assert.Equal(new string('0', 64), AuditChainFormat.GenesisHash);
    }

    public static TheoryData<string, Func<AuditFields, AuditFields>> EveryField() => new()
    {
        { "sequence", f => f with { Sequence = 8 } },
        { "occurredAt", f => f with { OccurredAt = "2026-10-12T08:00:00.123457Z" } },
        { "actorType", f => f with { ActorType = "system" } },
        { "actorId", f => f with { ActorId = "0199aaaa-0000-7000-8000-0000000000ff" } },
        { "actorRole", f => f with { ActorRole = "CaseReviewer" } },
        { "action", f => f with { Action = "UserReactivated" } },
        { "targetType", f => f with { TargetType = "Agency" } },
        { "targetId", f => f with { TargetId = "other" } },
        { "agencyId", f => f with { AgencyId = null } },
        { "correlationId", f => f with { CorrelationId = "another-trace" } },
        { "ipAddress", f => f with { IpAddress = "203.0.113.6" } },
        { "detail", f => f with { Detail = "another note" } },
        { "before", f => f with { Before = "{\"state\":\"Pending\"}" } },
        { "after", f => f with { After = "{\"state\":\"Approved\"}" } },
        { "previousHash", f => f with { PreviousHash = new string('b', 64) } },
        { "keyId", f => f with { KeyId = "k2" } },
    };

    [Theory]
    [MemberData(nameof(EveryField))]
    public void Changing_any_single_field_changes_the_hash(string field, Func<AuditFields, AuditFields> change)
    {
        Assert.NotEqual(AuditChainFormat.Hash(_key, Fields()), AuditChainFormat.Hash(_key, change(Fields())));
        Assert.NotEmpty(field);
    }

    [Fact]
    public void Every_field_of_the_entry_is_covered_by_the_hash()
    {
        var covered = EveryField().Select(row => (string)row[0]).Order(StringComparer.Ordinal);

        Assert.Equal(typeof(AuditFields).GetProperties().Select(property => char.ToLowerInvariant(property.Name[0]) + property.Name[1..]).Order(StringComparer.Ordinal), covered);
    }

    [Fact]
    public void The_hash_needs_the_key()
    {
        Assert.NotEqual(AuditChainFormat.Hash(_key, Fields()), AuditChainFormat.Hash(_otherKey, Fields()));

        // And it is not a plain hash of the canonical text that anyone could recompute.
        var plain = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AuditChainFormat.Canonical(Fields()))));
        Assert.NotEqual(plain, AuditChainFormat.Hash(_key, Fields()));
    }

    [Fact]
    public void The_entry_is_hashed_as_canonical_JSON_with_the_previous_hash_inside_it()
    {
        var canonical = AuditChainFormat.Canonical(Fields());

        Assert.StartsWith("{\"action\":\"UserSuspended\",\"actorId\":", canonical, StringComparison.Ordinal); // keys sorted
        Assert.Contains($"\"previousHash\":\"{new string('a', 64)}\"", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", canonical.Replace("a note", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("\"after\":{\"state\":\"Suspended\"}", canonical, StringComparison.Ordinal); // JSON inside, not a string
    }

    [Fact]
    public void Before_and_after_hash_the_same_whatever_order_their_keys_were_written_in()
    {
        var one = Fields() with { Before = "{\"b\":1,\"a\":2}" };
        var two = Fields() with { Before = "{ \"a\": 2, \"b\": 1 }" };

        Assert.Equal(AuditChainFormat.Hash(_key, one), AuditChainFormat.Hash(_key, two));
    }

    [Fact]
    public void Times_are_cut_to_the_microsecond_so_what_Postgres_stores_hashes_the_same()
    {
        var precise = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero).AddTicks(1_234_567); // 7 digits: 100 ns resolution

        Assert.Equal("2026-10-12T08:00:00.123456Z", AuditChainFormat.FormatTime(precise));
        Assert.Equal(precise.AddTicks(-7), AuditChainFormat.TruncateToMicroseconds(precise));
        Assert.Equal(AuditChainFormat.FormatTime(precise), AuditChainFormat.FormatTime(AuditChainFormat.TruncateToMicroseconds(precise)));
        Assert.Equal(TimeSpan.Zero, AuditChainFormat.TruncateToMicroseconds(precise.ToOffset(TimeSpan.FromHours(2))).Offset);
    }

    [Fact]
    public void A_new_day_starts_at_midnight_in_Zambia()
    {
        Assert.Equal(new DateOnly(2026, 10, 12), AuditChainFormat.ZambianDate(new DateTimeOffset(2026, 10, 12, 21, 59, 59, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 10, 13), AuditChainFormat.ZambianDate(new DateTimeOffset(2026, 10, 12, 22, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void An_anchors_signature_covers_all_of_it()
    {
        var mac = AuditChainFormat.AnchorMac(_key, 5, new string('a', 64), "k1", "2026-10-12T08:00:00.000000Z", "2026-10-12");

        Assert.Equal(mac, AuditChainFormat.AnchorMac(_key, 5, new string('a', 64), "k1", "2026-10-12T08:00:00.000000Z", "2026-10-12"));
        Assert.NotEqual(mac, AuditChainFormat.AnchorMac(_key, 6, new string('a', 64), "k1", "2026-10-12T08:00:00.000000Z", "2026-10-12"));
        Assert.NotEqual(mac, AuditChainFormat.AnchorMac(_key, 5, new string('b', 64), "k1", "2026-10-12T08:00:00.000000Z", "2026-10-12"));
        Assert.NotEqual(mac, AuditChainFormat.AnchorMac(_otherKey, 5, new string('a', 64), "k1", "2026-10-12T08:00:00.000000Z", "2026-10-12"));
    }
}

public class AuditRedactorTests
{
    // Plain words: a long unbroken run of letters would itself look like a token and be redacted.
    private static string Filler(int length) => string.Concat(Enumerable.Repeat("lorem ipsum ", (length / 12) + 1))[..length];

    [Fact]
    public void Nothing_is_nothing()
    {
        Assert.Null(AuditRedactor.Prepare(null));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("newPassword")]
    [InlineData("currentPassword")]
    [InlineData("token")]
    [InlineData("setupToken")]
    [InlineData("accessToken")]
    [InlineData("twoFactorCode")]
    [InlineData("recoveryCode")]
    [InlineData("code")]
    [InlineData("otp")]
    [InlineData("secret")]
    [InlineData("apiKey")]
    [InlineData("api_key")]
    [InlineData("nrc")]
    [InlineData("Nrc")]
    [InlineData("passportNumber")]
    [InlineData("narrative")]
    [InlineData("authorization")]
    [InlineData("cookie")]
    [InlineData("connectionString")]
    [InlineData("sessionId")]
    [InlineData("passwordHash")]
    public void A_value_called_by_a_secret_name_is_never_kept(string name)
    {
        var json = AuditRedactor.Prepare(new Dictionary<string, object?> { [name] = "hunter2-this-is-secret", ["state"] = "Approved" })!;

        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        Assert.Contains("[redacted]", json, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"Approved\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void It_looks_inside_nested_objects_and_lists()
    {
        var json = AuditRedactor.Prepare(new { user = new { name = "Jane", password = "hunter2" }, items = new object[] { new { token = "abc" }, new { fine = 1 } } })!;

        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", json, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"Jane\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fine\":1", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("123456/10/1")]
    [InlineData("an NRC of 987654/32/1 inside text")]
    public void An_NRC_is_redacted_whatever_the_property_is_called(string value)
    {
        var json = AuditRedactor.Prepare(new { note = value })!;

        Assert.DoesNotContain("/1", json.Replace("[redacted]", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal("{\"note\":\"[redacted]\"}", json);
    }

    [Fact]
    public void A_long_random_looking_string_is_redacted_whatever_the_property_is_called()
    {
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var json = AuditRedactor.Prepare(new { whatever = token, shortValue = "Approved" })!;

        Assert.DoesNotContain(token, json, StringComparison.Ordinal);
        Assert.Contains("Approved", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("payloadHash")]
    [InlineData("errorCode")]
    [InlineData("tokenHash")]
    public void Names_that_only_look_like_secrets_are_kept(string name)
    {
        var json = AuditRedactor.Prepare(new Dictionary<string, object?> { [name] = "keep-me" })!;

        Assert.Contains("keep-me", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_result_is_canonical_JSON_so_it_hashes_the_same_every_time()
    {
        Assert.Equal("{\"a\":1,\"b\":2}", AuditRedactor.Prepare(new Dictionary<string, object?> { ["b"] = 2, ["a"] = 1 }));
    }

    [Fact]
    public void A_value_over_the_size_cap_is_replaced_by_a_marker_that_is_still_valid_JSON()
    {
        var json = AuditRedactor.Prepare(new { notes = Filler(10_000) })!;

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= AuditRedactor.MaxBytes);
        var parsed = JsonNode.Parse(json)!.AsObject();
        Assert.True(parsed["truncated"]!.GetValue<bool>());
        Assert.True(parsed["originalBytes"]!.GetValue<int>() > AuditRedactor.MaxBytes);
        Assert.True(parsed["preview"]!.GetValue<string>().Length <= 200);
    }

    [Fact]
    public void A_value_just_under_the_cap_is_kept_whole_and_one_over_is_not()
    {
        var fits = Filler(AuditRedactor.MaxBytes - "{\"n\":\"\"}".Length);
        var over = fits + " ";

        Assert.Contains(fits, AuditRedactor.Prepare(new { n = fits })!, StringComparison.Ordinal);
        Assert.Contains("\"truncated\":true", AuditRedactor.Prepare(new { n = over })!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_preview_of_a_large_value_is_redacted_too()
    {
        var json = AuditRedactor.Prepare(new { password = "hunter2", filler = Filler(10_000) })!;

        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
    }
}

public class AuditOptionsTests
{
    private static string Key(int length = 32) => Convert.ToBase64String(new byte[length]);

    private static AuditOptions Options(string current = "k1", string? previous = null, params (string Id, string Value)[] keys) =>
        new() { CurrentKeyId = current, PreviousKeyId = previous, Keys = keys.ToDictionary(pair => pair.Id, pair => pair.Value) };

    [Fact]
    public void A_current_key_of_at_least_32_bytes_is_enough()
    {
        Assert.Null(Options("k1", null, ("k1", Key())).Problem());
        Assert.Null(Options("k1", "k0", ("k1", Key(64)), ("k0", Key())).Problem());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_missing_current_key_id_is_a_problem(string id)
    {
        Assert.Contains("CurrentKeyId", Options(id, null, ("k1", Key())).Problem(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_current_key_that_is_not_configured_is_a_problem_that_says_which()
    {
        Assert.Contains("Audit:Keys:k1", Options("k1").Problem(), StringComparison.Ordinal);
        Assert.Contains("Audit__Keys__k1", Options("k1").Problem(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_that_is_too_short_or_not_base64_is_a_problem()
    {
        Assert.Contains("at least 32", Options("k1", null, ("k1", Key(31))).Problem(), StringComparison.Ordinal);
        Assert.Contains("base64", Options("k1", null, ("k1", "not base64!")).Problem(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_previous_key_that_is_named_must_be_there_and_must_differ()
    {
        Assert.Contains("Audit:Keys:k0", Options("k1", "k0", ("k1", Key())).Problem(), StringComparison.Ordinal);
        Assert.Contains("differ", Options("k1", "k1", ("k1", Key())).Problem(), StringComparison.Ordinal);
    }
}
