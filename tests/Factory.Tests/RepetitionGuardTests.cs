using System.Text;
using Factory.Engine.Llm;

namespace Factory.Tests;

public class RepetitionGuardTests
{
    /// <summary>Feeds text in small pieces, as a stream does, and returns the first verdict with how much had been fed by then.</summary>
    private static (RepetitionVerdict? Verdict, int Fed) Stream(string text, bool thinking = false, int piece = 7)
    {
        var guard = new RepetitionGuard();
        for (var i = 0; i < text.Length; i += piece)
        {
            var chunk = text.Substring(i, Math.Min(piece, text.Length - i));
            if (guard.Push(chunk, thinking) is { } verdict) return (verdict, i + chunk.Length);
        }
        return (null, text.Length);
    }

    private const string PatchStart = """{"summaryOfChanges":"Adds the OUI lookup.","operations":[{"relativeFilePath":"src/Resources/oui.csv","action":"CREATE","fileContent":"Registry,Assignment,Organization Name,Organization Address\n""";

    [Fact]
    public void A_table_counting_up_inside_a_json_string_is_caught_within_a_few_thousand_tokens()
    {
        // What the Code Generator wrote for 45 minutes: one made-up row after another, the address counting up.
        var rows = Enumerable.Range(0x27B0, 1000).Select(i => $"IEEE REGISTRY,00-{i >> 8:X2}-{i & 0xFF:X2},Apple Inc.,Cupertino, CA");
        var (verdict, fed) = Stream(PatchStart + string.Join("\\n", rows));

        Assert.NotNull(verdict);
        Assert.False(verdict.InThinking);
        Assert.Equal(RepetitionGuard.AnswerRun, verdict.Repeats);
        Assert.StartsWith("IEEE REGISTRY,00-", verdict.Sample);
        Assert.True(fed < 6_000, $"Caught only after {fed} characters.");
        Assert.Contains("got stuck repeating itself in its answer: 64 lines in a row like `IEEE REGISTRY", verdict.Describe("Code Generator"));
    }

    [Fact]
    public void Real_code_tests_and_short_data_files_are_not_loops()
    {
        var code = new StringBuilder();
        code.AppendLine("namespace NetMap.Discovery.Vendors;");
        code.AppendLine("public enum DeviceKind");
        code.AppendLine("{");
        foreach (var name in new[] { "Unknown", "Router", "AccessPoint", "Phone", "Tablet", "Laptop", "Desktop", "Printer", "Television", "Speaker", "Camera", "Thermostat", "Doorbell", "Plug", "Bulb", "Console", "Watch", "Hub", "Nas", "Server" })
            code.AppendLine($"    {name},");
        code.AppendLine("}");
        code.AppendLine("public class OuiVendorLookupTests");
        code.AppendLine("{");
        for (var i = 0; i < 80; i++)
        {
            code.AppendLine("    [Fact]");
            code.AppendLine($"    public void Lookup_case_{i}_returns_the_vendor_for_its_prefix()");
            code.AppendLine("    {");
            code.AppendLine($"        var lookup = new OuiVendorLookup(Fixture(\"sample-{i}.csv\"));");
            code.AppendLine($"        Assert.Equal(\"Vendor {i}\", lookup.Lookup(MacAddress.Parse(\"00:1A:{i:X2}:00:00:01\")));");
            code.AppendLine("    }");
        }
        code.AppendLine("}");
        // A real seed file: each row names a different vendor.
        var vendors = new[] { "Apple, Inc.", "Google, Inc.", "Amazon Technologies Inc.", "Samsung Electronics Co.,Ltd", "Sonos, Inc.", "Espressif Inc.", "Allterco Robotics EOOD", "Tuya Smart Inc.", "TP-LINK TECHNOLOGIES CO.,LTD.", "NETGEAR", "ASUSTek COMPUTER INC.", "Intel Corporate", "Raspberry Pi Trading Ltd", "HP Inc.", "Brother Industries, LTD.", "Canon Inc.", "Seiko Epson Corporation", "Roku, Inc", "LG Electronics", "Sony Corporation", "Microsoft Corporation", "Ubiquiti Inc", "Nintendo Co.,Ltd" };
        code.AppendLine("Registry,Assignment,Organization Name,Organization Address");
        for (var i = 0; i < vendors.Length; i++) code.AppendLine($"MA-L,{0x001A11 + i * 977:X6},\"{vendors[i]}\",Somewhere");
        // A CRC table: rows of numbers, which get a longer run before they count.
        for (var row = 0; row < 64; row++)
            code.AppendLine(string.Join(", ", Enumerable.Range(row * 4, 4).Select(i => $"0x{(uint)(i * 2654435761):X8}")) + ",");

        var (verdict, _) = Stream(code.ToString().Replace("\r\n", "\n").Replace("\n", "\\n"));

        Assert.Null(verdict);
    }

    [Fact]
    public void Thinking_that_restates_the_same_thought_is_caught_sooner()
    {
        var thought = string.Concat(Enumerable.Repeat("Wait, the assignment for Apple is 00:03:93, so the next row should be checked again.\n", 30));

        var (verdict, _) = Stream(thought, thinking: true);

        Assert.NotNull(verdict);
        Assert.True(verdict.InThinking);
        Assert.Equal(RepetitionGuard.ThinkingRun, verdict.Repeats);
        Assert.Contains("in its thinking", verdict.Describe("Task Planner"));
    }

    [Fact]
    public void Lines_that_alternate_between_a_few_shapes_are_caught_too()
    {
        var lines = Enumerable.Range(0, 300).Select(i => i % 2 == 0 ? $"Row {i}: the vendor is Apple Inc. in Cupertino" : $"Next row {i}: check the vendor again for this one");

        var (verdict, _) = Stream(string.Join("\n", lines));

        Assert.NotNull(verdict);
        Assert.Equal(0, verdict.Repeats);
    }

    [Fact]
    public void A_loop_with_no_line_breaks_is_caught_by_how_well_it_compresses()
    {
        var (verdict, fed) = Stream(string.Concat(Enumerable.Repeat("Apple Inc. Cupertino CA 95014 US ", 2_000)));

        Assert.NotNull(verdict);
        Assert.True(fed < 20_000, $"Caught only after {fed} characters.");
    }

    [Fact]
    public void The_answer_and_the_thinking_are_watched_separately()
    {
        // Each stream on its own varies, though together they alternate between two kinds of line.
        var words = new[] { "parser", "lookup", "fixture", "script", "header", "quotes", "gzip", "resource", "random", "local" };
        var guard = new RepetitionGuard();
        for (var i = 0; i < 40; i++)
        {
            Assert.Null(guard.Push($"The answer covers the {words[i % 10]} with case {words[(i / 10 + i) % 10]}\n", false));
            Assert.Null(guard.Push($"Next I think about the {words[(i + 3) % 10]} and the {words[i / 10]}\n", true));
        }
    }

    [Theory]
    [InlineData("IEEE REGISTRY,00-27-B4,Apple Inc.,Cupertino, CA", "IEEE REGISTRY,#-#-#,Apple Inc.,Cupertino, #")]
    [InlineData("IEEE REGISTRY,00-27-BA,Apple Inc.,Cupertino, CA", "IEEE REGISTRY,#-#-#,Apple Inc.,Cupertino, #")]
    [InlineData("    Assert.Equal(0x1F, buffer[12]);", "Assert.Equal(#, buffer[#]);")]
    [InlineData("var faceplate = Decode(cafe);", "var faceplate = Decode(cafe);")]
    public void A_line_s_shape_masks_numbers_and_short_hex_groups(string line, string shape) =>
        Assert.Equal(shape, RepetitionGuard.Shape(line));
}
