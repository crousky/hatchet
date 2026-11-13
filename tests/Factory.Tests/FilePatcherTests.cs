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

    [Theory]
    [InlineData(".git./hooks/pre-commit")]
    [InlineData(".GIT./config")]
    [InlineData("GIT~1/hooks/x")]
    [InlineData("git~1/x")]
    [InlineData(".git /x")]
    [InlineData("a/.git.")]
    [InlineData(".g‌it/config")]
    public void Git_directory_aliases_are_rejected(string path) =>
        Assert.Throws<PatchRejectedException>(() => FilePatcher.ResolveSafePath(_repo.Path, path));

    [Theory]
    [InlineData(".gitignore")]
    [InlineData(".editorconfig")]
    [InlineData("src/.config/x.json")]
    [InlineData("git~notes.txt")]
    [InlineData("notes~draft.md")]
    [InlineData("a~b/c.txt")]
    [InlineData("v1~2.md")]
    [InlineData("notes~1.txt")]
    [InlineData("PROGRA~1/x")]
    public void Ordinary_dotfiles_are_accepted(string path) =>
        Assert.StartsWith(Path.GetFullPath(_repo.Path), FilePatcher.ResolveSafePath(_repo.Path, path));

    [Fact]
    public void Windows_short_names_of_existing_files_are_rejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        _repo.Write(".env.local", "KEY=1");
        _repo.Write(".github/x.yml", "on: push");
        _repo.Write("package.json", "{}");
        if (!File.Exists(_repo.File("ENV~1.LOC"))) return; // Short names are switched off on this volume.

        foreach (var alias in new[] { "ENV~1.LOC", "GITHUB~1/x.yml", "PACKAG~1.JSO" })
        {
            var error = Assert.Throws<PatchRejectedException>(() => FilePatcher.ResolveSafePath(_repo.Path, alias));
            Assert.Contains("8.3", error.Message);
        }
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml", true)]
    [InlineData(".GitHub/x.yml", true)]
    [InlineData(".claude\\settings.json", true)]
    [InlineData(".hatchet/a", true)]
    [InlineData(".vscode/tasks.json", true)]
    [InlineData(".husky/pre-commit", true)]
    [InlineData(".gitattributes", true)]
    [InlineData("sub/.gitmodules", true)]
    [InlineData("src/.npmrc", true)]
    [InlineData("src/NuGet.Config", true)]
    [InlineData("src/.github/x", false)]
    [InlineData(".gitignore", false)]
    [InlineData("src/app.cs", false)]
    public void IsSensitivePath_flags_config_that_can_run_code(string path, bool expected) =>
        Assert.Equal(expected, FilePatcher.IsSensitivePath(path));

    [Theory]
    [InlineData(".env", true)]
    [InlineData("app/.env.local", true)]
    [InlineData(".ENV.production", false)]
    [InlineData(".env.development", false)]
    [InlineData(".env.production.local", true)]
    [InlineData("src/secrets.ts", false)]
    [InlineData(".env.example", false)]
    [InlineData(".env.sample", false)]
    [InlineData(".env.template", false)]
    [InlineData("a/cert.pem", true)]
    [InlineData("a.pfx", true)]
    [InlineData("a.P12", true)]
    [InlineData("tls.key", false)]
    [InlineData("home/id_rsa", true)]
    [InlineData("id_rsa.pub", false)]
    [InlineData("id_ed25519", true)]
    [InlineData("id_ecdsa", true)]
    [InlineData("config/secrets.json", true)]
    [InlineData("infra/prod.tfvars", false)]
    [InlineData(".aws/credentials", true)]
    [InlineData("home/.netrc", true)]
    [InlineData(".pgpass", true)]
    [InlineData("AuthKey_ABC.p8", true)]
    [InlineData("app/release.jks", true)]
    [InlineData("debug.keystore", true)]
    [InlineData("infra/terraform.tfstate", true)]
    [InlineData("infra/terraform.tfstate.backup", true)]
    [InlineData("config/credentials.json", true)]
    [InlineData("service-account.json", true)]
    [InlineData("service-account-prod.json", true)]
    [InlineData("prod.env", false)]
    [InlineData("src/.npmrc", false)]
    [InlineData("appsettings.Production.json", false)]
    [InlineData("service-accounts.cs", false)]
    [InlineData("credentials.cs", false)]
    [InlineData("src/environment.cs", false)]
    public void IsSecretPath_flags_credential_files(string path, bool expected) =>
        Assert.Equal(expected, FilePatcher.IsSecretPath(path));

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

    private static string Lines(int count) => string.Join("\n", Enumerable.Range(1, count).Select(n => $"line {n}"));

    private static string Block(string search, string replace) =>
        $"{FilePatcher.SearchMarker}\n{search}\n{FilePatcher.DividerMarker}\n{replace}\n{FilePatcher.ReplaceMarker}";

    [Fact]
    public void Edit_replaces_only_the_named_lines()
    {
        _repo.Write("a.txt", Lines(300));

        var result = FilePatcher.Apply(_repo.Path, [Op("a.txt", FileActions.Edit, Block("line 150", "line 150\nadded"))]);

        var lines = File.ReadAllText(_repo.File("a.txt")).Split('\n');
        Assert.Equal(301, lines.Length);
        Assert.Equal("added", lines[150]);
        Assert.Equal("line 300", lines[^1]);
        Assert.Equal(["EDIT a.txt"], result.Applied);
    }

    [Fact]
    public void Edit_applies_several_blocks_in_order_and_keeps_crlf()
    {
        _repo.Write("a.txt", "one\r\ntwo\r\nthree\r\n");

        FilePatcher.Apply(_repo.Path, [Op("a.txt", FileActions.Edit, Block("one", "1") + "\n" + Block("three", "3"))]);

        Assert.Equal("1\r\ntwo\r\n3\r\n", File.ReadAllText(_repo.File("a.txt")));
    }

    [Fact]
    public void Edit_rejects_text_that_is_missing_or_ambiguous_and_writes_nothing()
    {
        _repo.Write("a.txt", "same\nsame\n");
        _repo.Write("b.txt", "keep");

        var ambiguous = Assert.Throws<PatchRejectedException>(() => FilePatcher.Apply(_repo.Path,
            [Op("b.txt", FileActions.Overwrite, "changed"), Op("a.txt", FileActions.Edit, Block("same", "x"))]));
        var missing = Assert.Throws<PatchRejectedException>(() => FilePatcher.Apply(_repo.Path,
            [Op("a.txt", FileActions.Edit, Block("absent", "x"))]));

        Assert.Contains("more than once", ambiguous.Message);
        Assert.Contains("isn't in the file", missing.Message);
        Assert.Equal("keep", File.ReadAllText(_repo.File("b.txt")));
        Assert.Equal("same\nsame\n", File.ReadAllText(_repo.File("a.txt")));
    }

    [Fact]
    public void Edit_rejects_a_missing_file_and_malformed_blocks()
    {
        _repo.Write("a.txt", "x");

        Assert.Throws<PatchRejectedException>(() => FilePatcher.Apply(_repo.Path, [Op("none.txt", FileActions.Edit, Block("x", "y"))]));
        Assert.Throws<PatchRejectedException>(() => FilePatcher.Apply(_repo.Path, [Op("a.txt", FileActions.Edit, "just some code")]));
        Assert.Throws<PatchRejectedException>(() => FilePatcher.Apply(_repo.Path, [Op("a.txt", FileActions.Edit, $"{FilePatcher.SearchMarker}\nx\n")]));
    }

    [Fact]
    public void Overwrite_that_cuts_a_long_file_sharply_is_rejected()
    {
        _repo.Write("big.cs", Lines(1000));

        var ex = Assert.Throws<PatchRejectedException>(() => FilePatcher.Apply(_repo.Path, [Op("big.cs", FileActions.Overwrite, Lines(600))]));

        Assert.Contains("1000 lines to 600", ex.Message);
        Assert.Contains("EDIT", ex.Message);
        Assert.Equal(Lines(1000), File.ReadAllText(_repo.File("big.cs")));
    }

    [Fact]
    public void Overwrite_that_keeps_most_of_a_long_file_or_shortens_a_short_one_is_allowed()
    {
        _repo.Write("big.cs", Lines(1000));
        _repo.Write("small.cs", Lines(100));

        FilePatcher.Apply(_repo.Path, [Op("big.cs", FileActions.Overwrite, Lines(800)), Op("small.cs", FileActions.Overwrite, Lines(10))]);

        Assert.Equal(Lines(800), File.ReadAllText(_repo.File("big.cs")));
        Assert.Equal(Lines(10), File.ReadAllText(_repo.File("small.cs")));
    }

    [Fact]
    public void Delete_then_create_replaces_a_long_file_on_purpose()
    {
        _repo.Write("big.cs", Lines(1000));

        FilePatcher.Apply(_repo.Path, [Op("big.cs", FileActions.Delete), Op("big.cs", FileActions.Create, Lines(50))]);

        Assert.Equal(Lines(50), File.ReadAllText(_repo.File("big.cs")));
    }
}
