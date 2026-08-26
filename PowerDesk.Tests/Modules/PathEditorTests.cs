using PowerDesk.Core.Logging;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Core.Storage;
using PowerDesk.Modules.PathEditor.Models;
using PowerDesk.Modules.PathEditor.Services;
using PowerDesk.Modules.PathEditor.ViewModels;

namespace PowerDesk.Tests.Modules;

public sealed class PathEditorTests
{
    private sealed class NullLogger : ILogger
    {
        public string LogFilePath => string.Empty;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }

    private sealed class FakeConfirm : IConfirmationService
    {
        public bool Answer { get; set; } = true;
        public int Calls { get; private set; }
        public bool Confirm(string message, string title, bool destructive = false)
        {
            Calls++;
            return Answer;
        }
    }

    private sealed class FakePathStore : IPathStore
    {
        public Dictionary<PathScope, string> Values { get; } = new()
        {
            [PathScope.User] = @"%USERPROFILE%\bin;C:\Tools;",
            [PathScope.Machine] = @"%SystemRoot%\system32;%SystemRoot%;C:\Program Files\Git\cmd",
        };
        public List<(PathScope Scope, string Value)> Writes { get; } = new();

        public string Read(PathScope scope) => Values.TryGetValue(scope, out var v) ? v : string.Empty;

        public void Write(PathScope scope, string value)
        {
            Writes.Add((scope, value));
            Values[scope] = value;
        }
    }

    private static (PathEditorViewModel Vm, FakePathStore Store, FakeConfirm Confirm, string SettingsFile) CreateViewModel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PowerDeskTests", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "settings.json");
        var store = new FakePathStore();
        var confirm = new FakeConfirm();
        var vm = new PathEditorViewModel(
            new NullLogger(),
            new JsonStorageService(new NullLogger()),
            new StatusService(),
            new RecentActionsService(),
            new PermissionService(),
            confirm,
            store,
            file);
        return (vm, store, confirm, file);
    }

    // ---------- PathLogic ----------

    [Fact]
    public void Split_TrimsAndDropsEmptyEntries()
    {
        var entries = PathLogic.Split(@" C:\A ;;C:\B; ;C:\C\ ");
        Assert.Equal(new[] { @"C:\A", @"C:\B", @"C:\C\" }, entries);
        Assert.Empty(PathLogic.Split(null));
        Assert.Empty(PathLogic.Split(";;"));
    }

    [Fact]
    public void Split_HonoursQuotedEntriesContainingSemicolons()
    {
        var entries = PathLogic.Split(@"C:\First;""C:\Odd;Name"";C:\Last");
        Assert.Equal(new[] { @"C:\First", @"""C:\Odd;Name""", @"C:\Last" }, entries);
    }

    [Fact]
    public void Join_QuotesEntriesWithSemicolonsAndSkipsBlanks()
    {
        var joined = PathLogic.Join(new[] { @"C:\A", "", " ", @"C:\Odd;Name", @"""C:\Already;Quoted""", null });
        Assert.Equal(@"C:\A;""C:\Odd;Name"";""C:\Already;Quoted""", joined);
    }

    [Fact]
    public void SplitThenJoin_RoundTripsExpandableEntries()
    {
        const string raw = @"%SystemRoot%\system32;%SystemRoot%;""C:\Odd;Name"";C:\Tools";
        Assert.Equal(raw, PathLogic.Join(PathLogic.Split(raw)));
    }

    [Theory]
    [InlineData(@"C:\Tools", @"c:\tools\")]
    [InlineData(@"C:\Tools", @"C:/Tools/")]
    [InlineData(@"C:\Tools", @"""C:\Tools""")]
    [InlineData(@"C:\Tools", @"  C:\Tools  ")]
    [InlineData(@"C:\Tools\Sub", @"C:\Tools\.\Sub\")]
    public void NormalizeForCompare_TreatsVariantsAsEqual(string a, string b)
    {
        Assert.Equal(PathLogic.NormalizeForCompare(a), PathLogic.NormalizeForCompare(b), ignoreCase: true);
    }

    [Fact]
    public void NormalizeForCompare_ExpandsEnvironmentVariables()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        Assert.False(string.IsNullOrEmpty(systemRoot));
        Assert.Equal(
            PathLogic.NormalizeForCompare(Path.Combine(systemRoot!, "system32")),
            PathLogic.NormalizeForCompare(@"%SystemRoot%\system32\"),
            ignoreCase: true);
    }

    [Fact]
    public void NormalizeForCompare_KeepsDriveRootsAndBlankStaysBlank()
    {
        Assert.Equal(@"C:\", PathLogic.NormalizeForCompare(@"C:\"));
        Assert.Equal(@"C:\", PathLogic.NormalizeForCompare("C:"));
        Assert.Equal(string.Empty, PathLogic.NormalizeForCompare("   "));
        Assert.Equal(string.Empty, PathLogic.NormalizeForCompare("\"\""));
    }

    [Fact]
    public void TrimTrailingSeparators_HandlesRootsAndNestedPaths()
    {
        Assert.Equal(@"C:\Tools", PathLogic.TrimTrailingSeparators(@"C:\Tools\\"));
        Assert.Equal(@"C:\", PathLogic.TrimTrailingSeparators(@"C:\"));
        Assert.Equal(@"C:\", PathLogic.TrimTrailingSeparators("C:"));
        Assert.Equal(@"\", PathLogic.TrimTrailingSeparators(@"\"));
        Assert.Equal(string.Empty, PathLogic.TrimTrailingSeparators(string.Empty));
    }

    [Fact]
    public void FlagDuplicates_FlagsLaterOccurrencesOnlyCaseInsensitively()
    {
        var flags = PathLogic.FlagDuplicates(new[] { @"C:\Tools", @"C:\Other", @"c:\tools\", "", @"""C:\Tools""", @"C:\OTHER" });
        Assert.Equal(new[] { false, false, true, false, true, true }, flags);
    }

    [Fact]
    public void EntryExists_UsesExpandedQuotedPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PowerDeskTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(PathLogic.EntryExists(dir));
            Assert.True(PathLogic.EntryExists($"\"{dir}\\\""));
            Assert.True(PathLogic.EntryExists(@"%SystemRoot%"));
            Assert.False(PathLogic.EntryExists(Path.Combine(dir, "missing")));
            Assert.False(PathLogic.EntryExists(""));
            Assert.False(PathLogic.EntryExists(null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("\"C:\\Tools", false)]
    [InlineData("C:\\Tools<x>", false)]
    [InlineData("C:\\Tools|x", false)]
    [InlineData("C:\\Tools", true)]
    [InlineData("%USERPROFILE%\\bin", true)]
    [InlineData("\"C:\\Odd;Name\"", true)]
    public void ValidateNewEntry_RejectsImpossiblePaths(string value, bool ok)
    {
        var problem = PathLogic.ValidateNewEntry(value);
        Assert.Equal(ok, problem is null);
    }

    // ---------- ViewModel ----------

    [Fact]
    public void LoadPath_ReadsRawEntriesWithoutExpandingVariables()
    {
        var (vm, _, _, _) = CreateViewModel();
        vm.LoadPathCommand.Execute(null);

        Assert.Equal(new[] { @"%USERPROFILE%\bin", @"C:\Tools" }, vm.Entries.Select(e => e.Value));
        Assert.False(vm.IsDirty);
        Assert.True(vm.HasEntries);
        Assert.Same(vm.Entries[0], vm.SelectedEntry);
        Assert.NotNull(vm.LastLoaded);
    }

    [Fact]
    public void AddEntry_AppendsSelectsMarksDirtyAndFlagsDuplicates()
    {
        var (vm, _, _, _) = CreateViewModel();
        vm.LoadPathCommand.Execute(null);

        Assert.False(vm.AddEntryCommand.CanExecute(null));
        vm.NewEntry = @"c:\tools\";
        Assert.True(vm.AddEntryCommand.CanExecute(null));
        vm.AddEntryCommand.Execute(null);

        Assert.Equal(3, vm.EntryCount);
        Assert.True(vm.IsDirty);
        Assert.Equal(string.Empty, vm.NewEntry);
        Assert.Same(vm.Entries[2], vm.SelectedEntry);
        Assert.True(vm.Entries[2].IsDuplicate);
        Assert.False(vm.Entries[1].IsDuplicate);
        Assert.Equal(1, vm.DuplicateCount);
    }

    [Fact]
    public void AddEntry_RejectsInvalidInputWithoutChangingTheList()
    {
        var (vm, _, _, _) = CreateViewModel();
        vm.LoadPathCommand.Execute(null);
        vm.NewEntry = "C:\\Bad|Path";
        vm.AddEntryCommand.Execute(null);
        Assert.Equal(2, vm.EntryCount);
        Assert.False(vm.IsDirty);
        Assert.Equal("C:\\Bad|Path", vm.NewEntry);
    }

    [Fact]
    public void MoveUpAndDown_ReorderAndKeepSelectionAndCanExecuteInSync()
    {
        var (vm, _, _, _) = CreateViewModel();
        vm.LoadPathCommand.Execute(null);
        vm.NewEntry = @"C:\Third";
        vm.AddEntryCommand.Execute(null);
        vm.IsDirty = false;

        var third = vm.Entries[2];
        vm.SelectedEntry = third;
        Assert.True(vm.MoveSelectedUpCommand.CanExecute(null));
        Assert.False(vm.MoveSelectedDownCommand.CanExecute(null));

        vm.MoveSelectedUpCommand.Execute(null);
        Assert.Equal(new[] { @"%USERPROFILE%\bin", @"C:\Third", @"C:\Tools" }, vm.Entries.Select(e => e.Value));
        Assert.Same(third, vm.SelectedEntry);
        Assert.True(vm.IsDirty);
        Assert.True(vm.MoveSelectedDownCommand.CanExecute(null));

        vm.MoveSelectedUpCommand.Execute(null);
        Assert.Same(third, vm.Entries[0]);
        Assert.False(vm.MoveSelectedUpCommand.CanExecute(null));

        vm.MoveSelectedDownCommand.Execute(null);
        vm.MoveSelectedDownCommand.Execute(null);
        Assert.Same(third, vm.Entries[2]);
        Assert.False(vm.MoveSelectedDownCommand.CanExecute(null));
        Assert.Equal(@"%USERPROFILE%\bin;C:\Tools;C:\Third", vm.RawPath);
    }

    [Fact]
    public void RemoveSelectedEntry_SelectsNeighbourAndUpdatesCounts()
    {
        var (vm, _, _, _) = CreateViewModel();
        vm.LoadPathCommand.Execute(null);
        vm.SelectedEntry = vm.Entries[0];
        Assert.True(vm.RemoveSelectedEntryCommand.CanExecute(null));

        vm.RemoveSelectedEntryCommand.Execute(null);
        Assert.Single(vm.Entries);
        Assert.Equal(@"C:\Tools", vm.SelectedEntry?.Value);
        Assert.True(vm.IsDirty);

        vm.RemoveSelectedEntryCommand.Execute(null);
        Assert.Empty(vm.Entries);
        Assert.Null(vm.SelectedEntry);
        Assert.False(vm.HasEntries);
        Assert.False(vm.RemoveSelectedEntryCommand.CanExecute(null));
        Assert.Equal(string.Empty, vm.RawPath);
    }

    [Fact]
    public void EditingAnEntry_ResetsValidationAndRecomputesDuplicates()
    {
        var (vm, _, _, _) = CreateViewModel();
        vm.LoadPathCommand.Execute(null);
        var entry = vm.Entries[0];
        entry.IsValidated = true;
        entry.IsMissing = true;
        vm.IsDirty = false;

        entry.Value = @"C:\TOOLS";

        Assert.False(entry.IsValidated);
        Assert.False(entry.IsMissing);
        Assert.Equal("Unchecked", entry.MissingLabel);
        Assert.True(vm.IsDirty);
        Assert.True(vm.Entries[1].IsDuplicate);
        Assert.False(entry.IsDuplicate);
    }

    [Fact]
    public async Task Save_WritesJoinedPathTakesBackupAndClearsDirty()
    {
        var (vm, store, confirm, file) = CreateViewModel();
        vm.LoadPathCommand.Execute(null);
        vm.NewEntry = @"C:\New";
        vm.AddEntryCommand.Execute(null);

        await vm.SavePathCommand.ExecuteAsync(null);

        Assert.Equal(1, confirm.Calls);
        var write = Assert.Single(store.Writes);
        Assert.Equal(PathScope.User, write.Scope);
        Assert.Equal(@"%USERPROFILE%\bin;C:\Tools;C:\New", write.Value);
        Assert.False(vm.IsDirty);

        var backup = Assert.Single(vm.Backups);
        Assert.Equal(PathScope.User, backup.Scope);
        Assert.Equal(@"%USERPROFILE%\bin;C:\Tools;", backup.Value);
        Assert.True(vm.HasBackups);
        Assert.True(File.Exists(file));
        Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
    }

    [Fact]
    public async Task Save_IsCancelledByConfirmation()
    {
        var (vm, store, confirm, _) = CreateViewModel();
        vm.LoadPathCommand.Execute(null);
        vm.NewEntry = @"C:\New";
        vm.AddEntryCommand.Execute(null);
        confirm.Answer = false;

        await vm.SavePathCommand.ExecuteAsync(null);

        Assert.Empty(store.Writes);
        Assert.True(vm.IsDirty);
        Assert.Empty(vm.Backups);
    }

    [Fact]
    public void AddBackup_SkipsIdenticalConsecutiveValuesAndCapsHistory()
    {
        var (vm, _, _, _) = CreateViewModel();
        vm.AddBackup(PathScope.User, "A");
        vm.AddBackup(PathScope.User, "A");
        vm.AddBackup(PathScope.Machine, "A");
        Assert.Equal(2, vm.Backups.Count);
        Assert.Equal(PathScope.Machine, vm.Backups[0].Scope);

        for (var i = 0; i < 40; i++) vm.AddBackup(PathScope.User, $"value {i}");
        Assert.Equal(PathLogic.MaxBackups, vm.Backups.Count);
        Assert.Equal("value 39", vm.Backups[0].Value);
    }

    [Fact]
    public void RestoreBackup_LoadsEntriesIntoEditorWithoutWriting()
    {
        var (vm, store, _, _) = CreateViewModel();
        vm.LoadPathCommand.Execute(null);
        var backup = new PathBackup { Scope = PathScope.Machine, Value = @"%SystemRoot%\system32;C:\Old" };
        vm.Backups.Add(backup);

        vm.RestoreBackupCommand.Execute(backup);

        Assert.Equal(PathScope.Machine, vm.SelectedScope);
        Assert.Equal(new[] { @"%SystemRoot%\system32", @"C:\Old" }, vm.Entries.Select(e => e.Value));
        Assert.True(vm.IsDirty);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void SwitchingScope_LoadsThatScopeAndConfirmsWhenDirty()
    {
        var (vm, _, confirm, _) = CreateViewModel();
        vm.LoadPathCommand.Execute(null);
        vm.SelectedScope = PathScope.Machine;
        Assert.Equal(0, confirm.Calls);
        Assert.Equal(3, vm.EntryCount);
        Assert.Equal(@"%SystemRoot%\system32", vm.Entries[0].Value);
        Assert.Equal(new PermissionService().IsAdministrator, vm.SavePathCommand.CanExecute(null));
        Assert.Equal(!new PermissionService().IsAdministrator, vm.ShowAdminHint);

        vm.NewEntry = @"C:\Extra";
        vm.AddEntryCommand.Execute(null);
        vm.SelectedScope = PathScope.User;
        Assert.Equal(1, confirm.Calls);
        Assert.Equal(2, vm.EntryCount);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void Reload_WhenDirtyAndDeclined_KeepsEdits()
    {
        var (vm, _, confirm, _) = CreateViewModel();
        vm.LoadPathCommand.Execute(null);
        vm.NewEntry = @"C:\Extra";
        vm.AddEntryCommand.Execute(null);
        confirm.Answer = false;

        vm.LoadPathCommand.Execute(null);

        Assert.Equal(1, confirm.Calls);
        Assert.Equal(3, vm.EntryCount);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task ValidateEntries_MarksMissingFoldersOffTheUiThread()
    {
        var (vm, store, _, _) = CreateViewModel();
        var dir = Path.Combine(Path.GetTempPath(), "PowerDeskTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            store.Values[PathScope.User] = $"{dir};{Path.Combine(dir, "missing")}";
            vm.LoadPathCommand.Execute(null);

            await vm.ValidateEntriesCommand.ExecuteAsync(null);

            Assert.All(vm.Entries, e => Assert.True(e.IsValidated));
            Assert.False(vm.Entries[0].IsMissing);
            Assert.True(vm.Entries[1].IsMissing);
            Assert.Equal("OK", vm.Entries[0].MissingLabel);
            Assert.Equal("Missing", vm.Entries[1].MissingLabel);
            Assert.Equal(1, vm.MissingCount);
            Assert.False(vm.IsValidating);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- drag-drop ----------

    [Fact]
    public void ResolveDropFolders_FoldersAsIsFilesToParentDedupedMissingIgnored()
    {
        // Like Directory.Exists, the fake tolerates a trailing separator.
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Tools", @"C:\Other" };
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Tools\app.exe", @"C:\Bin\x.cmd" };

        var (folders, ignored) = PathLogic.ResolveDropFolders(
            new[] { @"C:\Tools\", @"C:\Tools\app.exe", @"c:\tools", @"C:\Bin\x.cmd", @"C:\Other\", @"C:\Nope", "" },
            p => dirs.Contains(p.TrimEnd('\\')), files.Contains);

        Assert.Equal(new[] { @"C:\Tools", @"C:\Bin", @"C:\Other" }, folders);
        Assert.Equal(1, ignored);

        var (none, noneIgnored) = PathLogic.ResolveDropFolders(null, _ => false, _ => false);
        Assert.Empty(none);
        Assert.Equal(0, noneIgnored);
    }

    [Fact]
    public void AddEntriesFromDrop_AppendsFoldersMarksDirtyAndReportsCount()
    {
        var (vm, _, _, _) = CreateViewModel();
        vm.LoadPathCommand.Execute(null);
        var dir = Path.Combine(Path.GetTempPath(), "PowerDeskTests", Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        var exe = Path.Combine(sub, "tool.exe");
        File.WriteAllText(exe, "x");
        try
        {
            var added = vm.AddEntriesFromDrop(new[] { dir, exe, Path.Combine(dir, "missing") });

            Assert.Equal(2, added);
            Assert.Equal(4, vm.EntryCount);
            Assert.Equal(dir, vm.Entries[2].Value);
            Assert.Equal(sub, vm.Entries[3].Value);
            Assert.Same(vm.Entries[3], vm.SelectedEntry);
            Assert.True(vm.IsDirty);
            Assert.True(vm.RemoveSelectedEntryCommand.CanExecute(null));

            // A second drop of the same folder is appended but flagged as a duplicate.
            Assert.Equal(1, vm.AddEntriesFromDrop(new[] { dir + "\\" }));
            Assert.True(vm.Entries[4].IsDuplicate);
            Assert.Equal(1, vm.DuplicateCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AddEntriesFromDrop_NothingUsable_LeavesListUntouched()
    {
        var (vm, _, _, _) = CreateViewModel();
        vm.LoadPathCommand.Execute(null);
        Assert.Equal(0, vm.AddEntriesFromDrop(new[] { Path.Combine(Path.GetTempPath(), "PowerDeskTests", Guid.NewGuid().ToString("N")) }));
        Assert.Equal(0, vm.AddEntriesFromDrop(null));
        Assert.Equal(2, vm.EntryCount);
        Assert.False(vm.IsDirty);
    }
}
