using Xunit;

namespace MockGenerator.Tests;

/// <summary>Golden tests for the interface emitted from <c>[GenerateInterface]</c>.</summary>
public class GenerateInterfaceTests
{
	/// <summary>
	/// The public surface is copied with its nullable annotations, optional-parameter defaults and
	/// nullable-analysis attributes, so the interface declares exactly what the class does.
	/// </summary>
	[Fact]
	public void NullableAnnotationsAndAttributesAreCopied()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using System.Diagnostics.CodeAnalysis;
			using MockGenerator;

			namespace Sample
			{
				[GenerateInterface]
				public partial class Session
				{
					[AllowNull] public string Name { get; set; } = "";
					public string? Title { get; set; }
					public event System.Action<string?>? OnClosed;
					public string? Find(string? key, string? fallback = null) => null;
					public T? Pick<T>(T? seed) where T : class? => seed;
					public bool TryGet(int index, [NotNullWhen(true)] out string? value) { value = null; return false; }
					[return: MaybeNull] public string Peek() => "";
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("GenerateInterface_NullableAnnotations", r.Source("Sample.Session.GenerateInterface.cs"));
	}
}
