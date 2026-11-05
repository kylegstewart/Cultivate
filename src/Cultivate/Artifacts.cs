using System.Reflection;

namespace Cultivate;

internal static class Artifacts
{
    internal static List<DeployFile> Collect(string run, string snapshot, List<ProjectInfo> projects)
    {
        var files = new List<DeployFile>();
        foreach (var project in projects)
        {
            var manifest = Path.Combine(run, "manifests", Path.GetFileNameWithoutExtension(project.RelativePath) + ".artifacts");
            if (!File.Exists(manifest))
                throw new FileNotFoundException($"The existing CopyBuildOutputToWebfarm target did not capture {project.Assembly}.", manifest);
            var entries = File.ReadAllLines(manifest).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
            if (entries.Count != 1)
                throw new InvalidDataException($"Expected only {project.Assembly}.dll in the project output manifest.");
            var source = Files.Child(run, Path.GetRelativePath(run, entries[0]));
            var name = project.Assembly + ".dll";
            if (!File.Exists(source) || !Files.Comparer.Equals(Path.GetFileName(source), name) ||
                !Files.Comparer.Equals(AssemblyName.GetAssemblyName(source).Name, project.Assembly))
                throw new InvalidDataException($"The captured output is not the expected {name}.");
            var destination = @"bin\" + name;
            if (files.Any(file => Files.Comparer.Equals(file.RelativePath, destination)))
                throw new InvalidDataException($"Multiple changed projects produce {destination}.");
            var staged = Files.Child(snapshot, Path.Combine(".cultivate-artifacts", destination));
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            File.Copy(source, staged, true);
            files.Add(new(staged, destination));
        }
        return files;
    }
}
