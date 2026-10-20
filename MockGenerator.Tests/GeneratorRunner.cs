using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MockGenerator.Tests;

/// <summary>
/// Runs the generators of this project over a single compilation unit and hands back everything a
/// golden test needs: the compilation's own errors, the generators' diagnostics, and the generated files.
/// </summary>
internal static class GeneratorRunner
{
	static readonly MetadataReference[] metadataReferences =
	{
		MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
		MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
		MetadataReference.CreateFromFile(typeof(Action).Assembly.Location),
		MetadataReference.CreateFromFile(typeof(CSharpCompilation).Assembly.Location),
		MetadataReference.CreateFromFile(typeof(Compilation).Assembly.Location),
		MetadataReference.CreateFromFile(typeof(GCSettings).Assembly.Location),
	};

	public sealed record RunResult(
		List<Diagnostic> Diagnostics,
		List<Diagnostic> CompilationErrors,
		List<GeneratedSourceResult> Sources)
	{
		/// <summary>Returns the generated file whose hint name is <paramref name="hintName"/>.</summary>
		/// <exception cref="InvalidOperationException">No file with that hint name was generated.</exception>
		public string Source(string hintName)
		{
			var hit = Sources.FirstOrDefault(x => x.HintName == hintName);
			if (hit.SourceText == null)
			{
				var names = string.Join("\n  ", Sources.Select(x => x.HintName));
				throw new InvalidOperationException($"No generated source named \"{hintName}\". Generated:\n  {names}");
			}
			return hit.SourceText.ToString();
		}
	}

	/// <summary>
	/// Compiles <paramref name="source"/> with the given generators attached.
	/// <para>
	/// The attributes the generators key off are emitted by their own post-initialization output, so
	/// <paramref name="source"/> only needs to declare the types under test.
	/// </para>
	/// </summary>
	public static RunResult Run(string source, params IIncrementalGenerator[] generators)
	{
		var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
		GeneratorDriver driver = CSharpGeneratorDriver
			.Create(generators.Select(g => g.AsSourceGenerator()))
			.WithUpdatedParseOptions(parseOptions);

		var compilation = CSharpCompilation
			.Create("MockGeneratorTest",
				new[] {CSharpSyntaxTree.ParseText(source, parseOptions)},
				metadataReferences,
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);

		return new RunResult(
			diagnostics.ToList(),
			updated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList(),
			driver.GetRunResult().Results.SelectMany(r => r.GeneratedSources).ToList());
	}
}
