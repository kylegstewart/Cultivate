using System.Reflection;

namespace Cultivate;

internal static class Artifacts
{
    internal static List<DeployFile> Collect(string run, string snapshot, List<ProjectInfo> projects)
    {
        var files = new List<DeployFile>();
        void Stage(string source, string destination)
        {
            var staged = Files.Child(snapshot, Path.Combine(".cultivate-artifacts", destination));
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            File.Copy(source, staged, true);
            files.Add(new(staged, destination));
        }
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
            Stage(source, destination);
        }
        // Projects that bump the same package each captured it; publish it once, and never two different copies.
        foreach (var project in projects)
            foreach (var name in project.PackageDependencies.Select(hint => Path.GetFileName(hint)))
            {
                var source = Path.Combine(run, "dependencies", project.Assembly, name);
                var destination = @"bin\" + name;
                var existing = files.FirstOrDefault(file => Files.Comparer.Equals(file.RelativePath, destination));
                if (existing is null) Stage(source, destination);
                else if (Files.Hash(existing.Source) != Files.Hash(source))
                    throw new InvalidDataException($"Changed projects produce different copies of {destination}.");
            }
        return files;
    }
}
