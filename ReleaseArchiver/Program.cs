using System.IO.Compression;
using System.Reflection;

namespace ReleaseArchiver
{
	/// <summary>
	/// Packages the Release build of KVStore into versioned Linux and Windows zip files in the "Releases" folder at the root of the repository, ready to be attached to a GitHub release.
	/// </summary>
	class Program
	{
		static void Main(string[] args)
		{
			try
			{
				string repoRoot = FindRepositoryRoot();
				string projectDir = Path.Combine(repoRoot, "KVStore");
				string releasesDir = Path.Combine(repoRoot, "Releases");

				// Inspect both builds before creating anything, so a problem with either build doesn't leave a half-finished release behind.
				// KVStore.csproj removes the ServiceUI and Properties folders from the Linux build, so changes there don't make the Linux build out of date.
				BuildOutput linux = InspectBuild(projectDir, "net10.0", "KVStore Linux", "KVStoreLinux.dll",
					new string[] { "KVStoreLinux.deps.json", "KVStoreLinux.runtimeconfig.json", "BPUtil.dll", "LiteDB.dll", "Newtonsoft.Json.dll" },
					new string[] { "ServiceUI", "Properties" });
				BuildOutput windows = InspectBuild(projectDir, "net10.0-windows7.0", "KVStore Windows", "KVStore.dll",
					new string[] { "KVStore.exe", "KVStore.deps.json", "KVStore.runtimeconfig.json", "BPUtil.dll", "LiteDB.dll", "Newtonsoft.Json.dll" },
					new string[0]);
				if (linux.Version != windows.Version)
					throw new Exception("The Linux build is version " + linux.Version + " but the Windows build is version " + windows.Version + ". Rebuild the solution in the Release configuration.");

				CreateZip(linux, releasesDir);
				CreateZip(windows, releasesDir);
				Console.WriteLine();
				Console.WriteLine("Release files are in " + releasesDir);
			}
			catch (Exception ex)
			{
				WriteColor(ConsoleColor.Red, ex.Message);
				WriteColor(ConsoleColor.Red, "Release not created.");
			}
			Console.WriteLine();
			Console.WriteLine("Press ENTER to exit.");
			Console.ReadLine();
		}

		/// <summary>
		/// Finds the repository root by searching upward from this program's location for KVStore.slnx, so the archiver works no matter which folder it is started from.
		/// </summary>
		private static string FindRepositoryRoot()
		{
			for (DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
				if (File.Exists(Path.Combine(dir.FullName, "KVStore.slnx")))
					return dir.FullName;
			throw new Exception("KVStore.slnx was not found in any parent folder of " + AppContext.BaseDirectory);
		}

		/// <summary>
		/// Verifies that a target framework's Release build exists, is complete, and is newer than the source files, and reads its version.
		/// </summary>
		/// <param name="projectDir">The KVStore project folder.</param>
		/// <param name="folder">The target framework folder name inside bin/Release.</param>
		/// <param name="name">The name of the release, which begins the zip file name.</param>
		/// <param name="dllName">The main assembly, whose version names the release.</param>
		/// <param name="requiredFiles">Other files which must exist in the build output.</param>
		/// <param name="excludedSourceDirs">Top-level project folders that are not compiled into this build.</param>
		private static BuildOutput InspectBuild(string projectDir, string folder, string name, string dllName, string[] requiredFiles, string[] excludedSourceDirs)
		{
			string buildDir = Path.Combine(projectDir, "bin", "Release", folder);
			if (!Directory.Exists(buildDir))
				throw new Exception(buildDir + " not found. Build the solution in the Release configuration.");

			foreach (string file in requiredFiles.Prepend(dllName))
				if (!File.Exists(Path.Combine(buildDir, file)))
					throw new Exception(Path.Combine(buildDir, file) + " not found. Rebuild the solution in the Release configuration.");

			// Any change to a compile input causes the assembly to be rewritten, so an input newer than the assembly means the build is out of date (for example, the solution was last built in the Debug configuration).
			string dllPath = Path.Combine(buildDir, dllName);
			DateTime builtAt = File.GetLastWriteTimeUtc(dllPath);
			FileInfo newestSource = GetSourceFiles(projectDir, excludedSourceDirs).MaxBy(fi => fi.LastWriteTimeUtc);
			if (newestSource != null && newestSource.LastWriteTimeUtc > builtAt)
				throw new Exception("The " + name + " build is out of date: " + Path.GetRelativePath(projectDir, newestSource.FullName) + " was modified after " + dllName + " was built. Rebuild the solution in the Release configuration.");

			return new BuildOutput
			{
				Name = name,
				Directory = buildDir,
				Version = AssemblyName.GetAssemblyName(dllPath).Version.ToString()
			};
		}

		/// <summary>
		/// Returns the project's compile inputs, following the item rules in KVStore.csproj: code, project, and resource files, plus the files embedded from the AdminUI and Resources folders.
		/// </summary>
		private static IEnumerable<FileInfo> GetSourceFiles(string projectDir, string[] excludedSourceDirs)
		{
			string[] skipDirs = excludedSourceDirs.Concat(new string[] { "bin", "obj" }).ToArray();
			string[] embeddedDirs = new string[] { "AdminUI", "Resources" };
			string[] sourceExtensions = new string[] { ".cs", ".csproj", ".resx" };
			foreach (FileInfo fi in new DirectoryInfo(projectDir).EnumerateFiles("*", SearchOption.AllDirectories))
			{
				string relativePath = Path.GetRelativePath(projectDir, fi.FullName);
				int separatorIdx = relativePath.IndexOf(Path.DirectorySeparatorChar);
				string topDir = separatorIdx == -1 ? "" : relativePath.Substring(0, separatorIdx);
				if (skipDirs.Contains(topDir, StringComparer.OrdinalIgnoreCase))
					continue;
				if (embeddedDirs.Contains(topDir, StringComparer.OrdinalIgnoreCase) || sourceExtensions.Contains(fi.Extension, StringComparer.OrdinalIgnoreCase))
					yield return fi;
			}
		}

		private static void CreateZip(BuildOutput build, string releasesDir)
		{
			string zipName = build.Name + " " + build.Version + ".zip";
			string zipPath = Path.Combine(releasesDir, zipName);
			if (File.Exists(zipPath))
			{
				WriteColor(ConsoleColor.Yellow, zipName + " ALREADY EXISTS. To make a new release, increase the Version in KVStore.csproj and rebuild.");
				return;
			}

			Console.WriteLine("Creating " + zipName);
			Directory.CreateDirectory(releasesDir);
			// Write to a temporary file first, so an interrupted run can't leave behind a partial zip that looks finished.
			string tempPath = zipPath + ".tmp";
			File.Delete(tempPath);
			ZipFile.CreateFromDirectory(build.Directory, tempPath);
			File.Move(tempPath, zipPath);
		}

		private static void WriteColor(ConsoleColor color, string message)
		{
			Console.ForegroundColor = color;
			Console.WriteLine(message);
			Console.ResetColor();
		}

		private class BuildOutput
		{
			public string Name;
			public string Directory;
			public string Version;
		}
	}
}
