using MinerBot.Shared.Requests;
using System.Text;
using System.Text.RegularExpressions;

namespace MinerBot.Shared.Tests
{
    // The signature on the dashboard's requests to the bot. The four vectors were worked out from the layout's rules by two
    // scripts of their own and by .NET, and are written out here byte for byte. Their key is the 32 bytes 00 01 ... 1f.
    public class RequestSigningTests
    {
        static readonly byte[] Key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        static readonly DateTime Made = new(2026, 11, 20, 15, 4, 5, 123, DateTimeKind.Utc);
        static readonly DateTime MadeOnTheSecond = new(2026, 11, 20, 15, 4, 5, DateTimeKind.Utc);

        static RequestFields V1 => new("00112233445566778899aabbccddeeff", RequestKinds.PostsPause, 123456789012345678, "Owner", RequestRoles.Owner,
            null, null, "Live check", Made, Made.AddMinutes(2));

        static RequestFields V2 => new("ffeeddccbbaa99887766554433221100", RequestKinds.GuildTestMessage, 234567890123456789, "Zo\u00EB \U0001F3AE",
            RequestRoles.ServerAdmin, 345678901234567890, 456789012345678901, null, MadeOnTheSecond, MadeOnTheSecond.AddMinutes(2));

        static RequestFields V3 => V1 with { RequestKey = "0123456789abcdef0123456789abcdef", Kind = RequestKinds.StatusSet, Argument = null };

        static RequestFields V4 => V3 with { Argument = "" };

        // Each vector's fields, its bytes field by field, and its HMAC-SHA256
        static readonly Dictionary<string, (RequestFields Fields, string[] Bytes, int Length, string Signature)> Vectors = new()
        {
            ["V1"] = (V1,
            [
                "6d696e6572626f742d726571756573742d7631",                                         // "minerbot-request-v1"
                "01 00000020 3030313132323333343435353636373738383939616162626363646465656666",   // request_key
                "01 0000000b 706f7374732e7061757365",                                             // kind, posts.pause
                "01 00000012 313233343536373839303132333435363738",                               // actor_id
                "01 00000005 4f776e6572",                                                         // actor_name, Owner
                "01 00000005 6f776e6572",                                                         // actor_role, owner
                "00",                                                                             // guild_id, null
                "00",                                                                             // channel_id, null
                "01 0000000a 4c69766520636865636b",                                               // argument, Live check
                "01 00000018 323032362d31312d32305431353a30343a30352e3132335a",                   // created_at
                "01 00000018 323032362d31312d32305431353a30363a30352e3132335a"                    // expires_at
            ], 190, "08a3017ad7e05a862348dae7a01b286b2fb0f2e0839bd36a1eb1c6285ec27868"),
            ["V2"] = (V2,
            [
                "6d696e6572626f742d726571756573742d7631",                                         // "minerbot-request-v1"
                "01 00000020 6666656564646363626261613939383837373636353534343333323231313030",   // request_key
                "01 00000012 6775696c642e746573745f6d657373616765",                               // kind, guild.test_message
                "01 00000012 323334353637383930313233343536373839",                               // actor_id
                "01 00000009 5a6fc3ab20f09f8eae",                                                 // actor_name, a two-byte letter and a four-byte emoji
                "01 0000000c 7365727665725f61646d696e",                                           // actor_role, server_admin
                "01 00000012 333435363738393031323334353637383930",                               // guild_id
                "01 00000012 343536373839303132333435363738393031",                               // channel_id
                "00",                                                                             // argument, null
                "01 00000018 323032362d31312d32305431353a30343a30352e3030305a",                   // created_at, .000
                "01 00000018 323032362d31312d32305431353a30363a30352e3030305a"                    // expires_at
            ], 238, "f743a1a0b84f22d65ca3c0023fd3b55fe4ba1616c92370fd96fedb05f306e34a"),
            ["V3"] = (V3,
            [
                "6d696e6572626f742d726571756573742d7631",                                         // "minerbot-request-v1"
                "01 00000020 3031323334353637383961626364656630313233343536373839616263646566",   // request_key
                "01 0000000a 7374617475732e736574",                                               // kind, status.set
                "01 00000012 313233343536373839303132333435363738",                               // actor_id
                "01 00000005 4f776e6572",                                                         // actor_name
                "01 00000005 6f776e6572",                                                         // actor_role
                "00",                                                                             // guild_id, null
                "00",                                                                             // channel_id, null
                "00",                                                                             // argument, null
                "01 00000018 323032362d31312d32305431353a30343a30352e3132335a",                   // created_at
                "01 00000018 323032362d31312d32305431353a30363a30352e3132335a"                    // expires_at
            ], 175, "e06b7d33c91221b2596073dcfe9b4697a7e7a77e2457c14c0b047b36384d35dc"),
            ["V4"] = (V4,
            [
                "6d696e6572626f742d726571756573742d7631",                                         // "minerbot-request-v1"
                "01 00000020 3031323334353637383961626364656630313233343536373839616263646566",   // request_key
                "01 0000000a 7374617475732e736574",                                               // kind, status.set
                "01 00000012 313233343536373839303132333435363738",                               // actor_id
                "01 00000005 4f776e6572",                                                         // actor_name
                "01 00000005 6f776e6572",                                                         // actor_role
                "00",                                                                             // guild_id, null
                "00",                                                                             // channel_id, null
                "01 00000000",                                                                    // argument, empty
                "01 00000018 323032362d31312d32305431353a30343a30352e3132335a",                   // created_at
                "01 00000018 323032362d31312d32305431353a30363a30352e3132335a"                    // expires_at
            ], 179, "d5607f61d29292c9b7d37b8dd7f57a873c57d9292c60aa89b847a54ab333581a")
        };

        static byte[] Hex(IEnumerable<string> parts) => Convert.FromHexString(string.Concat(parts).Replace(" ", ""));

        [Theory]
        [InlineData("V1")]
        [InlineData("V2")]
        [InlineData("V3")]
        [InlineData("V4")]
        public void VectorsGiveTheirBytesAndSignatures(string vector)
        {
            var (fields, parts, length, signature) = Vectors[vector];
            var bytes = RequestSigning.CanonicalBytes(fields);

            Assert.Equal(length, bytes.Length);
            Assert.Equal(Convert.ToHexStringLower(Hex(parts)), Convert.ToHexStringLower(bytes));
            Assert.Equal(signature, Convert.ToHexStringLower(RequestSigning.Sign(Key, fields)));
            Assert.True(RequestSigning.Verify(Key, fields, Convert.FromHexString(signature)));
        }

        // Each signed field, changed by one character, given a value where it had none, or with its value taken away
        public static TheoryData<string> SignedFields => new(
            "request_key", "kind", "actor_id", "actor_name", "actor_role", "guild_id", "channel_id", "argument", "created_at", "expires_at");

        static IEnumerable<RequestFields> Changed(string field, RequestFields fields) => field switch
        {
            "request_key" => [fields with { RequestKey = "1" + fields.RequestKey[1..] }],
            "kind" => [fields with { Kind = fields.Kind[..^1] + "x" }],
            "actor_id" => [fields with { ActorId = fields.ActorId + 1 }],
            "actor_name" => [fields with { ActorName = "x" + fields.ActorName[1..] }],
            "actor_role" => [fields with { ActorRole = fields.ActorRole[..^1] + "x" }],
            "guild_id" => fields.GuildId is { } guildId ? [fields with { GuildId = guildId + 1 }, fields with { GuildId = null }] : [fields with { GuildId = 1 }],
            "channel_id" => fields.ChannelId is { } channelId ? [fields with { ChannelId = channelId + 1 }, fields with { ChannelId = null }] : [fields with { ChannelId = 1 }],
            "argument" => fields.Argument is { } argument ? [fields with { Argument = argument[..^1] + "x" }, fields with { Argument = null }] : [fields with { Argument = "x" }],
            "created_at" => [fields with { CreatedAt = fields.CreatedAt.AddMilliseconds(1) }],
            "expires_at" => [fields with { ExpiresAt = fields.ExpiresAt.AddMilliseconds(-1) }],
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "The test has no such field")
        };

        [Theory]
        [MemberData(nameof(SignedFields))]
        public void EverySignedFieldChangesTheSignature(string field)
        {
            foreach (var original in new[] { V1, V2 })
            {
                var signature = RequestSigning.Sign(Key, original);
                var changes = Changed(field, original).ToList();

                Assert.NotEmpty(changes);
                Assert.All(changes, changed =>
                {
                    Assert.NotEqual(original, changed);
                    Assert.NotEqual(RequestSigning.CanonicalBytes(original), RequestSigning.CanonicalBytes(changed));
                    Assert.False(RequestSigning.Verify(Key, changed, signature), $"{field} changed and the signature still verified");
                });
            }
        }

        [Fact]
        public void NullAndEmptyAreSignedApart()
        {
            Assert.NotEqual(RequestSigning.CanonicalBytes(V3), RequestSigning.CanonicalBytes(V4));
            Assert.NotEqual(RequestSigning.Sign(Key, V3), RequestSigning.Sign(Key, V4));
            Assert.False(RequestSigning.Verify(Key, V4, RequestSigning.Sign(Key, V3)));
            Assert.False(RequestSigning.Verify(Key, V1 with { Argument = "" }, RequestSigning.Sign(Key, V1 with { Argument = null })));
        }

        // The same characters in the same order, split between two neighbouring fields another way, are other bytes
        [Fact]
        public void ACharacterMovedBetweenFieldsChangesTheBytes()
        {
            var pairs = new (RequestFields First, RequestFields Second, Func<RequestFields, string> Neighbours)[]
            {
                (V1, V1 with { ActorName = "Owne", ActorRole = "rowner" }, fields => fields.ActorName + fields.ActorRole),
                (V1, V1 with { ActorId = 12345678901234567, ActorName = "8Owner" }, fields => $"{fields.ActorId}{fields.ActorName}"),
                (V2, V2 with { GuildId = 3456789012345678904, ChannelId = 56789012345678901 }, fields => $"{fields.GuildId}{fields.ChannelId}")
            };

            Assert.All(pairs, pair =>
            {
                Assert.Equal(pair.Neighbours(pair.First), pair.Neighbours(pair.Second));
                Assert.NotEqual(RequestSigning.CanonicalBytes(pair.First), RequestSigning.CanonicalBytes(pair.Second));
                Assert.False(RequestSigning.Verify(Key, pair.Second, RequestSigning.Sign(Key, pair.First)));
            });

            // A text may hold the byte that starts a field. Without the lengths in front, these two would be the same bytes,
            // 01 "Own" 01 "er" 01 "x", so only the lengths keep them apart.
            var first = V1 with { ActorName = "Own\u0001er", ActorRole = "x" };
            var second = V1 with { ActorName = "Own", ActorRole = "er\u0001x" };

            Assert.Equal(WithoutLengths(first.ActorName, first.ActorRole), WithoutLengths(second.ActorName, second.ActorRole));
            Assert.NotEqual(RequestSigning.CanonicalBytes(first), RequestSigning.CanonicalBytes(second));
            Assert.False(RequestSigning.Verify(Key, second, RequestSigning.Sign(Key, first)));
        }

        static byte[] WithoutLengths(params string[] texts) => texts.SelectMany(text => new byte[] { 1 }.Concat(Encoding.UTF8.GetBytes(text))).ToArray();

        // DATETIME(3) keeps milliseconds, and MySql.Data reads times back with no kind, so a time with more than that, or a
        // local one, could never verify as the bot reads it back
        [Fact]
        public void SubMillisecondTicksAndLocalTimesAreRefused()
        {
            var refused = new[]
            {
                V1 with { CreatedAt = Made.AddTicks(1) },
                V1 with { ExpiresAt = V1.ExpiresAt.AddTicks(9999) },
                V1 with { CreatedAt = DateTime.SpecifyKind(Made, DateTimeKind.Local) },
                V1 with { ExpiresAt = DateTime.SpecifyKind(V1.ExpiresAt, DateTimeKind.Local) }
            };

            Assert.All(refused, fields =>
            {
                Assert.ThrowsAny<ArgumentException>(() => RequestSigning.CanonicalBytes(fields));
                Assert.ThrowsAny<ArgumentException>(() => RequestSigning.Sign(Key, fields));
                Assert.False(RequestSigning.Verify(Key, fields, RequestSigning.Sign(Key, V1)));
            });

            // A time with no kind is taken as the UTC it holds, as the bot reads them
            Assert.Equal(RequestSigning.CanonicalBytes(V1), RequestSigning.CanonicalBytes(V1 with { CreatedAt = DateTime.SpecifyKind(Made, DateTimeKind.Unspecified) }));
        }

        // UTF-8 can't write half of a surrogate pair. A lenient encoder writes U+FFFD for any half, so two different names
        // would sign alike.
        [Fact]
        public void LoneSurrogatesAreRefused()
        {
            var refused = new[]
            {
                V1 with { ActorName = "Own\uD800er" },
                V1 with { ActorName = "Owner\uDBFF" },
                V1 with { Argument = "\uDC00Live check" },
                V1 with { Argument = "Live check\uD83C" }
            };

            Assert.All(refused, fields =>
            {
                Assert.ThrowsAny<ArgumentException>(() => RequestSigning.CanonicalBytes(fields));
                Assert.False(RequestSigning.Verify(Key, fields, RequestSigning.Sign(Key, V1)));
            });

            // A whole pair is fine
            Assert.True(RequestSigning.Verify(Key, V2, RequestSigning.Sign(Key, V2)));
        }

        [Fact]
        public void ASignatureOfAnotherLengthIsRefused()
        {
            var signature = RequestSigning.Sign(Key, V1);

            Assert.Equal(RequestSigning.SignatureLength, signature.Length);
            Assert.False(RequestSigning.Verify(Key, V1, signature[..31]));
            Assert.False(RequestSigning.Verify(Key, V1, [.. signature, 0]));
            Assert.False(RequestSigning.Verify(Key, V1, []));

            // And a key of another length signs nothing
            Assert.False(RequestSigning.Verify(Key[..31], V1, signature));
            Assert.Throws<ArgumentException>(() => RequestSigning.Sign(Key[..31], V1));
            Assert.Throws<ArgumentException>(() => RequestSigning.Sign([.. Key, 0], V1));
        }

        [Fact]
        public void AnotherKeysSignatureIsRefused()
        {
            var other = Key.Select(value => (byte)(value + 0x20)).ToArray();

            Assert.NotEqual(RequestSigning.Sign(Key, V1), RequestSigning.Sign(other, V1));
            Assert.False(RequestSigning.Verify(other, V1, RequestSigning.Sign(Key, V1)));
            Assert.True(RequestSigning.Verify(other, V1, RequestSigning.Sign(other, V1)));
        }

        // The bot verifies whatever a row holds, so nothing in one can make it throw
        [Fact]
        public void VerifyNeverThrows()
        {
            var signature = RequestSigning.Sign(Key, V1);
            var bad = new RequestFields?[]
            {
                null,
                V1 with { RequestKey = null! },
                V1 with { Kind = null! },
                V1 with { ActorName = null! },
                V1 with { ActorRole = null! },
                V1 with { CreatedAt = DateTime.Now },
                V1 with { ExpiresAt = DateTime.MaxValue },
                V1 with { CreatedAt = DateTime.MinValue },
                V1 with { ActorName = "\uDFFF" },
                V1 with { Argument = new string('\uD800', 3) }
            };

            foreach (var fields in bad)
            {
                Assert.False(RequestSigning.Verify(Key, fields, signature));
                Assert.False(RequestSigning.Verify([], fields, signature));
                Assert.False(RequestSigning.Verify(Key, fields, []));
            }
        }

        // A comparison that stops at the first byte that differs takes longer the more of a guess is right, so the code
        // must compare in fixed time. Read from RequestSigning's own source, which the project copies in.
        [Fact]
        public void SignaturesAreComparedInFixedTime()
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "shared-source", "RequestSigning.cs"));

            Assert.Contains("CryptographicOperations.FixedTimeEquals(expected, signature)", source);
            Assert.DoesNotContain("SequenceEqual", source);
            Assert.DoesNotContain(".Equals(", source);
            Assert.DoesNotMatch(new Regex(@"\b(signature|expected)\s*[!=]=|[!=]=\s*(signature|expected)\b", RegexOptions.IgnoreCase), source);
        }

        [Fact]
        public void KeyMustBe64SmallHexDigits()
        {
            var good = Convert.ToHexStringLower(Key);

            Assert.Null(RequestSigning.KeyProblem(good));
            Assert.Equal(Key, RequestSigning.ParseKey(good));
            Assert.Null(RequestSigning.KeyProblem("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));

            foreach (var missing in new[] { null, "" })
            {
                Assert.Equal("isn't set", RequestSigning.KeyProblem(missing));
                Assert.Null(RequestSigning.ParseKey(missing));
            }

            foreach (var malformed in new[]
            {
                good[..63], good + "0", good.ToUpperInvariant(), good[..63] + "A", good[..63] + "g", " " + good[1..], good + "\n", good[..62] + "\r\n",
                "0x" + good[2..], new string(' ', 64), Convert.ToHexStringLower(Key[..16])
            })
            {
                Assert.Equal("isn't 64 hexadecimal digits in small letters", RequestSigning.KeyProblem(malformed));
                Assert.Null(RequestSigning.ParseKey(malformed));
            }
        }

        // A problem is said after the setting's name, where a log or a page would show it, so it never holds the value
        [Fact]
        public void KeyProblemsNeverQuoteTheKey()
        {
            var good = Convert.ToHexStringLower(Key);

            foreach (var malformed in new[] { good[..63], good.ToUpperInvariant(), good + "ff", "not a key at all, but a long sentence instead" })
            {
                var problem = RequestSigning.KeyProblem(malformed);

                Assert.NotNull(problem);

                for (var start = 0; start + 8 <= malformed.Length; start++)
                    Assert.DoesNotContain(malformed.Substring(start, 8), problem, StringComparison.OrdinalIgnoreCase);
            }
        }

        // DATETIME(3) drops what's past the millisecond without a word, so a new request is made to the millisecond, keeping
        // its kind of time, and expires after its kind's expiry
        [Fact]
        public void ANewRequestIsMadeToTheMillisecond()
        {
            var now = Made.AddTicks(9999);
            var pause = RequestKinds.Find(RequestKinds.PostsPause)!;
            var fields = RequestFields.New("00112233445566778899aabbccddeeff", pause, 123456789012345678, "Owner", RequestRoles.Owner, null, null, "Live check", now);

            Assert.Equal(Made, fields.CreatedAt);
            Assert.Equal(DateTimeKind.Utc, fields.CreatedAt.Kind);
            Assert.Equal(Made.AddMinutes(10), fields.ExpiresAt);
            Assert.Equal(RequestKinds.PostsPause, fields.Kind);
            Assert.True(RequestSigning.Verify(Key, fields, RequestSigning.Sign(Key, fields)));

            Assert.Matches("^[0-9a-f]{32}$", RequestFields.NewKey());
            Assert.NotEqual(RequestFields.NewKey(), RequestFields.NewKey());
        }
    }
}
