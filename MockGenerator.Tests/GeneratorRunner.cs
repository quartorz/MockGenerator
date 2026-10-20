using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MockGenerator.Tests;

/// <summary>
/// Runs the generators of this project over a single compilation unit and hands back everything a
/// golden test needs: the compilation's own errors, the generators' diagnostics, the compiler's
/// diagnostics inside the generated files, and the generated files.
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

	/// <param name="Diagnostics">Diagnostics reported by the generators themselves.</param>
	/// <param name="CompilationErrors">Errors of the whole compilation (input source and generated files).</param>
	/// <param name="GeneratedDiagnostics">
	/// Compiler warnings and errors whose location is inside a generated file. The compilation runs with
	/// nullable reference types enabled, so this is where a dropped <c>?</c> or an unguarded <c>default</c>
	/// in the generated code shows up.
	/// </param>
	/// <param name="Sources">Every generated file.</param>
	public sealed record RunResult(
		List<Diagnostic> Diagnostics,
		List<Diagnostic> CompilationErrors,
		List<Diagnostic> GeneratedDiagnostics,
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
	/// Compiles <paramref name="source"/> with nullable reference types enabled and all three generators of
	/// this project attached, as they ship together in one analyzer assembly.
	/// <para>
	/// The attributes the generators key off are emitted by their own post-initialization output, so
	/// <paramref name="source"/> only needs to declare the types under test.
	/// </para>
	/// </summary>
	public static RunResult Run(string source)
	{
		IIncrementalGenerator[] generators =
		{
			new global::MockGenerator.MockGenerator(),
			new global::MockGenerator.GenerateInterfaceGenerator(),
			new global::MockGenerator.GenerateMockForGenerator(),
		};

		var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
		GeneratorDriver driver = CSharpGeneratorDriver
			.Create(generators.Select(g => g.AsSourceGenerator()))
			.WithUpdatedParseOptions(parseOptions);

		var compilation = CSharpCompilation
			.Create("MockGeneratorTest",
				new[] {CSharpSyntaxTree.ParseText(source, parseOptions)},
				metadataReferences,
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
					.WithNullableContextOptions(NullableContextOptions.Enable));

		driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);

		var runResult = driver.GetRunResult();
		var generatedTrees = new HashSet<SyntaxTree>(runResult.GeneratedTrees);
		var compilationDiagnostics = updated.GetDiagnostics();

		return new RunResult(
			diagnostics.ToList(),
			compilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList(),
			compilationDiagnostics
				.Where(d => d.Severity >= DiagnosticSeverity.Warning
					&& d.Location.SourceTree != null && generatedTrees.Contains(d.Location.SourceTree))
				.ToList(),
			runResult.Results.SelectMany(r => r.GeneratedSources).ToList());
	}
}
