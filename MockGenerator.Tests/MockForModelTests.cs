using Xunit;

namespace MockGenerator.Tests;

/// <summary>Golden tests for the mock emitted from <c>[GenerateMockFor]</c>.</summary>
public class MockForModelTests
{
	/// <summary>A single interface produces unprefixed members: property, event plus raiser, and method.</summary>
	[Fact]
	public void SingleInterfaceProducesUnprefixedMembers()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				public interface ISession
				{
					string Name { get; set; }
					event System.Action OnClosed;
					int Count(string key);
				}

				[GenerateMockFor(typeof(ISession))]
				public partial class MockSession { }
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockFor_SingleInterface", r.Source("Sample.MockSession.GenerateMockFor.cs"));
	}

	/// <summary>
	/// When base interfaces declare the same event name with different delegate types, each one gets a
	/// prefixed slot whose raiser keeps the prefix in front — <c>IUpper_RaiseOnChanged</c>.
	/// </summary>
	[Fact]
	public void ConflictingEventTypesGetPrefixedSlotAndPrefixedRaiser()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				public interface IUpper
				{
					event System.Action<int> OnChanged;
				}

				public interface ILower
				{
					event System.Action<string> OnChanged;
				}

				public interface ISession : IUpper, ILower { }

				[GenerateMockFor(typeof(ISession))]
				public partial class MockSession { }
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockFor_ConflictingEventTypes", r.Source("Sample.MockSession.GenerateMockFor.cs"));
	}

	/// <summary>
	/// Nullable annotations of the interface are copied as declared: <c>string?</c> parameters and returns,
	/// an optional <c>string? x = null</c>, <c>event Action?</c>, <c>T?</c> with <c>where T : class?</c>, and
	/// annotated array elements. A non-nullable return or <c>out</c> parameter of an unset slot gets <c>!</c>.
	/// </summary>
	[Fact]
	public void NullableAnnotationsAreCopied()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				public interface ISession
				{
					string? Title { get; set; }
					string Name { get; }
					event System.Action<string?>? OnClosed;
					string? Find(string? key, string? fallback = null);
					T? Pick<T>(T? seed) where T : class?;
					string Describe(string[] parts, string?[] optional);
					bool TryGet(int index, out string value);
				}

				[GenerateMockFor(typeof(ISession))]
				public partial class MockSession { }
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockFor_NullableAnnotations", r.Source("Sample.MockSession.GenerateMockFor.cs"));
	}

	/// <summary>
	/// Nullable-analysis attributes (<c>System.Diagnostics.CodeAnalysis</c>) declared on parameters, return
	/// values and properties are copied, fully qualified, onto the delegate, the forwarder and the property.
	/// </summary>
	[Fact]
	public void NullableAttributesAreCopied()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using System.Diagnostics.CodeAnalysis;
			using MockGenerator;

			namespace Sample
			{
				public interface ISession
				{
					[AllowNull] string Name { get; set; }
					[MaybeNull] string Label { get; set; }
					[NotNull] string? Cache { get; set; }
					[DisallowNull] string? Tag { get; set; }
					bool TryFind(string key, [NotNullWhen(true)] out string? value);
					[return: NotNullIfNotNull("fallback")] string? Resolve(string? fallback);
					[return: MaybeNull] string Peek();
					void Put([DisallowNull] string? item);
					bool Fill([AllowNull] string first, [MaybeNullWhen(false)] out string second);
				}

				[GenerateMockFor(typeof(ISession))]
				public partial class MockSession { }
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockFor_NullableAttributes", r.Source("Sample.MockSession.GenerateMockFor.cs"));
	}

	/// <summary>An explicit implementation of a method whose unconstrained <c>T?</c> is a nullable reference annotation restates it as <c>where T : default</c>.</summary>
	[Fact]
	public void UnconstrainedNullableTypeParameterInExplicitImplementationGetsDefaultConstraint()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				public interface IStore<TKey>
				{
					TValue? Find<TValue>(TKey key);
					void Put<TValue>(TKey key, TValue? value);
				}

				public interface ISession : IStore<int> { }

				[GenerateMockFor(typeof(ISession))]
				public partial class MockSession { }
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.CompilationErrors);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockFor_UnconstrainedNullableTypeParameter", r.Source("Sample.MockSession.GenerateMockFor.cs"));
	}

	/// <summary>Members with the same signature declared by different interfaces get one prefixed slot per interface.</summary>
	[Fact]
	public void SameSignatureFromDifferentInterfacesGetsPerInterfaceSlots()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				public interface IA { void Set(string v); string Name { get; } event System.Action OnChanged; }

				public interface IB { void Set(string v); string Name { get; } event System.Action OnChanged; }

				public interface ISession : IA, IB { }

				[GenerateMockFor(typeof(ISession))]
				public partial class MockSession { }
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.CompilationErrors);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockFor_SameSignatureFromDifferentInterfaces", r.Source("Sample.MockSession.GenerateMockFor.cs"));
	}

	/// <summary>A member reached through several inheritance paths is emitted once, without a prefix.</summary>
	[Fact]
	public void MemberReachedThroughDiamondInheritanceIsEmittedOnce()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				public interface IBase { void Set(string v); string Name { get; } event System.Action OnChanged; }

				public interface IA : IBase { }

				public interface IB : IBase { }

				public interface ISession : IA, IB { }

				[GenerateMockFor(typeof(ISession))]
				public partial class MockSession { }
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.CompilationErrors);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockFor_DiamondInheritanceEmittedOnce", r.Source("Sample.MockSession.GenerateMockFor.cs"));
	}
}
