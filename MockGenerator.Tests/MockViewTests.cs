using Microsoft.CodeAnalysis;
using Xunit;

namespace MockGenerator.Tests;

/// <summary>Golden tests for the mock emitted from <c>[GenerateMockView]</c>.</summary>
public class MockViewTests
{
	static GeneratorRunner.RunResult Run(string source) =>
		GeneratorRunner.Run(source, new global::MockGenerator.MockGenerator());

	/// <summary>
	/// Members declared on the view itself carry no interface prefix, so the event helper is
	/// <c>RaiseOnClick</c> and the property callback is <c>OnTextSet</c>.
	/// </summary>
	[Fact]
	public void OwnMembersAreEmittedWithoutPrefix()
	{
		var r = Run(/* lang=c# */ """
			using MockGenerator;

			namespace Sample
			{
				[GenerateMockView]
				public partial class Panel
				{
					[Input] public event System.Action OnClick;
					[Output] public string Text { get; set; }
					[Output] public void Show(int value) { }
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Golden.Equal("MockView_OwnMembers", r.Source("MockView.Sample.MockPanel.cs"));
	}

	/// <summary>
	/// Two interfaces declaring the same event name force prefixed slots. The helper keeps the prefix
	/// in front — <c>IDialog_RaiseOnClick</c>, not <c>RaiseIDialog_OnClick</c>.
	/// </summary>
	[Fact]
	public void SameEventFromTwoInterfacesGetsPrefixedSlotAndPrefixedRaiser()
	{
		var r = Run(/* lang=c# */ """
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
		Golden.Equal("MockView_PrefixedEventSlots", r.Source("MockView.Sample.MockPanel.cs"));
	}

	/// <summary>An explicitly implemented member is prefixed even when only one interface declares it.</summary>
	[Fact]
	public void ExplicitImplementationGetsPrefixedSlot()
	{
		var r = Run(/* lang=c# */ """
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
		Golden.Equal("MockView_ExplicitImplementation", r.Source("MockView.Sample.MockPanel.cs"));
	}

	/// <summary>
	/// Generic interfaces route through the <c>As{Iface}&lt;...&gt;()</c> accessor instead of the flat
	/// prefixed slots, so distinct closed type arguments never collide.
	/// </summary>
	[Fact]
	public void GenericInterfaceUsesAccessor()
	{
		var r = Run(/* lang=c# */ """
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
		Golden.Equal("MockView_GenericAccessor", r.Source("MockView.Sample.MockPanel.cs"));
	}

	/// <summary>
	/// A delegate with a return value or an <c>out</c> parameter takes the guarded helper body: the
	/// return value falls back to <c>default</c> and every <c>out</c> parameter is initialised.
	/// </summary>
	[Fact]
	public void EventWithReturnValueAndOutParameterGetsGuardedRaiser()
	{
		var r = Run(/* lang=c# */ """
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
		Golden.Equal("MockView_GuardedRaiser", r.Source("MockView.Sample.MockPanel.cs"));
	}

	/// <summary>The generated mock is valid C# on its own, not just the expected text.</summary>
	[Fact]
	public void GeneratedMockCompiles()
	{
		var r = Run(/* lang=c# */ """
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
					public string Text { get; [Output] set; }
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.CompilationErrors);
	}
}
