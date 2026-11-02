using System.Text;
using Factory.Contracts;
using Factory.Engine.Workspace;

namespace Factory.Tests;

public class FilePatcherTests : IDisposable
{
    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    private static FileOperationDto Op(string path, string action, string content = "") => new(path, action, content);

    [Fact]
    public void Create_writes_a_new_file_and_its_directories()
    {
        var result = FilePatcher.Apply(_repo.Path, [Op("src/deep/a.txt", FileActions.Create, "hello")]);

        Assert.Equal("hello", File.ReadAllText(_repo.File("src/deep/a.txt")));
        Assert.Equal(["CREATE src/deep/a.txt"], result.Applied);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Overwrite_replaces_content()
    {
        _repo.Write("a.txt", "old");

        FilePatcher.Apply(_repo.Path, [Op("a.txt", FileActions.Overwrite, "new")]);

        Assert.Equal("new", File.ReadAllText(_repo.File("a.txt")));
    }

    [Fact]
    public void Delete_removes_a_file()
    {
        _repo.Write("a.txt", "old");

        FilePatcher.Apply(_repo.Path, [Op("a.txt", FileActions.Delete)]);

        Assert.False(File.Exists(_repo.File("a.txt")));
    }

    [Fact]
    public void Mismatched_actions_are_applied_with_a_warning()
    {
        _repo.Write("exists.txt", "old");

        var result = FilePatcher.Apply(_repo.Path,
        [
            Op("exists.txt", FileActions.Create, "x"),
            Op("missing.txt", FileActions.Overwrite, "y"),
            Op("gone.txt", FileActions.Delete)
        ]);

        Assert.Equal(3, result.Warnings.Count);
        Assert.Equal("x", File.ReadAllText(_repo.File("exists.txt")));
        Assert.Equal("y", File.ReadAllText(_repo.File("missing.txt")));
    }

    [Fact]
    public void Actions_are_case_insensitive() =>
        Assert.Equal(["CREATE a.txt"], FilePatcher.Apply(_repo.Path, [Op("a.txt", "create", "x")]).Applied);

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("a/../../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("C:\\Windows\\evil.txt")]
    [InlineData("/etc/evil.txt")]
    [InlineData(".git/config")]
    [InlineData("sub/.GIT/hooks/pre-commit")]
    [InlineData("a.txt:stream")]
    [InlineData("")]
    [InlineData("   ")]
    public void Unsafe_paths_are_rejected(string path) =>
        Assert.Throws<PatchRejectedException>(() => FilePatcher.Apply(_repo.Path, [Op(path, FileActions.Create, "x")]));

    [Fact]
    public void A_set_with_one_bad_operation_writes_nothing()
    {
        Assert.Throws<PatchRejectedException>(() => FilePatcher.Apply(_repo.Path,
        [
            Op("good.txt", FileActions.Create, "x"),
            Op("../bad.txt", FileActions.Create, "y")
        ]));

        Assert.False(File.Exists(_repo.File("good.txt")));
    }

    [Fact]
    public void Unknown_action_is_rejected_before_anything_is_written()
    {
        Assert.Throws<PatchRejectedException>(() => FilePatcher.Apply(_repo.Path,
        [
            Op("good.txt", FileActions.Create, "x"),
            Op("other.txt", "PATCH", "y")
        ]));

        Assert.False(File.Exists(_repo.File("good.txt")));
    }

    [Fact]
    public void Overwrite_keeps_crlf_and_bom_of_the_existing_file()
    {
        File.WriteAllBytes(_repo.File("a.cs"), [.. Encoding.UTF8.Preamble, .. "one\r\ntwo\r\n"u8.ToArray()]);

        FilePatcher.Apply(_repo.Path, [Op("a.cs", FileActions.Overwrite, "one\ntwo\nthree\n")]);

        byte[] expected = [.. Encoding.UTF8.Preamble, .. "one\r\ntwo\r\nthree\r\n"u8.ToArray()];
        Assert.Equal(expected, File.ReadAllBytes(_repo.File("a.cs")));
    }

    [Fact]
    public void New_files_use_lf_without_bom()
    {
        FilePatcher.Apply(_repo.Path, [Op("a.txt", FileActions.Create, "one\r\ntwo\n")]);

        Assert.Equal("one\ntwo\n"u8.ToArray(), File.ReadAllBytes(_repo.File("a.txt")));
    }
}
