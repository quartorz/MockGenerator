using System;
using System.IO;
using Xunit;

namespace MockGenerator.Tests;

/// <summary>
/// Full-text comparison of generated output against a checked-in file under <c>Goldens/</c>.
/// <para>
/// Set the environment variable <c>UPDATE_GOLDENS=1</c> to write the actual output to the golden file
/// instead of comparing. The run then fails, so an accidental update never passes silently — review the
/// rewritten files in <c>git diff</c> and run again without the variable.
/// </para>
/// </summary>
internal static class Golden
{
	static bool Updating => Environment.GetEnvironmentVariable("UPDATE_GOLDENS") == "1";

	/// <summary>Asserts that <paramref name="actual"/> equals the golden named <paramref name="name"/> (without extension).</summary>
	public static void Equal(string name, string actual)
	{
		var path = Path.Combine(Directory(), name + ".txt");
		var normalized = actual.Replace("\r\n", "\n");

		var existed = File.Exists(path);
		if (Updating || !existed)
		{
			File.WriteAllText(path, normalized);
			Assert.True(false, existed
				? $"Golden \"{name}\" was rewritten (UPDATE_GOLDENS=1). Review it and run again without the variable."
				: $"Golden \"{name}\" did not exist and was written. Review it and run again.");
		}

		Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), normalized);
	}

	/// <summary>Locates <c>Goldens/</c> next to the test sources, walking up from the test binary.</summary>
	static string Directory()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MockGenerator.Tests.csproj")))
		{
			dir = dir.Parent;
		}
		if (dir == null) throw new InvalidOperationException("Could not locate the test project directory.");
		var goldens = Path.Combine(dir.FullName, "Goldens");
		System.IO.Directory.CreateDirectory(goldens);
		return goldens;
	}
}
