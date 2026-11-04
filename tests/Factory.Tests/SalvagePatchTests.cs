using Factory.Engine.Llm;

namespace Factory.Tests;

public class SalvagePatchTests
{
    [Fact]
    public void Keeps_the_operations_that_closed_and_names_the_file_cut_off()
    {
        const string cutOff = """
            <think>Plan the files.</think>{"summaryOfChanges": "Adds the \"OUI\" parser and lookup.", "operations": [
              {"relativeFilePath": "src/Vendors/OuiCsvParser.cs", "action": "CREATE", "fileContent": "class P { string s = \"{\\\"a\\\": [1]}\"; }"},
              {"relativeFilePath": "src/Vendors/OuiVendorLookup.cs", "action": "CREATE", "fileContent": "class L { }"},
              {"relativeFilePath": "src/Resources/oui.csv", "action": "CREATE", "fileContent": "IEEE REGISTRY,00-27-B4,Apple Inc.\nIEEE REGISTRY,00-27-B5,Ap
            """;

        var kept = StructuredOutputParser.SalvagePatch(cutOff);

        Assert.NotNull(kept);
        Assert.Equal("Adds the \"OUI\" parser and lookup.", kept.Patch.SummaryOfChanges);
        Assert.Equal(["src/Vendors/OuiCsvParser.cs", "src/Vendors/OuiVendorLookup.cs"], kept.Patch.Operations.Select(o => o.RelativeFilePath));
        Assert.Equal("class P { string s = \"{\\\"a\\\": [1]}\"; }", kept.Patch.Operations[0].FileContent);
        Assert.Equal("src/Resources/oui.csv", kept.CutOffPath);
    }

    [Fact]
    public void Nothing_is_kept_when_the_first_file_was_cut_off()
    {
        Assert.Null(StructuredOutputParser.SalvagePatch("""{"summaryOfChanges": "x", "operations": [{"relativeFilePath": "a.csv", "action": "CREATE", "fileContent": "1,2\n3,"""));
        Assert.Null(StructuredOutputParser.SalvagePatch("I could not do it."));
    }

    [Fact]
    public void A_cut_between_operations_keeps_them_all_without_a_cut_off_file()
    {
        var kept = StructuredOutputParser.SalvagePatch("""{"operations": [{"relativeFilePath": "a.txt", "action": "CREATE", "fileContent": "a"}, """);

        Assert.NotNull(kept);
        Assert.Single(kept.Patch.Operations);
        Assert.Equal("", kept.Patch.SummaryOfChanges);
        Assert.Null(kept.CutOffPath);
    }
}
