using Xunit;

namespace MockGenerator.Tests;

/// <summary>Golden tests for the interfaces emitted from <c>[GenerateViewInterfaces]</c>.</summary>
public class ViewInterfaceTests
{
	/// <summary>
	/// <c>[Input]</c> members land in <c>I{Name}Input</c> and <c>[Output]</c> members in
	/// <c>I{Name}Output</c>, both in the view's <c>Generated</c> namespace.
	/// </summary>
	[Fact]
	public void InputAndOutputMembersAreSplitIntoTwoInterfaces()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[GenerateViewInterfaces]
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
		Golden.Equal("ViewInterfaces_Input", r.Source("Sample.Generated.IPanelInput.cs"));
		Golden.Equal("ViewInterfaces_Output", r.Source("Sample.Generated.IPanelOutput.cs"));
	}

	/// <summary>
	/// A member that a hand-written <c>[Output]</c> base interface already declares is inherited by
	/// <c>I{Name}Output</c>, so it is not declared again there (that would only hide it, CS0108). The same
	/// applies to <c>[Input]</c> bases and <c>I{Name}Input</c>.
	/// </summary>
	[Fact]
	public void MemberDeclaredByBaseInterfaceIsNotRedeclared()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[Output]
				public interface IToggle
				{
					void SetOn(bool on);
					string Label { set; }
				}

				[Input]
				public interface IPressable
				{
					event System.Action OnPressed;
				}

				[GenerateViewInterfaces]
				public partial class Toggle : IToggle, IPressable
				{
					[Output] public void SetOn(bool on) { }
					[Output] public void SetTint(float tint) { }
					public string Label { [Output] set { } }
					public string Hint { [Output] set { } }
					[Input] public event System.Action OnPressed;
					[Input] public event System.Action OnReleased;
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("ViewInterfaces_InheritedNotRedeclared_Output", r.Source("Sample.Generated.IToggleOutput.cs"));
		Golden.Equal("ViewInterfaces_InheritedNotRedeclared_Input", r.Source("Sample.Generated.IToggleInput.cs"));
	}

	/// <summary>A nullable child view field is projected as a nullable interface.</summary>
	[Fact]
	public void NullableChildFieldProjectsNullableInterface()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[GenerateViewInterfaces]
				public partial class Child
				{
					[Output] public void Show() { }
				}

				[GenerateViewInterfaces]
				public partial class Panel
				{
					[Output] private Child? _child;
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.CompilationErrors);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("ViewInterfaces_NullableChildField_Output", r.Source("Sample.Generated.IPanelOutput.cs"));
		Golden.Equal("ViewInterfaces_NullableChildField_PartialClass", r.Source("Sample.Panel.cs"));
	}

	/// <summary>A child field whose property a base interface already declares with the projected type is not declared again.</summary>
	[Fact]
	public void ChildFieldSatisfyingBaseInterfaceMemberIsNotRedeclared()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[GenerateViewInterfaces]
				public partial class Child
				{
					[Output] public void Show() { }
				}

				[Output]
				public interface IHasChild { global::Sample.Generated.IChildOutput child { get; } }

				[GenerateViewInterfaces]
				public partial class Panel : IHasChild
				{
					[Output] private Child _child = null!;
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.CompilationErrors);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("ViewInterfaces_ChildSatisfiesBase_Output", r.Source("Sample.Generated.IPanelOutput.cs"));
		Golden.Equal("ViewInterfaces_ChildSatisfiesBase_PartialClass", r.Source("Sample.Panel.cs"));
	}

	/// <summary>An <c>[Output]</c> setter on a property that a base interface declares get-only is declared again with <c>new</c> and both accessors.</summary>
	[Fact]
	public void AccessorAddedToBaseInterfacePropertyIsDeclaredWithNew()
	{
		var r = GeneratorRunner.Run(/* lang=c# */ """
			#nullable enable
			using MockGenerator;

			namespace Sample
			{
				[Output]
				public interface IHasText { string Text { get; } }

				[GenerateViewInterfaces]
				public partial class Panel : IHasText
				{
					public string Text { get; [Output] set; } = "";
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Assert.Empty(r.CompilationErrors);
		Assert.Empty(r.GeneratedDiagnostics);
		Golden.Equal("ViewInterfaces_AccessorAddedToBase_Output", r.Source("Sample.Generated.IPanelOutput.cs"));
	}
}
