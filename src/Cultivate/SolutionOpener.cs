using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace Cultivate;

internal static class SolutionOpener
{
    private static readonly HashSet<string> SourceExtensions = new(
        [".cs", ".ascx", ".aspx", ".js", ".css", ".html", ".htm"], Files.Comparer);

    /// <summary>Solutions at a checkout's root, or one folder down when the root has none.</summary>
    internal static List<string> Solutions(string root)
    {
        var top = Directory.EnumerateFiles(root, "*.sln").ToList();
        if (top.Count == 0)
        {
            try { top = Directory.EnumerateDirectories(root).SelectMany(folder => Directory.EnumerateFiles(folder, "*.sln")).ToList(); }
            catch (UnauthorizedAccessException) { }
        }
        return top.Order(Files.Comparer).ToList();
    }

    /// <summary>Changed source files of a checkout that belong under the folder of the solution being opened.</summary>
    internal static List<string> FilesToOpen(string root, string solution, IEnumerable<Change> actionable) => actionable
        .Where(change => !change.Deleted && SourceExtensions.Contains(Path.GetExtension(change.Path)) &&
            !Planner.IsExcluded(change.Path) && !change.Path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) &&
            !change.Path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
            !change.Path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase))
        .Select(change => Files.Child(root, change.Path))
        .Where(file => File.Exists(file) && file.StartsWith(Path.GetDirectoryName(solution)! + "\\", StringComparison.OrdinalIgnoreCase))
        .Distinct(Files.Comparer).Order(Files.Comparer).ToList();

    internal static List<string> MissingDocuments(IEnumerable<string> requested, IEnumerable<string> opened)
    {
        var seen = new HashSet<string>(opened.Where(Path.IsPathFullyQualified).Select(Files.Full), Files.Comparer);
        return requested.Select(Files.Full).Where(seen.Add).ToList();
    }

    public static Task Open(string visualStudio, string solution, List<string> files)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Opening Visual Studio requires Windows.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Opening Visual Studio requires Windows.");
                OpenWindows(visualStudio, Files.Full(solution), files);
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(new InvalidOperationException("Visual Studio could not open the solution's changed files: " +
                    exception.Message, exception));
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [SupportedOSPlatform("windows")]
    private static void OpenWindows(string visualStudio, string solution, List<string> files)
    {
        // Reuse the exact solution, never whichever unrelated VS instance happens to be most recent.
        dynamic? dte = Retry(() => FindInstance((_, instance) => Files.Comparer.Equals((string)instance.Solution.FullName, solution)));
        try
        {
            if (dte is null)
            {
                // Passing the solution skips the Start window, which otherwise stays open and blocks editor
                // commands such as Backspace and Delete in the documents opened afterward.
                using var process = Process.Start(new ProcessStartInfo(visualStudio)
                {
                    UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(solution)!, ArgumentList = { solution }
                }) ?? throw new InvalidOperationException("Visual Studio could not be started.");
                // Visual Studio rejects automation while loading, so the solution reports open only after its tabs are restored.
                var until = DateTime.UtcNow.AddMinutes(5);
                while (dte is null && DateTime.UtcNow < until)
                {
                    dte = Retry(() => FindInstance((name, instance) => name.EndsWith(":" + process.Id, StringComparison.Ordinal) &&
                        Files.Comparer.Equals((string)instance.Solution.FullName, solution)));
                    if (dte is null) Thread.Sleep(250);
                }
                if (dte is null) throw new InvalidOperationException("Visual Studio did not open the solution within five minutes. Try again once it finishes loading.");
                // A new instance restores the solution's last session tabs, such as DLLs; keep only the changed files.
                Retry(() => { CloseDocumentWindows(dte); return true; });
            }
            Retry(() =>
            {
                var opened = new List<string>();
                dynamic documents = dte.Documents;
                try
                {
                    for (var index = 1; index <= (int)documents.Count; index++)
                    {
                        dynamic document = documents.Item(index);
                        try { opened.Add((string)document.FullName); }
                        finally { Release(document); }
                    }
                }
                finally { Release(documents); }
                foreach (var file in MissingDocuments(files, opened))
                {
                    // The default file command reuses the solution's document instead of creating another view.
                    object window = dte.ItemOperations.OpenFile(file);
                    Release(window);
                }
                dte.UserControl = true;
                dte.MainWindow.Visible = true;
                dte.MainWindow.Activate();
                return true;
            });
        }
        finally { if (dte is not null) Release(dte); }
    }

    [SupportedOSPlatform("windows")]
    private static void CloseDocumentWindows(dynamic dte)
    {
        dynamic windows = dte.Windows;
        try
        {
            for (var index = (int)windows.Count; index >= 1; index--)
            {
                if (index > (int)windows.Count) continue; // Closing a document can close its other views too.
                dynamic window = windows.Item(index);
                try
                {
                    if ((string)window.Kind == "Document") window.Close(3); // vsSaveChangesPrompt
                }
                finally { Release(window); }
            }
        }
        finally { Release(windows); }
    }

    [SupportedOSPlatform("windows")]
    private static object? FindInstance(Func<string, dynamic, bool> matches)
    {
        Marshal.ThrowExceptionForHR(CreateBindCtx(0, out var context));
        IRunningObjectTable? table = null;
        IEnumMoniker? enumerator = null;
        try
        {
            context.GetRunningObjectTable(out table);
            table!.EnumRunning(out enumerator);
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                object? instance = null;
                try
                {
                    string name;
                    moniker.GetDisplayName(context, null, out name);
                    if (!name.StartsWith("!VisualStudio.DTE.", StringComparison.OrdinalIgnoreCase)) continue;
                    table.GetObject(moniker, out instance);
                    if (instance is not null && matches(name, instance))
                    {
                        var selected = instance;
                        instance = null;
                        return selected;
                    }
                }
                catch (UnauthorizedAccessException) { } // Elevated or another user's ROT entries are not ours to control.
                catch (COMException exception) when (exception.HResult is unchecked((int)0x800401E3) or unchecked((int)0x80010108))
                {
                    // An instance can close while its ROT entry is being inspected.
                }
                finally
                {
                    if (instance is not null) Release(instance);
                    Release(moniker);
                }
            }
            return null;
        }
        finally
        {
            if (enumerator is not null) Release(enumerator);
            if (table is not null) Release(table);
            Release(context);
        }
    }

    private static bool IsBusy(Exception exception) => exception switch
    {
        COMException => exception.HResult is unchecked((int)0x80010001) or unchecked((int)0x8001010A),
        // The dynamic COM binder puts GetIDsOfNames failures in the message instead of retaining their HRESULT.
        MissingMemberException => exception.Message.Contains("0x80010001)", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("0x8001010A)", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    internal static T Retry<T>(Func<T> action, TimeSpan? timeout = null)
    {
        var elapsed = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromMinutes(2);
        while (true)
        {
            try { return action(); }
            catch (Exception exception) when (elapsed.Elapsed < limit && IsBusy(exception))
            {
                Thread.Sleep(250); // Visual Studio rejects automation calls while loading or displaying a modal dialog.
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Release(object value)
    {
        if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx context);
}