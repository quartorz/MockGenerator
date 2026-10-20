using Xunit;

namespace MockGenerator.Tests;

/// <summary>Golden tests for the interfaces emitted from <c>[GenerateViewInterfaces]</c>.</summary>
public class ViewInterfaceTests
{
	static GeneratorRunner.RunResult Run(string source) =>
		GeneratorRunner.Run(source, new global::MockGenerator.MockGenerator());

	/// <summary>
	/// <c>[Input]</c> members land in <c>I{Name}Input</c> and <c>[Output]</c> members in
	/// <c>I{Name}Output</c>, both in the view's <c>Generated</c> namespace.
	/// </summary>
	[Fact]
	public void InputAndOutputMembersAreSplitIntoTwoInterfaces()
	{
		var r = Run(/* lang=c# */ """
			using MockGenerator;

			namespace Sample
			{
				[GenerateViewInterfaces]
				public partial class Panel
				{
					[Input] public event System.Action OnClick;
					[Output] public string Text { get; set; }
					[Output] public void Show(int value) { }
				}
			}
			""");

		Assert.Empty(r.Diagnostics);
		Golden.Equal("ViewInterfaces_Input", r.Source("Sample.Generated.IPanelInput.cs"));
		Golden.Equal("ViewInterfaces_Output", r.Source("Sample.Generated.IPanelOutput.cs"));
	}
}
