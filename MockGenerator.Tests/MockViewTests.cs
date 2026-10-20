using Microsoft.CodeAnalysis;
using Xunit;

namespace MockGenerator.Tests;

/// <summary>Golden tests for the mock emitted from <c>[GenerateMockView]</c>.</summary>
public class MockViewTests
{
	/// <summary>
	/// Members declared on the view itself carry no interface prefix, so the event helper is
	/// <c>RaiseOnClick</c> and the property callback is <c>OnTextSet</c>.
	/// </summary>
	[Fact]
	public void OwnMembersAreEmittedWithoutPrefix()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[GenerateMockView]
				public partial class Panel
				{
					[Input] public event System.Action OnClick;
					public string Text { get; [Output] set; } = "";
					[Output] public void Show(int value) { }
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockView_OwnMembers", r.Source("MockView.Sample.MockPanel.cs"));
	}

	/// <summary>
	/// Two interfaces declaring the same event name force prefixed slots. The helper keeps the prefix
	/// in front — <c>IDialog_RaiseOnClick</c>, not <c>RaiseIDialog_OnClick</c>.
	/// </summary>
	[Fact]
	public void SameEventFromTwoInterfacesGetsPrefixedSlotAndPrefixedRaiser()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[Input]
				public interface IDialog
				{
					event System.Action OnClick;
				}

				[Input]
				public interface ISheet
				{
					event System.Action OnClick;
				}

				[GenerateMockView]
				public partial class Panel : IDialog, ISheet
				{
					public event System.Action OnClick;
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockView_PrefixedEventSlots", r.Source("MockView.Sample.MockPanel.cs"));
	}

	/// <summary>An explicitly implemented member is prefixed even when only one interface declares it.</summary>
	[Fact]
	public void ExplicitImplementationGetsPrefixedSlot()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[Input]
				public interface IDialog
				{
					event System.Action OnClick;
				}

				[GenerateMockView]
				public partial class Panel : IDialog
				{
					event System.Action IDialog.OnClick { add { } remove { } }
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockView_ExplicitImplementation", r.Source("MockView.Sample.MockPanel.cs"));
	}

	/// <summary>
	/// Generic interfaces route through the <c>As{Iface}&lt;...&gt;()</c> accessor instead of the flat
	/// prefixed slots, so distinct closed type arguments never collide.
	/// </summary>
	[Fact]
	public void GenericInterfaceUsesAccessor()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[Output]
				public interface IList<T>
				{
					void Set(T value);
					event System.Action<T> OnChanged;
				}

				[GenerateMockView]
				public partial class Panel : IList<int>, IList<string>
				{
					public void Set(int value) { }
					public void Set(string value) { }
					public event System.Action<int> OnChanged;
					event System.Action<string> IList<string>.OnChanged { add { } remove { } }
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockView_GenericAccessor", r.Source("MockView.Sample.MockPanel.cs"));
	}

	/// <summary>
	/// A delegate with a return value or an <c>out</c> parameter takes the guarded helper body: the
	/// return value falls back to <c>default</c> and every <c>out</c> parameter is initialised.
	/// </summary>
	[Fact]
	public void EventWithReturnValueAndOutParameterGetsGuardedRaiser()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				public delegate bool TryPick(int index, out string picked);

				[GenerateMockView]
				public partial class Panel
				{
					[Input] public event TryPick OnPick;
					[Input] public event System.Func<int> OnCount;
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockView_GuardedRaiser", r.Source("MockView.Sample.MockPanel.cs"));
	}

	/// <summary>
	/// Nullable annotations of the view and its interfaces are copied as declared (<c>string?</c>,
	/// <c>event Action?</c>, <c>T?</c> with <c>where T : class?</c>, <c>string? x = null</c>). The mock's own
	/// slots are nullable, and only the spots where an unset slot yields a null the declared type forbids
	/// (<c>default</c> of a non-nullable reference type, <c>out</c> initialisation, backing storage) carry <c>!</c>.
	/// </summary>
	[Fact]
	public void NullableAnnotationsAreCopied()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[Input]
				public interface IDialog
				{
					string? Find(string? key, string? fallback = null);
					event System.Action<string?>? OnClosed;
				}

				[GenerateMockView]
				public partial class Panel : IDialog
				{
					public string? Find(string? key, string? fallback = null) => null;
					public event System.Action<string?>? OnClosed;
					[Input] public event System.Func<string>? OnQuery;
					public string? Title { get; [Output] set; }
					public string Label { get; [Output] set; } = "";
					[Output] public T? Pick<T>(T? seed) where T : class? => seed;
					[Output] public string Describe(string[] parts, string?[] optional) => "";
					[Output] public bool TryGet(int index, out string value) { value = ""; return false; }
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockView_NullableAnnotations", r.Source("MockView.Sample.MockPanel.cs"));
	}

	/// <summary>The generated mock is valid C# on its own, not just the expected text.</summary>
	[Fact]
	public void GeneratedMockCompiles()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[Input]
				public interface IDialog
				{
					event System.Action OnClick;
				}

				[Input]
				public interface ISheet
				{
					event System.Action OnClick;
				}

				[GenerateMockView]
				public partial class Panel : IDialog, ISheet
				{
					public event System.Action OnClick;
					public string Text { get; [Output] set; } = "";
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.CompilationErrors);
		Assert.Empty(r.GeneratedDiagnostics);
	}

	/// <summary>A view that implements an <c>[Input]</c> method explicitly gets the forwarder with <c>where T : default</c>.</summary>
	[Fact]
	public void UnconstrainedNullableTypeParameterInExplicitImplementationGetsDefaultConstraint()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[Input]
				public interface IFinder { TValue? Find<TValue>(string key); }

				[Input]
				public interface IPutter { void Put<TValue>(TValue? value); }

				[GenerateMockView]
				public partial class Panel : IFinder, IPutter
				{
					TValue? IFinder.Find<TValue>(string key) where TValue : default => default;
					public void Put<TValue>(TValue? value) { }
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.CompilationErrors);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockView_UnconstrainedNullableTypeParameter", r.Source("MockView.Sample.MockPanel.cs"));
	}

	/// <summary>The same for a view that explicitly implements a closed generic interface.</summary>
	[Fact]
	public void UnconstrainedNullableTypeParameterInGenericInterfaceExplicitImplementationGetsDefaultConstraint()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[Input]
				public interface IStore<TKey> { TValue? Find<TValue>(TKey key); }

				[GenerateMockView]
				public partial class Panel : IStore<int>
				{
					TValue? IStore<int>.Find<TValue>(int key) where TValue : default => default;
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.CompilationErrors);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockView_UnconstrainedNullableTypeParameter_GenericInterface", r.Source("MockView.Sample.MockPanel.cs"));
	}

	/// <summary>A nullable child view field is mocked as a nullable slot with no initializer.</summary>
	[Fact]
	public void NullableChildFieldGetsNullableMockSlot()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[GenerateViewInterfaces]
				[GenerateMockView]
				public partial class Child
				{
					[Output] public void Show() { }
				}

				[GenerateViewInterfaces]
				[GenerateMockView]
				public partial class Panel
				{
					[Output] private Child? _child;
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.CompilationErrors);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockView_NullableChildField", r.Source("MockView.Sample.MockPanel.cs"));
	}

	/// <summary>One view method implementing the same-named members of two interfaces yields one slot with the implementation's signature.</summary>
	[Fact]
	public void OneImplementationForSeveralInterfaceMembersYieldsOneSlotWithImplementationSignature()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[Input]
				public interface IA { void Set(string v); }

				[Input]
				public interface IB { void Set(string? v); }

				[Input]
				public interface ISession : IA, IB { }

				[GenerateMockView]
				public partial class Panel : ISession
				{
					public void Set(string? v) { }
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.CompilationErrors);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockView_OneImplementationForSeveralMembers", r.Source("MockView.Sample.MockPanel.cs"));
	}

	/// <summary>Explicit implementations of the same-named members of two interfaces keep one slot each.</summary>
	[Fact]
	public void ExplicitImplementationsForSeveralInterfaceMembersYieldSeparateSlots()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[Input]
				public interface IA { void Set(string v); }

				[Input]
				public interface IB { void Set(string? v); }

				[Input]
				public interface ISession : IA, IB { }

				[GenerateMockView]
				public partial class Panel : ISession
				{
					void IA.Set(string v) { }
					void IB.Set(string? v) { }
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.CompilationErrors);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockView_ExplicitImplementationsForSeveralMembers", r.Source("MockView.Sample.MockPanel.cs"));
	}

	/// <summary>A child field that a base interface already declares is implemented explicitly for that base, and the base member gets no slot of its own.</summary>
	[Fact]
	public void ChildFieldSatisfyingBaseInterfaceMemberIsImplementedExplicitlyForBase()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[GenerateViewInterfaces]
				[GenerateMockView]
				public partial class Child
				{
					[Output] public void Show() { }
				}

				[Output]
				public interface IHasChild { global::Sample.Generated.IChildOutput child { get; } }

				[GenerateViewInterfaces]
				[GenerateMockView]
				public partial class Panel : IHasChild
				{
					[Output] private Child _child = null!;
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.CompilationErrors);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("MockView_ChildSatisfiesBase", r.Source("MockView.Sample.MockPanel.cs"));
	}
}
